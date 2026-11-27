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
    private const int EdgeSnapDips = 32;

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

    internal static Rectangle? ExitDockArea(Rectangle clientScreenBounds, Rectangle monitorBounds, Size stripSize,
        RdpDockEdge edge = RdpDockEdge.Top)
    {
        var visible = Rectangle.Intersect(clientScreenBounds, monitorBounds);
        bool edgeVisible = edge switch
        {
            RdpDockEdge.Top => visible.Top == monitorBounds.Top,
            RdpDockEdge.Left => visible.Left == monitorBounds.Left,
            RdpDockEdge.Right => visible.Right == monitorBounds.Right,
            _ => false
        };
        return edgeVisible && visible.Width >= stripSize.Width && visible.Height >= stripSize.Height ? visible : null;
    }

    internal static RdpDockEdge? ExitDockEdge(Rectangle client, Rectangle monitor, Size stripSize, Point pointer,
        RdpDockEdge currentEdge, int dpi)
    {
        if (!monitor.Contains(pointer) || !client.Contains(pointer))
            return null;

        RdpDockEdge? nearest = null, fallback = null;
        int snapDistance = (int)Math.Round(EdgeSnapDips * (dpi / 96d));
        int nearestDistance = snapDistance;
        ReadOnlySpan<RdpDockEdge> edges = [RdpDockEdge.Top, RdpDockEdge.Left, RdpDockEdge.Right];
        foreach (var edge in edges)
        {
            if (!ExitDockArea(client, monitor, stripSize, edge).HasValue)
                continue;
            if (fallback is null || edge == currentEdge)
                fallback = edge;
            int distance = edge switch
            {
                RdpDockEdge.Top => pointer.Y - monitor.Top,
                RdpDockEdge.Left => pointer.X - monitor.Left,
                _ => monitor.Right - 1 - pointer.X
            };
            if (distance <= snapDistance && (nearest is null || distance < nearestDistance ||
                distance == nearestDistance && edge == currentEdge))
            {
                nearest = edge;
                nearestDistance = distance;
            }
        }
        return nearest ?? fallback;
    }

    internal static Point ExitDockLocation(Rectangle client, Rectangle dockArea, Size stripSize,
        int offsetDips, int dpi, RdpDockEdge edge = RdpDockEdge.Top)
    {
        double pixels = Math.Round(offsetDips * (dpi / 96d));
        int offset = (int)Math.Clamp(pixels, 0, ExitDockTravel(dockArea, stripSize, edge));
        var location = edge switch
        {
            RdpDockEdge.Left => new Point(dockArea.Left, dockArea.Top + offset),
            RdpDockEdge.Right => new Point(dockArea.Right - stripSize.Width, dockArea.Top + offset),
            _ => new Point(dockArea.Right - stripSize.Width - offset, dockArea.Top)
        };
        return new Point(location.X - client.Left, location.Y - client.Top);
    }

    internal static int ExitDockOffsetDips(Rectangle dockArea, Size stripSize, Point pointer, Point grabOffset,
        RdpDockEdge edge, int dpi)
    {
        int offset = edge == RdpDockEdge.Top
            ? dockArea.Right - stripSize.Width - (pointer.X - grabOffset.X)
            : pointer.Y - grabOffset.Y - dockArea.Top;
        int pixels = Math.Clamp(offset, 0, ExitDockTravel(dockArea, stripSize, edge));
        return (int)Math.Round(pixels * (96d / dpi));
    }

    private static int ExitDockTravel(Rectangle dockArea, Size stripSize, RdpDockEdge edge) => edge == RdpDockEdge.Top
        ? dockArea.Width - stripSize.Width
        : dockArea.Height - stripSize.Height;
}

