using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace VpnRdpTray;

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu = new();
    private readonly ToolStripMenuItem summary = new() { Enabled = false };
    private readonly ToolStripMenuItem setupMenu = new("Setup…");
    private readonly ToolStripMenuItem connectionMenu = new();
    private readonly ToolStripMenuItem autoStartMenu = new();
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
    private string? loadFailure, preferenceFailure;
    private ConnectionStatus vpn = new("VPN idle"), rdp = new("RDP waiting for VPN");

    internal TrayApp(SynchronizationContext ui, EventWaitHandle stop, EventWaitHandle show, bool openSetup,
        bool openStatus = false)
    {
        this.ui = ui;
        menu.Items.AddRange([summary, new ToolStripSeparator(), setupMenu, connectionMenu, autoStartMenu,
            openRdp, new ToolStripSeparator(), exit]);
        icon = new NotifyIcon { ContextMenuStrip = menu, Icon = glyphs["off"], Visible = true };
        controller = new ConnectionController((vpn, rdp) =>
        {
            bool closed = rdp.Name == ConnectionController.RdpClosedStatus &&
                this.rdp.Name != ConnectionController.RdpClosedStatus;
            this.vpn = vpn;
            this.rdp = rdp;
            UpdateMenu();
            if (closed && connection && !busy && !exiting)
            {
                icon.ShowBalloonTip(4500, "Remote Desktop closed",
                    rdp.RetryAt is null ? "Choose Open Remote Desktop to try again." : "Reconnecting through the VPN.",
                    ToolTipIcon.Info);
            }
        });
        SetupPreferences();
        setupMenu.Click += (_, _) => ShowSetup();
        connectionMenu.Click += async (_, _) => await ChangeConnectionAsync();
        autoStartMenu.Click += (_, _) => SetAutoStart(!autoStart);
        openRdp.Click += (_, _) => OpenRemoteDesktop();
        exit.Click += async (_, _) => await ExitAsync();
        icon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
                ShowStatus();
        };
        icon.BalloonTipClicked += (_, _) => ShowStatus();
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
            StartConnection();
            if (openSetup)
                ShowSetup();
            else if (openStatus)
                ShowStatus();
            else if (!hasConfiguration)
                icon.ShowBalloonTip(4000, "Setup required", "Choose Setup to import a VPN profile and add your Remote Desktop target.", ToolTipIcon.Info);
        }, null);
    }

    private void SetupPreferences()
    {
        try
        {
            connection = Settings.Enabled("Connection");
            autoStart = Settings.Enabled("AutoStart");
            Settings.SetStartupEntry(autoStart);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { preferenceFailure = "Windows blocked a startup setting"; }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or SetupException)
        {
            hasConfiguration = false;
            loadFailure = "Saved setup could not be opened. Save a new configuration.";
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
            connection = !connection;
            if (connection)
                StartConnection();
            else
                await controller.StopAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { Alert("Windows could not save the Connection switch."); }
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
            preferenceFailure = null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { preferenceFailure = "Windows blocked the Auto-start change"; }
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
        setup = new SetupForm(ReadConfiguration(), SaveConfigurationAsync, autoStart, SetAutoStart);
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

        var window = new StatusForm(OpenRemoteDesktop, async () => await ChangeConnectionAsync(), ShowSetup,
            async () => await ExitAsync());
        statusForm = window;
        window.FormClosed += (_, _) => statusForm = null;
        window.UpdateStatus(summary.Text ?? "Setup required", connection, !busy && !exiting);
        window.Show();
        window.Activate();
    }

    private async Task<string?> SaveConfigurationAsync(Configuration candidate)
    {
        if (busy || exiting)
            return "Wait for the current connection change, then save again.";
        busy = true;
        UpdateMenu();
        try
        {
            await controller.StopAsync();
            await MachineSetup.SetHostsAsync(candidate.Rdp);
            Settings.Save(Settings.DirectoryPath, candidate);
            hasConfiguration = true;
            loadFailure = null;
            Settings.VpnAction = null;
            Settings.VpnRejections = 0;
            Settings.VpnNotBefore = null;
            Settings.RdpAction = null;
            return null;
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

    private void UpdateMenu()
    {
        connectionMenu.Text = connection ? "Connection On (turn off)" : "Connection Off (turn on)";
        connectionMenu.Checked = connection;
        autoStartMenu.Text = autoStart ? "Auto-start On (turn off)" : "Auto-start Off (turn on)";
        autoStartMenu.Checked = autoStart;
        setupMenu.Enabled = !exiting;
        exit.Enabled = !busy && !exiting;
        connectionMenu.Enabled = autoStartMenu.Enabled = openRdp.Enabled = !busy && !exiting;
        var name = !hasConfiguration ? "Setup required" : !connection ? "Connection off" : $"{vpn.Name} · {rdp.Name}";
        var reason = loadFailure ?? preferenceFailure ?? (connection ? rdp.Reason ?? vpn.Reason : null);
        var retry = connection && hasConfiguration ? vpn.RetryAt ?? rdp.RetryAt : null;
        bool action = loadFailure is not null || preferenceFailure is not null ||
            hasConfiguration && connection && (vpn.Action || rdp.Action);
        string prefix = action ? $"Action required · {name}" : name;
        string time = retry is { } when ? " · Retry at " + when.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) : "";
        string details = reason is null ? prefix : prefix + " · " + reason;
        summary.Text = details + time;
        statusForm?.UpdateStatus(summary.Text, connection, !busy && !exiting);
        icon.Text = (details.Length + time.Length > 127 ? details[..(126 - time.Length)] + "…" : details) + time;
        string glyph = !hasConfiguration || action ? "action" : !connection ? "off" :
            retry is not null ? "retry" : rdp.Name == "RDP connected" ? "rdp" :
            rdp.Name == "Opened in Remote Desktop" ? "handoff" :
            vpn.Name == "VPN connected" ? "vpn" : "waiting";
        icon.Icon = glyphs[glyph];
    }

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
