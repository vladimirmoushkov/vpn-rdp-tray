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

internal enum FailureKind { Transient, ActionRequired, CredentialRejected, ManualRetry, Dismissed }
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
            // Its final diagnostics may still be buffered after the service reports that the child exited.
            await SendAsync(network, "state on", childEnded.Token);
            await RequireAcknowledgmentAsync(reader, "SUCCESS: real-time state notification set to ON", deferred, token);
            await SendAsync(network, "log on", childEnded.Token);
            await RequireAcknowledgmentAsync(reader, "SUCCESS: real-time log notification set to ON", deferred, token);
            await SendAsync(network, "hold release", childEnded.Token);
            await RequireAcknowledgmentAsync(reader, "SUCCESS: hold release succeeded", deferred, token);
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
            if (!established && ClientCertificateWarning(line) is { } certificateWarning)
                progress.ObserveClientCertificateWarning(certificateWarning);
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
            if (CertificateProblem(line) is { } certificateProblem)
                throw new SetupException(certificateProblem);
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
        var message = ManagementLogMessage(line);
        if (message.IsEmpty || message.StartsWith("VERIFY ", StringComparison.Ordinal))
            return null;
        if (message.StartsWith("TLS Error: incoming packet authentication failed", StringComparison.Ordinal) ||
            message.StartsWith("TLS Error: cannot locate HMAC in incoming packet", StringComparison.Ordinal) ||
            message.StartsWith("TLS Error: tls-crypt unwrapping failed", StringComparison.Ordinal))
            return new("OpenVPN could not authenticate a control packet. If this repeats, ask your VPN administrator to check the endpoint and the profile's tls-auth or tls-crypt key.");
        if (line.Contains("Cannot resolve host", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Cannot resolve address", StringComparison.OrdinalIgnoreCase))
            return new("Windows could not resolve the VPN server. Check DNS or ask your VPN administrator.");
        if (line.Contains("TLS key negotiation failed", StringComparison.OrdinalIgnoreCase))
            return new("The VPN server did not complete the TLS handshake. Check the network, then ask your VPN administrator to check the endpoint, protocol and server log.");
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

        internal void ObserveClientCertificateWarning(string warning) =>
            Report(new("VPN client certificate warning", warning));

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
        if (CertificateProblem(line) is { } certificateProblem)
            return new(certificateProblem, FailureKind.ActionRequired);
        var message = ManagementLogMessage(line);
        const string authMessage = "AUTH: Received control message: ";
        if (message.StartsWith(authMessage, StringComparison.Ordinal))
            message = message[authMessage.Length..];
        const string authFailed = "AUTH_FAILED";
        if (message.SequenceEqual(authFailed) || message.StartsWith("AUTH_FAILED,", StringComparison.Ordinal))
            return AuthFailure(message[authFailed.Length..].ToString());
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

    private const string CheckServerCertificate = "Ask your VPN administrator to check the server certificate and provide an updated profile.";
    private const string CheckClientCertificate = "Ask your VPN administrator for a current client certificate and profile.";
    private const string ClientKeyMismatch = "The client certificate and private key in the VPN profile do not match. Import a matched pair from your VPN administrator in Settings.";
    private const string CheckClock = "Check this PC's date and time.";
    private const string UnreadableServerName = "OpenVPN could not read the required VPN server certificate subject or name. Ask your VPN administrator to check the certificate's Common Name and export a compatible profile.";
    private const string TlsVersionMismatch = "OpenVPN and the endpoint could not agree on a TLS version. Ask your VPN administrator to confirm the endpoint and support TLS 1.2 or newer.";
    private const string TlsCryptoMismatch = "The VPN TLS handshake could not agree on a cipher, signature or key exchange. Ask your VPN administrator for a compatible server configuration and profile.";

    private static string? ClientCertificateWarning(string line) => ManagementLogMessage(line) switch
    {
        "WARNING: Your certificate has expired!" => $"OpenVPN reports that this PC's client certificate has expired. {CheckClock} {CheckClientCertificate}",
        "WARNING: Your certificate is not yet valid!" => $"OpenVPN reports that this PC's client certificate is not yet valid. {CheckClock} {CheckClientCertificate}",
        _ => null
    };

    private static string? CertificateProblem(string line)
    {
        var message = ManagementLogMessage(line);
        if (message.StartsWith("VERIFY X509NAME ERROR", StringComparison.Ordinal))
            return $"The VPN server certificate name does not match the saved profile. {CheckServerCertificate}";
        if (message.StartsWith("TLS Error: --tls-verify/--peer-fingerprint certificate hash verification failed", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("peer fingerprint mismatch", StringComparison.OrdinalIgnoreCase))
            return $"The VPN server certificate fingerprint does not match the saved profile. {CheckServerCertificate}";
        if (message.StartsWith("VERIFY KU ERROR", StringComparison.Ordinal) ||
            message.StartsWith("VERIFY EKU ERROR", StringComparison.Ordinal))
            return $"The VPN server certificate is missing the required key usage or Server Authentication purpose. {CheckServerCertificate}";
        if (message.StartsWith("TLS Error: Convoluted certificate chain detected", StringComparison.Ordinal))
            return "The VPN server certificate chain is too long. Ask your VPN administrator to check which certificates the server sends.";
        if (message.StartsWith("VERIFY ERROR: could not extract ", StringComparison.Ordinal) ||
            message.StartsWith("VERIFY ERROR: could not append ", StringComparison.Ordinal))
            return UnreadableServerName;
        if (message.StartsWith("VERIFY ERROR", StringComparison.Ordinal))
            return DescribeServerCertificateError(message);
        if (message.StartsWith("VERIFY CERT ERROR", StringComparison.Ordinal) ||
            message.StartsWith("VERIFY SCRIPT ERROR", StringComparison.Ordinal) ||
            message.StartsWith("certificate verify failed", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("certificate verification failed", StringComparison.OrdinalIgnoreCase))
            return $"OpenVPN could not verify the VPN server certificate. {CheckServerCertificate}";

        if (message.StartsWith("Cannot load CA certificate file", StringComparison.Ordinal) ||
            message.StartsWith("Cannot get certificate store", StringComparison.Ordinal))
            return "OpenVPN could not load the CA certificates from the VPN profile. Ask your VPN administrator for a complete profile and import it in Settings.";
        if (message.StartsWith("Private key does not match the certificate", StringComparison.Ordinal))
            return ClientKeyMismatch;
        if (message.StartsWith("Cannot load private key file", StringComparison.Ordinal) ||
            message.StartsWith("Cannot use private key", StringComparison.Ordinal))
            return "OpenVPN could not open the client private key. Check its passphrase and re-import the VPN profile in Settings.";
        if (message.StartsWith("Error reading inline PKCS#12 file", StringComparison.Ordinal) ||
            message.StartsWith("Error reading PKCS#12 file", StringComparison.Ordinal) ||
            message.StartsWith("Decoding PKCS12 failed", StringComparison.Ordinal))
            return "OpenVPN could not open the VPN certificate bundle (PKCS#12). Check its passphrase or ask your VPN administrator to re-export it with current encryption.";
        if (message.StartsWith("Cannot load inline certificate file", StringComparison.Ordinal) ||
            message.StartsWith("Cannot load certificate file", StringComparison.Ordinal) ||
            message.StartsWith("Cannot load extra-certs file", StringComparison.Ordinal) ||
            message.StartsWith("Cannot use certificate", StringComparison.Ordinal))
            return "OpenVPN could not load the client certificate or chain from the VPN profile. Import a complete profile from your VPN administrator in Settings.";

        return DescribeOpenSslCertificateError(message);
    }

    private static string DescribeServerCertificateError(ReadOnlySpan<char> message)
    {
        // OpenVPN writes the verifier's error before the certificate subject and serial. Inspect only that field.
        const string verifyPrefix = "VERIFY ERROR: depth=";
        var reason = ReadOnlySpan<char>.Empty;
        var certificate = "VPN server certificate";
        if (message.StartsWith(verifyPrefix, StringComparison.Ordinal))
        {
            var result = message[verifyPrefix.Length..];
            int separator = result.IndexOf(',');
            if (separator >= 0 && int.TryParse(result[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out int depth) && depth > 0)
                certificate = "CA or intermediate certificate in the VPN server chain";
            const string error = ", error=";
            if (separator >= 0 && result[separator..].StartsWith(error, StringComparison.Ordinal))
            {
                reason = result[(separator + error.Length)..];
                int subject = reason.IndexOf(':');
                if (subject >= 0)
                    reason = reason[..subject];
            }
            else if (separator >= 0 && result[separator..].StartsWith(", could not extract X509 ", StringComparison.Ordinal))
                return UnreadableServerName;
        }
        if (message.SequenceEqual("VERIFY ERROR: CRL not loaded"))
            reason = "unable to get certificate CRL";

        return reason switch
        {
            "certificate has expired" => $"The {certificate} has expired. {CheckClock} Ask your VPN administrator to renew the certificate.",
            "certificate is not yet valid" => $"The {certificate} is not yet valid. {CheckClock} Ask your VPN administrator to check the certificate dates.",
            "certificate revoked" => $"The {certificate} has been revoked. Ask your VPN administrator for a replacement.",
            "CRL has expired" or "CRL is not yet valid" or "unable to get certificate CRL" or "unable to get CRL issuer certificate" =>
                "OpenVPN could not validate the certificate revocation list. Check this PC's date and time and ask your VPN administrator for an updated profile.",
            "unable to get issuer certificate" or "unable to get local issuer certificate" or "unable to verify the first certificate" or
            "self-signed certificate" or "self-signed certificate in certificate chain" or "certificate not trusted" or
            "invalid CA certificate" or "path length constraint exceeded" =>
                "OpenVPN could not establish trust in the VPN server certificate chain. Ask your VPN administrator to check the server's intermediates and the CA in the profile.",
            "unsuitable certificate purpose" or "key usage does not include certificate signing" or "key usage does not include digital signature" =>
                $"The VPN certificate chain is missing a required key usage. {CheckServerCertificate}",
            "hostname mismatch" or "IP address mismatch" => $"The VPN server certificate identity does not match the profile. {CheckServerCertificate}",
            "EE certificate key too weak" or "CA certificate key too weak" or "CA signature digest algorithm too weak" =>
                "The VPN server certificate chain uses a key or signature that OpenVPN rejects. Ask your VPN administrator to replace the certificate chain.",
            "certificate signature failure" or "unsupported signature algorithm" =>
                "OpenVPN could not verify a signature in the VPN server certificate chain. Ask your VPN administrator to check or replace the chain.",
            _ => $"OpenVPN could not verify the VPN server certificate. {CheckServerCertificate}"
        };
    }

    private static string? DescribeOpenSslCertificateError(ReadOnlySpan<char> message)
    {
        const string prefix = "OpenSSL: error:";
        if (!message.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        // ERR_error_string uses error:code:library:function:reason. Ignore diagnostic data appended afterward.
        var reason = message[prefix.Length..];
        for (int field = 0; field < 3; field++)
        {
            int separator = reason.IndexOf(':');
            if (separator < 0)
                return null;
            reason = reason[(separator + 1)..];
        }
        int extra = reason.IndexOf(':');
        if (extra >= 0)
            reason = reason[..extra];

        const string alert = " alert ";
        int alertAt = reason.IndexOf(alert, StringComparison.Ordinal);
        if (alertAt >= 0)
            return reason[(alertAt + alert.Length)..] switch
            {
                "unknown ca" => $"The VPN server does not trust the issuer of this PC's client certificate. {CheckClientCertificate}",
                "certificate expired" => $"The VPN server reported that this PC's client certificate has expired. {CheckClientCertificate}",
                "certificate revoked" => $"The VPN server reported that this PC's client certificate has been revoked. {CheckClientCertificate}",
                "certificate required" or "no certificate" => $"The VPN server requires a client certificate. {CheckClientCertificate}",
                "bad certificate" or "certificate unknown" or "unsupported certificate" =>
                    $"The VPN server rejected this PC's client certificate. {CheckClientCertificate}",
                "protocol version" or "inappropriate fallback" => TlsVersionMismatch,
                "insufficient security" => "The VPN endpoint rejected the TLS security settings. Ask your VPN administrator to check the endpoint and profile against its TLS policy.",
                _ => null
            };

        return reason switch
        {
            "certificate verify failed" or "certificate verification failed" =>
                $"OpenVPN could not verify the VPN server certificate. {CheckServerCertificate}",
            "peer did not return a certificate" => $"The VPN server did not present a certificate. {CheckServerCertificate}",
            "key values mismatch" => ClientKeyMismatch,
            "ee key too small" or "ca key too small" or "ca md too weak" =>
                "OpenVPN rejected a weak key or signature while loading the client certificate or its chain. Ask your VPN administrator to reissue it.",
            "unsupported protocol" or "version too low" or "version too high" => TlsVersionMismatch,
            "no shared cipher" or "no shared groups" or "no shared signature algorithms" or
            "no suitable signature algorithm" or "legacy sigalg disallowed or unsupported" => TlsCryptoMismatch,
            "dh key too small" => "OpenVPN rejected weak Diffie-Hellman parameters. Ask your VPN administrator to update the server's TLS configuration.",
            _ => null
        };
    }

    private static ReadOnlySpan<char> ManagementLogMessage(string line)
    {
        if (line.StartsWith(">FATAL:", StringComparison.Ordinal))
            return line.AsSpan(">FATAL:".Length).TrimStart();
        if (!line.StartsWith(">LOG:", StringComparison.Ordinal))
            return default;
        var entry = line.AsSpan(">LOG:".Length);
        int timestamp = entry.IndexOf(',');
        if (timestamp < 0)
            return default;
        var flagsAndMessage = entry[(timestamp + 1)..];
        int flags = flagsAndMessage.IndexOf(',');
        return flags < 0 ? default : flagsAndMessage[(flags + 1)..].TrimStart();
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
