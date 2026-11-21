using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using VpnRdpTray;

internal static class Program
{
    private static readonly VpnSettings TestVpn = new("imported-key-fixture", "test-user", "test-vpn-password", 2, "test-key-passphrase");
    private static readonly RdpSettings TestRdp = new("prototype.invalid", "192.0.2.42", 3390,
        "prototype@example.invalid", RdpMode.Entra, "test-rdp-password", false, false);

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        TestVpnSignInAsync().GetAwaiter().GetResult();
        TestConfiguredTotpAsync().GetAwaiter().GetResult();
        TestVpnProgressAsync().GetAwaiter().GetResult();
        TestProfiles();
        TestDpapi();
        TestPreferences();
        TestInstallerPath();
        TestProtocolClassification();
        TestTransportAsync().GetAwaiter().GetResult();
        TestRdpProbeResults();
        TestRdpSettings();
        TestRdpActivitySchedule();
        TestNativeRedirectionSettings();
        TestWindowGlow();
        if (args is ["--native-client"])
            TestNativeClient();
        if (args is ["--interactive-service"])
            TestServiceAsync().GetAwaiter().GetResult();
        // Creating the fixture controls installs a WinForms synchronization context.
        TestWakeNotifications();
        TestRdpPointerActivity();
        return RdpPrototype.CheckLocalControl();
    }

    private static void TestRdpActivitySchedule()
    {
        var activity = new RdpActivitySchedule(1_000, focused: true);
        Require(!activity.CanSend(true, 60_999, 60_000), "Wait a full minute after sign-in");
        Require(!activity.CanSend(true, 61_000, 1), "Focused RDP defers while the user is active");
        Require(!activity.CanSend(true, 61_000, 59_999), "Require a full minute since focused keyboard or mouse input");
        Require(!activity.CanSend(true, 61_000, null), "Skip focused RDP if local idle time is unavailable");
        Require(activity.CanSend(true, 61_000, 60_000), "Allow an idle focused session after a minute");
        activity.RecordAttempt(61_000);
        Require(!activity.CanSend(true, 120_999, 119_999), "Limit sequential taps to one per minute");

        activity.ObserveFocus(false, 100_000);
        Require(!activity.CanSend(false, 159_999, null), "Wait a full minute after switching away");
        Require(activity.CanSend(false, 160_000, null), "Allow RDP that has stayed in the background for a minute");
        activity.ObserveFocus(true, 160_001);
        activity.ObserveFocus(false, 160_002);
        Require(!activity.CanSend(false, 161_000, null), "A brief focus return resets the quiet window between timer ticks");
        Require(!activity.CanSend(null, 230_000, null), "Skip when Windows cannot identify the foreground window");
        Require(!activity.CanSend(false, 230_001, null), "Resume the quiet window when the foreground becomes known");

        const uint systemTickAfterRollover = 2_000;
        var rollover = new RdpActivitySchedule(1_000, focused: true);
        int idleAcrossRollover = RdpActivitySchedule.InputElapsed(systemTickAfterRollover,
            unchecked(systemTickAfterRollover - 60_000u));
        Require(rollover.CanSend(true, 121_000, idleAcrossRollover),
            "The separate Windows last-input clock can wrap during the idle interval");
        int recentInput = RdpActivitySchedule.InputElapsed(systemTickAfterRollover, systemTickAfterRollover + 1);
        Require(!rollover.CanSend(true, 121_000, recentInput),
            "A concurrent input timestamp after the timer sample is treated as recent");
        Console.WriteLine("RDP activity waits for the quiet period, honors focus transitions and handles Windows tick rollover.");
    }

    private static void TestRdpPointerActivity()
    {
        using var window = new Form { ShowInTaskbar = false, ClientSize = new(660, 500) };
        using var host = new Panel { Size = new(640, 480) };
        using var child = new Panel { Bounds = new(10, 8, 620, 460) };
        window.Controls.Add(host);
        host.Controls.Add(child);
        nint hostHandle = host.Handle;
        Size desktop = new(1280, 960);
        var pointer = new RdpPointerActivity();
        var moves = new List<Point>();
        bool Collect(Point point)
        {
            moves.Add(point);
            return true;
        }
        static Message Mouse(nint window, int x, int y, nint buttons = 0) =>
            Message.Create(window, RdpPointerActivity.MouseMove, buttons, (nint)(x | (y << 16)));

        pointer.Move(desktop, Collect);
        pointer.Observe(hostHandle, host.ClientSize, desktop, Mouse(window.Handle, 42, 27));
        pointer.Observe(hostHandle, host.ClientSize, desktop, Mouse(child.Handle, 42, 27, buttons: 1));
        pointer.Move(desktop, Collect);
        Require(moves.Count == 0, "Skip unobserved targets, other windows and mouse drags");

        pointer.Observe(hostHandle, host.ClientSize, desktop, Mouse(child.Handle, 42, 27));
        pointer.Move(new Size(1920, 1080), Collect);
        Require(moves.Count == 0, "Desktop resizing invalidates the previous pointer coordinates");
        pointer.Observe(hostHandle, host.ClientSize, desktop, Mouse(child.Handle, 42, 27));
        pointer.Move(desktop, Collect);
        pointer.Move(desktop, Collect);
        Require(moves.SequenceEqual(new[] { new Point(105, 70), new Point(104, 70) }),
            "Project a real child-window pointer through smart sizing and alternate by one remote pixel");
        pointer.Observe(hostHandle, host.ClientSize, desktop, Mouse(hostHandle, 0, 0));
        pointer.Move(desktop, Collect);
        Require(moves.Count == 2, "Keep simulated movement away from desktop edges");
        Console.WriteLine("RDP pointer target scoping, smart sizing and one-pixel movement passed.");
    }

    private static async Task TestVpnSignInAsync()
    {
        Require(VpnAuthentication.IsValidCode("000042") && !VpnAuthentication.IsValidCode("12345") &&
            !VpnAuthentication.IsValidCode("1234567") && !VpnAuthentication.IsValidCode("１２３４５６") &&
            !VpnAuthentication.IsValidCode("12345x"), "A code has exactly six ASCII digits and may begin with zeros");

        const string authRequest = ">PASSWORD:Need 'Auth' username/password\n";
        const string usernameAccepted = "SUCCESS: 'Auth' username entered, but not yet verified\n";
        const string keyRequest = ">PASSWORD:Need 'Private Key' password\n";
        static StreamReader Responses(string messages) => new(new MemoryStream(Encoding.UTF8.GetBytes(messages)));
        using var output = new MemoryStream();
        using var startup = new CancellationTokenSource();
        var settings = TestVpn with { Password = null, KeyPassword = null };
        using (var input = Responses(keyRequest + authRequest + usernameAccepted + authRequest))
        {
            var outcome = await OpenVpnSession.MonitorAsync(input, output, settings,
                _ => Task.FromResult<VpnAuthentication?>(new("fixture-pass", "000042")),
                _ => Task.FromResult<string?>("fixture-key"), _ => { }, startup, startup.Token);
            Require(outcome.Kind == FailureKind.CredentialRejected, "Block a second initial credential submission");
            string sent = Encoding.UTF8.GetString(output.ToArray());
            Require(sent == "password \"Private Key\" \"fixture-key\"\nusername \"Auth\" \"test-user\"\npassword \"Auth\" \"fixture-pass000042\"\n",
                "Send the manually entered secrets once with the original leading zeroes");
        }

        output.SetLength(0);
        using (var input = Responses(authRequest))
        {
            var outcome = await OpenVpnSession.MonitorAsync(input, output, TestVpn,
                _ => Task.FromResult<VpnAuthentication?>(null), _ => throw new InvalidOperationException(),
                _ => { }, startup, startup.Token);
            Require(outcome.Kind == FailureKind.ManualRetry && output.Length == 0, "Cancel sends no credentials and pauses for a manual retry");
        }

        using (var input = Responses(authRequest + usernameAccepted + "SUCCESS: 'Auth' password entered, but not yet verified\n"))
        {
            var progress = new List<VpnProgress>();
            var outcome = await OpenVpnSession.MonitorAsync(input, output, TestVpn,
                _ => Task.FromResult<VpnAuthentication?>(new(null, "000042")), _ => throw new InvalidOperationException(),
                _ => { }, startup, startup.Token, new OpenVpnSession.AttemptProgress(progress.Add));
            Require(Encoding.UTF8.GetString(output.ToArray()).EndsWith("test-vpn-password000042\"\n", StringComparison.Ordinal),
                "The saved-password path still requires a fresh code");
            Require(progress.Any(stage => stage.Name == "VPN handing sign-in to OpenVPN") &&
                progress.Any(stage => stage.Name == "VPN starting server connection") &&
                outcome.Reason.Contains("did not report starting", StringComparison.Ordinal),
                "Observe the local acknowledgement without claiming the VPN server authenticated");
        }
        Console.WriteLine("Manual VPN code validation, password handling, cancellation and retry protection passed.");
    }

    private static async Task TestConfiguredTotpAsync()
    {
        var seed = Totp.NormalizeSeed("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
        var hexadecimal = Totp.NormalizeSeed("hex:3132333435363738393031323334353637383930");
        byte[] protectedSeed;
        try
        {
            Require(seed.SequenceEqual(hexadecimal), "Base32 and hexadecimal enrollment formats decode identically");
            Require(Totp.Generate(seed, TotpAlgorithm.Sha1, 1, 8) == "94287082" &&
                Totp.Generate(seed, TotpAlgorithm.Sha1, 37_037_036) == "081804", "RFC 6238 counters and leading zeros");
            Require(Totp.Generate("12345678901234567890123456789012"u8, TotpAlgorithm.Sha256, 1, 8) == "46119246" &&
                Totp.Generate("1234567890123456789012345678901234567890123456789012345678901234"u8,
                    TotpAlgorithm.Sha512, 1, 8) == "90693936", "RFC 6238 SHA-256 and SHA-512");
            protectedSeed = ProtectedData.Protect(seed, null, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(hexadecimal);
        }
        AssertThrows<SetupException>(() => Totp.NormalizeSeed("invalid-seed-!"));

        var clock = new TestClock(DateTimeOffset.UnixEpoch.AddSeconds(27));
        var totp = new Totp(TestVpn with { ProtectedTotpSeed = protectedSeed }, clock);
        var request = ConnectionController.GetVpnAuthenticationAsync(totp, false,
            _ => throw new InvalidOperationException("Configured TOTP requested manual input."),
            () => throw new InvalidOperationException("Saved password requested input."), CancellationToken.None);
        Require(!request.IsCompleted, "Wait for the next step when a code is about to expire");
        clock.Advance(TimeSpan.FromMilliseconds(3100));
        Require(await request.WaitAsync(TimeSpan.FromSeconds(1)) is { Password: null, Code: "287082" },
            "Stored password and seed generate a code without a prompt");

        request = ConnectionController.GetVpnAuthenticationAsync(totp, true,
            _ => throw new InvalidOperationException("Configured TOTP requested manual input."),
            () => Task.FromResult<string?>("fixture-password"), CancellationToken.None);
        Require(!request.IsCompleted, "Do not reuse a time step that was already issued");
        clock.Advance(TimeSpan.FromSeconds(30));
        Require(await request.WaitAsync(TimeSpan.FromSeconds(1)) is { Password: "fixture-password", Code: "359152" },
            "Only the unsaved password is requested when a seed is configured");

        var handEntered = await ConnectionController.GetVpnAuthenticationAsync(null, false,
            needsPassword => Task.FromResult<VpnAuthentication?>(new(needsPassword ? "fixture-password" : null, "000042")),
            () => throw new InvalidOperationException(), CancellationToken.None);
        Require(handEntered is { Password: null, Code: "000042" }, "Manual code is available when the seed is absent");
        using var stop = new CancellationTokenSource();
        var pending = totp.FreshCodeAsync(stop.Token);
        stop.Cancel();
        try { _ = await pending; throw new InvalidOperationException("Expected the TOTP wait to cancel."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("Protected automatic TOTP, code rollover, manual fallback and password-only input passed.");
    }

    private static async Task TestVpnProgressAsync()
    {
        var stages = new List<VpnProgress>();
        const string remoteText = "fixture-remote.invalid";
        string messages = $"""
            >STATE:0,RESOLVE,,,{remoteText}
            >STATE:0,TCP_CONNECT,,,{remoteText}
            >STATE:0,WAIT,,,{remoteText}
            >STATE:0,AUTH,,,{remoteText}
            >STATE:0,AUTH_PENDING,,,{remoteText}
            >LOG:0,N,TLS Error: TLS key negotiation failed for {remoteText}
            >FATAL:network error at {remoteText}
            """;
        using var input = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(messages)));
        using var output = new MemoryStream();
        using var startup = new CancellationTokenSource();
        var tracker = new OpenVpnSession.AttemptProgress(stages.Add);
        var failure = await OpenVpnSession.MonitorAsync(input, output, TestVpn,
            _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException(),
            _ => { }, startup, startup.Token, tracker);
        Require(stages.Select(stage => stage.Name).SequenceEqual(new[]
        {
            "VPN resolving server", "VPN opening server connection", "VPN waiting for server",
            "VPN verifying sign-in", "VPN waiting for authentication", "VPN checking server connection"
        }), "OpenVPN state changes are shown before a final failure");
        Require(failure.Kind == FailureKind.Transient && failure.Reason.Contains("TLS handshake", StringComparison.Ordinal),
            "Retain the specific handshake failure across a generic network exit");
        Require(!failure.Reason.Contains(remoteText, StringComparison.Ordinal) &&
            stages.All(stage => !stage.Name.Contains(remoteText, StringComparison.Ordinal) &&
                !stage.Detail.Contains(remoteText, StringComparison.Ordinal)), "Status never prints the remote diagnostic text");
        foreach (var (phase, phrase, kind) in new[]
        {
            (OpenVpnSession.VpnPhase.WaitServer, "did not answer", FailureKind.Transient),
            (OpenVpnSession.VpnPhase.ProfileTransfer, "loading the profile", FailureKind.ActionRequired),
            (OpenVpnSession.VpnPhase.DeliverCredentials, "did not acknowledge", FailureKind.ActionRequired),
            (OpenVpnSession.VpnPhase.TunnelRoutes, "VPN routes", FailureKind.ActionRequired)
        })
        {
            var timed = new OpenVpnSession.AttemptProgress(null);
            timed.MoveTo(phase);
            var timeout = timed.DescribeFailure(timedOut: true);
            Require(timeout.Kind == kind && timeout.Reason.Contains(phrase, StringComparison.Ordinal),
                "VPN timeouts identify the stage and distinguish retry from user action");
        }
        Console.WriteLine("VPN progress, phase-specific timeouts and redacted diagnostics passed.");
    }

    private static void TestProfiles()
    {
        const string input = """
            client
            dev tun
            proto udp
            remote vpn.example.invalid 1194
            verify-x509-name "VPN: testing" name
            cipher AES-256-CBC
            <ca>
            test certificate
            </ca>
            <key>
            test private key
            </key>
            auth-user-pass
            """;

        var output = OpenVpnProfile.Sanitize(input, (_, _) => throw new InvalidOperationException("Unexpected reference"));
        Require(output.Contains("verify-x509-name \"VPN: testing\" \"name\""), "Subject check");
        Require(output.Contains("remote-cert-tls server"), "Server certificate role is enforced for CA profiles");
        OpenVpnProfile.VerifySavedServerIdentity(output);
        var caOnly = input.Replace("verify-x509-name \"VPN: testing\" name", "remote-cert-tls server");
        AssertThrows<SetupException>(() => OpenVpnProfile.Sanitize(caOnly, (_, _) => null));
        AssertThrows<SetupException>(() => OpenVpnProfile.VerifySavedServerIdentity(caOnly));
        AssertThrows<SetupException>(() => OpenVpnProfile.Sanitize(input.Replace("\" name\n", "\" name-prefix\n"), (_, _) => null));
        var fingerprint = string.Join(":", Enumerable.Repeat("AB", 32));
        var pinned = $"client\ndev tun\nremote vpn.example.invalid\n<peer-fingerprint>\n{fingerprint}\n</peer-fingerprint>";
        OpenVpnProfile.VerifySavedServerIdentity(OpenVpnProfile.Sanitize(pinned, (_, _) => null));
        Require(output.Contains("auth-retry none"), "Controlled VPN auth retries");
        Require(output.Contains("data-ciphers-fallback AES-256-CBC"), "Legacy data cipher");
        AssertThrows<SetupException>(() => OpenVpnProfile.Sanitize(input + "\nup evil.exe\n", (_, _) => null));
        AssertThrows<SetupException>(() => OpenVpnProfile.Sanitize(input + "\nmanagement 127.0.0.1 7435\n", (_, _) => null));
        AssertThrows<SetupException>(() => OpenVpnProfile.Sanitize(input + "\nstatic-challenge \"Token\" 1\n", (_, _) => null));
        AssertThrows<SetupException>(() => OpenVpnProfile.Sanitize(input.Replace("</key>", ""), (_, _) => null));

        string selected = Path.Combine(Path.GetTempPath(), $"vpn-rdp-selected-{Guid.NewGuid():N}.pem");
        try
        {
            File.WriteAllText(selected, "selected private key");
            var external = input.Replace("<key>\ntest private key\n</key>", "key a-different-file.pem");
            var imported = OpenVpnProfile.Sanitize(external, (directive, _) => directive == "key" ? selected : null);
            Require(imported.Contains("<key>\nselected private key\n</key>"), "Explicit referenced file selection");
            AssertThrows<OperationCanceledException>(() => OpenVpnProfile.Sanitize(external, (_, _) => null));
        }
        finally { File.Delete(selected); }
        Console.WriteLine("Profile allowlist, TLS identity, inline material and selected references passed.");
    }

    private static void TestDpapi()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"VpnRdpTray.Test.{Guid.NewGuid():N}");
        try
        {
            Require(Settings.Load(directory) is null, "First-run setup");
            var configuration = new Configuration(TestVpn, TestRdp);
            Settings.Save(directory, configuration);
            var bytes = File.ReadAllBytes(Settings.ConfigPath(directory));
            var readable = Encoding.UTF8.GetString(bytes);
            foreach (var secret in new[] { TestVpn.Password!, TestVpn.Profile, TestVpn.KeyPassword!, TestRdp.Password! })
                Require(!readable.Contains(secret, StringComparison.Ordinal), "Protected credentials and imported key");
            Require(configuration.ToString() == nameof(Configuration) && TestVpn.ToString() == nameof(VpnSettings) &&
                TestRdp.ToString() == nameof(RdpSettings), "Diagnostic record formatting omits private fields");
            Require(Settings.Load(directory) == configuration, "DPAPI same-user round trip");
            var changed = configuration with { Rdp = TestRdp with
            {
                Port = 3389,
                MultiMonitor = false,
                SimulateActivity = true,
                RedirectSecurityKeys = false,
                RedirectUsbDevices = false,
                RedirectPrinters = false,
                RedirectClipboard = false,
                RedirectDrives = false
            } };
            Settings.Save(directory, changed);
            Require(Settings.Load(directory) == changed, "Atomic replacement preserves explicitly disabled RDP options");
            Require(Directory.GetFiles(directory).Length == 1, "No temporary setup left");

            var legacy = JsonSerializer.SerializeToNode(configuration)!.AsObject();
            var legacyVpn = legacy["Vpn"]!.AsObject();
            legacyVpn["TotpSecret"] = Convert.ToBase64String("test-migration-seed"u8);
            legacyVpn["Algorithm"] = (int)TotpAlgorithm.Sha256;
            legacyVpn["Period"] = 45;
            legacyVpn.Remove(nameof(VpnSettings.ProtectedTotpSeed));
            legacyVpn.Remove(nameof(VpnSettings.TotpAlgorithm));
            legacyVpn.Remove(nameof(VpnSettings.TotpPeriod));
            var legacyRdp = legacy["Rdp"]!.AsObject();
            foreach (var option in new[]
            {
                nameof(RdpSettings.MultiMonitor), nameof(RdpSettings.RedirectSecurityKeys),
                nameof(RdpSettings.RedirectUsbDevices), nameof(RdpSettings.RedirectPrinters),
                nameof(RdpSettings.RedirectClipboard), nameof(RdpSettings.RedirectDrives), nameof(RdpSettings.SimulateActivity)
            })
                Require(legacyRdp.Remove(option), $"New configurations include {option}");
            File.WriteAllBytes(Settings.ConfigPath(directory),
                ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(legacy), null, DataProtectionScope.CurrentUser));
            var legacyConfiguration = Settings.Load(directory)!;
            Require(legacyConfiguration.Rdp is
            {
                MultiMonitor: true, RedirectSecurityKeys: true, RedirectUsbDevices: true,
                RedirectPrinters: true, RedirectClipboard: true, RedirectDrives: true, SimulateActivity: false
            }, "Existing profiles keep display and resource defaults and activity stays off");
            byte[] migrated = ProtectedData.Unprotect(File.ReadAllBytes(Settings.ConfigPath(directory)), null, DataProtectionScope.CurrentUser);
            try
            {
                using var document = JsonDocument.Parse(migrated);
                var vpn = document.RootElement.GetProperty("Vpn");
                Require(!vpn.TryGetProperty("TotpSecret", out _) && !vpn.TryGetProperty("Algorithm", out _) &&
                    !vpn.TryGetProperty("Period", out _) &&
                    legacyConfiguration.Vpn is { TotpAlgorithm: TotpAlgorithm.Sha256, TotpPeriod: 45 },
                    "Migration replaces the original seed field and preserves the enrolled algorithm and period");
                Require(vpn.GetProperty(nameof(VpnSettings.ProtectedTotpSeed)).ValueKind == JsonValueKind.String &&
                    legacyConfiguration.Vpn.Password == TestVpn.Password &&
                    legacyConfiguration.Vpn.KeyPassword == TestVpn.KeyPassword && legacyConfiguration.Rdp.Password == TestRdp.Password,
                    "Migration keeps separately encrypted seed bytes and preserves password choices");
                byte[] decoded = ProtectedData.Unprotect(legacyConfiguration.Vpn.ProtectedTotpSeed!, null, DataProtectionScope.CurrentUser);
                try { Require(decoded.AsSpan().SequenceEqual("test-migration-seed"u8), "Only DPAPI can reopen the stored fixture seed"); }
                finally { CryptographicOperations.ZeroMemory(decoded); }
            }
            finally { CryptographicOperations.ZeroMemory(migrated); }

            var noPasswords = configuration with
            {
                Vpn = TestVpn with { Password = null, KeyPassword = null },
                Rdp = TestRdp with { Password = null }
            };
            Settings.Save(directory, noPasswords);
            Require(Settings.Load(directory) == noPasswords, "All three passwords can remain unsaved");
        }
        finally
        {
            File.Delete(Settings.ConfigPath(directory));
            Directory.Delete(directory);
        }
        Console.WriteLine("DPAPI seed migration, optional passwords and independent RDP settings passed.");
    }

    private static void TestPreferences()
    {
        string root = $"Software\\VpnRdpTray.Test.{Guid.NewGuid():N}";
        string preferences = root + "\\Preferences";
        string startup = root + "\\Startup";
        try
        {
            Require(Settings.Enabled("AutoStart", preferences) && Settings.Enabled("Connection", preferences), "Both defaults on");
            Settings.SetEnabled("AutoStart", false, preferences, startup);
            Require(!Settings.Enabled("AutoStart", preferences) && Settings.Enabled("Connection", preferences),
                "Auto-start is independent");
            Settings.SetEnabled("Connection", false, preferences, startup);
            Settings.SetEnabled("AutoStart", true, preferences, startup);
            Require(Settings.Enabled("AutoStart", preferences) && !Settings.Enabled("Connection", preferences),
                "Connection is independent");
            using var startupKey = Registry.CurrentUser.OpenSubKey(startup);
            Require(startupKey?.GetValue("VpnRdpTray") is string, "Sign-in startup entry");
            Settings.SetEnabled("AutoStart", false, preferences, startup);
            Require(startupKey?.GetValue("VpnRdpTray") is null, "Remove only the app startup entry");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }
        Console.WriteLine("Independent switch defaults, persistence and startup registration passed.");
    }

    private static void TestProtocolClassification()
    {
        var connected = "0,CONNECTED,SUCCESS,10.0.2.15,198.51.100.8,1194,192.0.2.9,42120,fd00::2".Split(',');
        Require(OpenVpnSession.ParseTunnelAddresses(connected).SequenceEqual(new[]
            { IPAddress.Parse("10.0.2.15"), IPAddress.Parse("fd00::2") }), "Management connected IPv4 and IPv6");
        Require(OpenVpnSession.Classify(">PASSWORD:Verification Failed: 'Auth'")?.Kind == FailureKind.CredentialRejected,
            "Rejected OpenVPN credentials");
        Require(OpenVpnSession.Classify("ERROR: no password is currently needed at this time")?.Kind == FailureKind.ActionRequired,
            "OpenVPN management command rejection is actionable");
        Require(OpenVpnSession.Classify(">LOG:0,N,AUTH: Received control message: AUTH_FAILED,Account disabled")?.Kind ==
            FailureKind.CredentialRejected, "Server-supplied reason still counts as a rejection");
        Require(OpenVpnSession.Classify(">PASSWORD:Verification Failed: ['CRV1:fixture']")?.Kind == FailureKind.ActionRequired,
            "Recognize a dynamic challenge before trying another password");
        Require(OpenVpnSession.Classify(">LOG:0,N,AUTH: Received control message: AUTH_FAILED,TEMP[backoff 180,advance no]: unavailable")
            is { Kind: FailureKind.Transient, MinimumDelay.TotalSeconds: 180 }, "Honor a temporary server backoff");
        Require(OpenVpnSession.Classify(">LOG:0,N,AUTH_FAILED,TEMP[backoff 9999999]: unavailable")?.Kind ==
            FailureKind.ActionRequired, "Keep unusually long server-requested pauses under user control");
        Require(OpenVpnSession.Classify(">PASSWORD:Need 'Auth' username/password SC:1,Enter token")?.Kind == FailureKind.ActionRequired,
            "Unsupported challenge-response");
        Require(OpenVpnSession.Classify(">LOG:0,N,VERIFY X509NAME ERROR: test")?.Kind == FailureKind.ActionRequired, "Bad VPN certificate");
        const string privateFields = "CN=fixture-private-subject, serial=314159";
        (string Message, string Expected)[] certificateCases =
        [
            ($">LOG:0,N,VERIFY ERROR: depth=0, error=certificate has expired: {privateFields}", "expired"),
            ($">LOG:0,N,VERIFY ERROR: depth=1, error=certificate is not yet valid: {privateFields}", "not yet valid"),
            ($">LOG:0,N,VERIFY ERROR: depth=0, error=unable to get local issuer certificate: {privateFields}", "certificate chain"),
            ($">LOG:0,N,VERIFY ERROR: depth=0, error=certificate revoked: {privateFields}", "revoked"),
            (">LOG:0,N,VERIFY ERROR: CRL not loaded", "revocation list"),
            ($">LOG:0,N,VERIFY X509NAME ERROR: {privateFields}", "name does not match"),
            ($">LOG:0,N,TLS Error: --tls-verify/--peer-fingerprint certificate hash verification failed. ({privateFields})", "fingerprint does not match"),
            ($">LOG:0,N,VERIFY EKU ERROR: {privateFields}", "key usage"),
            ($">LOG:0,N,OpenSSL: error:0A000418:SSL routines::tlsv1 alert unknown ca: {privateFields}", "does not trust the issuer"),
            ($">LOG:0,N,OpenSSL: error:0A00045C:SSL routines::tlsv13 alert certificate required: {privateFields}", "requires a client certificate"),
            ($">FATAL:Cannot load inline certificate file {privateFields}", "client certificate"),
            ($">FATAL:Cannot load CA certificate file [[INLINE]] {privateFields}", "CA certificates")
        ];
        foreach (var (message, expected) in certificateCases)
        {
            var failure = OpenVpnSession.Classify(message);
            Require(failure is { Kind: FailureKind.ActionRequired } && failure.Reason.Contains(expected, StringComparison.OrdinalIgnoreCase) &&
                !failure.Reason.Contains("fixture-private-subject", StringComparison.Ordinal) &&
                !failure.Reason.Contains("314159", StringComparison.Ordinal), $"Actionable and redacted certificate diagnosis for {expected}");
        }
        Require(OpenVpnSession.Classify(">LOG:0,,VERIFY OK: depth=0, CN=VERIFY ERROR AUTH_FAILED") is null,
            "Do not classify certificate subject content as a verification or login failure");
        var command = OpenVpnSession.EncodeCredential("password \"Auth\" ", "text\\\"", "000042");
        try { Require(Encoding.UTF8.GetString(command) == "password \"Auth\" \"text\\\\\\\"000042\"\n", "Escaping and appending a six-digit code"); }
        finally { CryptographicOperations.ZeroMemory(command); }
        AssertThrows<SetupException>(() => OpenVpnSession.EncodeCredential("password \"Auth\" ", "line\nbreak"));
        Require(Enumerable.Range(1, 15).Select(ConnectionController.Backoff).All(delay => delay <= TimeSpan.FromMinutes(2)),
            "Capped backoff");
        Console.WriteLine("Management states, authentication and certificate handling passed.");
    }

    private static void TestInstallerPath()
    {
        string root = Path.Combine(Path.GetTempPath(), $"VpnRdpTray.Test.{Guid.NewGuid():N}");
        var directory = Directory.CreateDirectory(Path.Combine(root, "package"));
        string renamed = Path.Combine(root, "renamed");
        string file = Path.Combine(directory.FullName, "fixture.msi");
        try
        {
            File.WriteAllBytes(file, [1, 2, 3]);
            using var location = new InstallerDownloadDirectory(directory.FullName);
            using var package = InstallerDownloadDirectory.OpenPackage(file);
            AssertThrows<IOException>(() => File.OpenWrite(file).Dispose());
            AssertThrows<IOException>(() => Directory.Move(directory.FullName, renamed));
            using var reOpened = File.OpenRead(file);
            Require(reOpened.ReadByte() == 1, "A second reader can open the same held package");
        }
        finally
        {
            foreach (string candidate in new[] { directory.FullName, renamed }.Where(Directory.Exists))
            {
                File.Delete(Path.Combine(candidate, "fixture.msi"));
                Directory.Delete(candidate);
            }
            Directory.Delete(root);
        }
        Console.WriteLine("Verified Windows Installer staging resists file replacement and directory renaming.");
    }

    private static async Task TestTransportAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var accepted = await listener.AcceptTcpClientAsync();
        var localPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        Require(OpenVpnSession.OwnsLoopbackConnection(Environment.ProcessId, localPort, port), "Verified loopback peer PID");
        Require(!OpenVpnSession.OwnsLoopbackConnection(0, localPort, port), "Reject other loopback peer");
        Require(!RdpRoute.UsesTunnel([IPAddress.Loopback], new([IPAddress.Loopback])), "No RDP via loopback without a tunnel");
        Console.WriteLine("Windows loopback peer attribution and tunnel route gate passed.");
    }

    private static void TestRdpProbeResults()
    {
        var refused = RdpRoute.DescribePortCheck(RdpRoute.ClassifySocketError(SocketError.ConnectionRefused));
        var reset = RdpRoute.DescribePortCheck(RdpRoute.ClassifySocketError(SocketError.ConnectionReset));
        var timedOut = RdpRoute.DescribePortCheck(RdpRoute.ClassifySocketError(SocketError.TimedOut));
        var missingRoute = RdpRoute.DescribePortCheck(RdpRoute.ClassifySocketError(SocketError.NetworkUnreachable));
        Require(!refused.MayNeedTunnelRefresh && refused.Reason.Contains("refused"), "TCP refusal proves there was a response");
        Require(!reset.MayNeedTunnelRefresh && reset.Reason.Contains("reset"), "TCP reset proves there was a response");
        Require(timedOut.MayNeedTunnelRefresh && timedOut.Reason.Contains("may be asleep or stalled"),
            "A TCP timeout is evidence of nonresponse, not proof that the PC stalled");
        Require(missingRoute.MayNeedTunnelRefresh && missingRoute.Reason.Contains("route"), "Diagnose a VPN route failure separately");
        Console.WriteLine("RDP TCP diagnostics distinguish responses, timeouts and route failures.");
    }

    private static void TestWakeNotifications()
    {
        const int suspend = 4, interactiveResume = 7, automaticResume = 0x12, powerSettingChange = 0x8013;
        int checks = 0;
        using var window = new TrayApp.TrayWindow(() => { }, () => checks++);
        Require(window.ReceivesWakeNotifications, "Windows registered wake notifications for the tray window");

        // POWERBROADCAST_SETTING consists of a GUID, payload length and the display state DWORD.
        nint data = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.StructureToPtr(new Guid("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5"), data, false);
            Marshal.WriteInt32(data, 16, sizeof(int));
            Display(1);
            Require(checks == 0, "Initial display-on notification is not a wake");
            Display(0);
            Display(1);
            Display(1);
            Require(checks == 1, "Display off then on triggers one Modern Standby recheck");

            Display(0);
            SendMessageW(window.Handle, 0x0218, suspend, 0);
            SendMessageW(window.Handle, 0x0218, automaticResume, 0);
            SendMessageW(window.Handle, 0x0218, interactiveResume, 0);
            Display(1);
            Require(checks == 2, "Automatic resume, interactive resume and display-on share one recheck");

            void Display(int state)
            {
                Marshal.WriteInt32(data, 20, state);
                SendMessageW(window.Handle, 0x0218, powerSettingChange, data);
            }
        }
        finally { Marshal.FreeHGlobal(data); }
        Console.WriteLine("Tray wake registration, Modern Standby notification and duplicate suppression passed.");
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    private static void TestRdpSettings()
    {
        var endpoint = new RdpEndpoint(TestRdp.Host, [IPAddress.Parse(TestRdp.Ip!)], TestRdp.Port);
        var profile = NativeRdpSession.RdpFile(endpoint, TestRdp, multiMonitorAvailable: false);
        Require(profile.Contains("full address:s:prototype.invalid:3390\r\n") &&
            profile.Contains("enablerdsaadauth:i:1\r\n") &&
            profile.Contains("authentication level:i:1\r\n") &&
            !profile.Contains(TestRdp.Ip!) && !profile.Contains(TestRdp.Password!), "Hostname-only Entra RDP settings");
        Require(profile.Contains("screen mode id:i:1\r\n") && profile.Contains("use multimon:i:0\r\n"),
            "A single connected display keeps the native profile windowed");
        var multiple = NativeRdpSession.RdpFile(endpoint, TestRdp, multiMonitorAvailable: true);
        Require(multiple.Contains("screen mode id:i:2\r\n") && multiple.Contains("use multimon:i:1\r\n"),
            "A supported multi-monitor layout enables native full screen");
        var single = NativeRdpSession.RdpFile(endpoint, TestRdp with { MultiMonitor = false }, multiMonitorAvailable: true);
        Require(single.Contains("screen mode id:i:1\r\n") && single.Contains("use multimon:i:0\r\n"),
            "Turning off multi-monitor overrides connected displays");
        Require(RdpDisplayTopology.MultiMonitorBounds([new(-1920, 0, 1920, 1080), new(0, 0, 2560, 1440)]) ==
            new Rectangle(-1920, 0, 4480, 1440), "RDP layout supports different sizes and displays left of the primary");
        Require(RdpDisplayTopology.MultiMonitorBounds([new(0, 0, 1920, 1080), new(2000, 0, 1920, 1080)]) is null,
            "Separated displays use the single-monitor fallback");
        var fullScreenBounds = new Rectangle(-1920, 0, 3840, 1080);
        var buttonSize = new Size(120, 28);
        var dock = RdpDisplayTopology.ExitDockArea(fullScreenBounds, new Rectangle(-1920, 0, 1920, 1080), buttonSize);
        Require(dock is not null && RdpDisplayTopology.ExitDockLocation(fullScreenBounds, dock.Value, buttonSize, 24, 144) ==
            new Point(1764, 0), "Full-screen exit docks on the real monitor’s top edge with a DPI-scaled 24 DIP right margin");
        Require(RdpDisplayTopology.ExitDockLocation(fullScreenBounds, dock!.Value, buttonSize, 5000, 144) ==
            Point.Empty, "Full-screen exit remains visible when restoring a large saved offset");
        Require(HostedRdpSession.DescribeDisconnect(1798, false, false, true).Kind == FailureKind.ActionRequired, "RDP bad certificate");
        Require(HostedRdpSession.DescribeDisconnect(6919, false, false, true).Reason.Contains("expired RDP certificate"),
            "Report an expired Remote Desktop certificate separately from a VPN certificate");
        Require(HostedRdpSession.DescribeDisconnect(3335, false, false, true).Kind == FailureKind.CredentialRejected, "RDP locked account");
        Require(HostedRdpSession.DescribeDisconnect(264, false, false, true).Kind == FailureKind.Transient, "RDP timeout");
        Require(HostedRdpSession.DescribeDisconnect(1, false, true, false).Kind == FailureKind.ManualRetry, "RDP prompt canceled");
        Require(HostedRdpSession.DescribeDisconnect(1, true, false, false).Kind == FailureKind.Transient, "Signed-in RDP window closes and retries");
        Require(HostedRdpSession.DescribeDisconnect(2, true, false, false).Kind == FailureKind.Transient, "Signed-in RDP disconnects and retries");
        var display = RdpActiveX.DisplaySize(new Size(1933, 1080));
        Require(display.Width % 2 == 0 && Math.Abs(display.Width - 1933) <= 1 && display.Height == 1080,
            "RDP desktop resolution follows the pixel viewport");
        Require(HostedRdpSession.FitDisplay(new Size(1600, 1000), new Size(1920, 1080)) ==
            new Rectangle(0, 50, 1600, 900), "RDP viewport preserves the desktop aspect ratio");
        Require(HostedRdpSession.WindowedMinimumSize(new Size(1280, 800)) == new Size(854, 534) &&
            HostedRdpSession.WindowedMinimumSize(new Size(1920, 1080)) == new Size(1280, 720),
            "Windowed minimum rounds each physical screen dimension up to at least two-thirds");
        Console.WriteLine("RDP settings, monitor topology, exit docking, retry and sign-in outcome classification passed.");
    }

    private static void TestNativeRedirectionSettings()
    {
        var endpoint = new RdpEndpoint(TestRdp.Host, [IPAddress.Parse(TestRdp.Ip!)], TestRdp.Port);
        var cases = new (RdpSettings Settings, string[] EnabledLines, string[] DisabledLines)[]
        {
            (TestRdp with { RedirectSecurityKeys = false }, ["redirectsmartcards:i:1", "redirectwebauthn:i:1"],
                ["redirectsmartcards:i:0", "redirectwebauthn:i:0"]),
            (TestRdp with { RedirectUsbDevices = false }, ["usbdevicestoredirect:s:*", "devicestoredirect:s:*"],
                ["usbdevicestoredirect:s:", "devicestoredirect:s:"]),
            (TestRdp with { RedirectPrinters = false }, ["redirectprinters:i:1"], ["redirectprinters:i:0"]),
            (TestRdp with { RedirectClipboard = false }, ["redirectclipboard:i:1"], ["redirectclipboard:i:0"]),
            (TestRdp with { RedirectDrives = false }, ["drivestoredirect:s:*"], ["drivestoredirect:s:"])
        };
        var defaults = Lines(TestRdp);
        foreach (var (settings, enabled, disabled) in cases)
        {
            Require(enabled.All(defaults.Contains), $"Default redirection for {enabled[0]}");
            var expected = defaults.Except(enabled).Concat(disabled).ToHashSet(StringComparer.Ordinal);
            Require(expected.SetEquals(Lines(settings)), $"Independent redirection setting for {disabled[0]}");
        }
        Console.WriteLine("Native RDP resource redirection defaults and independent opt-outs passed.");

        HashSet<string> Lines(RdpSettings settings) => NativeRdpSession.RdpFile(endpoint, settings, multiMonitorAvailable: false)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    }

    private static void TestWindowGlow()
    {
        var frame = new Rectangle(-320, 50, 640, 480);
        Require(RdpWindowGlow.GetOverlayBounds(frame, 96) == Rectangle.Inflate(frame, 18, 18),
            "Windowed halo extends 18 pixels past the visible frame");
        Require(RdpWindowGlow.GetOverlayBounds(frame, 192) == Rectangle.Inflate(frame, 36, 36),
            "Windowed halo scales with display DPI");

        var color = Color.FromArgb(30, 125, 240);
        int Alpha(double distance) => (int)(unchecked((uint)RdpWindowGlow.GlowPixel(distance, 18, color)) >> 24);
        Require(Alpha(1) is > 0 and < 255 && Alpha(1) > Alpha(8) && Alpha(8) > Alpha(15) && Alpha(15) > 0,
            "Translucent glow fades and remains visible 15 pixels outside the frame");
        Require(Alpha(0) == 0 && Alpha(18) == 0, "Window interior and outer edge remain transparent");

        int extent = RdpWindowGlow.FullScreenExtent(216);
        Require(RdpWindowGlow.FullScreenExtent(96) == 5 && extent == 12,
            "The five-DIP full-screen glow scales with the monitor DPI");
        const int width = 40, height = 30;
        nint surface = Marshal.AllocHGlobal(width * height * sizeof(int));
        try
        {
            RdpWindowGlow.WriteGlow(surface, width, height, extent, color, inset: true);
            int AlphaAt(int x, int y) => (int)(unchecked((uint)Marshal.ReadInt32(surface, (y * width + x) * sizeof(int))) >> 24);
            int edge = AlphaAt(0, height / 2);
            int faded = AlphaAt(extent - 1, height / 2);
            Require(edge is >= 200 and < 255 && edge > faded && faded > 0 &&
                AlphaAt(extent, height / 2) == 0, "Full-screen glow begins visibly at the edge and fades over five DIPs");
            Require(AlphaAt(width / 2, 0) == edge && AlphaAt(width - 1, height / 2) == edge &&
                AlphaAt(width / 2, height - 1) == edge && AlphaAt(width - 1, height - 1) == edge &&
                AlphaAt(width / 2, height / 2) == 0, "The full-screen glow covers all edges and leaves the center clear");
        }
        finally { Marshal.FreeHGlobal(surface); }
        Console.WriteLine("Windowed and full-screen glow geometry and alpha gradients passed.");
    }

    private static void TestNativeClient()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"VpnRdpTray.Test.{Guid.NewGuid():N}");
        using var closedPort = new TcpListener(IPAddress.Loopback, 0);
        closedPort.Start();
        int port = ((IPEndPoint)closedPort.LocalEndpoint).Port;
        closedPort.Stop();
        try
        {
            var settings = TestRdp with { Host = "localhost", Port = port };
            using (var desktop = new NativeRdpSession(new("localhost", [IPAddress.Loopback], port), settings, false, directory))
                Require(File.Exists(Path.Combine(directory, "entra.rdp")), "Native client receives a temporary profile");
            Require(!File.Exists(Path.Combine(directory, "entra.rdp")), "Native client and its profile are cleaned up");
        }
        finally { Directory.Delete(directory); }
        Console.WriteLine("Native mstsc startup in a job and shutdown on disposal passed.");
    }

    private static async Task TestServiceAsync()
    {
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var port = ((IPEndPoint)target.LocalEndpoint).Port;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Local VPN fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var profile = $"""
            client
            dev tun
            proto tcp-client
            remote 127.0.0.1 {port}
            nobind
            connect-timeout 3
            connect-retry 1 1
            connect-retry-max 1
            remote-cert-tls server
            verify-x509-name "Local VPN fixture" name
            auth-user-pass
            <ca>
            {certificate.ExportCertificatePem()}
            </ca>
            """;
        var settings = TestVpn with { Profile = profile };
        bool ready = false;
        bool credentialsRequested = false;
        var stages = new List<VpnProgress>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var attempt = OpenVpnSession.RunAsync(settings,
            _ =>
            {
                credentialsRequested = true;
                return Task.FromResult<VpnAuthentication?>(new(null, "000042"));
            },
            _ => throw new InvalidOperationException("Unexpected key prompt in the test profile."),
            tunnel => Require(tunnel is null, "Synthetic fixture never establishes a tunnel"),
            stop.Token, () => ready = true, stages.Add);
        try
        {
            var inbound = target.AcceptTcpClientAsync(stop.Token).AsTask();
            if (await Task.WhenAny(inbound, attempt) == attempt && !stop.IsCancellationRequested)
                throw new InvalidOperationException($"OpenVPN exited before attempting the loopback connection: {(await attempt).Reason}");
            using var connected = await inbound;
        }
        finally
        {
            stop.Cancel();
            try { await attempt; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        Require(ready && credentialsRequested && stages.Any(stage => stage.Name == "VPN starting server connection"),
            $"Verified local password acknowledgement and a loopback connection [{string.Join(", ", stages.Select(stage => stage.Name))}]");
        Console.WriteLine("The Interactive Service launched OpenVPN, authenticated management, acknowledged a synthetic credential, opened a loopback connection and cleaned up.");
    }

    private static void Require(bool condition, string check)
    {
        if (!condition)
            throw new InvalidOperationException($"Failed: {check}");
    }

    private static void AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private TestTimer? pending;
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            pending = new TestTimer(now + dueTime, callback, state);

        internal void Advance(TimeSpan amount)
        {
            now += amount;
            if (pending is { } timer && now >= timer.Due)
            {
                pending = null;
                timer.Fire();
            }
        }

        private sealed class TestTimer(DateTimeOffset due, TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            internal DateTimeOffset Due => due;
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            internal void Fire() { if (!disposed) callback(state); }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
