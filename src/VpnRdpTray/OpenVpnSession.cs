using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VpnRdpTray;

internal enum FailureKind { Transient, ActionRequired, CredentialRejected, ManualRetry }
internal sealed record Failure(string Reason, FailureKind Kind = FailureKind.Transient, TimeSpan? MinimumDelay = null);
internal sealed record VpnProgress(string Name, string Detail);

internal sealed record Tunnel(IPAddress[] Addresses);

internal static class OpenVpnSession
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(95);
    private static readonly TimeSpan CredentialsTimeout = TimeSpan.FromSeconds(12);
    private const string AuthUsernameAccepted = "SUCCESS: 'Auth' username entered, but not yet verified";
    private const string AuthPasswordAccepted = "SUCCESS: 'Auth' password entered, but not yet verified";
    private const string KeyPasswordAccepted = "SUCCESS: 'Private Key' password entered, but not yet verified";

    internal static async Task<Failure> RunAsync(VpnSettings settings,
        Func<CancellationToken, Task<VpnAuthentication?>> authenticate,
        Func<CancellationToken, Task<string?>> unlockKey, Action<Tunnel?> changed,
        CancellationToken cancellationToken, Action? managementAuthenticated = null, Action<VpnProgress>? progress = null)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = startup.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var service = new NamedPipeClientStream(".", @"openvpn\service", PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        using var childEnded = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var stopMonitor = new CancellationTokenSource();
        Task? serviceClosed = null;
        TcpClient? management = null;
        bool authenticated = false;
        var attempt = new AttemptProgress(progress);
        try
        {
            StartPhase(VpnPhase.ProfileCheck, 5);
            OpenVpnProfile.VerifySavedServerIdentity(settings.Profile);
            var install = OpenVpnInstall.Get();
            if (!install.IsProvisioned)
                return new("The OpenVPN profile needs provisioning. In Settings, choose Prepare this Windows account.", FailureKind.ActionRequired);

            StartPhase(VpnPhase.Service, 5);
            using var stream = new NamedPipeServerStream(MachineSetup.ProfilePipe, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly |
                PipeOptions.FirstPipeInstance);

            uint servicePid = ServiceProcessId();
            if (servicePid == 0)
                return new("The OpenVPN Interactive Service is unavailable. In Settings, choose Prepare this Windows account.", FailureKind.ActionRequired);
            await service.ConnectAsync(5000, token);
            if (!GetNamedPipeServerProcessId(service.SafePipeHandle, out uint peer) || peer != servicePid)
                return new("Windows could not verify the OpenVPN service. In Settings, check the Interactive Service and prepare this Windows account.", FailureKind.ActionRequired);

            StartPhase(VpnPhase.Launch, 10);
            service.ReadMode = PipeTransmissionMode.Message;
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var proof = CreateManagementProof();
            var status = await LaunchAsync(service, install, port, proof, token);
            if (status.Pid == 0)
                return status.Failure!;

            // The service owns the child. Its launch pipe closes after the child and privileged cleanup finish.
            serviceClosed = TrackChildAsync(service, childEnded, stopMonitor.Token);
            StartPhase(VpnPhase.ProfileTransfer, 12);
            if (!await TransferProfileAsync(stream, status.Pid, settings.Profile, childEnded.Token))
                return new("The OpenVPN process receiving the profile could not be verified. Close other copies of the app and try again.", FailureKind.ActionRequired);
            stream.Dispose();
            StartPhase(VpnPhase.ManagementPeer, 10);
            management = await AcceptChildAsync(listener, port, status.Pid, childEnded.Token);
            listener.Stop();
            StartPhase(VpnPhase.ManagementAuth, 5);
            var network = management.GetStream();
            using var reader = new StreamReader(network, Encoding.UTF8, leaveOpen: true);
            await SendAsync(network, proof, token);
            while (await reader.ReadLineAsync(token) is { } line)
            {
                const string prompt = "ENTER PASSWORD:";
                var message = line.StartsWith(prompt, StringComparison.Ordinal) ? line[prompt.Length..].TrimStart() : line;
                if (message == "SUCCESS: password is correct")
                {
                    authenticated = true;
                    break;
                }
                if (message.StartsWith("ERROR:", StringComparison.Ordinal) || message.StartsWith(">INFO:", StringComparison.Ordinal))
                    return attempt.DescribeFailure(timedOut: false);
            }
            if (!authenticated)
                return attempt.DescribeFailure(timedOut: false);
            managementAuthenticated?.Invoke();
            var deferred = new Queue<string>();
            StartPhase(VpnPhase.ManagementConfigure, (int)CredentialsTimeout.TotalSeconds);
            // OpenVPN resets pending command output on each read. Allow it to acknowledge one command before issuing the next.
            await SendAsync(network, "state on", childEnded.Token);
            await RequireAcknowledgmentAsync(reader, "SUCCESS: real-time state notification set to ON", deferred, childEnded.Token);
            await SendAsync(network, "log on", childEnded.Token);
            await RequireAcknowledgmentAsync(reader, "SUCCESS: real-time log notification set to ON", deferred, childEnded.Token);
            await SendAsync(network, "hold release", childEnded.Token);
            await RequireAcknowledgmentAsync(reader, "SUCCESS: hold release succeeded", deferred, childEnded.Token);
            StartPhase(VpnPhase.ServerConnect, (int)ConnectionTimeout.TotalSeconds);
            return await MonitorAsync(reader, network, settings, authenticate, unlockKey, changed, startup, childEnded.Token, attempt, deferred);
        }
        catch (SetupException ex) { return new(ex.Message, FailureKind.ActionRequired); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return attempt.DescribeFailure(timedOut: startup.IsCancellationRequested);
        }
        catch (TimeoutException) { return attempt.DescribeFailure(timedOut: true); }
        catch (UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new("Windows denied access to OpenVPN. In Settings, choose Prepare this Windows account.", FailureKind.ActionRequired);
        }
        catch (Exception ex) when (ex is IOException or SocketException or Win32Exception or InvalidOperationException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return attempt.DescribeFailure(timedOut: false);
        }
        finally
        {
            listener.Stop();
            changed(null);
            await StopAsync(serviceClosed, management, authenticated);
            stopMonitor.Cancel();
            if (serviceClosed is not null)
                await serviceClosed;
        }

        void StartPhase(VpnPhase phase, int timeoutSeconds)
        {
            startup.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            attempt.MoveTo(phase);
        }
    }

    private static string CreateManagementProof()
    {
        Span<byte> random = stackalloc byte[24];
        RandomNumberGenerator.Fill(random);
        try { return Convert.ToHexString(random); }
        finally { CryptographicOperations.ZeroMemory(random); }
    }

    private static async Task TrackChildAsync(NamedPipeClientStream service, CancellationTokenSource childEnded,
        CancellationToken token)
    {
        var messages = new byte[4096];
        try
        {
            while (await service.ReadAsync(messages, token) != 0) { }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { }
        finally { childEnded.Cancel(); }
    }

    private static async Task<(int Pid, Failure? Failure)> LaunchAsync(NamedPipeClientStream service,
        OpenVpnInstall install, int port, string proof, CancellationToken token)
    {
        string args = $"--config \"{install.BootstrapPath}\" --management 127.0.0.1 {port} stdin " +
            "--management-hold --management-query-passwords --verb 3";
        // The third NUL-terminated field becomes stdin. It is separate from the OpenVPN arguments.
        string request = $"{install.Directory}\0{args}\0";
        var startup = new byte[Encoding.Unicode.GetByteCount(request) + (proof.Length + 2) * sizeof(char)];
        int proofOffset = Encoding.Unicode.GetBytes(request, startup);
        int end = proofOffset + Encoding.Unicode.GetBytes(proof, startup.AsSpan(proofOffset));
        startup[end] = (byte)'\n';
        try { await service.WriteAsync(startup, token); }
        finally { CryptographicOperations.ZeroMemory(startup); }

        var response = new byte[16384];
        int length = 0;
        do
        {
            int received = await service.ReadAsync(response.AsMemory(length), token);
            if (received == 0)
                return (0, new("The OpenVPN Interactive Service ended before creating a child"));
            length += received;
        } while (!service.IsMessageComplete && length < response.Length);

        var tokens = Encoding.Unicode.GetString(response, 0, length).TrimEnd('\0').Split('\n');
        if (tokens.Length >= 3 && tokens[0] == "0x00000000" && tokens[2].Trim() == "Process ID" &&
            tokens[1].StartsWith("0x", StringComparison.Ordinal) &&
            int.TryParse(tokens[1].AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int pid) && pid > 0)
            return (pid, null);
        return (0, new("The OpenVPN service refused to start the approved profile. Check the profile in Settings and ask your administrator to review service policy.",
            FailureKind.ActionRequired));
    }

    private static async Task<bool> TransferProfileAsync(NamedPipeServerStream pipe, int childPid,
        string profile, CancellationToken token)
    {
        await pipe.WaitForConnectionAsync(token);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peer) || peer != childPid)
            return false;

        var suffix = "\nmanagement-client\n"u8;
        var data = new byte[Encoding.UTF8.GetByteCount(profile) + suffix.Length];
        int end = Encoding.UTF8.GetBytes(profile, data);
        suffix.CopyTo(data.AsSpan(end));
        await WriteAndClearAsync(pipe, data, token);
        return true;
    }

    private static async Task<TcpClient> AcceptChildAsync(TcpListener listener, int port, int childPid, CancellationToken token)
    {
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync(token);
            try
            {
                int peerPort = ((IPEndPoint)client.Client.RemoteEndPoint!).Port;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (OwnsLoopbackConnection(childPid, peerPort, port))
                        return client;
                    await Task.Delay(40, token);
                }
            }
            catch
            {
                client.Dispose();
                throw;
            }
            client.Dispose();
        }
    }

    internal static bool OwnsLoopbackConnection(int pid, int localPort, int remotePort)
    {
        const uint loopback = 0x0100007f;
        int size = 0;
        _ = GetExtendedTcpTable(0, ref size, false, 2, 5, 0);
        if (size < 4)
            return false;
        nint table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, 2, 5, 0) != 0)
                return false;

            int rows = Marshal.ReadInt32(table);
            for (int i = 0; i < rows && 4 + (i + 1) * 24 <= size; i++)
            {
                nint row = table + 4 + i * 24;
                if (Marshal.ReadInt32(row) == 5 && Marshal.ReadInt32(row, 4) == loopback &&
                    Marshal.ReadInt32(row, 12) == loopback && Marshal.ReadInt32(row, 20) == pid &&
                    Port(row, 8) == localPort && Port(row, 16) == remotePort)
                    return true;
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    private static int Port(nint row, int offset) =>
        (ushort)IPAddress.NetworkToHostOrder(unchecked((short)Marshal.ReadInt32(row, offset)));

    internal static async Task<Failure> MonitorAsync(StreamReader reader, Stream network, VpnSettings settings,
        Func<CancellationToken, Task<VpnAuthentication?>> authenticate,
        Func<CancellationToken, Task<string?>> unlockKey, Action<Tunnel?> changed,
        CancellationTokenSource startup, CancellationToken processToken, AttemptProgress? progress = null,
        Queue<string>? deferred = null)
    {
        progress ??= new AttemptProgress(null);
        deferred ??= new Queue<string>();
        bool keySent = false, authSubmitted = false, established = false, awaitingCredentials = false;
        // Read final diagnostics even if the child-exit monitor observes shutdown before the socket reaches EOF.
        while ((deferred.TryDequeue(out var queued) ? queued : await reader.ReadLineAsync(startup.Token)) is { } line)
        {
            if (line == ">PASSWORD:Need 'Auth' username/password")
            {
                if (authSubmitted && !established)
                    return new("OpenVPN repeated the sign-in request before connecting", FailureKind.CredentialRejected);
                if (!established)
                    progress.MoveTo(VpnPhase.PrepareCredentials);
                if (!await SubmitAuthenticationAsync())
                    return new("VPN sign-in canceled. Choose Sign in to VPN to try again.", FailureKind.ManualRetry);
                authSubmitted = true;
                if (!established)
                    AwaitLocalAcknowledgment(VpnPhase.DeliverCredentials);
                continue;
            }
            if (line == ">PASSWORD:Need 'Private Key' password" && !keySent)
            {
                if (!established)
                    progress.MoveTo(VpnPhase.PrivateKey);
                if (!await SubmitKeyPasswordAsync())
                    return new("VPN private key sign-in canceled. Choose Sign in to VPN to try again.", FailureKind.ManualRetry);
                keySent = true;
                if (!established)
                    AwaitLocalAcknowledgment(VpnPhase.DeliverKey);
                continue;
            }
            if (awaitingCredentials && (line == AuthPasswordAccepted || line == KeyPasswordAccepted))
            {
                awaitingCredentials = false;
                startup.CancelAfter(ConnectionTimeout);
                progress.MoveTo(line == AuthPasswordAccepted ? VpnPhase.CredentialsAccepted : VpnPhase.ServerConnect);
                continue;
            }
            if (Classify(line) is { } failure)
            {
                if (failure.Kind == FailureKind.Transient && !established &&
                    line.StartsWith(">FATAL:", StringComparison.Ordinal) && !line.Contains("AUTH_FAILED", StringComparison.Ordinal))
                    return progress.DescribeNetworkExit(failure);
                return failure;
            }
            if (KnownNetworkFailure(line) is { } networkFailure)
                progress.ObserveNetworkFailure(networkFailure, publish: !established);
            if (!line.StartsWith(">STATE:", StringComparison.Ordinal))
                continue;

            string[] fields = line[7..].Split(',');
            if (fields.Length < 3)
                continue;
            if (fields[1] == "CONNECTED" && fields[2] == "SUCCESS")
            {
                var addresses = ParseTunnelAddresses(fields);
                if (addresses.Length == 0)
                    return new("The VPN server did not assign a usable tunnel address. Ask your VPN administrator to check the profile.", FailureKind.ActionRequired);
                established = true;
                startup.CancelAfter(Timeout.Infinite);
                progress.MoveTo(VpnPhase.Connected);
                changed(new Tunnel(addresses));
            }
            else if (fields[1] is "RECONNECTING" or "EXITING")
            {
                if (StateDisconnectReason(fields[2]) is { } disconnect)
                    progress.ObserveNetworkFailure(disconnect, publish: !established);
                if (established)
                    return progress.DescribeFailure(timedOut: false);
            }
            else if (!established && StatePhase(fields[1]) is { } phase)
            {
                // A reported server phase can arrive before OpenVPN flushes its acknowledgement.
                if (awaitingCredentials)
                {
                    awaitingCredentials = false;
                    startup.CancelAfter(ConnectionTimeout);
                }
                progress.MoveTo(phase);
            }
        }
        return progress.DescribeFailure(timedOut: false);

        void AwaitLocalAcknowledgment(VpnPhase phase)
        {
            awaitingCredentials = true;
            progress.MoveTo(phase);
            startup.CancelAfter(CredentialsTimeout);
        }

        async Task<bool> SubmitAuthenticationAsync()
        {
            var credentials = await WaitForInputAsync(authenticate);
            if (credentials is null)
                return false;
            var password = string.IsNullOrEmpty(settings.Password) ? credentials.Password : settings.Password;
            if (string.IsNullOrEmpty(password) || !VpnAuthentication.IsValidCode(credentials.Code))
                throw new SetupException("A VPN password and a six-digit authenticator code are required.");

            if (!established)
                progress.MoveTo(VpnPhase.DeliverCredentials);
            using var usernameDeadline = CancellationTokenSource.CreateLinkedTokenSource(processToken);
            usernameDeadline.CancelAfter(CredentialsTimeout);
            try
            {
                await WriteAndClearAsync(network, EncodeCredential("username \"Auth\" ", settings.Username), usernameDeadline.Token);
                await RequireAcknowledgmentAsync(reader, AuthUsernameAccepted, deferred, usernameDeadline.Token);
            }
            catch (OperationCanceledException) when (!processToken.IsCancellationRequested)
            {
                throw new SetupException("OpenVPN did not acknowledge the username over its local control connection. Check the OpenVPN installation and VPN settings.");
            }
            await WriteAndClearAsync(network, EncodeCredential("password \"Auth\" ", password, credentials.Code), processToken);
            return true;
        }

        async Task<bool> SubmitKeyPasswordAsync()
        {
            var password = string.IsNullOrEmpty(settings.KeyPassword) ? await WaitForInputAsync(unlockKey) : settings.KeyPassword;
            if (string.IsNullOrEmpty(password))
                return false;
            await WriteAndClearAsync(network, EncodeCredential("password \"Private Key\" ", password), processToken);
            return true;
        }

        async Task<T?> WaitForInputAsync<T>(Func<CancellationToken, Task<T?>> request) where T : class
        {
            startup.CancelAfter(Timeout.InfiniteTimeSpan);
            var result = await request(processToken);
            processToken.ThrowIfCancellationRequested();
            if (!established)
                startup.CancelAfter(ConnectionTimeout);
            return result;
        }
    }

    private static async Task RequireAcknowledgmentAsync(StreamReader reader, string expected, Queue<string> deferred,
        CancellationToken token)
    {
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (line == expected)
                return;
            if (line.StartsWith("ERROR:", StringComparison.Ordinal) && Classify(line) is { } failure)
                throw new SetupException(failure.Reason);
            if (!line.StartsWith('>'))
                continue;
            if (deferred.Count >= 128)
                throw new SetupException("OpenVPN sent unexpected local control messages. Check the OpenVPN version in Settings.");
            deferred.Enqueue(line);
        }
        throw new SetupException("OpenVPN closed its local control connection before acknowledging a command. Check OpenVPN in Settings.");
    }

    private static VpnPhase? StatePhase(string state) => state switch
    {
        "CONNECTING" => VpnPhase.ServerConnect,
        "RESOLVE" => VpnPhase.Resolve,
        "TCP_CONNECT" => VpnPhase.TcpConnect,
        "WAIT" => VpnPhase.WaitServer,
        "AUTH" => VpnPhase.ServerAuth,
        "AUTH_PENDING" => VpnPhase.ServerPending,
        "GET_CONFIG" => VpnPhase.TunnelConfig,
        "ASSIGN_IP" => VpnPhase.TunnelAddress,
        "ADD_ROUTES" => VpnPhase.TunnelRoutes,
        _ => null
    };

    private const string CheckConnection = "Check your internet connection or contact your VPN administrator.";
    private const string CheckTunnel = "Ask your VPN administrator to check the tunnel profile and Windows driver.";

    private static Failure? StateDisconnectReason(string reason) => reason switch
    {
        "connection-reset" or "transport-error" => new($"The VPN connection was reset. {CheckConnection}"),
        "ping-restart" or "server_poll" => new($"The VPN server stopped responding. {CheckConnection}"),
        "dco-connect-timeout" => new($"The Windows VPN driver timed out reaching the server. {CheckConnection}"),
        "dco-connect-error" => new($"The Windows VPN driver could not connect to the server. {CheckConnection}"),
        "tls-error" => new($"VPN TLS negotiation ended unexpectedly. {CheckConnection}"),
        "resolve" or "resolve-failed" => new("Windows could not resolve the VPN server. Check DNS or ask your VPN administrator."),
        _ => null
    };

    private static Failure? KnownNetworkFailure(string line)
    {
        if (!line.StartsWith(">LOG:", StringComparison.Ordinal) && !line.StartsWith(">FATAL:", StringComparison.Ordinal))
            return null;
        if (line.Contains("Cannot resolve host", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Cannot resolve address", StringComparison.OrdinalIgnoreCase))
            return new("Windows could not resolve the VPN server. Check DNS or ask your VPN administrator.");
        if (line.Contains("TLS key negotiation failed", StringComparison.OrdinalIgnoreCase))
            return new($"The VPN server did not complete the TLS handshake. {CheckConnection}");
        if (line.Contains("dco connect timeout", StringComparison.OrdinalIgnoreCase))
            return StateDisconnectReason("dco-connect-timeout");
        if (line.Contains("dco connect error", StringComparison.OrdinalIgnoreCase))
            return StateDisconnectReason("dco-connect-error");
        if (line.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("WSAECONNREFUSED", StringComparison.OrdinalIgnoreCase))
            return new("The VPN server refused the connection. Ask your VPN administrator to check the server and port.");
        if (line.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("No route to host", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("WSAENETUNREACH", StringComparison.OrdinalIgnoreCase))
            return new($"Windows has no network route to the VPN server. {CheckConnection}");
        return null;
    }

    internal enum VpnPhase
    {
        ProfileCheck, Service, Launch, ProfileTransfer, ManagementPeer, ManagementAuth, ManagementConfigure,
        ServerConnect, Resolve, TcpConnect, WaitServer, PrepareCredentials, PrivateKey,
        DeliverCredentials, DeliverKey, CredentialsAccepted, ServerAuth, ServerPending,
        TunnelConfig, TunnelAddress, TunnelRoutes, Connected
    }

    internal sealed class AttemptProgress(Action<VpnProgress>? publish)
    {
        private VpnPhase phase = VpnPhase.ServerConnect;
        private VpnProgress? lastStatus;
        private Failure? lastNetworkFailure;

        internal void MoveTo(VpnPhase current)
        {
            phase = current;
            if (phase is VpnPhase.TunnelConfig or VpnPhase.TunnelAddress or VpnPhase.TunnelRoutes or VpnPhase.Connected)
                lastNetworkFailure = null;
            if (phase == VpnPhase.Connected)
                return;

            Report(phase switch
            {
                VpnPhase.ProfileCheck => new("VPN checking profile", "Checking the saved profile and OpenVPN installation."),
                VpnPhase.Service => new("VPN opening local service", "Waiting for the OpenVPN Interactive Service."),
                VpnPhase.Launch => new("VPN starting OpenVPN", "Waiting for the Interactive Service to launch the VPN process."),
                VpnPhase.ProfileTransfer => new("VPN loading profile", "Sending the saved profile to the verified OpenVPN process."),
                VpnPhase.ManagementPeer => new("VPN opening local control", "Waiting for OpenVPN to open its local management connection."),
                VpnPhase.ManagementAuth => new("VPN verifying local control", "Authenticating the local OpenVPN management connection."),
                VpnPhase.ManagementConfigure => new("VPN initializing local control", "Waiting for OpenVPN to enable status updates and start the connection."),
                VpnPhase.Resolve => new("VPN resolving server", "Looking up the VPN server address."),
                VpnPhase.TcpConnect => new("VPN opening server connection", "Connecting to the VPN server over TCP."),
                VpnPhase.WaitServer => new("VPN waiting for server", "Waiting for the initial reply from the VPN server."),
                VpnPhase.PrepareCredentials => new("VPN preparing sign-in", "Preparing the password and a current authenticator code."),
                VpnPhase.PrivateKey => new("VPN unlocking private key", "Preparing the private key passphrase."),
                VpnPhase.DeliverCredentials => new("VPN handing sign-in to OpenVPN", "Waiting for OpenVPN to acknowledge the password and code on its local control connection."),
                VpnPhase.DeliverKey => new("VPN handing passphrase to OpenVPN", "Waiting for OpenVPN to acknowledge the private key passphrase."),
                VpnPhase.CredentialsAccepted => new("VPN starting server connection", "OpenVPN acknowledged the local sign-in input. Waiting for it to start the server connection."),
                VpnPhase.ServerAuth => new("VPN verifying sign-in", "Negotiating TLS and authenticating with the VPN server."),
                VpnPhase.ServerPending => new("VPN waiting for authentication", "The VPN server is still processing the sign-in request."),
                VpnPhase.TunnelConfig => new("VPN receiving tunnel settings", "Waiting for the VPN server to send the tunnel configuration."),
                VpnPhase.TunnelAddress => new("VPN assigning tunnel address", "Windows is configuring the VPN tunnel adapter."),
                VpnPhase.TunnelRoutes => new("VPN applying routes", "Windows is applying the routes needed for the VPN."),
                _ => new("VPN preparing connection", "Waiting for OpenVPN to request sign-in or begin connecting to the VPN server.")
            });
        }

        internal void ObserveNetworkFailure(Failure failure, bool publish)
        {
            lastNetworkFailure ??= failure;
            if (publish)
                Report(new("VPN checking server connection", lastNetworkFailure.Reason));
        }

        internal Failure DescribeNetworkExit(Failure failure) => lastNetworkFailure ?? failure;

        internal Failure DescribeFailure(bool timedOut)
        {
            if (lastNetworkFailure is { } networkFailure)
                return networkFailure;
            return phase switch
            {
                VpnPhase.ProfileCheck => new("Windows could not read the OpenVPN configuration. Check the profile and installation in Settings.", FailureKind.ActionRequired),
                VpnPhase.Service => new("The OpenVPN Interactive Service did not answer. In Settings, choose Prepare this Windows account.", FailureKind.ActionRequired),
                VpnPhase.Launch => new(timedOut
                    ? "The Interactive Service timed out starting OpenVPN. In Settings, choose Prepare this Windows account."
                    : "The Interactive Service could not start OpenVPN. In Settings, choose Prepare this Windows account.", FailureKind.ActionRequired),
                VpnPhase.ProfileTransfer => new(timedOut
                    ? "OpenVPN timed out loading the profile. Re-import the profile in Settings."
                    : "OpenVPN stopped while loading the profile. Re-import the profile in Settings.", FailureKind.ActionRequired),
                VpnPhase.ManagementPeer or VpnPhase.ManagementAuth => new("OpenVPN did not establish a verified local control connection. Check the installation and prepare this Windows account in Settings.", FailureKind.ActionRequired),
                VpnPhase.ManagementConfigure => new("OpenVPN did not finish initializing its local control connection. Check the OpenVPN installation in Settings.", FailureKind.ActionRequired),
                VpnPhase.Resolve => new("The VPN server name could not be resolved. Check DNS or contact your VPN administrator."),
                VpnPhase.ServerAuth => new($"VPN authentication or TLS negotiation did not complete. {CheckConnection}"),
                VpnPhase.ServerPending => new("The VPN server did not finish processing sign-in. Ask your VPN administrator to check authentication."),
                VpnPhase.PrepareCredentials => new("OpenVPN stopped before receiving the sign-in details. Try signing in again."),
                VpnPhase.PrivateKey => new("OpenVPN stopped while opening the VPN private key. Check the profile and key passphrase in Settings.", FailureKind.ActionRequired),
                VpnPhase.DeliverCredentials => new("OpenVPN did not acknowledge the password and code over its local control connection. Check OpenVPN and the VPN settings before retrying.", FailureKind.ActionRequired),
                VpnPhase.DeliverKey => new("OpenVPN did not acknowledge the private key passphrase. Check OpenVPN and the VPN profile in Settings.", FailureKind.ActionRequired),
                VpnPhase.CredentialsAccepted or VpnPhase.ServerConnect => new("OpenVPN did not report starting a VPN server connection. Ask your VPN administrator to check the client and profile."),
                VpnPhase.TunnelConfig => new($"The server did not finish sending the VPN settings. {CheckTunnel}"),
                VpnPhase.TunnelAddress => new($"Windows did not finish assigning the VPN adapter an address. {CheckTunnel}", FailureKind.ActionRequired),
                VpnPhase.TunnelRoutes => new($"Windows did not finish setting up the VPN routes. {CheckTunnel}", FailureKind.ActionRequired),
                VpnPhase.Connected => new($"The VPN tunnel disconnected. {CheckConnection}"),
                _ => new($"The VPN server did not answer. {CheckConnection}")
            };
        }

        private void Report(VpnProgress status)
        {
            if (status == lastStatus)
                return;
            lastStatus = status;
            publish?.Invoke(status);
        }
    }

    internal static IPAddress[] ParseTunnelAddresses(string[] fields)
    {
        var addresses = new List<IPAddress>(2);
        foreach (int index in new[] { 3, 8 })
        {
            if (fields.Length > index && IPAddress.TryParse(fields[index], out var ip) &&
                !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(ip))
                addresses.Add(ip);
        }
        return addresses.ToArray();
    }

    internal static Failure? Classify(string line)
    {
        if (line.StartsWith("ERROR:", StringComparison.Ordinal))
        {
            if (line.Contains("username", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("password", StringComparison.OrdinalIgnoreCase))
                return new("OpenVPN rejected a local sign-in command. Check the saved VPN credentials and OpenVPN version in Settings.", FailureKind.ActionRequired);
            return new("OpenVPN rejected a local control command. Check the OpenVPN installation in Settings.", FailureKind.ActionRequired);
        }
        if (line.StartsWith(">PASSWORD:", StringComparison.Ordinal) && line.Contains("'Private Key'", StringComparison.Ordinal))
            return new("The VPN private key passphrase was rejected. Check the passphrase or re-import the profile in Settings.", FailureKind.ActionRequired);
        if (line.StartsWith(">PASSWORD:Verification Failed:", StringComparison.Ordinal))
            return AuthFailure(line);
        if (line.StartsWith(">PASSWORD:Need ", StringComparison.Ordinal) ||
            line.StartsWith(">NEED-", StringComparison.Ordinal) ||
            line.StartsWith(">INFOMSG:WEB_AUTH:", StringComparison.Ordinal) ||
            line.StartsWith(">INFOMSG:CR_TEXT:", StringComparison.Ordinal))
            return new("This VPN server uses a different sign-in method. Ask your administrator for a compatible profile or client.", FailureKind.ActionRequired);
        if (line.StartsWith(">STATE:", StringComparison.Ordinal) && line.Contains(",auth-failure,", StringComparison.Ordinal))
            return new("VPN credentials or one-time code rejected", FailureKind.CredentialRejected);
        if (!line.StartsWith(">LOG:", StringComparison.Ordinal) && !line.StartsWith(">FATAL:", StringComparison.Ordinal))
            return null;
        if (line.Contains("VERIFY ERROR", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("VERIFY KU ERROR", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("VERIFY EKU ERROR", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("VERIFY X509NAME ERROR", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("certificate verify failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("certificate verification failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("peer fingerprint mismatch", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("VERIFY CERT ERROR", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("VERIFY SCRIPT ERROR", StringComparison.OrdinalIgnoreCase))
            return new("The VPN server certificate could not be verified. Ask your VPN administrator for an updated profile.", FailureKind.ActionRequired);
        int authFailure = line.IndexOf("AUTH_FAILED", StringComparison.Ordinal);
        if (authFailure >= 0)
            return AuthFailure(line[(authFailure + "AUTH_FAILED".Length)..]);
        if (line.StartsWith(">FATAL:", StringComparison.Ordinal))
        {
            if (KnownNetworkFailure(line) is { } networkFailure)
                return networkFailure;
            if (line.Contains("connect-retry-max", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("RESOLVE", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("network", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("TLS handshake failed", StringComparison.OrdinalIgnoreCase))
                return new($"OpenVPN could not reach the VPN server or finish the handshake. {CheckConnection}");
            if (line.Contains("All TAP-Windows", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("There are no TAP-Windows", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Failed to open tun/tap", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Failed to open Wintun", StringComparison.OrdinalIgnoreCase))
                return new("OpenVPN could not open the Windows tunnel adapter. Ask your administrator to check the VPN driver.", FailureKind.ActionRequired);
            return new("OpenVPN stopped before the tunnel was ready. Check the profile in Settings or ask your administrator to check the VPN driver.", FailureKind.ActionRequired);
        }
        return null;
    }

    private static Failure AuthFailure(string reason)
    {
        if (reason.Contains("CRV1:", StringComparison.Ordinal) || reason.Contains("CR_TEXT:", StringComparison.Ordinal))
            return new("The VPN server requires a separate challenge-response flow", FailureKind.ActionRequired);
        if (reason.StartsWith(",Data channel cipher negotiation failed", StringComparison.Ordinal))
            return new("VPN data cipher negotiation failed. Ask your administrator for a compatible profile.", FailureKind.ActionRequired);
        if (reason is not ",TEMP" && !reason.StartsWith(",TEMP[", StringComparison.Ordinal) &&
            !reason.StartsWith(",TEMP:", StringComparison.Ordinal))
            return new("VPN credentials or one-time code rejected", FailureKind.CredentialRejected);

        TimeSpan? delay = null;
        int optionsEnd = reason.IndexOf(']');
        if (reason.StartsWith(",TEMP[", StringComparison.Ordinal) && optionsEnd >= 6)
        {
            foreach (string option in reason[6..optionsEnd].Split(','))
            {
                const string backoff = "backoff ";
                if (!option.StartsWith(backoff, StringComparison.Ordinal))
                    continue;
                if (!int.TryParse(option.AsSpan(backoff.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) ||
                    seconds > 86400)
                    return new("The VPN server requested an extended sign-in pause", FailureKind.ActionRequired);
                var value = TimeSpan.FromSeconds(seconds);
                if (delay is null || value > delay)
                    delay = value;
            }
        }
        return new("The VPN server temporarily deferred sign-in", MinimumDelay: delay);
    }

    internal static byte[] EncodeCredential(string prefix, string value, string suffix = "")
    {
        if (value.Any(char.IsControl) || suffix.Any(char.IsControl))
            throw new SetupException("Review the VPN credentials in Settings.");
        var command = new char[prefix.Length + 2 * (value.Length + suffix.Length) + 3];
        try
        {
            prefix.CopyTo(0, command, 0, prefix.Length);
            int length = prefix.Length;
            command[length++] = '"';
            WriteEscaped(value);
            WriteEscaped(suffix);
            command[length++] = '"';
            command[length++] = '\n';
            return Encoding.UTF8.GetBytes(command, 0, length);

            void WriteEscaped(string text)
            {
                foreach (char c in text)
                {
                    if (c is '\\' or '"')
                        command[length++] = '\\';
                    command[length++] = c;
                }
            }
        }
        finally { command.AsSpan().Clear(); }
    }

    private static Task SendAsync(Stream network, string command, CancellationToken token)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(command) + 1];
        Encoding.UTF8.GetBytes(command, bytes);
        bytes[^1] = (byte)'\n';
        return WriteAndClearAsync(network, bytes, token);
    }

    private static async Task WriteAndClearAsync(Stream stream, byte[] bytes, CancellationToken token)
    {
        try { await stream.WriteAsync(bytes, token); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task StopAsync(Task? serviceClosed, TcpClient? management, bool authenticated)
    {
        using (management)
        {
            if (management is not null && authenticated)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await SendAsync(management.GetStream(), "signal SIGTERM", stop.Token); }
                catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or OperationCanceledException) { }
            }
        }
        if (serviceClosed is not null)
        {
            try { await serviceClosed.WaitAsync(TimeSpan.FromSeconds(7)); }
            catch (TimeoutException) { }
        }
    }

    internal static uint ServiceProcessId()
    {
        nint scm = OpenSCManager(null, null, 1);
        if (scm == 0)
            return 0;
        try
        {
            nint service = OpenService(scm, "OpenVPNServiceInteractive", 4);
            if (service == 0)
                return 0;
            try
            {
                return QueryServiceStatusEx(service, 0, out var status, 36, out _) && status.CurrentState == 4
                    ? status.ProcessId : 0;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceExitCode;
        public uint CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(nint service, int level, out ServiceStatus status, int size, out int needed);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(nint table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool sort, int family, int tableClass, int reserved);
}
