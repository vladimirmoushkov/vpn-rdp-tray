using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;

namespace VpnRdpTray;

internal sealed class SetupForm : Form
{
    private static readonly string ShareableInstallerPath = Path.Combine(AppContext.BaseDirectory, "shareable-setup.exe");
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly CheckBox startAtSignIn = Check("Start VPN Remote Desktop when I sign in to Windows");
    private readonly TextBox vpnUser = Input();
    private readonly CheckBox saveVpnPassword = Check("Save VPN password on this PC");
    private readonly TextBox vpnPassword = Input(secret: true);
    private readonly CheckBox automaticTotp = Check("Generate VPN codes automatically using a saved TOTP seed");
    private readonly TextBox totpSeed = Input(secret: true);
    private readonly ComboBox totpAlgorithm = Choice("SHA-1", "SHA-256", "SHA-512");
    private readonly TextBox totpPeriod = Number("30", 3);
    private readonly TextBox rejectionLimit = Number("2", 1);
    private readonly CheckBox saveKeyPassword = Check("Save private key passphrase on this PC");
    private readonly TextBox keyPassword = Input(secret: true);
    private readonly CheckBox concatenate = Check("My VPN server expects the password followed by the TOTP code");
    private readonly Label profileStatus = new() { Text = "No profile imported", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TextBox rdpHost = Input("Registered computer name or DNS hostname");
    private readonly TextBox rdpIp = Input("Optional IPv4 or IPv6");
    private readonly TextBox port = Number("3389", 5);
    private readonly TextBox rdpUser = Input();
    private readonly CheckBox saveRdpPassword = Check("Save classic Remote Desktop password on this PC");
    private readonly TextBox rdpPassword = Input(secret: true);
    private readonly ComboBox mode = Choice("Microsoft Entra ID", "Classic Windows");
    private readonly ComboBox entraClient = Choice("Remote Desktop Connection (mstsc)", "Embedded ActiveX prototype");
    private readonly CheckBox hosts = Check("Add the hostname and IP to Windows hosts (administrator approval)");
    private readonly CheckBox multiMonitor = Check("Use all monitors in full screen", isChecked: true);
    private readonly CheckBox simulateActivity = Check("Simulate activity (embedded Remote Desktop only)");
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
    private readonly Button selectProfile = Button("Import .ovpn…");
    private readonly Button install = Button("Install OpenVPN…");
    private readonly Button prepare = Button("Prepare this Windows account…");
    private readonly Button saveInstaller = Button("Save installer…");
    private readonly Func<Configuration, Task<string?>> saveConfiguration;
    private readonly Func<bool, bool>? setAutoStart;
    private string? importedProfile;
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
        Text = "VPN Remote Desktop Settings";
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(900, 636);
        MinimumSize = new Size(700, 535);
        StartPosition = FormStartPosition.Manual;
        ShowIcon = false;

        vpnUser.AccessibleName = "VPN username";
        vpnPassword.AccessibleName = "VPN password";
        totpSeed.AccessibleName = "TOTP seed";
        totpAlgorithm.AccessibleName = "TOTP algorithm";
        totpPeriod.AccessibleName = "TOTP period in seconds";
        keyPassword.AccessibleName = "VPN private key passphrase";
        rdpHost.AccessibleName = "RDP hostname";
        rdpIp.AccessibleName = "RDP IP";
        rdpUser.AccessibleName = "RDP username";
        rdpPassword.AccessibleName = "RDP password";
        selectProfile.Click += (_, _) => ImportProfile();
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
        Row(vpn, "VPN profile", Inline(selectProfile, profileStatus));
        Row(vpn, "VPN username", vpnUser);
        Row(vpn, "", saveVpnPassword);
        Row(vpn, "VPN password", vpnPassword);
        Row(vpn, "", concatenate);
        Row(vpn, "", Hint("Check this once you’ve confirmed that your VPN expects the code immediately after the password."));
        Row(vpn, "", automaticTotp);
        Row(vpn, "TOTP seed", totpSeed);
        Row(vpn, "TOTP algorithm", totpAlgorithm);
        Row(vpn, "TOTP period (seconds)", totpPeriod);
        Row(vpn, "", Hint("Use the enrollment seed in Base32 or prefix a hexadecimal seed with hex:. Clear the checkbox and save to remove the seed and use manual codes."));
        Row(vpn, "Rejection limit", rejectionLimit);
        Row(vpn, "", saveKeyPassword);
        Row(vpn, "Private key passphrase", keyPassword);
        Row(vpn, "", Hint("Windows encrypts saved credentials for this Windows account. Clear a save checkbox and click Save to remove that credential."));
        AddTab("VPN Profile", vpn);

        var rdp = Grid();
        Row(rdp, "Authentication", mode);
        Row(rdp, "Entra client", entraClient);
        Row(rdp, "Hostname", rdpHost);
        Row(rdp, "Optional IP address", rdpIp);
        Row(rdp, "", ipHelp);
        Row(rdp, "Port", port);
        Row(rdp, "RDP username", rdpUser);
        Row(rdp, "", saveRdpPassword);
        Row(rdp, "Classic password", rdpPassword);
        Row(rdp, "", hosts);
        Row(rdp, "", multiMonitor);
        Row(rdp, "", simulateActivity);
        Row(rdp, "", Hint("Sends a brief Shift press to the signed-in desktop every 5–65 seconds. Remote server session limits still apply."));
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
        entraClient.SelectedIndexChanged += (_, _) => UpdateMode();
        vpnUser.TextChanged += (_, _) => UpdateVpnSecretFields();
        saveVpnPassword.CheckedChanged += (_, _) => UpdateVpnSecretFields();
        automaticTotp.CheckedChanged += (_, _) => UpdateVpnSecretFields();
        saveKeyPassword.CheckedChanged += (_, _) => UpdateVpnSecretFields();
        saveRdpPassword.CheckedChanged += (_, _) => UpdateMode();
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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        totpSeed.Clear();
        base.OnFormClosed(e);
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
        saveVpnPassword.Checked = !string.IsNullOrEmpty(current.Vpn.Password);
        automaticTotp.Checked = current.Vpn.ProtectedTotpSeed is { Length: > 0 };
        totpAlgorithm.SelectedIndex = Enum.IsDefined(current.Vpn.TotpAlgorithm) ? (int)current.Vpn.TotpAlgorithm : 0;
        totpPeriod.Text = current.Vpn.TotpPeriod.ToString(CultureInfo.InvariantCulture);
        saveKeyPassword.Checked = !string.IsNullOrEmpty(current.Vpn.KeyPassword);
        rejectionLimit.Text = current.Vpn.RejectLimit.ToString(CultureInfo.InvariantCulture);
        concatenate.Checked = true;
        mode.SelectedIndex = current.Rdp.Mode == RdpMode.Entra ? 0 : 1;
        saveRdpPassword.Checked = current.Rdp.Mode == RdpMode.Classic && RdpActiveX.PolicyAllowsPassword() &&
            !string.IsNullOrEmpty(current.Rdp.Password);
        entraClient.SelectedIndex = current.Rdp.EntraActiveX ? 1 : 0;
        rdpHost.Text = current.Rdp.Host;
        rdpIp.Text = current.Rdp.Ip ?? "";
        port.Text = current.Rdp.Port.ToString(CultureInfo.InvariantCulture);
        rdpUser.Text = current.Rdp.Username;
        hosts.Checked = current.Rdp.WindowsHosts;
        multiMonitor.Checked = current.Rdp.MultiMonitor;
        simulateActivity.Checked = current.Rdp.SimulateActivity;
        redirectSecurityKeys.Checked = current.Rdp.RedirectSecurityKeys;
        redirectUsbDevices.Checked = current.Rdp.RedirectUsbDevices;
        redirectPrinters.Checked = current.Rdp.RedirectPrinters;
        redirectClipboard.Checked = current.Rdp.RedirectClipboard;
        redirectDrives.Checked = current.Rdp.RedirectDrives;
    }