internal sealed class HostedRdpSession : IRdpSession, IMessageFilter
{
    private const int DefaultExitRightOffsetDips = 24;
    private const string ExitDockHint = "Ctrl+Shift+F exits full screen. Ctrl+Shift+N minimizes. Drag either button to the top, left, or right edge.";
    private readonly DesktopWindow window;
    private readonly RdpActiveX control;
    private readonly RdpWindowGlow windowGlow;
    private readonly RdpWindowShortcuts shortcuts = new();
    private readonly Panel viewport = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly RdpRecoveryPanel recovery = new();
    private readonly FullScreenControls fullScreenControls = new();
    private readonly ToolTip exitDockHelp = new();
    private readonly System.Windows.Forms.Timer displayUpdate = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer? activity;
    private readonly RdpPointerActivity? activityPointer;
    private RdpActivitySchedule? activitySchedule;
    private readonly TaskCompletionSource<Failure> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool savedPassword;
    private (Size Size, int Dpi) requestedDisplay;
    private Size remoteDisplay;
    private Rectangle windowedBounds, fullScreenMonitor;
    private Rectangle? multiMonitorBounds;
    private FullScreenExitPlacement? exitDock = Settings.FullScreenExitDock, dragDock;
    private bool multiMonitorConfirmed, authenticationDialogOpen;
    private bool signedIn, warningSeen, closing, fullScreen, recoveryOpen;
    private bool internalGlow, externalGlow;

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
        (internalGlow, externalGlow) = (settings.InternalGlow, settings.ExternalGlow);
        if (settings.SimulateActivity)
        {
            activityPointer = new();
            activity = new System.Windows.Forms.Timer { Interval = 1000 };
            activity.Tick += SendActivity;
            window.Activated += (_, _) => activitySchedule?.ObserveFocus(true, Environment.TickCount64);
            window.Deactivate += (_, _) => activitySchedule?.ObserveFocus(false, Environment.TickCount64);
        }

