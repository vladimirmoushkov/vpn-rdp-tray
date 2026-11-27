using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Win32;

namespace VpnRdpTray;

internal sealed class RdpActiveX() : AxHost("1DF7C823-B2D4-4B54-975A-F2AC5D7CF8B8")
{
    private static readonly Guid EventInterface = new("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6");
    private readonly List<(int Id, Delegate Callback)> subscriptions = [];
    private object? control;
    private dynamic Client => control ?? throw new InvalidOperationException("The RDP control is unavailable.");

    internal event Action? TransportConnected;
    internal event Action? LoginComplete;
    internal event Action<int>? Disconnected;
    internal event Action<int>? LogonError;
    internal event Action<int>? FatalError;
    internal event Action? AuthenticationWarning;
    internal event Action? AuthenticationDismissed;
    internal event Action? FullScreenRequested;
    internal event Action<Size>? RemoteDesktopSizeChanged;

    protected override void AttachInterfaces() => control = GetOcx();

    protected override void CreateSink()
    {
        if (control is null)
            throw new InvalidOperationException("The RDP event source is unavailable.");

        Subscribe(2, (Action)(() => TransportConnected?.Invoke()));
        Subscribe(3, (Action)(() => LoginComplete?.Invoke()));
        Subscribe(4, (Action<int>)(code => Disconnected?.Invoke(code)));
        Subscribe(8, (Action)(() => FullScreenRequested?.Invoke()));
        Subscribe(9, (Action)(() => FullScreenRequested?.Invoke()));
        Subscribe(10, (Action<int>)(code => FatalError?.Invoke(code)));
        Subscribe(12, (Action<int, int>)((width, height) => RemoteDesktopSizeChanged?.Invoke(new Size(width, height))));
        Subscribe(18, (Action)(() => AuthenticationWarning?.Invoke()));
        Subscribe(19, (Action)(() => AuthenticationDismissed?.Invoke()));
        Subscribe(22, (Action<int>)(code => LogonError?.Invoke(code)));
    }

    protected override void DetachSink()
    {
        if (control is null)
            return;

        foreach (var (id, callback) in subscriptions)
            ComEventsHelper.Remove(control, EventInterface, id, callback);

        subscriptions.Clear();
    }

    private void Subscribe(int id, Delegate callback)
    {
        ComEventsHelper.Combine(control!, EventInterface, id, callback);
        subscriptions.Add((id, callback));
    }

    internal void Configure(string server, int port, string user, string? password, bool entra, Size display, int dpi)
    {
        Client.Server = IPAddress.TryParse(server, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{server}]" : server;
        Client.ColorDepth = 32;
        Client.DesktopWidth = display.Width;
        Client.DesktopHeight = display.Height;
        var separator = entra ? -1 : user.IndexOf('\\');
        Client.UserName = separator < 0 ? user : user[(separator + 1)..];
        Client.Domain = separator < 0 ? "" : user[..separator];

        // The host manages full screen. Route Windows shortcuts while the control has focus in either view.
        Client.SecuredSettings2.KeyboardHookMode = 1;

        dynamic settings = Client.AdvancedSettings9;
        settings.RDPPort = port;
        settings.AuthenticationLevel = 1u;
        settings.EnableCredSspSupport = true;
        settings.NegotiateSecurityLayer = true;
        settings.EnableAutoReconnect = false;
        settings.SmartSizing = true;
        settings.overallConnectionTimeout = 30;

        // The host owns full-screen geometry, so the control stays inside the same window.
        try { settings.ContainerHandledFullScreen = 1; }
        catch (Exception ex) when (IsOptionalDisplayError(ex)) { }
        try
        {
            uint scale = DesktopScale(dpi);
            Extended("DesktopScaleFactor", scale);
            Extended("DeviceScaleFactor", DeviceScale(scale));
        }
        catch (Exception ex) when (IsOptionalDisplayError(ex)) { }

        // Never silently switch to Remote Desktop Gateway discovery.
        ((dynamic)Client.TransportSettings).GatewayUsageMethod = 0u;

        if (entra)
            Extended("EnableRdsAadAuth", true);
        else if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            Extended("allowed security protocols", "CredSSP");

        if (!entra && !string.IsNullOrEmpty(password))
            settings.ClearTextPassword = password;
    }

    internal void ConfigureRedirections(RdpSettings options)
    {
        dynamic advanced = Client.AdvancedSettings9;
        advanced.RedirectSmartCards = options.RedirectSecurityKeys;
        advanced.RedirectPrinters = options.RedirectPrinters;
        advanced.RedirectClipboard = options.RedirectClipboard;
        advanced.RedirectDrives = options.RedirectDrives;

        var settings = (IRdpDeviceRedirectionSettings)control!;
        settings.SetRedirectDynamicDrives(options.RedirectDrives);
        settings.SetRedirectDynamicDevices(options.RedirectUsbDevices);
        // Use the control's device inventory so it enforces Windows redirection policy.
        settings.GetDeviceCollection(out var devices);
        if (devices is null)
            return;
        devices.RescanDevices(options.RedirectUsbDevices);
        devices.GetDeviceCount(out uint count);
        for (uint index = 0; index < count; index++)
        {
            devices.GetDeviceByIndex(index, out var device);
            device.SetRedirectionState(options.RedirectUsbDevices);
        }
    }