    private bool IsSavedVpnProfile => savedVpn is not null && importedProfile == savedVpn.Profile;

    private string? SavedVpnPassword(string username) =>
        IsSavedVpnProfile && savedVpn!.Username == username ? savedVpn.Password : null;

    private byte[]? SavedTotpSeed(string username) =>
        IsSavedVpnProfile && savedVpn!.Username == username ? savedVpn.ProtectedTotpSeed : null;

    private void UpdateVpnSecretFields()
    {
        UpdateSecretField(vpnPassword, saveVpnPassword.Checked,
            !string.IsNullOrEmpty(SavedVpnPassword(vpnUser.Text.Trim())) ? "Saved. Leave blank to keep it." : "Enter the VPN password",
            "Enter at VPN sign-in");
        UpdateSecretField(totpSeed, automaticTotp.Checked,
            SavedTotpSeed(vpnUser.Text.Trim()) is { Length: > 0 } ? "Saved securely. Leave blank to keep it." : "Enter the enrollment seed",
            "Enter a code at VPN sign-in");
        totpAlgorithm.Enabled = totpPeriod.Enabled = automaticTotp.Checked;
        UpdateSecretField(keyPassword, saveKeyPassword.Checked,
            IsSavedVpnProfile && !string.IsNullOrEmpty(savedVpn?.KeyPassword) ? "Saved. Leave blank to keep it." : "For an encrypted key or PKCS#12",
            "Enter if OpenVPN needs it");
    }

