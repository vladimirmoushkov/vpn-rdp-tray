using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;

namespace VpnRdpTray;

internal interface IRdpSession : IDisposable
{
    Task<Failure> Completion { get; }
    void Reveal();
}

internal static class RdpDisplayTopology
{
    internal static Rectangle? ConnectedMultiMonitorBounds(bool enabled) => enabled
        ? MultiMonitorBounds(Screen.AllScreens.Select(screen => screen.Bounds))
        : null;

    internal static Rectangle? MultiMonitorBounds(IEnumerable<Rectangle> displayBounds)
    {
        var displays = displayBounds.Distinct().ToArray();
        if (displays.Length is < 2 or > 16 || displays.Any(display => display.Width <= 0 || display.Height <= 0))
            return null;

        var bounds = displays.Aggregate(Rectangle.Union);
        if (bounds.Width is < 200 or > 32766 || bounds.Height is < 200 or > 32766)
            return null;

        for (int i = 0; i < displays.Length; i++)
            for (int j = 0; j < i; j++)
                if (displays[i].IntersectsWith(displays[j]))
                    return null;

        var contiguous = new HashSet<Rectangle> { displays[0] };
        while (contiguous.Count < displays.Length)
        {
            var neighbors = displays.Where(display => !contiguous.Contains(display) &&
                contiguous.Any(other => display.Left <= other.Right && display.Right >= other.Left &&
                    display.Top <= other.Bottom && display.Bottom >= other.Top)).ToArray();
            if (neighbors.Length == 0)
                return null;
            contiguous.UnionWith(neighbors);
        }

        return bounds;
    }

    internal static Rectangle? ExitDockArea(Rectangle clientScreenBounds, Rectangle monitorBounds, Size buttonSize)
    {
        var visible = Rectangle.Intersect(clientScreenBounds, monitorBounds);
        return visible.Top == monitorBounds.Top && visible.Width >= buttonSize.Width && visible.Height >= buttonSize.Height
            ? visible
            : null;
    }

    internal static Point ExitDockLocation(Rectangle clientScreenBounds, Rectangle dockArea, Size buttonSize,
        int rightOffsetDips, int dpi)
    {
        int rightOffset = (int)Math.Clamp(Math.Round(rightOffsetDips * (dpi / 96d)), 0, dockArea.Width - buttonSize.Width);
        return new Point(dockArea.Right - buttonSize.Width - rightOffset - clientScreenBounds.Left,
            dockArea.Top - clientScreenBounds.Top);
    }
}

internal sealed class HostedRdpSession : IRdpSession, IMessageFilter
{
    private const int KeyDownMessage = 0x0100;
    private const int KeyUpMessage = 0x0101;
    private const long PreviousKeyState = 1L << 30;
    private const int DefaultExitRightOffsetDips = 24;
    private const string ExitDockHint = "Ctrl+Shift+F exits full screen. Ctrl+Shift+N minimizes. Drag to move the button.";
    private readonly DesktopWindow window;
    private readonly RdpActiveX control;
    private readonly RdpWindowGlow windowGlow;
    private readonly Panel viewport = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly FullScreenExitButton exitFullScreen = new()
    {
        AutoSize = true,
        Text = "Exit full screen",
        AccessibleDescription = ExitDockHint,
        Visible = false
    };
    private readonly ToolTip exitDockHelp = new();
    private readonly System.Windows.Forms.Timer displayUpdate = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer? activity;
    private readonly TaskCompletionSource<Failure> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool savedPassword;
    private (Size Size, int Dpi) requestedDisplay;
    private Size remoteDisplay;
    private Rectangle windowedBounds, fullScreenMonitor;
    private Rectangle? multiMonitorBounds;
    private FullScreenExitPlacement? exitDock = Settings.FullScreenExitDock, dragDock;
    private bool multiMonitorConfirmed, authenticationDialogOpen;
    private bool signedIn, warningSeen, closing, fullScreen, fullScreenShortcutDown, minimizeShortcutDown;

