namespace VpnRdpTray;

internal sealed class StatusForm : Form
{
    private readonly TextBox summary = new()
    {
        AccessibleName = "Connection status",
        BackColor = SystemColors.Control,
        BorderStyle = BorderStyle.None,
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        TabStop = false
    };
    private readonly Button openDesktop = ActionButton("&Open Remote Desktop", 0);
    private readonly Button toggleConnection = ActionButton("Turn &Connection On", 1);
    private readonly Button setup = ActionButton("&Setup…", 2);
    private readonly Button exit = ActionButton("E&xit app", 3);

    internal StatusForm(Action openRemoteDesktop, Action toggle, Action openSetup, Action exitApp)
    {
        SuspendLayout();
        Text = "VPN Remote Desktop Status";
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(500, 170);
        MinimumSize = new Size(420, 180);
        StartPosition = FormStartPosition.Manual;
        ShowIcon = false;

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill
        };
        buttons.Controls.AddRange([openDesktop, toggleConnection, setup, exit]);
        openDesktop.Click += (_, _) => openRemoteDesktop();
        toggleConnection.Click += (_, _) => toggle();
        setup.Click += (_, _) => openSetup();
        exit.Click += (_, _) => exitApp();

        var frame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        frame.Controls.Add(summary, 0, 0);
        frame.Controls.Add(buttons, 0, 1);
        Controls.Add(frame);

        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(performLayout: false);
    }

    internal void UpdateStatus(string status, bool connection, bool actionsEnabled)
    {
        var display = status.Replace(" · ", Environment.NewLine, StringComparison.Ordinal);
        if (summary.Text != display)
            summary.Text = display;

        toggleConnection.Text = connection ? "Turn &Connection Off" : "Turn &Connection On";
        openDesktop.Enabled = toggleConnection.Enabled = setup.Enabled = exit.Enabled = actionsEnabled;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var desktop = Screen.FromPoint(Cursor.Position).WorkingArea;
        var maximum = new Size(desktop.Width - 40, desktop.Height - 40);
        MinimumSize = new Size(Math.Min(MinimumSize.Width, maximum.Width), Math.Min(MinimumSize.Height, maximum.Height));
        Size = new Size(Math.Min(Width, maximum.Width), Math.Min(Height, maximum.Height));
        Location = new Point(desktop.Left + (desktop.Width - Width) / 2, desktop.Top + (desktop.Height - Height) / 2);
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

    private static Button ActionButton(string text, int tabIndex) => new()
    {
        Text = text,
        TabIndex = tabIndex,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink
    };
}
