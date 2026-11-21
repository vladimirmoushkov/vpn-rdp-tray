using System.Drawing.Drawing2D;
using System.Globalization;

namespace VpnRdpTray;

internal sealed class StatusPreviewPanel : Control
{
    private static readonly Color PanelBackground = Color.FromArgb(18, 32, 48);
    private static readonly Color TitleColor = Color.FromArgb(244, 249, 255);
    private static readonly Color DetailColor = Color.FromArgb(171, 190, 210);

    private enum Stage { Waiting, Running, Connected, Attention }

    private readonly StatusCircle vpnCircle = new() { TabIndex = 0 };
    private readonly StatusCircle rdpCircle = new() { TabIndex = 1 };
    private readonly PreviewSurface surface = new() { TabIndex = 2, Visible = false };
    private readonly ToolTip hints = new();

    private ConnectionStatus vpn = new("VPN idle"), rdp = new("RDP waiting for VPN");
    private Bitmap? frame;
    private DateTimeOffset lastCapture;
    private bool connection, hasConfiguration, actionsEnabled, rdpEnabled, minimized;

    internal StatusPreviewPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleName = "Connection preview";
        AccessibleRole = AccessibleRole.Pane;
        TabStop = false;
        Margin = Padding.Empty;
        vpnCircle.Click += (_, _) => VpnActivated?.Invoke();
        rdpCircle.Click += (_, _) => RdpActivated?.Invoke();
        surface.Click += (_, _) => PreviewActivated?.Invoke();
        Controls.AddRange([vpnCircle, rdpCircle, surface]);
        hints.SetToolTip(surface, "Open Remote Desktop");
    }

    internal event Action? VpnActivated;
    internal event Action? RdpActivated;
    internal event Action? PreviewActivated;

    internal bool WantsFrame => connection && rdpEnabled && rdp.Name == "RDP connected";

    private bool HasFrame => WantsFrame && frame is not null;

    internal bool IsAnimating => connection && (vpnCircle.IsAnimating || rdpCircle.IsAnimating);

    internal Size CaptureSize
    {
        get
        {
            var bounds = PreviewBounds.Size;
            int height = Math.Max(1, bounds.Height - Units(24));
            int width = Math.Max(1, bounds.Width);
            double scale = Math.Min(1, Math.Min(1200d / width, 800d / height));
            return new Size(Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
        }
    }

    private Rectangle PreviewBounds => new(Units(12), Units(62),
        Math.Max(0, ClientSize.Width - Units(24)), Math.Max(0, ClientSize.Height - Units(69)));

    internal void UpdateStatus(bool connection, bool hasConfiguration, bool actionsEnabled, bool canSignIn,
        bool rdpEnabled, ConnectionStatus vpn, ConnectionStatus rdp)
    {
        this.connection = connection;
        this.hasConfiguration = hasConfiguration;
        this.actionsEnabled = actionsEnabled;
        this.rdpEnabled = rdpEnabled;
        this.vpn = vpn;
        this.rdp = rdp;
        if (!WantsFrame)
            ClearFrame();

        var shownVpn = !hasConfiguration ? new ConnectionStatus("VPN settings required", Action: true) :
            !connection ? new ConnectionStatus("VPN off") : vpn;
        var vpnStage = shownVpn.Name == "VPN connected" ? Stage.Connected :
            shownVpn.Action || shownVpn.RetryAt.HasValue ? Stage.Attention :
            hasConfiguration && connection && shownVpn.Name != "VPN idle" ? Stage.Running : Stage.Waiting;
        string vpnAction = !actionsEnabled ? "Wait for the current change to finish." :
            !hasConfiguration ? "Open Settings to set up the VPN." :
            canSignIn ? "Sign in to VPN." : connection ? "Turn VPN off." : "Turn VPN on.";
        ConfigureCircle(vpnCircle, shownVpn, vpnStage, actionsEnabled, vpnAction);
        UpdateRdpCircle();
        surface.Enabled = actionsEnabled;
        SetAccessibleDescription();
    }

    private void UpdateRdpCircle()
    {
        var shownRdp = !rdpEnabled ? new ConnectionStatus(ConnectionController.RdpOffStatus) :
            !hasConfiguration || !connection ? new ConnectionStatus("RDP waiting for VPN") : rdp;
        var rdpStage = shownRdp.Name is "RDP connected" or "Opened in Remote Desktop" ? Stage.Connected :
            shownRdp.Name == ConnectionController.RdpClosedStatus ? Stage.Waiting :
            shownRdp.Action || shownRdp.RetryAt.HasValue ? Stage.Attention :
            rdpEnabled && connection && vpn.Name == "VPN connected" && shownRdp.Name != "RDP waiting for VPN" ? Stage.Running : Stage.Waiting;
        string rdpAction = !actionsEnabled ? "Wait for the current change to finish." :
            !hasConfiguration ? "Open Settings to set up Remote Desktop." :
            !connection ? "Turn VPN on to control Remote Desktop." :
            shownRdp.Name == ConnectionController.RdpClosedStatus ? "Open Remote Desktop." :
            rdpEnabled ? "Turn Remote Desktop off." : "Turn Remote Desktop on.";
        string? rdpContext = shownRdp.Name switch
        {
            "RDP connected" when HasFrame => "Click the preview to open Remote Desktop.",
            "RDP connected" when minimized => "Minimized · Waiting for a desktop frame. Use Open Remote Desktop in the tray menu.",
            "RDP connected" => "Waiting for a desktop frame. Use Open Remote Desktop in the tray menu.",
            "Opened in Remote Desktop" => "Choose Open Remote Desktop in the tray menu to return to the Windows session.",
            ConnectionController.RdpClosedStatus => "Click here to reopen Remote Desktop.",
            "RDP paused" => "Open Settings to review the Remote Desktop connection.",
            _ when shownRdp.Action => "Choose Open Remote Desktop in the tray menu to continue.",
            _ => null
        };

        ConfigureCircle(rdpCircle, shownRdp, rdpStage, actionsEnabled && hasConfiguration && connection, rdpAction, rdpContext);
    }

    private void ConfigureCircle(StatusCircle circle, ConnectionStatus status, Stage stage, bool enabled, string action,
        string? context = null)
    {
        string? retry = status.RetryAt is { } when
            ? "Retry at " + when.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) + "."
            : null;
        string? prior = status.LastFailure is { } failure && failure != status.Reason
            ? $"Previous attempt failed. {failure}"
            : null;
        string detail = context ?? status.Reason ?? retry ?? action;
        if (retry is not null && detail != retry)
            detail = $"{retry} {detail}";
        string description = string.Join(Environment.NewLine, new[] { status.Reason, prior, retry, context, action }
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        circle.SetStatus(status.Name, detail, action, description, stage, enabled);
        if (hints.GetToolTip(circle) != description)
            hints.SetToolTip(circle, description);
    }

    internal void UpdateFrame(Bitmap? image, bool sourceMinimized)
    {
        if (!WantsFrame)
        {
            image?.Dispose();
            return;
        }

        minimized = sourceMinimized;
        if (image is not null)
        {
            frame?.Dispose();
            frame = image;
            lastCapture = DateTimeOffset.Now;
        }
        else if (!minimized && frame is not null && DateTimeOffset.Now - lastCapture > TimeSpan.FromSeconds(16))
        {
            frame.Dispose();
            frame = null;
        }
        UpdateSurface();
    }

    internal void ClearFrame()
    {
        if (frame is null && !minimized)
            return;
        frame?.Dispose();
        frame = null;
        minimized = false;
        UpdateSurface();
    }

    private void UpdateSurface()
    {
        surface.SetFrame(frame, frame is null ? null : FrameCaption(), minimized);
        surface.Visible = HasFrame;
        if (WantsFrame)
            UpdateRdpCircle();
        PerformLayout();
        SetAccessibleDescription();
    }

    private void SetAccessibleDescription()
    {
        string state = !connection ? "Connection off." : HasFrame ? "Remote Desktop connected. " + FrameCaption()
            : minimized ? $"{vpn.Name}. {rdp.Name}. Minimized. Waiting for a desktop frame."
            : $"{vpn.Name}. {rdp.Name}.";
        AccessibleDescription = state;
        AccessibleName = "Connection preview. " + state;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        vpnCircle.SetCompact(HasFrame);
        rdpCircle.SetCompact(HasFrame);
        if (HasFrame)
        {
            int gap = Units(8);
            int leftWidth = Math.Max(0, (PreviewBounds.Width - gap) / 2);
            vpnCircle.Bounds = new Rectangle(PreviewBounds.X, Units(8), leftWidth, Units(46));
            rdpCircle.Bounds = new Rectangle(vpnCircle.Right + gap, Units(8),
                Math.Max(0, PreviewBounds.Right - vpnCircle.Right - gap), Units(46));
            surface.Bounds = PreviewBounds;
            return;
        }

        int verticalGap = Units(6);
        int rowHeight = Math.Min(Units(80), Math.Max(0, (ClientSize.Height - Units(24) - verticalGap) / 2));
        int width = Math.Max(0, Math.Min(ClientSize.Width - Units(24), Units(440)));
        int left = (ClientSize.Width - width) / 2;
        int top = Math.Max(0, (ClientSize.Height - rowHeight * 2 - verticalGap) / 2);
        vpnCircle.Bounds = new Rectangle(left, top, width, rowHeight);
        rdpCircle.Bounds = new Rectangle(left, top + rowHeight + verticalGap, width, rowHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(SystemInformation.HighContrast ? SystemColors.Window : PanelBackground);
        base.OnPaint(e);
    }

    private string FrameCaption() => minimized ? "Minimized · Last available frame" :
        "Captured at " + lastCapture.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    private int Units(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            frame?.Dispose();
            hints.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class StatusCircle : Button
    {
        private readonly Font titleFont = new((SystemFonts.MessageBoxFont ?? DefaultFont).FontFamily, 11.5f, FontStyle.Bold);
        private readonly Font noteFont = new((SystemFonts.MessageBoxFont ?? DefaultFont).FontFamily, 9.5f);
        private string label = "", detail = "";
        private Stage stage;
        private long changedAt;
        private bool hovered, compact;

        internal StatusCircle()
        {
            FlatStyle = FlatStyle.Flat;
            AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        internal void SetCompact(bool value)
        {
            if (compact == value)
                return;
            compact = value;
            Invalidate();
        }

        internal bool IsAnimating => stage == Stage.Running ||
            stage == Stage.Connected && Environment.TickCount64 - changedAt < 700;

        internal void SetStatus(string text, string detail, string action, string description, Stage stage, bool enabled)
        {
            if (label != text || this.stage != stage)
                changedAt = Environment.TickCount64;
            label = text;
            this.detail = detail;
            this.stage = stage;
            Enabled = enabled;
            Cursor = enabled ? Cursors.Hand : Cursors.Default;
            AccessibleName = $"{text}. {action}";
            AccessibleDescription = description;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            bool contrast = SystemInformation.HighContrast;
            bool highlight = Enabled && (hovered || Focused);
            var background = contrast ? highlight ? SystemColors.Highlight : SystemColors.Window :
                highlight ? Color.FromArgb(30, 51, 74) : PanelBackground;
            graphics.Clear(background);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color bright = contrast ? highlight ? SystemColors.HighlightText : SystemColors.WindowText : TitleColor;
            Color muted = contrast ? bright : DetailColor;
            Color accent = contrast ? bright : stage switch
            {
                Stage.Connected => Color.FromArgb(108, 226, 162),
                Stage.Attention => Color.FromArgb(255, 203, 118),
                Stage.Running => Color.FromArgb(122, 188, 255),
                _ => Color.FromArgb(138, 158, 179)
            };
            float diameter = Units(compact ? 24 : 32);
            float circleTop = compact ? (Height - diameter) / 2 : Units(10);
            var circle = new RectangleF(Units(compact ? 10 : 12), circleTop, diameter, diameter);
            PaintRing(graphics, circle, accent);

            int left = Units(compact ? 42 : 60);
            int width = Math.Max(0, Width - left - Units(8));
            var title = new Rectangle(left, compact ? 0 : Units(6), width, compact ? Height : Units(25));
            TextRenderer.DrawText(graphics, label, titleFont, title, bright,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (!compact)
            {
                int top = title.Bottom + Units(1);
                TextRenderer.DrawText(graphics, detail, noteFont, new Rectangle(left, top, width, Math.Max(0, Height - top - Units(4))), muted,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            }

            if (Focused)
                ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -2, -2), bright, background);
        }

        private void PaintRing(Graphics graphics, RectangleF circle, Color color)
        {
            using var pen = new Pen(color, Units(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (stage == Stage.Running)
            {
                graphics.DrawArc(pen, circle, (Environment.TickCount64 % 1000) * 0.36f, 245);
                return;
            }
            graphics.DrawEllipse(pen, circle);
            float unit = circle.Width / 32f;
            PointF At(float x, float y) => new(circle.X + x * unit, circle.Y + y * unit);
            if (stage == Stage.Connected)
            {
                graphics.DrawLines(pen, [At(8, 17), At(14, 23), At(24, 10)]);
                long elapsed = Environment.TickCount64 - changedAt;
                if (elapsed < 700 && !SystemInformation.HighContrast)
                {
                    float progress = elapsed / 700f;
                    circle.Inflate(Units(2) + Units(7) * progress, Units(2) + Units(7) * progress);
                    using var pulse = new Pen(Color.FromArgb((int)(150 * (1 - progress)), color), Units(2));
                    graphics.DrawEllipse(pulse, circle);
                }
            }
            else if (stage == Stage.Attention)
            {
                graphics.DrawLine(pen, At(16, 9), At(16, 18));
                var dot = At(15, 22);
                using var fill = new SolidBrush(color);
                graphics.FillEllipse(fill, dot.X, dot.Y, unit * 2, unit * 2);
            }
        }

        private int Units(int value) => (int)Math.Round(value * DeviceDpi / 96d);

        protected override void Dispose(bool disposing)
        {
            if (disposing) { titleFont.Dispose(); noteFont.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class PreviewSurface : Button
    {
        private readonly Font captionFont = new((SystemFonts.MessageBoxFont ?? DefaultFont).FontFamily, 9.5f);
        private Bitmap? image;
        private string? caption;
        private bool minimized;

        internal PreviewSurface()
        {
            FlatStyle = FlatStyle.Flat;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = "Open Remote Desktop preview";
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        internal void SetFrame(Bitmap? image, string? text, bool minimized)
        {
            this.image = image;
            caption = text;
            this.minimized = minimized;
            AccessibleDescription = text;
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            bool contrast = SystemInformation.HighContrast;
            Color background = contrast ? SystemColors.Window : PanelBackground;
            Color foreground = contrast ? SystemColors.WindowText : minimized ? DetailColor : TitleColor;
            graphics.Clear(background);
            if (image is null || Width <= 0 || Height <= 0)
                return;

            int footerHeight = (int)Math.Round(24 * DeviceDpi / 96d);
            var bounds = new Rectangle(0, 0, Width, Math.Max(1, Height - footerHeight));
            using (var black = new SolidBrush(contrast ? SystemColors.Window : Color.FromArgb(6, 11, 18)))
                graphics.FillRectangle(black, bounds);
            double scale = Math.Min((double)bounds.Width / image.Width, (double)bounds.Height / image.Height);
            int width = Math.Max(1, (int)Math.Round(image.Width * scale));
            int height = Math.Max(1, (int)Math.Round(image.Height * scale));
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image, new Rectangle((bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height));

            TextRenderer.DrawText(graphics, caption, captionFont, new Rectangle(0, bounds.Bottom, Width, footerHeight), foreground,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (Focused)
                ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -2, -2), foreground, background);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                captionFont.Dispose();
            base.Dispose(disposing);
        }
    }
}