        try
        {
            fullScreenControls.Exit.Click += (_, _) => ToggleFullScreen();
            fullScreenControls.Minimize.Click += (_, _) => MinimizeDesktop();
            foreach (var button in new[] { fullScreenControls.Exit, fullScreenControls.Minimize })
            {
                button.DockDragged += (pointer, grabPoint) => DragFullScreenControls(button, pointer, grabPoint);
                button.DockDragEnded += CompleteExitDock;
                exitDockHelp.SetToolTip(button, ExitDockHint);
            }
            ((ISupportInitialize)control).BeginInit();
            viewport.Controls.Add(control);
            viewport.Controls.Add(recovery);
            recovery.BringToFront();
            window.Controls.Add(viewport);
            window.Controls.Add(fullScreenControls);
            fullScreenControls.BringToFront();
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
                if (window.WindowState == FormWindowState.Minimized)
                    shortcuts.Disarm();
                else if (!fullScreen && window.WindowState == FormWindowState.Maximized)
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
            window.Activated += (_, _) => PostViewChange(RefreshShortcuts);
            window.Deactivate += (_, _) => shortcuts.Disarm();
            window.HandleCreated += (_, _) => PostViewChange(RefreshShortcuts);
            window.HandleDestroyed += (_, _) => shortcuts.Disarm();
            control.Enter += (_, _) => PostViewChange(RefreshShortcuts);
            control.GotFocus += (_, _) => PostViewChange(() =>
            {
                if (recoveryOpen && !authenticationDialogOpen && HasForeground() == true)
                    recovery.FocusPrimary();
                RefreshShortcuts();
            });
            window.KeyDown += (_, args) =>
            {
                if (args.Modifiers != (Keys.Control | Keys.Alt) || args.KeyCode is not (Keys.Pause or Keys.Cancel))
                    return;

                args.SuppressKeyPress = true;
                ToggleFullScreen();
            };
            displayUpdate.Tick += (_, _) => ApplyDisplayUpdate();
            window.ShortcutPressed += key =>
            {
                if (!CanHandleShortcuts())
                    return;
                if (key == Keys.F)
                    ToggleFullScreen();
                else if (key == Keys.N)
                    MinimizeDesktop();
            };
            window.MaximizeRequested += () => PostViewChange(EnterFullScreen);
            control.FullScreenRequested += () => PostViewChange(ToggleFullScreen);
            control.RemoteDesktopSizeChanged += size => PostViewChange(() =>
            {
                if (size.Width > 0 && size.Height > 0)
                {
                    if (remoteDisplay != size)
                        activityPointer?.Reset();
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
                PostViewChange(RefreshShortcuts);
                if (activity is not null && activitySchedule is null && control.SupportsPointerInput)
                {
                    activitySchedule = new(Environment.TickCount64, HasForeground());
                    activity.Start();
                }
                if (multiMonitorBounds.HasValue)
                    PostViewChange(ConfirmMultiMonitorLayout);
                else
                    QueueDisplayUpdate();
            };
            control.AuthenticationWarning += () =>
            {
                authenticationDialogOpen = true;
                shortcuts.Disarm();
                if (closing || signedIn)
                    return;
                warningSeen = true;
                report("RDP waiting for Windows sign-in or certificate review", true);
            };
            control.AuthenticationDismissed += () =>
            {
                authenticationDialogOpen = false;
                PostViewChange(RefreshShortcuts);
                if (!closing && !signedIn && !completion.Task.IsCompleted)
                    report("RDP signing in", !savedPassword);
            };
            control.Disconnected += code =>
            {
                if (!closing)
                    CompleteSession(DescribeDisconnect(code, signedIn, warningSeen, savedPassword,
                        control.ReadExtendedDisconnectReason()));
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
                if (closing || args.CloseReason != CloseReason.UserClosing)
                    return;
                args.Cancel = true;
                CompleteSession(WindowClosed(signedIn));
            };

            report("RDP opening connection", false);
            window.WindowState = foreground || !savedPassword && !entra ? FormWindowState.Normal : FormWindowState.Minimized;
            if (activityPointer is not null)
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

    internal bool IsPreviewMinimized => window.WindowState == FormWindowState.Minimized;

    internal bool CanShowRecovery => signedIn && !authenticationDialogOpen && !closing && !completion.Task.IsCompleted;

    internal bool RecoveryVisible => recoveryOpen;

    internal bool RecoveryWindowVisible => !closing && window.Visible && !IsPreviewMinimized;

    internal bool? TransportConnected =>
        !closing && !completion.Task.IsCompleted ? control.IsTransportConnected : false;

    internal void SetRecovery(string title, string detail, Action? reconnect, Action openStatus,
        Action? viewDesktop = null)
    {
        if (!CanShowRecovery)
            return;

        bool alreadyOpen = recoveryOpen;
        recoveryOpen = true;
        recovery.UpdateContent(title, detail, reconnect, openStatus, viewDesktop is null ? null : () =>
        {
            ClearRecovery();
            viewDesktop();
        });
        UpdateRecoveryArea();
        recovery.Show();
        recovery.PerformLayout();
        recovery.BringToFront();
        if (!alreadyOpen)
        {
            activityPointer?.Reset();
            if (RecoveryWindowVisible && !authenticationDialogOpen && HasForeground() == true)
                recovery.FocusPrimary();
        }
    }

    internal void ClearRecovery()
    {
        if (!recoveryOpen || closing)
            return;

        bool hadFocus = recovery.ContainsFocus;
        recoveryOpen = false;
        recovery.ResetActions();
        recovery.Hide();
        if (hadFocus && !completion.Task.IsCompleted && !authenticationDialogOpen && HasForeground() == true)
            control.Focus();
    }

    internal Bitmap? CapturePreview(Size maximumSize)
    {
        if (!signedIn || closing || recoveryOpen || authenticationDialogOpen || completion.Task.IsCompleted || !control.IsHandleCreated)
            return null;

        return RdpPreview.Capture(control.Handle, control.ClientSize, maximumSize);
    }

    private void CompleteSession(Failure failure)
    {
        if (completion.TrySetResult(failure))
        {
            shortcuts.Disarm();
            activity?.Stop();
            windowGlow.Refresh(false);
        }
    }

    private void SendActivity(object? sender, EventArgs args)
    {
        if (activitySchedule is not { } schedule || activityPointer is not { } pointer || closing || recoveryOpen || !signedIn ||
            authenticationDialogOpen || completion.Task.IsCompleted || !control.IsHandleCreated)
            return;

        long now = Environment.TickCount64;
        bool? focused = HasForeground();
        var input = focused == true ? RdpActivitySchedule.ReadLocalInput() : null;
        if (!schedule.CanSend(focused, now, input?.IdleMilliseconds) || RdpActivitySchedule.HasHeldModifierOrButton())
            return;

        bool? currentFocus = HasForeground();
        if (currentFocus != focused)
        {
            schedule.ObserveFocus(currentFocus, Environment.TickCount64);
            return;
        }
        if (focused == true && RdpActivitySchedule.ReadLocalInput()?.Timestamp != input?.Timestamp)
            return;

        schedule.RecordAttempt(Environment.TickCount64);
        try { pointer.Move(remoteDisplay, control.MoveRemotePointer); }
        catch (Exception ex) when (IsClientException(ex)) { }
    }

    private bool? HasForeground()
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0 || !window.IsHandleCreated)
            return null;
        return foreground == window.Handle || IsChild(window.Handle, foreground);
    }

    private bool CanHandleShortcuts() => !closing && signedIn && !authenticationDialogOpen &&
        !completion.Task.IsCompleted && window.WindowState != FormWindowState.Minimized && HasForeground() == true;

    private void RefreshShortcuts()
    {
        if (CanHandleShortcuts())
            shortcuts.Arm(window.Handle);
        else
            shortcuts.Disarm();
    }

    bool IMessageFilter.PreFilterMessage(ref Message message)
    {
        if (closing)
            return false;

        if (message.Msg == RdpPointerActivity.MouseMove && activitySchedule is not null && signedIn && !recoveryOpen &&
            !authenticationDialogOpen && control.IsHandleCreated && (!multiMonitorBounds.HasValue || multiMonitorConfirmed))
            activityPointer?.Observe(control.Handle, control.ClientSize, remoteDisplay, message);

        return false;
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

            fullScreenControls.Visible = fullScreen;
        }
        finally { window.ResumeLayout(performLayout: true); }
        SetTaskbarFullScreen(fullScreen);
        UpdateWindowMinimum();
        FitViewport();
        if (!fullScreen)
            RefreshWindowGlow();
        QueueDisplayUpdate();
        FocusDesktopSurface();
        PostViewChange(RefreshShortcuts);
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
        {
            if (remoteDisplay != layout.Size)
                activityPointer?.Reset();
            remoteDisplay = layout.Size;
        }

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
        if (recoveryOpen)
            UpdateRecoveryArea();
        if (fullScreen)
        {
            PositionFullScreenControls();
            RefreshWindowGlow();
        }
    }