    internal HostedRdpSession(RdpEndpoint endpoint, RdpSettings settings, bool foreground, Action<string, bool> report)
    {
        bool entra = settings.Mode == RdpMode.Entra;
        string? password = !entra && RdpActiveX.PolicyAllowsPassword() ? settings.Password : null;
        savedPassword = !string.IsNullOrEmpty(password);
        var initialScreen = Screen.FromPoint(Cursor.Position);
        window = new DesktopWindow
        {
            Text = $"Remote Desktop · {settings.Host} · Ctrl+Shift+F full screen · Ctrl+Shift+N minimize",
            AutoScaleMode = AutoScaleMode.Dpi,
            KeyPreview = true,
            StartPosition = FormStartPosition.Manual,
            Location = initialScreen.WorkingArea.Location
        };
        control = new RdpActiveX();
        windowGlow = new RdpWindowGlow(window, control);
        if (settings.SimulateActivity)
        {
            activity = new System.Windows.Forms.Timer();
            activity.Tick += SendActivity;
        }

        try
        {
            exitFullScreen.Click += (_, _) => ToggleFullScreen();
            exitFullScreen.DockDragged += DragExitButton;
            exitFullScreen.DockDragEnded += CompleteExitDock;
            exitDockHelp.SetToolTip(exitFullScreen, ExitDockHint);
            ((ISupportInitialize)control).BeginInit();
            viewport.Controls.Add(control);
            window.Controls.Add(viewport);
            window.Controls.Add(exitFullScreen);
            exitFullScreen.BringToFront();
            ((ISupportInitialize)control).EndInit();
            _ = window.Handle;
            SizeWindow(initialScreen);
            window.PerformLayout();
            // Start with a valid single-monitor size. The control negotiates the actual monitor list at Connect.
            requestedDisplay = (RdpActiveX.DisplaySize(viewport.ClientRectangle.Size), window.DeviceDpi);
            remoteDisplay = requestedDisplay.Size;
            FitViewport();
            _ = control.Handle;
            control.Configure(endpoint.Server, endpoint.Port, settings.Username, password, entra,
                requestedDisplay.Size, requestedDisplay.Dpi);
            control.ConfigureRedirections(settings);
            if (RdpDisplayTopology.ConnectedMultiMonitorBounds(settings.MultiMonitor) is { } displayBounds)
            {
                try
                {
                    if (control.SetMultiMonitor(true))
                        multiMonitorBounds = displayBounds;
                }
                catch (Exception ex) when (IsClientException(ex)) { }
            }

            viewport.SizeChanged += (_, _) => FitViewport();
            viewport.DpiChangedAfterParent += (_, _) =>
            {
                FitViewport();
                QueueDisplayUpdate();
            };
            window.Resize += (_, _) =>
            {
                if (!fullScreen && window.WindowState == FormWindowState.Maximized)
                    PostViewChange(EnterFullScreen);
                else if (!fullScreen && window.WindowState == FormWindowState.Normal)
                    PostViewChange(UpdateWindowMinimum);
                QueueDisplayUpdate();
            };
            window.Move += (_, _) =>
            {
                if (!fullScreen)
                    PostViewChange(UpdateWindowMinimum);
            };
            window.DpiChanged += (_, _) =>
            {
                if (!fullScreen)
                    PostViewChange(UpdateWindowMinimum);
                QueueDisplayUpdate();
            };
            window.DisplayChanged += () => PostViewChange(UpdateWindowMinimum);
            window.KeyDown += (_, args) =>
            {
                if (args.Modifiers != (Keys.Control | Keys.Alt) || args.KeyCode is not (Keys.Pause or Keys.Cancel))
                    return;

                args.SuppressKeyPress = true;
                ToggleFullScreen();
            };
            displayUpdate.Tick += (_, _) => ApplyDisplayUpdate();
            window.FullScreenHotkeyPressed += () => PostViewChange(ToggleFullScreen);
            window.MinimizeHotkeyPressed += () => PostViewChange(MinimizeDesktop);
            window.MaximizeRequested += () => PostViewChange(EnterFullScreen);
            control.FullScreenRequested += () => PostViewChange(ToggleFullScreen);
            control.RemoteDesktopSizeChanged += size => PostViewChange(() =>
            {
                if (size.Width > 0 && size.Height > 0)
                {
                    remoteDisplay = size;
                    FitViewport();
                    if (signedIn && multiMonitorBounds.HasValue && !multiMonitorConfirmed)
                        ConfirmMultiMonitorLayout();
                }
            });

            // Cache the event result before disposing the ActiveX site. Transport connection alone is not a sign-in.
            control.TransportConnected += () =>
            {
                if (!closing && !signedIn && !warningSeen)
                    report("RDP signing in", !savedPassword);
            };
            control.LoginComplete += () =>
            {
                if (closing || completion.Task.IsCompleted)
                    return;
                signedIn = true;
                report("RDP connected", false);
                ScheduleActivity();
                if (multiMonitorBounds.HasValue)
                    PostViewChange(ConfirmMultiMonitorLayout);
                else
                    QueueDisplayUpdate();
            };
            control.AuthenticationWarning += () =>
            {
                authenticationDialogOpen = true;
                if (closing || signedIn)
                    return;
                warningSeen = true;
                report("RDP waiting for Windows sign-in or certificate review", true);
            };
            control.AuthenticationDismissed += () =>
            {
                authenticationDialogOpen = false;
                if (!closing && !signedIn && !completion.Task.IsCompleted)
                    report("RDP signing in", !savedPassword);
            };
            control.Disconnected += code =>
            {
                if (!closing)
                    CompleteSession(DescribeDisconnect(code, signedIn, warningSeen, savedPassword));
            };
            control.LogonError += code =>
            {
                if (closing || signedIn || completion.Task.IsCompleted)
                    return;
                if (DescribeLogon(code) is { } failure)
                    CompleteSession(failure);
                else if (code is -5 or -4 or 2 or 3)
                    report("RDP waiting for Windows sign-in", true);
            };
            control.FatalError += code =>
            {
                if (!closing)
                    CompleteSession(DescribeFatal(code));
            };
            window.FormClosing += (_, args) =>
            {
                if (closing || args.CloseReason == CloseReason.WindowsShutDown)
                    return;
                args.Cancel = true;
                CompleteSession(WindowClosed(signedIn));
            };

            report("RDP opening connection", false);
            window.WindowState = foreground || !savedPassword && !entra ? FormWindowState.Normal : FormWindowState.Minimized;
            Application.AddMessageFilter(this);
            window.Show();
            RefreshWindowGlow();
            control.Connect();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Task<Failure> Completion => completion.Task;

    private void CompleteSession(Failure failure)
    {
        if (completion.TrySetResult(failure))
        {
            activity?.Stop();
            windowGlow.Refresh(false);
        }
    }

    private void ScheduleActivity()
    {
        if (activity is null || closing || completion.Task.IsCompleted)
            return;
        activity.Interval = Random.Shared.Next(5, 66) * 1000;
        activity.Start();
    }

    private void SendActivity(object? sender, EventArgs args)
    {
        activity?.Stop();
        if (closing || !signedIn || completion.Task.IsCompleted)
            return;
        if (!authenticationDialogOpen && Control.ModifierKeys == Keys.None)
        {
            try { control.SendActivity(); }
            catch (Exception ex) when (IsClientException(ex)) { }
        }
        ScheduleActivity();
    }

    bool IMessageFilter.PreFilterMessage(ref Message message)
    {
        if (closing || (message.Msg is not (KeyDownMessage or KeyUpMessage)) || !window.IsHandleCreated)
            return false;

        var key = (Keys)message.WParam;
        if (key is not (Keys.F or Keys.N))
            return false;
        nint handle = window.Handle;
        if (message.HWnd != handle && !IsChild(handle, message.HWnd))
            return false;

        return key switch
        {
            Keys.F when !window.IsFullScreenHotkeyRegistered =>
                FilterLocalShortcut(ref message, ref fullScreenShortcutDown, ToggleFullScreen),
            Keys.N when !window.IsMinimizeHotkeyRegistered =>
                FilterLocalShortcut(ref message, ref minimizeShortcutDown, MinimizeDesktop),
            _ => false
        };
    }

    private bool FilterLocalShortcut(ref Message message, ref bool shortcutDown, Action action)
    {
        if (message.Msg == KeyUpMessage)
        {
            bool consumed = shortcutDown;
            shortcutDown = false;
            return consumed;
        }

        // The RDP control has native child windows that can consume the key before Form.KeyDown.
        if ((message.LParam.ToInt64() & PreviousKeyState) == 0)
        {
            shortcutDown = Control.ModifierKeys == (Keys.Control | Keys.Shift);
            if (shortcutDown)
                PostViewChange(action);
        }
        return shortcutDown;
    }

    private void SizeWindow(Screen screen)
    {
        var workArea = screen.WorkingArea;
        int DpiPixels(int pixels) => (int)Math.Round(pixels * (window.DeviceDpi / 96d));
        var frame = window.Size - window.ClientSize;
        int maxWidth = Math.Max(1, (int)(workArea.Width * 0.9) - frame.Width);
        int maxHeight = Math.Max(1, (int)(workArea.Height * 0.9) - frame.Height);
        window.MinimumSize = WindowedMinimumSize(screen.Bounds.Size);
        window.ClientSize = new Size(Math.Min(DpiPixels(1152), maxWidth), Math.Min(DpiPixels(720), maxHeight));
        window.Location = new Point(workArea.Left + (workArea.Width - window.Width) / 2,
            workArea.Top + (workArea.Height - window.Height) / 2);
    }

    internal static Size WindowedMinimumSize(Size monitor) =>
        new(monitor.Width - monitor.Width / 3, monitor.Height - monitor.Height / 3);

    private void UpdateWindowMinimum()
    {
        if (!fullScreen && window.WindowState == FormWindowState.Normal)
        {
            var minimum = WindowedMinimumSize(Screen.FromControl(window).Bounds.Size);
            if (window.MinimumSize != minimum)
                window.MinimumSize = minimum;
        }
    }

    private void PostViewChange(Action change)
    {
        if (!closing && window.IsHandleCreated)
            window.BeginInvoke((Action)(() =>
            {
                if (!closing && !completion.Task.IsCompleted)
                    change();
            }));
    }

    private void EnterFullScreen()
    {
        if (!fullScreen)
            ToggleFullScreen();
    }

    private void ToggleFullScreen()
    {
        if (closing || completion.Task.IsCompleted)
            return;

        windowGlow.Refresh(false);
        window.SuspendLayout();
        try
        {
            if (!fullScreen)
            {
                windowedBounds = window.WindowState == FormWindowState.Normal ? window.Bounds : window.RestoreBounds;
                fullScreenMonitor = Screen.FromControl(window).Bounds;
                fullScreen = true;
                window.MinimumSize = Size.Empty;
                window.WindowState = FormWindowState.Normal;
                window.FormBorderStyle = FormBorderStyle.None;
                ApplyFullScreenBounds();
            }
            else
            {
                fullScreen = false;
                window.FormBorderStyle = FormBorderStyle.Sizable;
                window.WindowState = FormWindowState.Normal;
                window.Bounds = windowedBounds;
            }

            exitFullScreen.Visible = fullScreen;
        }
        finally { window.ResumeLayout(performLayout: true); }
        SetTaskbarFullScreen(fullScreen);
        UpdateWindowMinimum();
        FitViewport();
        if (!fullScreen)
            RefreshWindowGlow();
        QueueDisplayUpdate();
        control.Focus();
    }

    private void MinimizeDesktop() => window.WindowState = FormWindowState.Minimized;

    private void ConfirmMultiMonitorLayout()
    {
        if (control.RemoteMonitorLayout() is not { } layout)
            return;

        multiMonitorConfirmed = layout.MonitorCount > 1 && layout.MatchesLocal;
        if (layout.MonitorCount == 1)
            multiMonitorBounds = null;
        else if (layout.Size.Width > 0 && layout.Size.Height > 0)
            remoteDisplay = layout.Size;

        if (fullScreen)
            ApplyFullScreenBounds();
        FitViewport();
        QueueDisplayUpdate();
    }

    private void ApplyFullScreenBounds()
    {
        window.Bounds = multiMonitorConfirmed && multiMonitorBounds is { } confirmed
            ? confirmed
            : fullScreenMonitor;
    }

    private void SetTaskbarFullScreen(bool enabled)
    {
        if (!window.IsHandleCreated)
            return;
        object? taskbar = null;
        try
        {
            taskbar = new TaskbarList();
            var shell = (ITaskbarList2)taskbar;
            shell.HrInit();
            shell.MarkFullscreenWindow(window.Handle, enabled);
        }
        catch (Exception ex) when (IsClientException(ex)) { }
        finally
        {
            if (taskbar is not null)
                Marshal.ReleaseComObject(taskbar);
        }
    }

    private void FitViewport()
    {
        if (closing)
            return;
        control.Bounds = FitDisplay(viewport.ClientRectangle.Size, remoteDisplay);
        if (fullScreen)
        {
            PositionExitButton();
            RefreshWindowGlow();
        }
    }

    private void PositionExitButton()
    {
        var client = window.RectangleToScreen(window.ClientRectangle);
        var buttonSize = exitFullScreen.GetPreferredSize(Size.Empty);
        var displays = ExitDisplays(client, buttonSize);
        var placement = dragDock ?? exitDock;
        var chosen = displays.FirstOrDefault(display =>
            string.Equals(display.Screen.DeviceName, placement?.Monitor, StringComparison.OrdinalIgnoreCase));
        int offset = DefaultExitRightOffsetDips;
        if (chosen.Screen is not null && placement is { } dock)
            offset = dock.RightOffsetDips;
        else
            chosen = displays.FirstOrDefault(display => display.Screen.Bounds == fullScreenMonitor, displays.FirstOrDefault());

        if (chosen.Screen is not null)
            exitFullScreen.Location = RdpDisplayTopology.ExitDockLocation(client, chosen.Area, buttonSize, offset, window.DeviceDpi);
    }

    private static (Screen Screen, Rectangle Area)[] ExitDisplays(Rectangle client, Size buttonSize) =>
        (from screen in Screen.AllScreens
         let area = RdpDisplayTopology.ExitDockArea(client, screen.Bounds, buttonSize)
         where area.HasValue
         select (screen, area.GetValueOrDefault())).ToArray();

    private void DragExitButton(Point pointer, int grabOffset)
    {
        if (!fullScreen || closing)
            return;
        var client = window.RectangleToScreen(window.ClientRectangle);
        var buttonSize = exitFullScreen.GetPreferredSize(Size.Empty);
        var chosen = ExitDisplays(client, buttonSize).FirstOrDefault(display => display.Area.Contains(pointer));
        if (chosen.Screen is null)
            return;

        int offset = Math.Clamp(chosen.Area.Right - pointer.X + grabOffset - buttonSize.Width,
            0, chosen.Area.Width - buttonSize.Width);
        dragDock = new(chosen.Screen.DeviceName, (int)Math.Round(offset * (96d / window.DeviceDpi)));
        PositionExitButton();
    }

    private void CompleteExitDock(bool commit)
    {
        if (commit && fullScreen && dragDock is { } placement)
        {
            exitDock = placement;
            Settings.FullScreenExitDock = placement;
        }
        dragDock = null;
        if (fullScreen && !closing)
            PositionExitButton();
    }

    internal static Rectangle FitDisplay(Size viewportSize, Size desktopSize)
    {
        if (viewportSize.Width <= 0 || viewportSize.Height <= 0 || desktopSize.Width <= 0 || desktopSize.Height <= 0)
            return Rectangle.Empty;
        double scale = Math.Min(viewportSize.Width / (double)desktopSize.Width,
            viewportSize.Height / (double)desktopSize.Height);
        int width = (int)Math.Round(desktopSize.Width * scale);
        int height = (int)Math.Round(desktopSize.Height * scale);
        return new Rectangle((viewportSize.Width - width) / 2, (viewportSize.Height - height) / 2, width, height);
    }

    private void QueueDisplayUpdate()
    {
        if (closing || !signedIn || multiMonitorBounds.HasValue || window.WindowState == FormWindowState.Minimized)
            return;
        displayUpdate.Stop();
        displayUpdate.Start();
    }

    private void ApplyDisplayUpdate()
    {
        displayUpdate.Stop();
        if (closing || completion.Task.IsCompleted || multiMonitorBounds.HasValue || window.WindowState == FormWindowState.Minimized)
            return;
        var display = (Size: RdpActiveX.DisplaySize(viewport.ClientRectangle.Size), Dpi: window.DeviceDpi);
        if (display == requestedDisplay)
            return;
        requestedDisplay = display;
        control.UpdateDisplay(display.Size, display.Dpi);
    }

    public void Reveal()
    {
        if (window.WindowState == FormWindowState.Minimized)
        {
            window.WindowState = FormWindowState.Normal;
            if (fullScreen)
            {
                ApplyFullScreenBounds();
                SetTaskbarFullScreen(true);
                FitViewport();
            }
        }
        window.Activate();
        RefreshWindowGlow();
    }

    private void RefreshWindowGlow() =>
        windowGlow.Refresh(!closing && !completion.Task.IsCompleted, fullScreen);

    internal static bool IsClientException(Exception ex) =>
        ex is COMException or RuntimeBinderException or InvalidOperationException or InvalidCastException or Win32Exception;

    internal static Failure? DescribeLogon(int code) => code switch
    {
        0 or unchecked((int)0xc000006d) =>
            new("RDP rejected the sign-in. Review the username and any saved RDP password in Setup.", FailureKind.CredentialRejected),
        1 or unchecked((int)0xc0000224) =>
            new("The RDP password must be changed. Update it through Windows or with your administrator, then review Setup.", FailureKind.ActionRequired),
        -1 or -6 or -7 or unchecked((int)0xc000006e) =>
            new("Account policy denied RDP sign-in. Ask your administrator to check access to the remote computer.", FailureKind.ActionRequired),
        _ => null
    };

    internal static Failure DescribeFatal(int code) => code switch
    {
        2 => new("Remote Desktop ran out of memory. Close unused apps, then choose Open Remote Desktop.", FailureKind.ManualRetry),
        3 => new("Windows could not open the Remote Desktop window. Choose Open Remote Desktop to retry (RDP code 3).", FailureKind.ManualRetry),
        100 => new("Windows could not initialize RDP networking. Check the network and choose Open Remote Desktop (RDP code 100).", FailureKind.ManualRetry),
        _ => new($"The Windows Remote Desktop client failed (RDP code {code}). Choose Open Remote Desktop or share the code with your administrator.", FailureKind.ManualRetry)
    };

    internal static Failure DescribeDisconnect(int code, bool signedIn, bool warningSeen, bool savedPassword) => code switch
    {
        1798 => new("Windows could not validate the RDP certificate. Verify the hostname in Setup and ask your administrator to check the certificate.",
            FailureKind.ActionRequired),
        6919 => new("The RDP server certificate has expired. Ask your administrator to renew it.", FailureKind.ActionRequired),
        2055 or 2567 => DescribeLogon(0)!,
        2823 or 3591 => new("The RDP account is disabled or expired. Ask your administrator to restore access.", FailureKind.CredentialRejected),
        3079 => new("Account policy restricted RDP sign-in. Ask your administrator to review the account's access.", FailureKind.CredentialRejected),
        3335 => new("The RDP account is locked. Ask your administrator to unlock it before retrying.", FailureKind.CredentialRejected),
        7175 => new("The smart card PIN was rejected. Confirm the PIN before trying again.", FailureKind.CredentialRejected),
        8711 => new("The smart card is blocked. Ask your administrator to unlock or replace it.", FailureKind.CredentialRejected),
        3847 or 4615 => DescribeLogon(1)!,
        5639 or 5895 or 6151 => new("Windows could not delegate or verify the RDP credentials. Ask your administrator to check domain and authentication policy.",
            FailureKind.ActionRequired),
        8455 => new("The RDP server requires fresh credentials. Clear the saved password in Setup and save, then sign in again.", FailureKind.ActionRequired),
        260 or 520 or 1288 or 1540 => new("Windows could not resolve the RDP hostname. Check the hostname in Setup and confirm VPN DNS can resolve it."),
        264 or 1796 => warningSeen && !signedIn
            ? new("RDP sign-in timed out. Choose Open Remote Desktop and finish the Windows prompt.", FailureKind.ManualRetry)
            : new("The RDP connection timed out. Check that the remote computer is online and accepts Remote Desktop."),
        516 => new("Windows could not connect to the RDP port. Verify the port in Setup and ask your administrator to check access from the VPN."),
        772 or 1028 or 2308 when signedIn => new("The network interrupted the RDP session. Check the VPN and remote computer if this repeats."),
        2056 or 2312 => new("RDP licensing could not be completed. Ask your administrator to check the remote computer's Remote Desktop licensing.",
            FailureKind.ActionRequired),
        776 or 2052 => new("Windows rejected the RDP address. Review the hostname and optional IP address in Setup.", FailureKind.ActionRequired),
        1030 or 1286 or 1542 or 2310 or 2566 or 2822 or 3078 =>
            new($"RDP could not establish a secure session (RDP code {code}). Ask your administrator to check the remote computer's security configuration.",
                FailureKind.ActionRequired),
        1 or 2 => WindowClosed(signedIn),
        3 when signedIn => new("The remote computer ended the RDP session. Ask your administrator to check session limits if this repeats."),
        _ when !signedIn && savedPassword =>
            new($"RDP sign-in failed (RDP code {code}). Review the account in Setup and share the code with your administrator if it repeats.",
                FailureKind.ActionRequired),
        _ => new($"The RDP session ended (RDP code {code}). Choose Open Remote Desktop to retry.", FailureKind.ManualRetry)
    };

    private static Failure WindowClosed(bool signedIn) => signedIn
        ? new("RDP window closed")
        : new("The RDP window closed before sign-in completed. Choose Open Remote Desktop to sign in again.", FailureKind.ManualRetry);

    public void Dispose()
    {
        if (closing)
            return;
        closing = true;
        windowGlow.Dispose();
        Application.RemoveMessageFilter(this);
        if (fullScreen)
            SetTaskbarFullScreen(false);
        exitDockHelp.Dispose();
        displayUpdate.Dispose();
        activity?.Dispose();
        try { control.Disconnect(); }
        catch (Exception ex) when (IsClientException(ex)) { }
        window.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint candidate);

    private sealed class FullScreenExitButton : Button
    {
        private const int LeftButtonUpMessage = 0x0202;
        private Rectangle dragThreshold;
        private int grabOffset;
        private bool pressed, dragging, releasing;

        internal event Action<Point, int>? DockDragged;
        internal event Action<bool>? DockDragEnded;

        internal FullScreenExitButton()
        {
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            ApplyColors();
        }

        protected override void OnSystemColorsChanged(EventArgs e)
        {
            base.OnSystemColorsChanged(e);
            ApplyColors();
        }

        private void ApplyColors()
        {
            bool highContrast = SystemInformation.HighContrast;
            BackColor = highContrast ? SystemColors.Highlight : Color.FromArgb(0, 89, 184);
            ForeColor = highContrast ? SystemColors.HighlightText : Color.White;
            FlatAppearance.BorderColor = ForeColor;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
                return;

            var start = PointToScreen(e.Location);
            var movement = SystemInformation.DragSize;
            dragThreshold = new Rectangle(start.X - movement.Width / 2, start.Y - movement.Height / 2,
                movement.Width, movement.Height);
            grabOffset = e.X;
            pressed = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!pressed || !Capture || (e.Button & MouseButtons.Left) == 0)
                return;

            var pointer = PointToScreen(e.Location);
            if (!dragging && !dragThreshold.Contains(pointer))
            {
                dragging = true;
                Cursor = Cursors.SizeWE;
            }
            if (dragging)
                DockDragged?.Invoke(pointer, grabOffset);
        }

        protected override void OnClick(EventArgs e)
        {
            if (!dragging)
                base.OnClick(e);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg != LeftButtonUpMessage)
            {
                base.WndProc(ref message);
                return;
            }

            // The button releases mouse capture and can raise Click while handling this message.
            releasing = true;
            try { base.WndProc(ref message); }
            finally
            {
                releasing = false;
                FinishDrag(commit: true);
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && !releasing)
                FinishDrag(commit: false);
        }

        private void FinishDrag(bool commit)
        {
            pressed = false;
            if (!dragging)
                return;

            dragging = false;
            Cursor = Cursors.Default;
            DockDragEnded?.Invoke(commit);
        }
    }