    private void UpdateMode()
    {
        bool entra = mode.SelectedIndex == 0;
        rdpUser.PlaceholderText = entra ? "user@example.invalid" : @"DOMAIN\user or COMPUTER\user";
        bool allowsPassword = !entra && RdpActiveX.PolicyAllowsPassword();
        saveRdpPassword.Enabled = allowsPassword;
        UpdateSecretField(rdpPassword, allowsPassword && saveRdpPassword.Checked,
            savedRdp?.Mode == RdpMode.Classic && !string.IsNullOrEmpty(savedRdp.Password)
                ? "Saved. Blank keeps it for the same target and account." : "Enter the Remote Desktop password",
            entra ? "Windows handles Entra sign-in" : "Windows handles Remote Desktop sign-in");
        entraClient.Enabled = entra;
        simulateActivity.Enabled = !entra || entraClient.SelectedIndex == 1;
        ipHelp.Text = entra ? "Entra connects by hostname. Use the IP to diagnose reachability or populate Windows hosts."
            : "If hostname connection is unavailable, Windows can use a routed IP with a matching certificate.";
    }

    private static void UpdateSecretField(TextBox input, bool saveSecret, string enabledHint, string disabledHint)
    {
        input.Enabled = saveSecret;
        input.PlaceholderText = saveSecret ? enabledHint : disabledHint;
        if (!saveSecret)
            input.Clear();
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
            SetStatus("This installation has no shareable copy. Use the original installer you downloaded.", error: true);
            return;
        }