    private void UpdateRecoveryArea()
    {
        var area = viewport.ClientRectangle;
        if (fullScreen)
        {
            var host = viewport.RectangleToScreen(area);
            var monitor = Rectangle.Intersect(host, fullScreenMonitor);
            if (monitor.Width > 0 && monitor.Height > 0)
                area = viewport.RectangleToClient(monitor);
        }
        recovery.SetContentArea(area);
    }

    private void PositionFullScreenControls()
    {
        var client = window.RectangleToScreen(window.ClientRectangle);
        var previousEdge = fullScreenControls.Edge;
        var screens = Screen.AllScreens;
        if ((dragDock ?? exitDock) is { } placement)
        {
            var saved = screens.FirstOrDefault(screen =>
                string.Equals(screen.DeviceName, placement.Monitor, StringComparison.OrdinalIgnoreCase));
            if (saved is not null && Place(saved.Bounds, placement.Edge, placement.OffsetDips))
                return;
        }

        if (Place(fullScreenMonitor, RdpDockEdge.Top, DefaultExitRightOffsetDips))
            return;
        foreach (var screen in screens)
        {
            if (Place(screen.Bounds, RdpDockEdge.Top, DefaultExitRightOffsetDips))
                return;
        }
        fullScreenControls.SetDockEdge(previousEdge);

        bool Place(Rectangle monitor, RdpDockEdge edge, int offset)
        {
            fullScreenControls.SetDockEdge(edge);
            var controlsSize = fullScreenControls.GetPreferredSize(Size.Empty);
            if (RdpDisplayTopology.ExitDockArea(client, monitor, controlsSize, edge) is not { } area)
                return false;
            fullScreenControls.Location = RdpDisplayTopology.ExitDockLocation(client, area, controlsSize,
                offset, window.DeviceDpi, edge);
            return true;
        }
    }

