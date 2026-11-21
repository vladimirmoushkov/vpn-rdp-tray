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
    private static readonly VpnSettings TestVpn = new("imported-key-fixture", "test-user", "test-vpn-password",
        Convert.ToBase64String("12345678901234567890"u8), TotpAlgorithm.Sha1, 30, 2, null);
    private static readonly RdpSettings TestRdp = new("prototype.invalid", "192.0.2.42", 3390,
        "prototype@example.invalid", RdpMode.Entra, "test-rdp-password", false, false);

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        TestTotpVectors();
        TestRolloverAsync().GetAwaiter().GetResult();
        TestProfiles();
        TestDpapi();
        TestPreferences();
        TestProtocolClassification();
        TestTransportAsync().GetAwaiter().GetResult();
        TestRdpSettings();
        TestNativeRedirectionSettings();
        TestWindowGlow();
        if (args is ["--native-client"])
            TestNativeClient();
        if (args is ["--interactive-service"])
            TestServiceAsync().GetAwaiter().GetResult();
        return RdpPrototype.CheckLocalControl();
    }

    private static void TestTotpVectors()
    {
        var vectors = new (long Time, string Sha1, string Sha256, string Sha512)[]
        {
            (59L, "94287082", "46119246", "90693936"),
            (1111111109L, "07081804", "68084774", "25091201"),
            (1111111111L, "14050471", "67062674", "99943326"),
            (1234567890L, "89005924", "91819424", "93441116"),
            (2000000000L, "69279037", "90698825", "38618901"),
            (20000000000L, "65353130", "77737706", "47863826")
        };
        var seeds = new[] { "12345678901234567890", "12345678901234567890123456789012",
            "1234567890123456789012345678901234567890123456789012345678901234" };

        foreach (var (time, sha1, sha256, sha512) in vectors)
        {
            var expected = new[] { sha1, sha256, sha512 };
            for (int algorithm = 0; algorithm < seeds.Length; algorithm++)
            {
                var seed = Encoding.ASCII.GetBytes(seeds[algorithm]);
                Require(Totp.Generate(seed, (TotpAlgorithm)algorithm, time / 30, 8) == expected[algorithm], "RFC 6238 vector");
                Require(Totp.Generate(seed, (TotpAlgorithm)algorithm, time / 30) == expected[algorithm][2..], "Six-digit TOTP");
            }
        }
        Require(Totp.NormalizeSecret("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ") == TestVpn.TotpSecret, "Unpadded Base32");
        Require(Totp.NormalizeSecret("hex:3132333435363738393031323334353637383930") == TestVpn.TotpSecret, "Hex secret");
        AssertThrows<SetupException>(() => Totp.NormalizeSecret("not-a-valid-Base32-secret!"));
        Console.WriteLine("RFC 6238 vectors, six-digit formatting and secret input passed.");
    }

    private static async Task TestRolloverAsync()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddSeconds(27));
        var totp = new Totp(TestVpn, clock);
        var waiting = totp.FreshCodeAsync(CancellationToken.None);
        Require(!waiting.IsCompleted, "Three-second rollover guard");
        clock.Advance(TimeSpan.FromMilliseconds(3100));
        Require(await waiting.WaitAsync(TimeSpan.FromSeconds(1)) == "287082", "Fresh post-rollover code");
        var next = totp.FreshCodeAsync(CancellationToken.None);
        Require(!next.IsCompleted, "Avoid reusing a sent code");
        clock.Advance(TimeSpan.FromSeconds(30));
        Require(await next.WaitAsync(TimeSpan.FromSeconds(1)) == "359152", "Next unused step");
        using var stop = new CancellationTokenSource();
        var canceled = totp.FreshCodeAsync(stop.Token);
        stop.Cancel();
        try { _ = await canceled; throw new InvalidOperationException("TOTP wait was not canceled."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("TOTP rollover, single-use wait and cancellation passed.");
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
            foreach (var secret in new[] { TestVpn.Password, TestVpn.Profile, TestVpn.TotpSecret, TestRdp.Password! })
                Require(!readable.Contains(secret, StringComparison.Ordinal), "Protected credentials and imported key");
            Require(configuration.ToString() == nameof(Configuration) && TestVpn.ToString() == nameof(VpnSettings) &&
                TestRdp.ToString() == nameof(RdpSettings), "Diagnostic record formatting omits private fields");
            Require(Settings.Load(directory) == configuration, "DPAPI same-user round trip");
            var changed = configuration with { Rdp = TestRdp with
            {
                Port = 3389,
                MultiMonitor = false,
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
            var legacyRdp = legacy["Rdp"]!.AsObject();
            foreach (var option in new[]
            {
                nameof(RdpSettings.MultiMonitor), nameof(RdpSettings.RedirectSecurityKeys),
                nameof(RdpSettings.RedirectUsbDevices), nameof(RdpSettings.RedirectPrinters),
                nameof(RdpSettings.RedirectClipboard), nameof(RdpSettings.RedirectDrives)
            })
                Require(legacyRdp.Remove(option), $"New configurations include {option}");
            File.WriteAllBytes(Settings.ConfigPath(directory),
                ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(legacy), null, DataProtectionScope.CurrentUser));
            Require(Settings.Load(directory)?.Rdp is
            {
                MultiMonitor: true, RedirectSecurityKeys: true, RedirectUsbDevices: true,
                RedirectPrinters: true, RedirectClipboard: true, RedirectDrives: true
            }, "Existing profiles default to multi-monitor and local resources enabled");
        }
        finally
        {
            File.Delete(Settings.ConfigPath(directory));
            Directory.Delete(directory);
        }
        Console.WriteLine("First run, DPAPI round trip and RDP preference migration passed.");
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
        var command = OpenVpnSession.EncodeCredential("password \"Auth\" ", "text\\\"", "000042");
        try { Require(Encoding.UTF8.GetString(command) == "password \"Auth\" \"text\\\\\\\"000042\"\n", "Escaping and appending a six-digit code"); }
        finally { CryptographicOperations.ZeroMemory(command); }
        AssertThrows<SetupException>(() => OpenVpnSession.EncodeCredential("password \"Auth\" ", "line\nbreak"));
        Require(Enumerable.Range(1, 15).Select(ConnectionController.Backoff).All(delay => delay <= TimeSpan.FromMinutes(2)),
            "Capped backoff");
        Console.WriteLine("Management states, authentication and certificate handling passed.");
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
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Local VPN fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var profile = $"""
            client
            dev tun
            proto tcp-client
            remote 127.0.0.1 1
            nobind
            remote-cert-tls server
            verify-x509-name "Local VPN fixture" name
            auth-user-pass
            <ca>
            {certificate.ExportCertificatePem()}
            </ca>
            """;
        var settings = TestVpn with { Profile = profile };
        bool ready = false;
        bool openedTunnel = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            var failure = await OpenVpnSession.RunAsync(settings, new Totp(settings), tunnel => openedTunnel |= tunnel is not null,
                stop.Token, () => { ready = true; stop.Cancel(); });
            throw new InvalidOperationException("Interactive Service smoke test failed before cancellation. " + failure.Reason);
        }
        catch (OperationCanceledException) when (ready && !openedTunnel) { }
        Console.WriteLine("The official Interactive Service launched OpenVPN, streamed its profile, authenticated management, and exited.");
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

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private Timer? scheduled;
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            scheduled = new Timer(now + dueTime, callback, state);

        internal void Advance(TimeSpan duration)
        {
            now += duration;
            if (scheduled is { } timer && now >= timer.Due)
            {
                scheduled = null;
                timer.Fire();
            }
        }

        private sealed class Timer(DateTimeOffset due, TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            internal DateTimeOffset Due { get; } = due;
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            internal void Fire() { if (!disposed) callback(state); }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
