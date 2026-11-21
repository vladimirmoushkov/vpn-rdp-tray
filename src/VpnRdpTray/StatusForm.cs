using System.Globalization;
using System.Runtime.InteropServices;

namespace VpnRdpTray;

internal sealed class StatusForm : Form
{
    private static readonly Size MinimumWindowSize = new(690, 345);
    private readonly RichTextBox log = new()
    {
        AccessibleName = "Connection activity log",
        BorderStyle = BorderStyle.None,
        DetectUrls = false,
        Dock = DockStyle.Fill,
        ReadOnly = true,
        ScrollBars = RichTextBoxScrollBars.ForcedVertical,
        WordWrap = true
    };
    private readonly StatusPreviewPanel preview = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 34 };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 2000 };
    private readonly Font logFont = new("Consolas", 10f);
    private readonly Font headingFont = new(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Bold);
    private readonly Func<Size, Bitmap?> capturePreview;
    private readonly Func<bool> isPreviewMinimized;
    private StatusLogEntry? firstEntry, lastEntry;
    private int renderedCount;
    private bool disposed, hasConfiguration, canSignIn;
    private readonly LinkLabel settings = new()
    {
        Text = "&Settings",
        AutoSize = true,
        Anchor = AnchorStyles.Right,
        Margin = Padding.Empty,
        LinkBehavior = LinkBehavior.HoverUnderline,
        LinkColor = SystemColors.HotTrack,
        ActiveLinkColor = SystemColors.HotTrack,
        VisitedLinkColor = SystemColors.HotTrack
    };

    internal StatusForm(Action signInToVpn, Action openRemoteDesktop, Action toggleVpn, Action toggleRdp, Action openSettings,
        Func<Size, Bitmap?> capturePreview, Func<bool> isPreviewMinimized)
    {
        this.capturePreview = capturePreview;
        this.isPreviewMinimized = isPreviewMinimized;
        SuspendLayout();
        Text = "VPN Remote Desktop Status";
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(1050, 430);
        MinimumSize = MinimumWindowSize;
        StartPosition = FormStartPosition.Manual;
        ShowIcon = false;

        log.Font = logFont;
        log.BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(20, 29, 41);
        log.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(235, 241, 249);

        settings.LinkClicked += (_, _) => openSettings();
        preview.VpnActivated += () =>
        {
            if (!hasConfiguration)
                openSettings();
            else if (canSignIn)
                signInToVpn();
            else
                toggleVpn();
        };
        preview.RdpActivated += toggleRdp;
        preview.PreviewActivated += openRemoteDesktop;

        var panes = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(12)
        };
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 49));
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 51));
        panes.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var left = CreatePane("Activity log", new Padding(0, 0, 6, 0));
        left.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(10, 8, 10, 8),
            BackColor = log.BackColor, Controls = { log } }, 0, 1);
        panes.Controls.Add(left, 0, 0);

        var right = CreatePane("Connection preview", new Padding(6, 0, 0, 0), settings);
        right.Controls.Add(preview, 0, 1);
        panes.Controls.Add(right, 1, 0);
        Controls.Add(panes);

        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        animation.Tick += (_, _) =>
        {
            preview.Invalidate(invalidateChildren: true);
            if (!preview.IsAnimating)
                animation.Stop();
        };
        refresh.Tick += (_, _) => RefreshPreview();
        ResumeLayout(performLayout: false);
    }

    internal void UpdateStatus(bool connection, bool hasConfiguration, bool actionsEnabled, bool canSignIn, bool rdpEnabled,
        ConnectionStatus vpn, ConnectionStatus rdp, IReadOnlyList<StatusLogEntry> entries)
    {
        this.hasConfiguration = hasConfiguration;
        this.canSignIn = canSignIn;
        settings.Enabled = actionsEnabled;
        UpdateLog(entries);
        preview.UpdateStatus(connection, hasConfiguration, actionsEnabled, canSignIn, rdpEnabled, vpn, rdp);
        UpdateTimers();
    }

    private TableLayoutPanel CreatePane(string heading, Padding margin, Control? action = null)
    {
        var pane = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = margin };
        pane.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = new Padding(0, 0, 0, 8) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = heading, Font = headingFont, AutoSize = true,
            Anchor = AnchorStyles.Left, Margin = Padding.Empty }, 0, 0);
        if (action is not null)
            header.Controls.Add(action, 1, 0);
        pane.Controls.Add(header, 0, 0);
        return pane;
    }

    private void UpdateLog(IReadOnlyList<StatusLogEntry> entries)
    {
        if (entries.Count == 0)
        {
            if (renderedCount > 0)
            {
                log.Clear();
                renderedCount = 0;
                firstEntry = lastEntry = null;
            }
            return;
        }
        if (entries.Count == renderedCount && entries[0] == firstEntry && entries[^1] == lastEntry)
            return;

        bool append = renderedCount > 0 && entries.Count >= renderedCount && entries[0] == firstEntry &&
            entries[renderedCount - 1] == lastEntry;
        int followAt = Math.Max(0, log.TextLength - 160);
        bool follow = renderedCount == 0 || log.GetCharIndexFromPosition(new Point(1, log.ClientSize.Height - 2)) >= followAt;
        int selected = log.SelectionStart;
        int length = log.SelectionLength;
        int top = log.GetCharIndexFromPosition(new Point(1, 1));
        if (!append)
            log.Clear();
        for (int index = append ? renderedCount : 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var local = entry.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            var level = entry.Level.ToUpperInvariant();
            AddText($"{local}  ", Color.FromArgb(157, 178, 205));
            AddText($"{level,-5}  ", level switch
            {
                "ERROR" => Color.FromArgb(255, 159, 156),
                "WARN" or "WARNING" => Color.FromArgb(255, 209, 128),
                "OK" or "SUCCESS" => Color.FromArgb(130, 241, 173),
                _ => Color.FromArgb(154, 208, 255)
            });
            AddText($"{entry.Area}  {entry.Message}{Environment.NewLine}", log.ForeColor);
        }
        renderedCount = entries.Count;
        firstEntry = entries[0];
        lastEntry = entries[^1];

        if (follow)
        {
            log.Select(log.TextLength, 0);
            log.ScrollToCaret();
        }
        else if (append)
        {
            log.Select(Math.Min(top, log.TextLength), 0);
            log.ScrollToCaret();
            log.Select(Math.Min(selected, log.TextLength), Math.Min(length, Math.Max(0, log.TextLength - selected)));
        }
    }

    private void AddText(string text, Color color)
    {
        log.SelectionStart = log.TextLength;
        log.SelectionLength = 0;
        log.SelectionColor = SystemInformation.HighContrast ? SystemColors.WindowText : color;
        log.AppendText(text);
    }

    private void RefreshPreview()
    {
        if (!Visible || WindowState == FormWindowState.Minimized || !preview.WantsFrame)
            return;

        Bitmap? frame;
        bool minimized = false;
        try
        {
            minimized = isPreviewMinimized();
            frame = capturePreview(preview.CaptureSize);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException or ArgumentException)
        { frame = null; }
        preview.UpdateFrame(frame, minimized);
    }

    private void UpdateTimers()
    {
        if (disposed)
            return;
        bool showing = Visible && WindowState != FormWindowState.Minimized;
        animation.Enabled = showing && preview.IsAnimating;
        refresh.Enabled = showing && preview.WantsFrame;
        if (!showing)
            preview.ClearFrame();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var desktop = Screen.FromPoint(Cursor.Position).WorkingArea;
        var maximum = FitMinimumSize(desktop);
        Size = new Size(Math.Min(Width, maximum.Width), Math.Min(Height, maximum.Height));
        Location = new Point(desktop.Left + (desktop.Width - Width) / 2, desktop.Top + (desktop.Height - Height) / 2);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitMinimumSize(Screen.FromRectangle(e.SuggestedRectangle).WorkingArea);
    }

    private Size FitMinimumSize(Rectangle workingArea)
    {
        var maximum = new Size(Math.Max(1, workingArea.Width - 40), Math.Max(1, workingArea.Height - 40));
        var minimum = LogicalToDeviceUnits(MinimumWindowSize);
        MinimumSize = new Size(Math.Min(minimum.Width, maximum.Width), Math.Min(minimum.Height, maximum.Height));
        return maximum;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateTimers();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateTimers();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            disposed = true;
            animation.Dispose();
            refresh.Dispose();
            logFont.Dispose();
            headingFont.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }

        return base.ProcessDialogKey(keyData);
    }

}