    internal bool RedirectionsMatch(RdpSettings options)
    {
        dynamic advanced = Client.AdvancedSettings9;
        var settings = (IRdpDeviceRedirectionSettings)control!;
        settings.GetRedirectDynamicDrives(out bool dynamicDrives);
        settings.GetRedirectDynamicDevices(out bool dynamicDevices);
        return advanced.RedirectSmartCards == options.RedirectSecurityKeys &&
            advanced.RedirectPrinters == options.RedirectPrinters &&
            advanced.RedirectClipboard == options.RedirectClipboard &&
            advanced.RedirectDrives == options.RedirectDrives &&
            dynamicDrives == options.RedirectDrives && dynamicDevices == options.RedirectUsbDevices;
    }

    internal bool RedirectsWindowsShortcuts => Client.SecuredSettings2.KeyboardHookMode == 1;

    internal static Size DisplaySize(Size viewport) =>
        new(Math.Clamp(viewport.Width, 200, 8192) & ~1, Math.Clamp(viewport.Height, 200, 8192));

    internal bool SetMultiMonitor(bool enabled)
    {
        var settings = (IRdpMultiMonitorSettings)control!;
        settings.SetUseMultimon(enabled);
        settings.GetUseMultimon(out bool configured);
        return configured;
    }

    internal (uint MonitorCount, Size Size, bool MatchesLocal)? RemoteMonitorLayout()
    {
        try
        {
            var settings = (IRdpMultiMonitorSettings)control!;
            settings.GetRemoteMonitorCount(out uint count);
            if (count < 2)
                return count == 0 ? null : (count, System.Drawing.Size.Empty, false);
            settings.GetRemoteMonitorsBoundingBox(out int left, out int top, out int right, out int bottom);
            settings.GetRemoteMonitorLayoutMatchesLocal(out bool matches);
            return (count, new Size(right - left, bottom - top), matches);
        }
        catch (Exception ex) when (IsOptionalDisplayError(ex)) { return null; }
    }

    internal void UpdateDisplay(Size size, int dpi)
    {
        uint scale = DesktopScale(dpi);
        // Physical monitor size is optional. Zero leaves it unspecified.
        try { Client.UpdateSessionDisplaySettings((uint)size.Width, (uint)size.Height, 0u, 0u, 0u, scale, DeviceScale(scale)); }
        catch (Exception ex) when (IsOptionalDisplayError(ex)) { }
    }

    private static uint DesktopScale(int dpi) => (uint)Math.Clamp((int)Math.Round(dpi * (100d / 96)), 100, 500);

    private static uint DeviceScale(uint desktopScale) => desktopScale switch
    {
        < 120 => 100u,
        < 160 => 140u,
        _ => 180u
    };

    private static bool IsOptionalDisplayError(Exception ex) =>
        ex is COMException or RuntimeBinderException or InvalidOperationException or InvalidCastException;

    private void Extended(string name, object value)
    {
        var extended = (IRdpExtendedSettings)control!;
        extended.SetProperty(name, ref value);
    }

    internal void Connect() => Client.Connect();

