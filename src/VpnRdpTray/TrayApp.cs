using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace VpnRdpTray;

internal sealed record StatusLogEntry(DateTimeOffset Time, string Level, string Area, string Message);

internal sealed class TrayApp : ApplicationContext
{
    private const int MaxLogEntries = 200;
    private readonly NotifyIcon icon;
    private readonly Icon? applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem summary = new() { Enabled = false };
    private readonly ToolStripMenuItem setupMenu = new("Settings…");
    private readonly ToolStripMenuItem connectionMenu = new();
    private readonly ToolStripMenuItem autoStartMenu = new();
    private readonly ToolStripMenuItem signInVpn = new("Sign in to VPN") { Visible = false };
    private readonly ToolStripMenuItem openRdp = new("Open Remote Desktop");
    private readonly ToolStripMenuItem exit = new("Exit");
    private readonly ConnectionController controller;
    private readonly StatusHotKey statusHotKey;
    private readonly SynchronizationContext ui;
    private readonly RegisteredWaitHandle stopRegistration, showRegistration;
    private readonly Dictionary<string, Icon> glyphs = new()
    {
        ["off"] = Glyph(Color.FromArgb(113, 126, 139)),
        ["action"] = Glyph(Color.FromArgb(231, 158, 36)),
        ["waiting"] = Glyph(Color.FromArgb(53, 138, 226)),
        ["vpn"] = Glyph(Color.FromArgb(54, 185, 187)),
        ["handoff"] = Glyph(Color.FromArgb(156, 122, 229)),
        ["rdp"] = Glyph(Color.FromArgb(53, 186, 107)),
        ["retry"] = Glyph(Color.FromArgb(221, 77, 75))
    };
    private SetupForm? setup;
    private StatusForm? statusForm;
    private bool hasConfiguration, connection, autoStart, busy, exiting, exitPending;
    private readonly List<StatusLogEntry> connectionLog = new(MaxLogEntries);
    private string? loadFailure, saveFailure, startupFailure, connectionFailure;
    private ConnectionStatus vpn = new("VPN idle"), rdp = new("RDP waiting for VPN");

    internal TrayApp(SynchronizationContext ui, EventWaitHandle stop, EventWaitHandle show, bool openSetup,
        bool openStatus = false)
    {
        this.ui = ui;
        menu.Items.AddRange([summary, new ToolStripSeparator(), signInVpn, setupMenu, connectionMenu, autoStartMenu,
            openRdp, new ToolStripSeparator(), exit]);
        icon = new NotifyIcon { ContextMenuStrip = menu, Icon = glyphs["off"], Visible = true };
        controller = new ConnectionController(new VpnSignInPrompt(), (vpn, rdp) =>
        {
            bool closed = rdp.Name == ConnectionController.RdpClosedStatus &&
                this.rdp.Name != ConnectionController.RdpClosedStatus;
            bool vpnPrompt = vpn.Name == ConnectionController.VpnSignInStatus && this.vpn.Name != vpn.Name;
            bool vpnBlocked = vpn.Action && !vpn.SignIn && (!this.vpn.Action || this.vpn.Reason != vpn.Reason);
            bool rdpBlocked = vpn.Name == "VPN connected" && rdp.Name == "RDP paused" &&
                (!this.rdp.Action || this.rdp.Reason != rdp.Reason);
            LogStatus("VPN", this.vpn, vpn);
            LogStatus("RDP", this.rdp, rdp);
            this.vpn = vpn;
            this.rdp = rdp;
            UpdateMenu();
            if (!connection || busy || exiting)
                return;
            if (vpnPrompt)
                icon.ShowBalloonTip(4000, vpn.Name, vpn.Reason ?? "Choose Sign in to VPN.", ToolTipIcon.Info);
            else if (vpnBlocked)
                icon.ShowBalloonTip(5000, "VPN needs attention", vpn.Reason ?? "Open the connection status for details.", ToolTipIcon.Warning);
            else if (rdpBlocked)
                icon.ShowBalloonTip(5000, "Remote Desktop needs attention", rdp.Reason ?? "Open the connection status for details.", ToolTipIcon.Warning);
            else if (closed && vpn.Name == "VPN connected")
            {
                icon.ShowBalloonTip(4500, "Remote Desktop closed",
                    rdp.RetryAt is null ? "Choose Open Remote Desktop to try again." : "Reconnecting through the VPN.",
                    ToolTipIcon.Info);
            }
        });
        SetupPreferences();
        AddLog("APP", "INFO", hasConfiguration ? "Loaded connection preferences." : "Settings required. Import a VPN profile to begin.");
        setupMenu.Click += (_, _) => ShowSetup();
        connectionMenu.Click += async (_, _) => await ChangeConnectionAsync();
        autoStartMenu.Click += (_, _) => SetAutoStart(!autoStart);
        signInVpn.Click += (_, _) => OpenVpnSignIn();
        openRdp.Click += (_, _) => OpenRemoteDesktop();
        exit.Click += async (_, _) => await ExitAsync();
        icon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
                ShowStatus();
        };
        icon.BalloonTipClicked += (_, _) =>
        {
            if (CanSignInToVpn)
                OpenVpnSignIn();
            else
                ShowStatus();
        };
        stopRegistration = ThreadPool.RegisterWaitForSingleObject(stop,
            (_, _) => ui.Post(async _ => await ExitAsync(), null), null, Timeout.Infinite, false);
        showRegistration = ThreadPool.RegisterWaitForSingleObject(show,
            (_, _) => ui.Post(_ => ShowSetup(), null), null, Timeout.Infinite, false);
        statusHotKey = new StatusHotKey(ShowStatus);
        SystemEvents.SessionEnding += OnSessionEnding;