    private sealed class DesktopWindow : Form
    {
        private const int DisplayChangeMessage = 0x007E;
        private const int HotkeyMessage = 0x0312;
        private const int SystemCommandMessage = 0x0112;
        private const int MaximizeCommand = 0xF030;
        private const int FullScreenHotkeyId = 1;
        private const int MinimizeHotkeyId = 2;
        private const uint ControlShift = 0x0002 | 0x0004;
        private const uint NoRepeat = 0x4000;
        private nint fullScreenHotkeyWindow, minimizeHotkeyWindow;

        internal event Action? FullScreenHotkeyPressed;
        internal event Action? MinimizeHotkeyPressed;
        internal event Action? MaximizeRequested;
        internal event Action? DisplayChanged;
        internal bool IsFullScreenHotkeyRegistered => fullScreenHotkeyWindow != 0;
        internal bool IsMinimizeHotkeyRegistered => minimizeHotkeyWindow != 0;

        protected override void OnActivated(EventArgs e)
        {
            if (WindowState != FormWindowState.Minimized)
                RegisterHotkeys();
            base.OnActivated(e);
        }

        protected override void OnResize(EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized)
                ReleaseHotkeys();
            else if (IsHandleCreated && GetForegroundWindow() == Handle)
                RegisterHotkeys();
            base.OnResize(e);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            ReleaseHotkeys();
            base.OnDeactivate(e);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            ReleaseHotkeys();
            base.OnHandleDestroyed(e);
        }