    internal bool? IsTransportConnected
    {
        get
        {
            try { return (short)Client.Connected == 1; }
            catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidOperationException or InvalidCastException)
            { return null; }
        }
    }

    internal int ReadExtendedDisconnectReason()
    {
        try { return (int)Client.ExtendedDisconnectReason; }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidOperationException or InvalidCastException)
        { return 0; }
    }

    internal bool SupportsPointerInput => control is IRdpPointerInput;

    internal bool MoveRemotePointer(Point point) => control is IRdpPointerInput input && (short)Client.Connected == 1 &&
        input.SendMouseMoveEvent((uint)point.X, (uint)point.Y) == 0;

    internal void Disconnect()
    {
        if (control is not null && Client.Connected != 0)
            Client.Disconnect();
    }

    internal static bool PolicyAllowsPassword()
    {
        const string path = @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(path);
            if (key?.GetValue("DisablePasswordSaving") is 1 ||
                key?.GetValue("PromptForCredsOnClient") is 1 ||
                key?.GetValue("fPromptForPassword") is 1)
                return false;
        }

        return true;
    }

    [ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRdpExtendedSettings
    {
        void SetProperty([MarshalAs(UnmanagedType.BStr)] string name, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    [ComImport, Guid("4606850E-76A7-4E28-A47E-C7174F619351"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRdpPointerInput
    {
        // The native IMsRdpInputSink vtable puts SendMouseButtonEvent before SendMouseMoveEvent.
        void _VtblGap1_1();
        [PreserveSig]
        int SendMouseMoveEvent(uint x, uint y);
    }

    [ComImport, Guid("B3378D90-0728-45C7-8ED7-B6159FB92219"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRdpDeviceRedirectionSettings
    {
        // IMsRdpClientNonScriptable3 places these properties after 22 preceding COM methods.
        void _VtblGap1_22();
        void SetRedirectDynamicDrives([MarshalAs(UnmanagedType.VariantBool)] bool enabled);
        void GetRedirectDynamicDrives([MarshalAs(UnmanagedType.VariantBool)] out bool enabled);
        void SetRedirectDynamicDevices([MarshalAs(UnmanagedType.VariantBool)] bool enabled);
        void GetRedirectDynamicDevices([MarshalAs(UnmanagedType.VariantBool)] out bool enabled);
        void GetDeviceCollection([MarshalAs(UnmanagedType.Interface)] out IRdpDeviceCollection? devices);
    }

    [ComImport, Guid("56540617-D281-488C-8738-6A8FDF64A118"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRdpDeviceCollection
    {
        void RescanDevices([MarshalAs(UnmanagedType.VariantBool)] bool enabledByDefault);
        void GetDeviceByIndex(uint index, [MarshalAs(UnmanagedType.Interface)] out IRdpDevice device);
        void _VtblGap1_1();
        void GetDeviceCount(out uint count);
    }

    [ComImport, Guid("60C3B9C8-9E92-4F5E-A3E7-604A912093EA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRdpDevice
    {
        void _VtblGap1_3();
        void SetRedirectionState([MarshalAs(UnmanagedType.VariantBool)] bool enabled);
    }

    [ComImport, Guid("4F6996D5-D7B1-412C-B0FF-063718566907"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRdpMultiMonitorSettings
    {
        // The mstscax type library places UseMultimon after 50 inherited non-IUnknown methods.
        void _VtblGap1_50();
        void SetUseMultimon([MarshalAs(UnmanagedType.VariantBool)] bool enabled);
        void GetUseMultimon([MarshalAs(UnmanagedType.VariantBool)] out bool enabled);
        void GetRemoteMonitorCount(out uint count);
        void GetRemoteMonitorsBoundingBox(out int left, out int top, out int right, out int bottom);
        void GetRemoteMonitorLayoutMatchesLocal([MarshalAs(UnmanagedType.VariantBool)] out bool matches);
    }
}

internal static class RdpPrototype
{
    public static int CheckLocalControl()
    {
        string stage = "instantiate the control and attach its event sink";
        try
        {
            using var window = new Form { ShowInTaskbar = false };
            using var rdp = new RdpActiveX();
            ((ISupportInitialize)rdp).BeginInit();
            window.Controls.Add(rdp);
            ((ISupportInitialize)rdp).EndInit();
            _ = window.Handle;
            _ = rdp.Handle;
            stage = "confirm the embedded pointer input interface";
            if (!rdp.SupportsPointerInput)
                throw new InvalidOperationException("The ActiveX control does not expose pointer input.");
            stage = "configure multiple monitors";
            if (!rdp.SetMultiMonitor(true) || rdp.SetMultiMonitor(false))
                throw new InvalidOperationException("The ActiveX control did not accept the monitor configuration.");
            stage = "configure device redirection";
            var redirections = new RdpSettings("prototype.invalid", null, 3389, "prototype@example.invalid",
                RdpMode.Entra, null, false, true);
            rdp.ConfigureRedirections(redirections);
            if (!rdp.RedirectionsMatch(redirections))
                throw new InvalidOperationException("The ActiveX control did not enable the requested redirections.");
            redirections = redirections with
            {
                RedirectSecurityKeys = false,
                RedirectUsbDevices = false,
                RedirectPrinters = false,
                RedirectClipboard = false,
                RedirectDrives = false
            };
            rdp.ConfigureRedirections(redirections);
            if (!rdp.RedirectionsMatch(redirections))
                throw new InvalidOperationException("The ActiveX control did not disable the requested redirections.");
            stage = "configure Entra authentication";
            rdp.Configure("prototype.invalid", 3389, "prototype@example.invalid", null, entra: true,
                RdpActiveX.DisplaySize(rdp.ClientSize), rdp.DeviceDpi);
            stage = "confirm Entra keyboard shortcut redirection";
            if (!rdp.RedirectsWindowsShortcuts)
                throw new InvalidOperationException("The Entra control did not accept keyboard shortcut redirection.");
            stage = "configure classic NLA and certificate validation";
            using var classic = new RdpActiveX();
            ((ISupportInitialize)classic).BeginInit();
            window.Controls.Add(classic);
            ((ISupportInitialize)classic).EndInit();
            _ = classic.Handle;
            classic.Configure("prototype.invalid", 3389, @"DOMAIN\prototype", null, entra: false,
                RdpActiveX.DisplaySize(classic.ClientSize), classic.DeviceDpi);
            stage = "confirm classic keyboard shortcut redirection";
            if (!classic.RedirectsWindowsShortcuts)
                throw new InvalidOperationException("The classic control did not accept keyboard shortcut redirection.");
            Console.WriteLine("ActiveX activation, event binding, scoped pointer input, device and keyboard redirections, and Entra/classic settings succeeded.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to {stage} ({ex.GetType().Name}, 0x{ex.HResult:X8}).");
            return 1;
        }
    }
}