    private void DragFullScreenControls(FullScreenDockButton button, Point pointer, Point grabPoint)
    {
        if (!fullScreen || closing)
            return;
        var client = window.RectangleToScreen(window.ClientRectangle);
        var screen = Screen.FromPoint(pointer);
        var previousEdge = fullScreenControls.Edge;
        // Decide which edge to try before measuring its layout.
        if (RdpDisplayTopology.ExitDockEdge(client, screen.Bounds, Size.Empty, pointer, previousEdge, window.DeviceDpi) is not { } edge)
            return;

        fullScreenControls.SetDockEdge(edge);
        var controlsSize = fullScreenControls.GetPreferredSize(Size.Empty);
        if (RdpDisplayTopology.ExitDockArea(client, screen.Bounds, controlsSize, edge) is not { } area)
        {
            fullScreenControls.SetDockEdge(previousEdge);
            return;
        }
        var grabOffset = new Point(button.Left + Math.Clamp(grabPoint.X, 0, Math.Max(0, button.Width - 1)),
            button.Top + Math.Clamp(grabPoint.Y, 0, Math.Max(0, button.Height - 1)));
        int offset = RdpDisplayTopology.ExitDockOffsetDips(area, controlsSize, pointer, grabOffset, edge, window.DeviceDpi);
        dragDock = new(screen.DeviceName, offset, edge);
        fullScreenControls.Location = RdpDisplayTopology.ExitDockLocation(client, area, controlsSize,
            offset, window.DeviceDpi, edge);
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
            PositionFullScreenControls();
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
        if (signedIn && HasForeground() == true)
            FocusDesktopSurface();
        PostViewChange(RefreshShortcuts);
        RefreshWindowGlow();
    }

    private void FocusDesktopSurface()
    {
        if (authenticationDialogOpen)
            return;
        if (recoveryOpen)
            recovery.FocusPrimary();
        else
            control.Focus();
    }

    internal void SetGlow(bool showInternal, bool showExternal)
    {
        (internalGlow, externalGlow) = (showInternal, showExternal);
        RefreshWindowGlow();
    }

    private void RefreshWindowGlow() =>
        windowGlow.Refresh(!closing && !completion.Task.IsCompleted && (fullScreen ? internalGlow : externalGlow), fullScreen);

    internal static bool IsClientException(Exception ex) =>
        ex is COMException or RuntimeBinderException or InvalidOperationException or InvalidCastException or Win32Exception;