        private void RegisterHotkey(ref nint registeredWindow, int id, Keys key)
        {
            if (registeredWindow == 0 && RegisterHotKey(Handle, id, ControlShift | NoRepeat, (uint)key))
                registeredWindow = Handle;
        }

        private void RegisterHotkeys()
        {
            RegisterHotkey(ref fullScreenHotkeyWindow, FullScreenHotkeyId, Keys.F);
            RegisterHotkey(ref minimizeHotkeyWindow, MinimizeHotkeyId, Keys.N);
        }

        private void ReleaseHotkeys()
        {
            ReleaseHotkey(ref fullScreenHotkeyWindow, FullScreenHotkeyId);
            ReleaseHotkey(ref minimizeHotkeyWindow, MinimizeHotkeyId);
        }

        private static void ReleaseHotkey(ref nint registeredWindow, int id)
        {
            if (registeredWindow == 0)
                return;
            UnregisterHotKey(registeredWindow, id);
            registeredWindow = 0;
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == SystemCommandMessage && (message.WParam.ToInt64() & 0xFFF0) == MaximizeCommand)
            {
                MaximizeRequested?.Invoke();
                return;
            }

            if (message.Msg == HotkeyMessage && GetForegroundWindow() == Handle)
            {
                if (message.WParam == FullScreenHotkeyId && IsFullScreenHotkeyRegistered)
                {
                    FullScreenHotkeyPressed?.Invoke();
                    return;
                }
                if (message.WParam == MinimizeHotkeyId && IsMinimizeHotkeyRegistered)
                {
                    MinimizeHotkeyPressed?.Invoke();
                    return;
                }
            }
            base.WndProc(ref message);
            if (message.Msg == DisplayChangeMessage)
                DisplayChanged?.Invoke();
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(nint window, int id);

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();
    }

    [ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    private class TaskbarList { }

    [ComImport, Guid("602D4995-B13A-429B-A66E-1935E44F4317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList2
    {
        void HrInit();
        void AddTab(nint handle);
        void DeleteTab(nint handle);
        void ActivateTab(nint handle);
        void SetActiveAlt(nint handle);
        void MarkFullscreenWindow(nint handle, [MarshalAs(UnmanagedType.Bool)] bool fullScreen);
    }
}

internal sealed class NativeRdpSession : IRdpSession
{
    private readonly OwnedProcess client;
    private readonly string path;

    internal NativeRdpSession(RdpEndpoint endpoint, RdpSettings settings, bool foreground, string directory)
    {
        path = Path.Combine(directory, "entra.rdp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, RdpFile(endpoint, settings), Encoding.Unicode);
            client = StartClient(path, foreground);
            Completion = WatchAsync(client.Process);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            DeleteFile(path);
            throw new SetupException("Windows denied access to the temporary RDP connection file. Ask your administrator to check your Windows profile permissions.");
        }
        catch (IOException)
        {
            DeleteFile(path);
            throw new SetupException("Windows could not create the temporary RDP connection file. Check free disk space and access to your Windows profile.");
        }
        catch
        {
            DeleteFile(path);
            throw;
        }
    }

    private static OwnedProcess StartClient(string path, bool foreground)
    {
        try { return OwnedProcess.Start(Path.Combine(Environment.SystemDirectory, "mstsc.exe"), path, minimized: !foreground); }
        catch (Win32Exception ex)
        {
            string action = ex.NativeErrorCode switch
            {
                2 or 3 => "Ask your administrator to check whether the Windows Remote Desktop Connection client is installed.",
                5 or 740 or 1260 => "Ask your administrator to check whether Windows policy allows Remote Desktop Connection to start.",
                _ => "Check whether Remote Desktop Connection opens from Start, and share the code with your administrator if it does not."
            };
            throw new SetupException($"Windows could not start Remote Desktop Connection (code {ex.NativeErrorCode}). {action}");
        }
        catch (ArgumentException)
        {
            throw new SetupException("Remote Desktop Connection closed before the app could attach to it. Check whether it opens from Start.");
        }
    }

    internal static string RdpFile(RdpEndpoint endpoint, RdpSettings settings) =>
        RdpFile(endpoint, settings, RdpDisplayTopology.ConnectedMultiMonitorBounds(settings.MultiMonitor).HasValue);

    internal static string RdpFile(RdpEndpoint endpoint, RdpSettings settings, bool multiMonitorAvailable)
    {
        var host = SetupInput.Host(endpoint.Server);
        var user = SetupInput.Credential(settings.Username, "Entra username", 512);
        bool useMultiMonitor = settings.MultiMonitor && multiMonitorAvailable;
        return string.Join("\r\n", new[]
        {
            $"full address:s:{host}:{endpoint.Port.ToString(CultureInfo.InvariantCulture)}",
            $"username:s:{user}",
            useMultiMonitor ? "screen mode id:i:2" : "screen mode id:i:1",
            useMultiMonitor ? "use multimon:i:1" : "use multimon:i:0",
            "desktopwidth:i:1280",
            "desktopheight:i:800",
            "enablerdsaadauth:i:1",
            "enablecredsspsupport:i:1",
            "authentication level:i:1",
            "gatewayusagemethod:i:0",
            "autoreconnection enabled:i:0",
            settings.RedirectSecurityKeys ? "redirectsmartcards:i:1" : "redirectsmartcards:i:0",
            settings.RedirectSecurityKeys ? "redirectwebauthn:i:1" : "redirectwebauthn:i:0",
            settings.RedirectPrinters ? "redirectprinters:i:1" : "redirectprinters:i:0",
            settings.RedirectClipboard ? "redirectclipboard:i:1" : "redirectclipboard:i:0",
            settings.RedirectDrives ? "drivestoredirect:s:*" : "drivestoredirect:s:",
            settings.RedirectUsbDevices ? "usbdevicestoredirect:s:*" : "usbdevicestoredirect:s:",
            settings.RedirectUsbDevices ? "devicestoredirect:s:*" : "devicestoredirect:s:",
            ""
        });
    }

    private static async Task<Failure> WatchAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
            return new(process.ExitCode == 0
                ? "Remote Desktop closed. Choose Open Remote Desktop to sign in again."
                : $"Remote Desktop closed with code {process.ExitCode}. Choose Open Remote Desktop and check for a Windows error.", FailureKind.ManualRetry);
        }
        catch (InvalidOperationException)
        { return new("Remote Desktop closed. Choose Open Remote Desktop to sign in again.", FailureKind.ManualRetry); }
        catch (Win32Exception ex)
        {
            return new($"Windows lost contact with Remote Desktop Connection (code {ex.NativeErrorCode}). Choose Open Remote Desktop to retry.",
                FailureKind.ManualRetry);
        }
    }

    public Task<Failure> Completion { get; }

    public void Reveal() => client.Reveal();

    internal static void DeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    public void Dispose()
    {
        try { client.Dispose(); }
        finally { DeleteFile(path); }
    }
}
