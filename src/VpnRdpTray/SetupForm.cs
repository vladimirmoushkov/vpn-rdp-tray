using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;

namespace VpnRdpTray;

internal sealed class SetupForm : Form
{
    private static readonly string ShareableInstallerPath = Path.Combine(AppContext.BaseDirectory, "shareable-setup.exe");
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly CheckBox startAtSignIn = Check("Start VPN Remote Desktop when I sign in to Windows");
    private readonly TextBox vpnUser = Input();
    private readonly TextBox vpnPassword = Input(secret: true);
    private readonly TextBox otp = Input(secret: true);
    private readonly ComboBox algorithm = Choice("SHA-1", "SHA-256", "SHA-512");
    private readonly TextBox period = Number("30", 3);
    private readonly TextBox rejectionLimit = Number("2", 1);
    private readonly TextBox keyPassword = Input(secret: true);
    private readonly CheckBox removeKeyPassword = Check("Remove saved private key passphrase");
    private readonly CheckBox concatenate = Check("My VPN server expects the password followed by the TOTP code");
    private readonly Label profileStatus = new() { Text = "No profile imported", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TextBox rdpHost = Input("Registered computer name or DNS hostname");
    private readonly TextBox rdpIp = Input("Optional IPv4 or IPv6");
    private readonly TextBox port = Number("3389", 5);
    private readonly TextBox rdpUser = Input();
    private readonly TextBox rdpPassword = Input(secret: true);
    private readonly CheckBox removeRdpPassword = Check("Remove saved Remote Desktop password");
    private readonly ComboBox mode = Choice("Microsoft Entra ID", "Classic Windows");
    private readonly ComboBox entraClient = Choice("Remote Desktop Connection (mstsc)", "Embedded ActiveX prototype");
    private readonly CheckBox hosts = Check("Add the hostname and IP to Windows hosts (administrator approval)");
    private readonly CheckBox multiMonitor = Check("Use all monitors in full screen", isChecked: true);
    private readonly CheckBox redirectSecurityKeys = Check("Smart cards and security keys (YubiKey/FIDO2)", isChecked: true);
    private readonly CheckBox redirectUsbDevices = Check("Supported local USB devices", isChecked: true);
    private readonly CheckBox redirectPrinters = Check("Local printers", isChecked: true);
    private readonly CheckBox redirectClipboard = Check("Clipboard copy and paste", isChecked: true);
    private readonly CheckBox redirectDrives = Check("Local drives and their files", isChecked: true);
    private readonly Label ipHelp = new() { AutoSize = true, MaximumSize = new Size(500, 0) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(580, 0), Anchor = AnchorStyles.Left };
    private readonly Label openVpnStatus = Hint("");
    private readonly Label serviceStatus = Hint("");
    private readonly Label accountStatus = Hint("");
    private readonly Button save = Button("Save");
    private readonly Button cancel = Button("Cancel");
    private readonly Button install = Button("Install OpenVPN…");
    private readonly Button prepare = Button("Prepare this Windows account…");
    private readonly Button saveInstaller = Button("Save installer…");
    private readonly Func<Configuration, Task<string?>> saveConfiguration;
    private readonly Func<bool, bool>? setAutoStart;
    private string? importedProfile, savedTotp;
    private VpnSettings? savedVpn;
    private RdpSettings? savedRdp;
    private bool autoStartEnabled;
    private bool busy;

    internal SetupForm(Configuration? current, Func<Configuration, Task<string?>> saveConfiguration,
        bool autoStart = true, Func<bool, bool>? setAutoStart = null)
    {
        SuspendLayout();
        this.saveConfiguration = saveConfiguration;
        this.setAutoStart = setAutoStart;
        Text = "VPN Remote Desktop Setup";
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(900, 636);
        MinimumSize = new Size(700, 535);
        StartPosition = FormStartPosition.Manual;
        ShowIcon = false;

        vpnUser.AccessibleName = "VPN username";
        vpnPassword.AccessibleName = "VPN password";
        otp.AccessibleName = "TOTP secret";
        rdpHost.AccessibleName = "RDP hostname";
        rdpIp.AccessibleName = "RDP IP";
        rdpUser.AccessibleName = "RDP username";
        rdpPassword.AccessibleName = "RDP password";
        var select = Button("Import .ovpn…");
        select.Click += (_, _) => ImportProfile();
        install.Click += async (_, _) => await UpdateOpenVpnAsync(installing: true);
        prepare.Click += async (_, _) => await UpdateOpenVpnAsync(installing: false);
        saveInstaller.Click += (_, _) => SaveShareableInstaller();
        SetAutoStart(autoStart);
        startAtSignIn.Enabled = setAutoStart is not null;
        startAtSignIn.CheckedChanged += (_, _) => ChangeAutoStart();

        var app = Grid();
        Row(app, "Windows startup", startAtSignIn);
        Row(app, "OpenVPN Community", Inline(openVpnStatus, install));
        Row(app, "OpenVPN Interactive Service", serviceStatus);
        Row(app, "Windows account", Inline(accountStatus, prepare));
        Row(app, "", Hint("The Interactive Service helps bring up the VPN tunnel. Installing OpenVPN or preparing this account requires administrator approval."));
        Row(app, "Shareable installer", saveInstaller);
        AddTab("App and OpenVPN", app);

        var vpn = Grid();
        Row(vpn, "VPN profile", Inline(select, profileStatus));
        Row(vpn, "VPN username", vpnUser);
        Row(vpn, "VPN password", vpnPassword);
        Row(vpn, "", Hint("Enter your regular VPN password. The app appends the generated code when it connects."));
        Row(vpn, "TOTP secret", otp);
        Row(vpn, "", Hint("Copy Secret (BASE32) from your VPN enrollment screen. For Secret (HEX), add hex: before the value."));
        Row(vpn, "", concatenate);
        Row(vpn, "", Hint("Check this once you’ve confirmed that your VPN expects the code immediately after the password."));
        Row(vpn, "", Hint("Automatic VPN sign-in saves the VPN password and TOTP secret for this Windows account. Software running as this account can access both."));
        Row(vpn, "TOTP algorithm", algorithm);
        Row(vpn, "Time step in seconds", period);
        Row(vpn, "Rejection limit", rejectionLimit);
        Row(vpn, "Private key passphrase", keyPassword);
        Row(vpn, "", removeKeyPassword);
        AddTab("VPN Profile", vpn);

        var rdp = Grid();
        Row(rdp, "Authentication", mode);
        Row(rdp, "Entra client", entraClient);
        Row(rdp, "Hostname", rdpHost);
        Row(rdp, "Optional IP address", rdpIp);
        Row(rdp, "", ipHelp);
        Row(rdp, "Port", port);
        Row(rdp, "RDP username", rdpUser);
        Row(rdp, "Classic password", rdpPassword);
        Row(rdp, "", removeRdpPassword);
        Row(rdp, "", hosts);
        Row(rdp, "", multiMonitor);
        Row(rdp, "Redirect to remote PC", redirectSecurityKeys);
        Row(rdp, "", redirectUsbDevices);
        Row(rdp, "", redirectPrinters);
        Row(rdp, "", redirectClipboard);
        Row(rdp, "", redirectDrives);
        Row(rdp, "", Hint("The remote computer can access enabled resources. Clear any you do not need to share."));
        AddTab("Remote Desktop", rdp);

        var footer = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 10, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(status);
        footer.Controls.Add(Inline(save, cancel));
        var frame = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(12) };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        frame.Controls.Add(tabs, 0, 0);
        frame.Controls.Add(footer, 0, 1);
        Controls.Add(frame);

        AcceptButton = save;
        CancelButton = cancel;
        cancel.Click += (_, _) => Close();
        save.Click += async (_, _) => await SaveAsync();
        FormClosing += (_, args) => { if (busy && args.CloseReason == CloseReason.UserClosing) args.Cancel = true; };
        mode.SelectedIndexChanged += (_, _) => UpdateMode();
        vpnUser.TextChanged += (_, _) => UpdateVpnSecretFields();
        removeKeyPassword.CheckedChanged += (_, _) => UpdateVpnSecretFields();
        removeRdpPassword.CheckedChanged += (_, _) => UpdateMode();
        if (current is not null)
            LoadConfiguration(current);
        UpdateVpnSecretFields();
        UpdateMode();
        ReadOpenVpnStatus();
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedIndex == 0 && !busy)
                ReadOpenVpnStatus();
        };

        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(performLayout: false);
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

    private static TextBox Input(string placeholder = "", bool secret = false) => new()
    {
        PlaceholderText = placeholder,
        UseSystemPasswordChar = secret,
        Dock = DockStyle.Fill,
        MaxLength = 1024
    };

    private static Button Button(string title) => new() { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };

    private static CheckBox Check(string title, bool isChecked = false) =>
        new() { Text = title, Checked = isChecked, AutoSize = true, Anchor = AnchorStyles.Left };

    private static Label Hint(string text) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(500, 0), Anchor = AnchorStyles.Left };

    private static TextBox Number(string value, int digits) =>
        new() { Text = value, MaxLength = digits, Dock = DockStyle.Fill };

    private static int ReadNumber(TextBox input, string label, int min, int max)
    {
        if (!int.TryParse(input.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ||
            number < min || number > max)
            throw new SetupException($"Enter a {label} from {min} to {max}.");
        return number;
    }

    private static ComboBox Choice(params string[] items)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        box.Items.AddRange(items);
        box.SelectedIndex = 0;
        return box;
    }

    private static FlowLayoutPanel Inline(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        row.Controls.AddRange(controls);
        return row;
    }

    private static TableLayoutPanel Grid()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(12) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    private void AddTab(string title, Control contents)
    {
        var page = new TabPage(title) { AutoScroll = true };
        page.Controls.Add(contents);
        tabs.TabPages.Add(page);
    }

    private static void Row(TableLayoutPanel layout, string name, Control input)
    {
        int index = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left }, 0, index);
        input.Margin = new Padding(3, 5, 3, 5);
        layout.Controls.Add(input, 1, index);
    }

    private void LoadConfiguration(Configuration current)
    {
        savedVpn = current.Vpn;
        savedRdp = current.Rdp;
        importedProfile = current.Vpn.Profile;
        profileStatus.Text = "Saved profile";
        vpnUser.Text = current.Vpn.Username;
        savedTotp = current.Vpn.TotpSecret;
        otp.PlaceholderText = "Saved secret. Enter a value to replace it.";
        algorithm.SelectedIndex = (int)current.Vpn.Algorithm;
        period.Text = current.Vpn.Period.ToString(CultureInfo.InvariantCulture);
        rejectionLimit.Text = current.Vpn.RejectLimit.ToString(CultureInfo.InvariantCulture);
        concatenate.Checked = true;
        mode.SelectedIndex = current.Rdp.Mode == RdpMode.Entra ? 0 : 1;
        entraClient.SelectedIndex = current.Rdp.EntraActiveX ? 1 : 0;
        rdpHost.Text = current.Rdp.Host;
        rdpIp.Text = current.Rdp.Ip ?? "";
        port.Text = current.Rdp.Port.ToString(CultureInfo.InvariantCulture);
        rdpUser.Text = current.Rdp.Username;
        hosts.Checked = current.Rdp.WindowsHosts;
        multiMonitor.Checked = current.Rdp.MultiMonitor;
        redirectSecurityKeys.Checked = current.Rdp.RedirectSecurityKeys;
        redirectUsbDevices.Checked = current.Rdp.RedirectUsbDevices;
        redirectPrinters.Checked = current.Rdp.RedirectPrinters;
        redirectClipboard.Checked = current.Rdp.RedirectClipboard;
        redirectDrives.Checked = current.Rdp.RedirectDrives;
    }

    private bool IsSavedVpnProfile => savedVpn is not null && importedProfile == savedVpn.Profile;

    private string? SavedVpnPassword(string username) =>
        IsSavedVpnProfile && savedVpn!.Username == username ? savedVpn.Password : null;

    private void UpdateVpnSecretFields()
    {
        vpnPassword.PlaceholderText = SavedVpnPassword(vpnUser.Text.Trim()) is not null
            ? "Saved. Leave blank to keep it." : "Enter the VPN password";
        bool hasSavedPassphrase = IsSavedVpnProfile && !string.IsNullOrEmpty(savedVpn?.KeyPassword);
        removeKeyPassword.Visible = hasSavedPassphrase;
        keyPassword.Enabled = !hasSavedPassphrase || !removeKeyPassword.Checked;
        keyPassword.PlaceholderText = hasSavedPassphrase ? "Saved. Leave blank to keep it." : "Optional for an encrypted key or PKCS#12";
    }

    private void UpdateMode()
    {
        bool entra = mode.SelectedIndex == 0;
        rdpUser.PlaceholderText = entra ? "user@example.invalid" : @"DOMAIN\user or COMPUTER\user";
        bool allowsPassword = !entra && RdpActiveX.PolicyAllowsPassword();
        bool hasSavedPassword = savedRdp?.Mode == RdpMode.Classic && !string.IsNullOrEmpty(savedRdp.Password);
        removeRdpPassword.Visible = allowsPassword && hasSavedPassword;
        rdpPassword.Enabled = allowsPassword && (!hasSavedPassword || !removeRdpPassword.Checked);
        rdpPassword.PlaceholderText = entra ? "Windows handles Entra sign-in" : !allowsPassword ? "Managed by Windows policy" :
            hasSavedPassword ? "Saved. Blank keeps it for the same target and account." : "Optional";
        entraClient.Enabled = entra;
        ipHelp.Text = entra ? "Entra connects by hostname. Use the IP to diagnose reachability or populate Windows hosts."
            : "If hostname connection is unavailable, Windows can use a routed IP with a matching certificate.";
    }

    internal void SetAutoStart(bool enabled)
    {
        autoStartEnabled = enabled;
        startAtSignIn.Checked = enabled;
    }

    private void ChangeAutoStart()
    {
        bool requested = startAtSignIn.Checked;
        if (requested == autoStartEnabled)
            return;

        bool current = setAutoStart?.Invoke(requested) ?? autoStartEnabled;
        SetAutoStart(current);
        if (current == requested)
            SetStatus(requested ? "The app will start when you sign in to Windows." : "Windows sign-in startup is off.");
        else
            SetStatus("Windows could not update the sign-in startup setting.", error: true);
    }

    private void SaveShareableInstaller()
    {
        if (!File.Exists(ShareableInstallerPath))
        {
            SetStatus("The shareable installer is unavailable in this installation.", error: true);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Save a copy of the installer",
            Filter = "Windows setup (*.exe)|*.exe",
            FileName = "VpnRdpTray-Setup-x64.exe",
            DefaultExt = "exe",
            AddExtension = true,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            File.Copy(ShareableInstallerPath, dialog.FileName, overwrite: true);
            SetStatus("Installer saved.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus("Windows could not copy the installer to the selected location.", error: true);
        }
    }

    private void ImportProfile()
    {
        using var open = new OpenFileDialog { Filter = "OpenVPN profiles (*.ovpn)|*.ovpn", Title = "Select the OpenVPN client profile" };
        if (open.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            var profile = OpenVpnProfile.Import(open.FileName, (directive, file) =>
            {
                using var attachment = new OpenFileDialog
                {
                    Title = $"Select the file referenced by {directive}",
                    InitialDirectory = Path.GetDirectoryName(open.FileName),
                    FileName = file,
                    CheckFileExists = true
                };
                return attachment.ShowDialog(this) == DialogResult.OK ? attachment.FileName : null;
            });
            importedProfile = profile;
            removeKeyPassword.Checked = false;
            UpdateVpnSecretFields();
            profileStatus.Text = "Profile imported";
            SetStatus("");
        }
        catch (OperationCanceledException) { SetStatus("Profile import canceled."); }
        catch (SetupException ex) { SetStatus(ex.Message, error: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { SetStatus("Windows could not open the selected profile or its files.", error: true); }
    }

    private async Task UpdateOpenVpnAsync(bool installing)
    {
        SetBusy(true);
        try
        {
            if (installing)
            {
                SetStatus("Downloading OpenVPN. Windows will ask for administrator approval to install it.");
                await MachineSetup.InstallOpenVpnAsync();
                SetStatus("OpenVPN is installed. Prepare this Windows account to continue.");
            }
            else
            {
                SetStatus("Windows will ask for administrator approval to prepare the profile launcher.");
                await MachineSetup.ProvisionAsync();
                SetStatus("OpenVPN is ready. Switch to VPN Profile to continue.");
            }
        }
        catch (SetupException ex) { SetStatus(ex.Message, error: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or Win32Exception)
        { SetStatus("Windows could not prepare OpenVPN. Try again or ask your administrator.", error: true); }
        finally { ReadOpenVpnStatus(); SetBusy(false); }
    }

    private void ReadOpenVpnStatus()
    {
        bool serviceRunning = OpenVpnSession.ServiceProcessId() != 0;
        serviceStatus.Text = serviceRunning ? "Running" : "Stopped or unavailable";
        OpenVpnInstall dependency;
        try
        {
            dependency = OpenVpnInstall.Get();
        }
        catch (Exception ex) when (ex is SetupException or IOException or UnauthorizedAccessException)
        {
            install.Enabled = true;
            prepare.Enabled = false;
            openVpnStatus.Text = "Install required";
            accountStatus.Text = "Install OpenVPN first";
            return;
        }

        bool provisioned;
        try { provisioned = dependency.IsProvisioned; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { provisioned = false; }
        install.Enabled = false;
        openVpnStatus.Text = "Installed";
        prepare.Enabled = !provisioned || !serviceRunning;
        accountStatus.Text = provisioned ? "Prepared" : "Preparation required";
    }

    private async Task SaveAsync()
    {
        SetBusy(true);
        bool close = false;
        Control? invalidInput = null;
        try
        {
            tabs.SelectedIndex = 1;
            if (importedProfile is null)
                throw new SetupException("Import an OpenVPN client profile.");
            if (!concatenate.Checked)
            {
                invalidInput = concatenate;
                throw new SetupException("Check the password-plus-TOTP confirmation below the TOTP secret once you’ve confirmed the sign-in format.");
            }
            string vpnUsername = SetupInput.Credential(vpnUser.Text.Trim(), "VPN username", 120);
            invalidInput = vpnPassword;
            string passwordForVpn = SetupInput.Credential(vpnPassword.TextLength == 0 ? SavedVpnPassword(vpnUsername) ?? "" : vpnPassword.Text,
                "VPN password", 114);
            invalidInput = otp;
            string secret = otp.Text.Length == 0 && savedTotp is not null ? savedTotp : Totp.NormalizeSecret(otp.Text);
            invalidInput = period;
            int timeStep = ReadNumber(period, "time step in seconds", 10, 300);
            invalidInput = keyPassword;
            string? passphrase = ReadOptionalSecret(keyPassword, IsSavedVpnProfile ? savedVpn?.KeyPassword : null,
                removeKeyPassword.Checked, "VPN private key passphrase", 120);
            invalidInput = null;
            var vpn = new VpnSettings(importedProfile, vpnUsername, passwordForVpn, secret,
                (TotpAlgorithm)algorithm.SelectedIndex, timeStep,
                ReadNumber(rejectionLimit, "rejection limit", 1, 5), passphrase);

            tabs.SelectedIndex = 2;
            string host = SetupInput.Host(rdpHost.Text);
            string? ip = SetupInput.Ip(rdpIp.Text);
            bool entra = mode.SelectedIndex == 0;
            string user = SetupInput.Credential(rdpUser.Text.Trim(), "RDP username", 512);
            if (entra && (user.Count(c => c == '@') != 1 || user.StartsWith('@') || user.EndsWith('@') ||
                user.Any(c => char.IsWhiteSpace(c) || c == '\\')))
                throw new SetupException("Enter the Microsoft Entra account’s user principal name, such as user@example.invalid.");
            if (hosts.Checked && ip is null)
                throw new SetupException("Enter the IP address for the Windows hosts entry.");
            int rdpPort = string.IsNullOrWhiteSpace(port.Text) ? 3389 : ReadNumber(port, "Remote Desktop port", 1, 65535);
            string? password = null;
            if (!entra && RdpActiveX.PolicyAllowsPassword())
            {
                bool matchesSavedRdp = savedRdp?.Mode == RdpMode.Classic && savedRdp.Port == rdpPort && savedRdp.Ip == ip &&
                    savedRdp.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && savedRdp.Username.Equals(user, StringComparison.OrdinalIgnoreCase);
                invalidInput = rdpPassword;
                password = ReadOptionalSecret(rdpPassword, matchesSavedRdp ? savedRdp?.Password : null,
                    removeRdpPassword.Checked, "RDP password", 512);
                invalidInput = null;
            }
            var rdp = new RdpSettings(host, ip, rdpPort, user, entra ? RdpMode.Entra : RdpMode.Classic,
                password, hosts.Checked, entra && entraClient.SelectedIndex == 1)
            {
                MultiMonitor = multiMonitor.Checked,
                RedirectSecurityKeys = redirectSecurityKeys.Checked,
                RedirectUsbDevices = redirectUsbDevices.Checked,
                RedirectPrinters = redirectPrinters.Checked,
                RedirectClipboard = redirectClipboard.Checked,
                RedirectDrives = redirectDrives.Checked
            };

            SetStatus(hosts.Checked && !MachineSetup.HostsMatch(rdp)
                ? "Windows will ask for administrator approval to update the hosts file." : "Saving setup…");
            var warning = await saveConfiguration(new Configuration(vpn, rdp));
            if (warning is not null)
            {
                SetStatus(warning, error: true);
                return;
            }
            close = true;
        }
        catch (SetupException ex) { SetStatus(ex.Message, error: true); }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException or Win32Exception)
        { SetStatus("Windows could not save the configuration. Check access to your Windows profile.", error: true); }
        finally
        {
            SetBusy(false);
            if (close)
                Close();
            else
                invalidInput?.Focus();
        }
    }

    private static string? ReadOptionalSecret(TextBox input, string? saved, bool remove, string label, int maximumBytes)
    {
        if (remove)
            return null;

        string? value = input.TextLength == 0 ? saved : input.Text;
        return string.IsNullOrEmpty(value) ? null : SetupInput.Credential(value, label, maximumBytes, required: false);
    }

    private void SetBusy(bool value)
    {
        busy = value;
        tabs.Enabled = !value;
        save.Enabled = !value;
        cancel.Enabled = !value;
        UseWaitCursor = value;
    }

    private void SetStatus(string message, bool error = false)
    {
        status.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
        status.Text = message;
    }
}