    internal static Failure? DescribeLogon(int code) => code switch
    {
        0 or unchecked((int)0xc000006d) =>
            new("RDP rejected the sign-in. Review the username and any saved RDP password in Settings.", FailureKind.CredentialRejected),
        1 or unchecked((int)0xc0000224) =>
            new("The RDP password must be changed. Update it through Windows or with your administrator, then review Settings.", FailureKind.ActionRequired),
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

    internal static Failure DescribeDisconnect(int code, bool signedIn, bool warningSeen, bool savedPassword,
        int extendedReason = 0) => code switch
    {
        _ when extendedReason == 5 => new("Windows reported that another connection replaced this Remote Desktop session.",
            FailureKind.Dismissed),
        _ when extendedReason is 11 or 12 => new("A user disconnected or signed out of the Remote Desktop session.",
            FailureKind.Dismissed),
        1798 => new("Windows could not read the certificate from the remote PC (RDP code 1798). Ask your administrator to check the certificate configured for Remote Desktop.",
            FailureKind.ActionRequired),
        6919 => new("Windows reported an expired RDP certificate (RDP code 6919). Check this PC's date and time, then ask your administrator to renew the remote PC's certificate.",
            FailureKind.ActionRequired),
        2055 or 2567 => DescribeLogon(0)!,
        2823 or 3591 => new("The RDP account is disabled or expired. Ask your administrator to restore access.", FailureKind.CredentialRejected),
        3079 => new("Account policy restricted RDP sign-in. Ask your administrator to review the account's access.", FailureKind.CredentialRejected),
        3335 => new("The RDP account is locked. Ask your administrator to unlock it before retrying.", FailureKind.CredentialRejected),
        7175 => new("The smart card PIN was rejected. Confirm the PIN before trying again.", FailureKind.CredentialRejected),
        8711 => new("The smart card is blocked. Ask your administrator to unlock or replace it.", FailureKind.CredentialRejected),
        3847 or 4615 => DescribeLogon(1)!,
        5639 or 5895 or 6151 => new("Windows could not delegate or verify the RDP credentials. Ask your administrator to check domain and authentication policy.",
            FailureKind.ActionRequired),
        8455 => new("The RDP server requires fresh credentials. Clear the saved password in Settings and save, then sign in again.", FailureKind.ActionRequired),
        260 or 520 or 1288 or 1540 => new("Windows could not resolve the RDP hostname. Check the hostname in Settings and confirm VPN DNS can resolve it."),
        264 or 1796 => warningSeen && !signedIn
            ? new("RDP sign-in timed out. Choose Open Remote Desktop and finish the Windows prompt.", FailureKind.ManualRetry)
            : new("The RDP connection timed out. Check that the remote computer is online and accepts Remote Desktop."),
        516 => new("Windows could not connect to the RDP port. Verify the port in Settings and ask your administrator to check access from the VPN."),
        772 or 1028 or 2308 when signedIn => new("The network interrupted the RDP session. Check the VPN and remote computer if this repeats."),
        2056 or 2312 => new("RDP licensing could not be completed. Ask your administrator to check the remote computer's Remote Desktop licensing.",
            FailureKind.ActionRequired),
        776 or 2052 => new("Windows rejected the RDP address. Review the hostname and optional IP address in Settings.", FailureKind.ActionRequired),
        1030 or 1286 or 1542 or 2310 or 2566 or 2822 or 3078 =>
            new($"RDP could not establish a secure session (RDP code {code}). Ask your administrator to check the remote computer's security configuration.",
                FailureKind.ActionRequired),
        1 => WindowClosed(signedIn),
        2 => new("A user ended the connection on the remote computer.", FailureKind.Dismissed),
        3 => new("The remote computer ended the Remote Desktop connection.", FailureKind.Dismissed),
        _ when !signedIn && savedPassword =>
            new($"RDP sign-in failed (RDP code {code}). Review the account in Settings and share the code with your administrator if it repeats.",
                FailureKind.ActionRequired),
        _ => new($"The RDP session ended (RDP code {code}). Choose Open Remote Desktop to retry.", FailureKind.ManualRetry)
    };

    private static Failure WindowClosed(bool signedIn) => signedIn
        ? new(ConnectionController.RdpClosedStatus, FailureKind.Dismissed)
        : new("The RDP window closed before sign-in completed.", FailureKind.Dismissed);

    public void Dispose()
    {
        if (closing)
            return;
        closing = true;
        shortcuts.Dispose();
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

    private sealed class FullScreenControls : TableLayoutPanel
    {
        private const string ExitText = "Exit full screen (Ctrl+Shift+F)";
        private const string MinimizeText = "Minimize (Ctrl+Shift+N)";
        private readonly FullScreenDivider divider = new();
        internal FullScreenDockButton Exit { get; } = new(ExitText);
        internal FullScreenDockButton Minimize { get; } = new(MinimizeText);
        internal RdpDockEdge Edge { get; private set; }

        internal FullScreenControls()
        {
            Visible = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(1);
            ColumnCount = 3;
            RowCount = 3;
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize;
            AccessibleName = "Full-screen controls";
            AccessibleRole = AccessibleRole.Grouping;
            Controls.Add(Exit, 0, 0);
            Controls.Add(divider, 1, 0);
            Controls.Add(Minimize, 2, 0);
            ApplyColors();
        }

        internal void SetDockEdge(RdpDockEdge edge)
        {
            bool stacked = edge != RdpDockEdge.Top;
            bool sameLayout = stacked == (Edge != RdpDockEdge.Top);
            Edge = edge;
            if (sameLayout)
                return;

            SuspendLayout();
            try
            {
                // Keep the original controls under the same parent while they hold mouse capture.
                SetCellPosition(divider, stacked ? new(0, 1) : new(1, 0));
                SetCellPosition(Minimize, stacked ? new(0, 2) : new(2, 0));
                Exit.Text = stacked ? "Exit full\nscreen\n(Ctrl+Shift+F)" : ExitText;
                Minimize.Text = stacked ? "Minimize\n(Ctrl+Shift+N)" : MinimizeText;
                var padding = stacked ? new Padding(3, 4, 3, 4) : Padding.Empty;
                Exit.Padding = Minimize.Padding = padding;
                var anchor = stacked ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Top | AnchorStyles.Left;
                Exit.Anchor = Minimize.Anchor = anchor;
                divider.SetStacked(stacked);
            }
            finally { ResumeLayout(performLayout: true); }
        }

        protected override void OnSystemColorsChanged(EventArgs e)
        {
            base.OnSystemColorsChanged(e);
            ApplyColors();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            ControlPaint.DrawBorder(e.Graphics, ClientRectangle, ForeColor, ButtonBorderStyle.Solid);
        }

        private void ApplyColors()
        {
            bool highContrast = SystemInformation.HighContrast;
            BackColor = highContrast ? SystemColors.Highlight : Color.FromArgb(0, 89, 184);
            ForeColor = highContrast ? SystemColors.HighlightText : Color.White;
        }
    }

    private sealed class FullScreenDivider : Label
    {
        private bool stacked;

        internal FullScreenDivider()
        {
            AutoSize = true;
            AccessibleRole = AccessibleRole.Separator;
            SetStacked(false);
        }

        internal void SetStacked(bool vertical)
        {
            stacked = vertical;
            Text = vertical ? "" : "|";
            Anchor = vertical ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.None;
            Margin = vertical ? new Padding(4, 2, 4, 2) : new Padding(2, 0, 2, 0);
            Invalidate();
        }

        public override Size GetPreferredSize(Size proposedSize) =>
            stacked ? new Size(1, 1) : base.GetPreferredSize(proposedSize);

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!stacked)
            {
                base.OnPaint(e);
                return;
            }
            using var pen = new Pen(ForeColor);
            e.Graphics.DrawLine(pen, 0, Height / 2, Width - 1, Height / 2);
        }
    }