        NativeRdpSession.DeleteFile(Path.Combine(Settings.DirectoryPath, "entra.rdp"));
        UpdateMenu();
        ui.Post(_ =>
        {
            // Migrate an older authenticator configuration even when Connection is off.
            if (!connection && hasConfiguration)
                ReadConfiguration();
            StartConnection();
            if (openSetup)
                ShowSetup();
            else if (openStatus)
                ShowStatus();
            else if (!hasConfiguration)
                icon.ShowBalloonTip(4000, "Settings required",
                    loadFailure ?? "Choose Settings to import a VPN profile and add your Remote Desktop target.", ToolTipIcon.Info);
        }, null);
    }

    private void SetupPreferences()
    {
        try { connection = Settings.Enabled("Connection"); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            connectionFailure = "Windows blocked reading the Connection setting. Try turning it on or check Windows account permissions.";
            AddLog("APP", "ERROR", connectionFailure);
        }
        try
        {
            autoStart = Settings.Enabled("AutoStart");
            Settings.SetStartupEntry(autoStart);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            startupFailure = "Windows blocked the Auto-start setting. Try the switch again or check Windows account permissions.";
            AddLog("APP", "ERROR", startupFailure);
        }
        hasConfiguration = File.Exists(Settings.ConfigPath(Settings.DirectoryPath));
    }

    private Configuration? ReadConfiguration()
    {
        try
        {
            var saved = Settings.Load(Settings.DirectoryPath);
            hasConfiguration = saved is not null;
            loadFailure = null;
            return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or
            SetupException or System.Security.SecurityException)
        {
            hasConfiguration = false;
            string reason = ex switch
            {
                SetupException setupError => setupError.Message,
                CryptographicException => "Windows could not decrypt saved settings for this account. Open Settings and enter the configuration again.",
                JsonException => "The saved settings could not be read. Open Settings and save a new configuration.",
                _ => "Windows blocked access to the saved settings. Check your Windows profile permissions and open Settings."
            };
            if (reason != loadFailure)
                AddLog("APP", "ERROR", reason);
            loadFailure = reason;
            return null;
        }
    }

    private void StartConnection()
    {
        if (!exiting && connection && ReadConfiguration() is { } saved)
            controller.Start(saved);
        UpdateMenu();
    }

    private async Task ChangeConnectionAsync()
    {
        if (busy || exiting)
            return;
        busy = true;
        UpdateMenu();
        try
        {
            Settings.SetEnabled("Connection", !connection);
            connectionFailure = null;
            connection = !connection;
            AddLog("APP", "INFO", connection ? "Connection turned on." : "Connection turned off. Closing owned sessions.");
            if (connection)
                StartConnection();
            else
                await controller.StopAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            connectionFailure = "Windows could not save the Connection switch. Try again or check Windows account permissions.";
            AddLog("APP", "ERROR", connectionFailure);
            Alert(connectionFailure);
        }
        finally
        {
            busy = false;
            if (exitPending) await ExitAsync();
            else UpdateMenu();
        }
    }

    private bool SetAutoStart(bool enabled)
    {
        if (exiting || busy || enabled == autoStart)
            return autoStart;

        try
        {
            Settings.SetEnabled("AutoStart", enabled);
            autoStart = enabled;
            startupFailure = null;
            AddLog("APP", "INFO", enabled ? "Windows sign-in startup enabled." : "Windows sign-in startup disabled.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            startupFailure = "Windows blocked the Auto-start change. Try again or check Windows account permissions.";
            AddLog("APP", "ERROR", startupFailure);
            Alert(startupFailure);
        }
        setup?.SetAutoStart(autoStart);
        UpdateMenu();
        return autoStart;
    }

    private void ShowSetup()
    {
        if (exiting)
            return;
        if (setup is not null)
        {
            setup.WindowState = FormWindowState.Normal;
            setup.Activate();
            return;
        }
        setup = new SetupForm(ReadConfiguration(), SaveConfigurationAsync, autoStart, SetAutoStart)
        {
            Icon = applicationIcon,
            ShowIcon = applicationIcon is not null
        };
        setup.FormClosed += (_, _) => setup = null;
        setup.Show();
        setup.Activate();
        UpdateMenu();
    }

    internal void ShowStatus()
    {
        if (exiting)
            return;
        if (statusForm is { } existing)
        {
            if (existing.WindowState == FormWindowState.Minimized)
                existing.WindowState = FormWindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new StatusForm(OpenVpnSignIn, OpenRemoteDesktop, async () => await ChangeConnectionAsync(),
            ToggleRemoteDesktop, ShowSetup, controller.CapturePreview, () => controller.IsPreviewMinimized)
        {
            Icon = applicationIcon,
            ShowIcon = applicationIcon is not null
        };
        statusForm = window;
        window.FormClosed += (_, _) => statusForm = null;
        UpdateStatusWindow();
        window.Show();
        window.Activate();
    }

    private async Task<string?> SaveConfigurationAsync(Configuration candidate)
    {
        if (busy || exiting)
            return "Wait for the current connection change, then save again.";
        busy = true;
        saveFailure = null;
        bool savedToDisk = false;
        UpdateMenu();
        try
        {
            await controller.StopAsync();
            await MachineSetup.SetHostsAsync(candidate.Rdp);
            Settings.Save(Settings.DirectoryPath, candidate);
            savedToDisk = true;
            hasConfiguration = true;
            loadFailure = null;
            Settings.VpnAction = null;
            Settings.VpnRejections = 0;
            Settings.VpnNotBefore = null;
            Settings.RdpAction = null;
            AddLog("APP", "OK", "Settings saved.");
            return null;
        }
        catch (Exception ex) when (ex is SetupException or IOException or UnauthorizedAccessException or CryptographicException or
            Win32Exception or System.Security.SecurityException)
        {
            saveFailure = savedToDisk
                ? "Settings were saved. Windows blocked resetting the connection state. Check permissions and save Settings again."
                : ex is SetupException setupError ? setupError.Message
                : "Windows could not save the settings. Check access to your Windows profile and try saving again.";
            AddLog("APP", "ERROR", saveFailure);
            return saveFailure;
        }
        finally
        {
            busy = false;
            if (exitPending) await ExitAsync();
            else StartConnection();
        }
    }

    private void OpenRemoteDesktop()
    {
        if (busy || exiting)
            return;
        if (!hasConfiguration || !connection || !controller.OpenRemoteDesktop())
            ShowStatus();
    }

    private void ToggleRemoteDesktop()
    {
        if (hasConfiguration && connection && !busy && !exiting)
            controller.ToggleRemoteDesktop();
    }

    private bool CanSignInToVpn => connection && hasConfiguration && vpn.SignIn && !busy && !exiting;

    private void OpenVpnSignIn()
    {
        if (CanSignInToVpn && controller.OpenVpnSignIn())
            return;
        ShowStatus();
    }

    private void UpdateMenu()
    {
        connectionMenu.Text = connection ? "Connection On (turn off)" : "Connection Off (turn on)";
        connectionMenu.Checked = connection;
        autoStartMenu.Text = autoStart ? "Auto-start On (turn off)" : "Auto-start Off (turn on)";
        autoStartMenu.Checked = autoStart;
        setupMenu.Enabled = !exiting;
        exit.Enabled = !busy && !exiting;
        connectionMenu.Enabled = autoStartMenu.Enabled = openRdp.Enabled = !busy && !exiting;
        signInVpn.Visible = CanSignInToVpn;
        string name = !hasConfiguration ? "Settings required" : !connection ? "Connection off" : vpn.Name;
        if (hasConfiguration && connection && vpn.Name == "VPN connected")
            name = $"{name} · {rdp.Name}";
        var reason = loadFailure ?? saveFailure ?? connectionFailure ?? startupFailure;
        if (reason is null && hasConfiguration && connection)
            reason = PrimaryStatus.LastFailure ?? PrimaryStatus.Reason;
        var retry = hasConfiguration && connection ? PrimaryStatus.RetryAt : null;
        bool action = ActionNeeded;
        string prefix = action ? $"Action required · {name}" : name;
        string time = retry is { } when ? $" · {RetryText(when)}" : "";
        string details = reason is null ? prefix : $"{prefix} · {reason}";
        summary.Text = $"{details}{time}";
        UpdateStatusWindow();
        int available = 127 - time.Length;
        icon.Text = (details.Length > available ? details[..(available - 1)] + "…" : details) + time;
        string glyph = !hasConfiguration || action ? "action" : !connection ? "off" :
            retry is not null ? "retry" : vpn.Name != "VPN connected" ? "waiting" :
            rdp.Name == "RDP connected" ? "rdp" : rdp.Name == "Opened in Remote Desktop" ? "handoff" : "vpn";
        icon.Icon = glyphs[glyph];
    }

    private void UpdateStatusWindow()
    {
        statusForm?.UpdateStatus(connection, hasConfiguration, !busy && !exiting, CanSignInToVpn,
            controller.RdpEnabled, vpn, rdp, connectionLog);
    }

    private void LogStatus(string area, ConnectionStatus previous, ConnectionStatus current)
    {
        if (previous == current)
            return;

        string level = current.RetryAt is not null ? "WARN" : current.Action ? "ACTION" :
            current.Name is "VPN connected" or "RDP connected" or "Opened in Remote Desktop" ? "OK" : "INFO";
        string message = current.Reason is null ? current.Name : $"{current.Name}. {current.Reason}";
        if (current.RetryAt is { } time)
            message = $"{message.TrimEnd('.')}. {RetryText(time)}.";
        AddLog(area, level, message);
    }

    private void AddLog(string area, string level, string message)
    {
        if (connectionLog.Count == MaxLogEntries)
            connectionLog.RemoveAt(0);
        connectionLog.Add(new(DateTimeOffset.Now, level, area, message));
    }

    private ConnectionStatus PrimaryStatus =>
        vpn.Name == "VPN connected" && (rdp.Action || rdp.Reason is not null || rdp.LastFailure is not null ||
            rdp.RetryAt is not null) ? rdp : vpn;

    private bool ActionNeeded => loadFailure is not null || saveFailure is not null || connectionFailure is not null ||
        startupFailure is not null || hasConfiguration && connection && PrimaryStatus.Action;

    private static string RetryText(DateTimeOffset time) =>
        "Retry at " + time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    private void Alert(string message)
    {
        icon.ShowBalloonTip(4500, "VPN Remote Desktop", message, ToolTipIcon.Info);
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs args) =>
        ui.Post(async _ => await ExitAsync(), null);

    private async Task ExitAsync()
    {
        if (exiting)
            return;
        if (busy)
        {
            exitPending = true;
            return;
        }
        exiting = true;
        UpdateMenu();
        try { await controller.StopAsync(); }
        finally
        {
            setup?.Dispose();
            statusForm?.Dispose();
            icon.Visible = false;
            ExitThread();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionEnding -= OnSessionEnding;
            stopRegistration.Unregister(null);
            showRegistration.Unregister(null);
            statusHotKey.Dispose();
            icon.Dispose();
            applicationIcon?.Dispose();
            menu.Dispose();
            foreach (var glyph in glyphs.Values)
                glyph.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon Glyph(Color state)
    {
        using var image = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(image);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var panel = new SolidBrush(Color.FromArgb(28, 48, 70));
        using var outline = new Pen(Color.FromArgb(241, 248, 255), 2.4f) { LineJoin = LineJoin.Round };
        using var badge = new SolidBrush(state);
        graphics.FillRectangle(panel, 3, 5, 24, 17);
        graphics.DrawRectangle(outline, 3, 5, 24, 17);
        graphics.DrawLine(outline, 15, 22, 15, 27);
        graphics.DrawLine(outline, 9, 27, 20, 27);
        graphics.FillEllipse(panel, 18, 17, 14, 14);
        graphics.FillEllipse(badge, 20, 19, 10, 10);
        nint handle = image.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    private sealed class StatusHotKey : NativeWindow, IDisposable
    {
        private const int Id = 1;
        private const int HotKeyMessage = 0x0312;
        private const uint ControlShiftNoRepeat = 0x0002 | 0x0004 | 0x4000;
        private static readonly nint MessageOnlyWindow = new(-3);
        private readonly Action showStatus;

        internal StatusHotKey(Action showStatus)
        {
            this.showStatus = showStatus;
            try
            {
                CreateHandle(new CreateParams { Parent = MessageOnlyWindow });
                if (!RegisterHotKey(Handle, Id, ControlShiftNoRepeat, (uint)Keys.F11))
                    DestroyHandle();
            }
            catch (Win32Exception)
            {
                if (Handle != 0)
                    DestroyHandle();
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == HotKeyMessage && message.WParam == Id)
            {
                showStatus();
                return;
            }
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (Handle == 0)
                return;
            UnregisterHotKey(Handle, Id);
            DestroyHandle();
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(nint window, int id);
    }
}