        try
        {
            using var dialog = new SaveFileDialog
            {
                Title = "Save a copy of the installer",
                Filter = "Windows installer (*.exe)|*.exe",
                FileName = "VpnRdpTray-Setup-x64.exe",
                DefaultExt = "exe",
                AddExtension = true,
                RestoreDirectory = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            File.Copy(ShareableInstallerPath, dialog.FileName, overwrite: true);
            SetStatus("Installer saved.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        { SetStatus("Windows denied access while saving the installer. Choose a folder you can write to.", error: true); }
        catch (Exception ex) when (ex is IOException or ArgumentException or ExternalException)
        {
            SetStatus("Windows could not save the installer. Check free space, close any open copy, or choose a different location.", error: true);
        }
    }

    private void ImportProfile()
    {
        try
        {
            using var open = new OpenFileDialog { Filter = "OpenVPN profiles (*.ovpn)|*.ovpn", Title = "Select the OpenVPN client profile" };
            if (open.ShowDialog(this) != DialogResult.OK)
                return;
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
            UpdateVpnSecretFields();
            profileStatus.Text = "Profile imported";
            SetStatus("");
        }
        catch (OperationCanceledException) { SetStatus("Profile import canceled. Choose Import .ovpn to select the profile and its required files."); }
        catch (SetupException ex) { SetStatus(ex.Message, error: true); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        { SetStatus("Windows denied access to a VPN profile file. Select a copy you can read.", error: true); }
        catch (Exception ex) when (ex is IOException or ArgumentException or ExternalException)
        { SetStatus("Windows could not open a VPN profile file. Check that the profile and its referenced files are available, then import again.", error: true); }
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
        catch (OperationCanceledException)
        {
            SetStatus(installing ? "The OpenVPN download timed out. Check your internet connection and try Install OpenVPN again."
                : "OpenVPN account preparation was interrupted. Choose Prepare this Windows account to try again.", error: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        { SetStatus("Windows denied access while preparing OpenVPN. Ask your administrator to install or prepare OpenVPN.", error: true); }
        catch (IOException)
        {
            SetStatus(installing ? "Windows could not stage the OpenVPN installer. Check available disk space and try Install OpenVPN again."
                : "Windows could not read the OpenVPN configuration. Ask your administrator to check the installation and folder access.", error: true);
        }
        catch (Win32Exception ex)
        {
            var operation = installing ? "start OpenVPN installation" : "prepare the OpenVPN profile launcher";
            SetStatus($"Windows could not {operation} (code {ex.NativeErrorCode}). Share this code with your administrator.", error: true);
        }
        finally { SetBusy(false); ReadOpenVpnStatus(); }
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
        catch (SetupException ex)
        {
            install.Enabled = true;
            prepare.Enabled = false;
            openVpnStatus.Text = ex.Message;
            accountStatus.Text = "Install OpenVPN first";
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or Win32Exception)
        {
            install.Enabled = prepare.Enabled = false;
            openVpnStatus.Text = "Windows could not check the OpenVPN installation. Ask your administrator to check access.";
            accountStatus.Text = "Preparation status unavailable";
            return;
        }

        bool provisioned;
        try { provisioned = dependency.IsProvisioned; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            install.Enabled = prepare.Enabled = false;
            openVpnStatus.Text = "Installed";
            accountStatus.Text = "Windows could not read the launcher. Ask your administrator to check the OpenVPN configuration folder.";
            return;
        }
        install.Enabled = false;
        openVpnStatus.Text = "Installed";
        prepare.Enabled = !provisioned || !serviceRunning;
        accountStatus.Text = !provisioned ? "Choose Prepare this Windows account to enable VPN access."
            : serviceRunning ? "Prepared" : "Choose Prepare this Windows account to start the service.";
    }

    private async Task SaveAsync()
    {
        SetBusy(true);
        bool close = false;
        Control? invalidInput = null;
        try
        {
            tabs.SelectedIndex = 1;
            invalidInput = selectProfile;
            if (importedProfile is null)
                throw new SetupException("Choose Import .ovpn and select your OpenVPN client profile.");
            OpenVpnProfile.VerifySavedServerIdentity(importedProfile);
            if (!concatenate.Checked)
            {
                invalidInput = concatenate;
                throw new SetupException("Confirm that your VPN expects the password followed by a six-digit code.");
            }
            invalidInput = vpnUser;
            string vpnUsername = SetupInput.Credential(vpnUser.Text.Trim(), "VPN username", 120);
            invalidInput = vpnPassword;
            string? passwordForVpn = ReadSavedSecret(vpnPassword, SavedVpnPassword(vpnUsername),
                saveVpnPassword.Checked, "VPN password", 114);
            invalidInput = keyPassword;
            string? passphrase = ReadSavedSecret(keyPassword, IsSavedVpnProfile ? savedVpn?.KeyPassword : null,
                saveKeyPassword.Checked, "VPN private key passphrase", 120);
            invalidInput = totpSeed;
            byte[]? protectedSeed = ReadProtectedTotpSeed(vpnUsername);
            invalidInput = protectedSeed is not null ? totpPeriod : rejectionLimit;
            int period = protectedSeed is not null ? ReadNumber(totpPeriod, "TOTP period", Totp.MinPeriod, Totp.MaxPeriod) : 30;
            invalidInput = rejectionLimit;
            var vpn = new VpnSettings(importedProfile, vpnUsername, passwordForVpn,
                ReadNumber(rejectionLimit, "rejection limit", 1, 5), passphrase)
            {
                ProtectedTotpSeed = protectedSeed,
                TotpAlgorithm = protectedSeed is not null ? (TotpAlgorithm)totpAlgorithm.SelectedIndex : TotpAlgorithm.Sha1,
                TotpPeriod = period
            };

            tabs.SelectedIndex = 2;
            invalidInput = rdpHost;
            string host = SetupInput.Host(rdpHost.Text);
            invalidInput = rdpIp;
            string? ip = SetupInput.Ip(rdpIp.Text);
            if (hosts.Checked && ip is null)
                throw new SetupException("Enter the IP address for the Windows hosts entry, or clear Add the hostname and IP to Windows hosts.");
            bool entra = mode.SelectedIndex == 0;
            invalidInput = rdpUser;
            string user = SetupInput.Credential(rdpUser.Text.Trim(), "RDP username", 512);
            if (entra && (user.Count(c => c == '@') != 1 || user.StartsWith('@') || user.EndsWith('@') ||
                user.Any(c => char.IsWhiteSpace(c) || c == '\\')))
                throw new SetupException("Enter the Microsoft Entra account’s user principal name, such as user@example.invalid.");
            invalidInput = port;
            int rdpPort = string.IsNullOrWhiteSpace(port.Text) ? 3389 : ReadNumber(port, "Remote Desktop port", 1, 65535);
            string? password = null;
            if (!entra && RdpActiveX.PolicyAllowsPassword())
            {
                bool matchesSavedRdp = savedRdp?.Mode == RdpMode.Classic && savedRdp.Port == rdpPort && savedRdp.Ip == ip &&
                    savedRdp.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && savedRdp.Username.Equals(user, StringComparison.OrdinalIgnoreCase);
                invalidInput = rdpPassword;
                password = ReadSavedSecret(rdpPassword, matchesSavedRdp ? savedRdp?.Password : null,
                    saveRdpPassword.Checked, "RDP password", 512);
            }
            invalidInput = null;
            var rdp = new RdpSettings(host, ip, rdpPort, user, entra ? RdpMode.Entra : RdpMode.Classic,
                password, hosts.Checked, entra && entraClient.SelectedIndex == 1)
            {
                MultiMonitor = multiMonitor.Checked,
                SimulateActivity = simulateActivity.Checked,
                RedirectSecurityKeys = redirectSecurityKeys.Checked,
                RedirectUsbDevices = redirectUsbDevices.Checked,
                RedirectPrinters = redirectPrinters.Checked,
                RedirectClipboard = redirectClipboard.Checked,
                RedirectDrives = redirectDrives.Checked
            };

            SetStatus(hosts.Checked || savedRdp?.WindowsHosts == true
                ? "Checking Windows hosts settings. Approve the administrator prompt if Windows asks." : "Saving settings…");
            var warning = await saveConfiguration(new Configuration(vpn, rdp));
            if (warning is not null)
            {
                SetStatus(warning, error: true);
                return;
            }
            close = true;
        }
        catch (SetupException ex) { SetStatus(ex.Message, error: true); }
        catch (CryptographicException)
        { SetStatus("Windows could not protect the saved settings. Sign in to your normal Windows account and try Save again.", error: true); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        { SetStatus("Windows denied access while saving settings. Ask your administrator to check permissions on your Windows profile.", error: true); }
        catch (IOException)
        { SetStatus("Windows could not write the settings. Check free disk space and try Save again.", error: true); }
        catch (Win32Exception ex)
        { SetStatus($"Windows could not finish saving settings (code {ex.NativeErrorCode}). Share this code with your administrator.", error: true); }
        finally
        {
            SetBusy(false);
            if (close)
                Close();
            else
                invalidInput?.Focus();
        }
    }

    private byte[]? ReadProtectedTotpSeed(string username)
    {
        if (!automaticTotp.Checked)
            return null;
        if (totpSeed.TextLength == 0)
            return SavedTotpSeed(username) is { Length: > 0 } saved
                ? saved : throw new SetupException("Enter the TOTP enrollment seed or turn off automatic VPN codes.");

        byte[] seed = Totp.NormalizeSeed(totpSeed.Text);
        try { return ProtectedData.Protect(seed, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    private static string? ReadSavedSecret(TextBox input, string? saved, bool saveSecret, string label, int maximumBytes)
    {
        if (!saveSecret)
            return null;

        string? value = input.TextLength == 0 ? saved : input.Text;
        if (string.IsNullOrWhiteSpace(value))
            throw new SetupException($"Enter the {label} or clear its save checkbox.");
        return SetupInput.Credential(value, label, maximumBytes);
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