    private sealed class FullScreenDockButton : Button
    {
        private const int LeftButtonUpMessage = 0x0202;
        private Rectangle dragThreshold;
        private Point grabPoint;
        private bool pressed, dragging, releasing;

        internal event Action<Point, Point>? DockDragged;
        internal event Action<bool>? DockDragEnded;

        internal FullScreenDockButton(string text)
        {
            Text = text;
            AccessibleName = text;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = Padding.Empty;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            AccessibleDescription = ExitDockHint;
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
            grabPoint = e.Location;
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
                Cursor = Cursors.SizeAll;
            }
            if (dragging)
                DockDragged?.Invoke(pointer, grabPoint);
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
        private const int SystemCommandMessage = 0x0112;
        private const int MaximizeCommand = 0xF030;

        internal event Action<Keys>? ShortcutPressed;
        internal event Action? MaximizeRequested;
        internal event Action? DisplayChanged;

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == SystemCommandMessage && (message.WParam.ToInt64() & 0xFFF0) == MaximizeCommand)
            {
                MaximizeRequested?.Invoke();
                return;
            }

            if (message.Msg == RdpWindowShortcuts.ShortcutMessage)
            {
                ShortcutPressed?.Invoke((Keys)message.WParam);
                return;
            }
            base.WndProc(ref message);
            if (message.Msg == DisplayChangeMessage)
                DisplayChanged?.Invoke();
        }

    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();

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

