namespace VpnRdpTray;

internal sealed record VpnAuthentication(string? Password, string Code)
{
    internal static bool IsValidCode(string code) => code.Length == 6 && code.All(char.IsAsciiDigit);

    public override string ToString() => nameof(VpnAuthentication);
}

internal sealed class VpnSignInPrompt
{
    private SignInForm? pending;

    internal Task<VpnAuthentication?> RequestAuthenticationAsync(bool needsPassword, CancellationToken token) =>
        RequestAsync(needsPassword ? PromptKind.PasswordAndCode : PromptKind.Code, token);

    internal async Task<string?> RequestPasswordAsync(CancellationToken token) =>
        (await RequestAsync(PromptKind.Password, token))?.Password;

    internal async Task<string?> RequestKeyPassphraseAsync(CancellationToken token) =>
        (await RequestAsync(PromptKind.KeyPassphrase, token))?.Password;

    internal bool Reveal()
    {
        if (pending is not { IsDisposed: false, Visible: true } dialog)
            return false;

        if (dialog.WindowState == FormWindowState.Minimized)
            dialog.WindowState = FormWindowState.Normal;
        dialog.Activate();
        return true;
    }

    private async Task<VpnAuthentication?> RequestAsync(PromptKind kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (pending is not null)
            throw new InvalidOperationException("A VPN sign-in prompt is already open.");

        using var dialog = new SignInForm(kind);
        pending = dialog;
        try
        {
            dialog.Show();
            var result = await dialog.Completion.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            pending = null;
            if (!dialog.IsDisposed)
                dialog.Close();
        }
    }

    private enum PromptKind { Code, Password, PasswordAndCode, KeyPassphrase }

    private sealed class SignInForm : Form
    {
        private readonly TaskCompletionSource<VpnAuthentication?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TextBox? password;
        private readonly TextBox? code;
        private readonly Label error;
        private readonly bool keyPassphrase;
        private VpnAuthentication? submission;

        internal Task<VpnAuthentication?> Completion => completion.Task;

        internal SignInForm(PromptKind kind)
        {
            keyPassphrase = kind == PromptKind.KeyPassphrase;
            SuspendLayout();
            Text = keyPassphrase ? "Unlock VPN private key" : "Sign in to VPN";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(392, 120);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            ShowIcon = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                Padding = new Padding(16)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label
            {
                Text = kind switch
                {
                    PromptKind.KeyPassphrase => "Enter the VPN private key passphrase.",
                    PromptKind.Password => "Enter your regular VPN password.",
                    _ => "Use the current six-digit code from your authenticator."
                },
                AutoSize = true,
                Margin = new Padding(3, 0, 3, 9)
            });

            if (kind != PromptKind.Code)
                password = AddInput(layout, keyPassphrase ? "Private key &passphrase" : "VPN &password", 256);
            if (kind is PromptKind.Code or PromptKind.PasswordAndCode)
                code = AddInput(layout, "Authenticator &code", 32);

            error = new Label
            {
                AutoSize = true,
                AccessibleRole = AccessibleRole.Alert,
                MaximumSize = new Size(350, 0),
                Margin = new Padding(3, 0, 3, 8),
                Visible = false
            };
            layout.Controls.Add(error);

            var submit = ActionButton(keyPassphrase ? "&Unlock" : "&Sign in");
            var cancel = ActionButton("Cancel");
            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            buttons.Controls.AddRange([cancel, submit]);
            layout.Controls.Add(buttons);
            submit.Click += (_, _) => Submit();
            cancel.Click += (_, _) => Close();
            AcceptButton = submit;
            CancelButton = cancel;

            Controls.Add(layout);
            layout.SizeChanged += (_, _) => ClientSize = new Size(ClientSize.Width, layout.Height);
            ActiveControl = password ?? code;
            ResumeLayout(performLayout: true);
        }

        protected override bool ShowWithoutActivation => true;

        private void Submit()
        {
            string? enteredPassword = null;
            if (password is not null)
            {
                try
                {
                    enteredPassword = SetupInput.Credential(password.Text,
                        keyPassphrase ? "private key passphrase" : "VPN password", keyPassphrase ? 120 : 114);
                }
                catch (SetupException ex)
                {
                    ShowError(password, ex.Message);
                    return;
                }
            }

            string enteredCode = code?.Text ?? "";
            if (code is not null && !VpnAuthentication.IsValidCode(enteredCode))
            {
                ShowError(code, "Enter exactly six digits from your authenticator.");
                return;
            }

            submission = new VpnAuthentication(enteredPassword, enteredCode);
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            password?.Clear();
            code?.Clear();
            completion.TrySetResult(submission);
            submission = null;
            base.OnFormClosed(e);
        }

        private void ShowError(TextBox input, string message)
        {
            error.Text = message;
            error.Show();
            input.SelectAll();
            input.Focus();
        }

        private static TextBox AddInput(TableLayoutPanel layout, string caption, int maximumLength)
        {
            layout.Controls.Add(new Label { AutoSize = true, Text = caption });
            var input = new TextBox
            {
                AccessibleName = caption.Replace("&", "", StringComparison.Ordinal),
                Dock = DockStyle.Fill,
                MaxLength = maximumLength,
                Margin = new Padding(3, 2, 3, 8),
                UseSystemPasswordChar = true
            };
            layout.Controls.Add(input);
            return input;
        }

        private static Button ActionButton(string text) => new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(72, 0),
            Text = text
        };
    }
}