internal sealed class RdpRecoveryPanel : Panel
{
    private readonly TableLayoutPanel card = new()
    {
        ColumnCount = 1,
        RowCount = 3,
        Margin = Padding.Empty,
        GrowStyle = TableLayoutPanelGrowStyle.FixedSize
    };
    private readonly Label heading = new() { AutoSize = true, UseMnemonic = false };
    private readonly Label explanation = new() { AutoSize = true, UseMnemonic = false };
    private readonly FlowLayoutPanel actions = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = true
    };
    private readonly Button view = CreateButton("View desktop");
    private readonly Button reconnect = CreateButton("Reconnect now");
    private readonly Button status = CreateButton("Connection status");
    private readonly Font headingFont = new(DefaultFont.FontFamily, 17f, FontStyle.Bold);
    private Action? viewAction, reconnectAction, statusAction;
    private Rectangle contentArea;
    private bool layingOut;

    internal RdpRecoveryPanel()
    {
        Dock = DockStyle.Fill;
        Visible = false;
        AccessibleName = "Remote Desktop connection recovery";
        AccessibleRole = AccessibleRole.Grouping;
        heading.Font = headingFont;
        card.Controls.Add(heading, 0, 0);
        card.Controls.Add(explanation, 0, 1);
        card.Controls.Add(actions, 0, 2);
        actions.Controls.Add(view);
        actions.Controls.Add(reconnect);
        actions.Controls.Add(status);
        Controls.Add(card);
        view.Click += (_, _) => viewAction?.Invoke();
        reconnect.Click += (_, _) => reconnectAction?.Invoke();
        status.Click += (_, _) => statusAction?.Invoke();
        ApplyColors();
    }

    internal void UpdateContent(string title, string detail, Action? retry, Action showStatus, Action? showDesktop)
    {
        viewAction = showDesktop;
        reconnectAction = retry;
        statusAction = showStatus;
        heading.Text = title;
        explanation.Text = detail;
        view.Visible = showDesktop is not null;
        reconnect.Visible = retry is not null;
        ApplyButtonColors();
        PerformLayout();
    }

    internal void ResetActions() => viewAction = reconnectAction = statusAction = null;

    internal void FocusPrimary()
    {
        if (view.Visible)
            view.Focus();
        else if (reconnect.Visible)
            reconnect.Focus();
        else
            status.Focus();
    }

    internal void SetContentArea(Rectangle area)
    {
        if (contentArea == area)
            return;
        contentArea = area;
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (layingOut || card.Parent != this)
            return;

        layingOut = true;
        try
        {
            var area = Rectangle.Intersect(contentArea, ClientRectangle);
            if (area.Width == 0 || area.Height == 0)
                area = ClientRectangle;
            int margin = Scale(16);
            int width = Math.Clamp(area.Width - 2 * margin, 1, Scale(490));
            int padding = Math.Min(Scale(20), width / 6);
            int textWidth = width - 2 * padding;
            card.SuspendLayout();
            try
            {
                card.Padding = new Padding(padding);
                heading.Margin = new Padding(0, 0, 0, Scale(8));
                explanation.Margin = new Padding(0, 0, 0, Scale(16));
                actions.Margin = Padding.Empty;
                heading.MaximumSize = explanation.MaximumSize = actions.MaximumSize = new Size(textWidth, 0);
                foreach (var button in new[] { view, reconnect, status })
                {
                    button.Padding = new Padding(Scale(9), Scale(4), Scale(9), Scale(4));
                    button.Margin = new Padding(0, 0, Scale(6), Scale(4));
                }
                int height = card.GetPreferredSize(new Size(width, 0)).Height;
                card.SetBounds(area.Left + (area.Width - width) / 2,
                    area.Top + Math.Max(0, (area.Height - height) / 2), width, height);
            }
            finally { card.ResumeLayout(performLayout: true); }
        }
        finally { layingOut = false; }
    }

    protected override void OnSystemColorsChanged(EventArgs e)
    {
        base.OnSystemColorsChanged(e);
        ApplyColors();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            headingFont.Dispose();
    }

    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    private void ApplyColors()
    {
        bool highContrast = SystemInformation.HighContrast;
        BackColor = highContrast ? SystemColors.Window : Color.FromArgb(15, 23, 33);
        card.BackColor = highContrast ? SystemColors.Window : Color.FromArgb(26, 39, 55);
        heading.ForeColor = highContrast ? SystemColors.WindowText : Color.White;
        explanation.ForeColor = highContrast ? SystemColors.WindowText : Color.FromArgb(214, 224, 236);
        ApplyButtonColors();
    }

    private void ApplyButtonColors()
    {
        bool highContrast = SystemInformation.HighContrast;
        var primary = viewAction is not null ? view : reconnectAction is not null ? reconnect : status;
        foreach (var button in new[] { view, reconnect, status })
        {
            button.BackColor = highContrast ? SystemColors.ButtonFace :
                button == primary ? Color.FromArgb(0, 89, 184) : card.BackColor;
            button.ForeColor = highContrast ? SystemColors.ControlText : Color.White;
            button.FlatAppearance.BorderColor = highContrast ? SystemColors.ControlText :
                button == primary ? Color.FromArgb(71, 148, 228) : Color.FromArgb(113, 135, 161);
        }
    }

    private static Button CreateButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlatStyle = FlatStyle.Flat,
        UseVisualStyleBackColor = false
    };
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
            "keyboardhook:i:1",
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
            return DescribeExit(process.ExitCode);
        }
        catch (InvalidOperationException)
        { return new("Remote Desktop closed. Choose Open Remote Desktop to sign in again.", FailureKind.ManualRetry); }
        catch (Win32Exception ex)
        {
            return new($"Windows lost contact with Remote Desktop Connection (code {ex.NativeErrorCode}). Choose Open Remote Desktop to retry.",
                FailureKind.ManualRetry);
        }
    }

    // The native client's exit confirms its window has ended; sign-in and cancellation details stay in Windows.
    internal static Failure DescribeExit(int code) => code == 0
        ? new(ConnectionController.RdpClosedStatus, FailureKind.Dismissed)
        : new($"Remote Desktop closed with code {code}. Check the Windows client for sign-in or connection errors.", FailureKind.Dismissed);

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
