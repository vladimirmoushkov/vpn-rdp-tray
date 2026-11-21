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

internal sealed record Tunnel(IPAddress[] Addresses);

internal static class OpenVpnSession
{
    internal static async Task<Failure> RunAsync(VpnSettings settings, Totp totp, Action<Tunnel?> changed,
        CancellationToken cancellationToken, Action? managementAuthenticated = null)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(95 + settings.Period));
        var token = startup.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        using var service = new NamedPipeClientStream(".", @"openvpn\service", PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        using var childEnded = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var stopMonitor = new CancellationTokenSource();
        Task? serviceClosed = null;
        TcpClient? management = null;
        bool authenticated = false;
        try
        {
            OpenVpnProfile.VerifySavedServerIdentity(settings.Profile);
            var install = OpenVpnInstall.Get();
            if (!install.IsProvisioned)
                return new("OpenVPN profile needs administrator provisioning in Setup", FailureKind.ActionRequired);

            using var stream = new NamedPipeServerStream(MachineSetup.ProfilePipe, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly |
                PipeOptions.FirstPipeInstance);

            uint servicePid = ServiceProcessId();
            if (servicePid == 0)
                return new("Start the OpenVPN Interactive Service in Setup", FailureKind.ActionRequired);
            await service.ConnectAsync(5000, token);
            if (!GetNamedPipeServerProcessId(service.SafePipeHandle, out uint peer) || peer != servicePid)
                return new("The OpenVPN service pipe could not be verified", FailureKind.ActionRequired);
            service.ReadMode = PipeTransmissionMode.Message;
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var proof = CreateManagementProof();
            var status = await LaunchAsync(service, install, port, proof, token);
            if (status.Pid == 0)
                return status.Failure!;

            // The service owns the child. Its launch pipe closes after the child and privileged cleanup finish.
            serviceClosed = TrackChildAsync(service, childEnded, stopMonitor.Token);
            if (!await TransferProfileAsync(stream, status.Pid, settings.Profile, childEnded.Token))
                return new("The OpenVPN profile pipe could not be verified", FailureKind.ActionRequired);
            stream.Dispose();
            management = await AcceptChildAsync(listener, port, status.Pid, childEnded.Token);
            listener.Stop();
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
                    return new("OpenVPN management authentication failed", FailureKind.ActionRequired);
            }
            if (!authenticated)
                return new("OpenVPN management closed before authenticating");
            managementAuthenticated?.Invoke();
            await SendAsync(network, "state on\nlog on\nhold release", token);
            return await MonitorAsync(reader, network, settings, totp, changed, startup, childEnded.Token);
        }
        catch (SetupException ex) { return new(ex.Message, FailureKind.ActionRequired); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (serviceClosed?.IsCompleted == true || childEnded.IsCancellationRequested && !startup.IsCancellationRequested)
                return authenticated ? new("OpenVPN disconnected") : StartupFailed();
            return new("VPN connection timed out");
        }
        catch (TimeoutException) { return new("The OpenVPN Interactive Service timed out"); }
        catch (Exception ex) when (ex is IOException or SocketException or Win32Exception or InvalidOperationException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new("The local OpenVPN connection ended unexpectedly");
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
    }

    private static Failure StartupFailed() =>
        new("OpenVPN exited before loading the profile and management connection. Review the VPN profile in Setup.",
            FailureKind.ActionRequired);

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
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
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
        return (0, new("The OpenVPN service refused to start the approved profile. Check Setup and service policy.",
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

    private static async Task<Failure> MonitorAsync(StreamReader reader, Stream network, VpnSettings settings,
        Totp totp, Action<Tunnel?> changed, CancellationTokenSource startup, CancellationToken processToken)
    {
        var token = startup.Token;
        bool keySent = false, authSubmitted = false, established = false;
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (line == ">PASSWORD:Need 'Auth' username/password")
            {
                if (authSubmitted && !established)
                    return new("OpenVPN repeated the sign-in request before connecting", FailureKind.CredentialRejected);
                string code = await totp.FreshCodeAsync(processToken);
                await WriteAndClearAsync(network, EncodeCredential("username \"Auth\" ", settings.Username), token);
                await WriteAndClearAsync(network, EncodeCredential("password \"Auth\" ", settings.Password, code), token);
                authSubmitted = true;
                continue;
            }
            if (line == ">PASSWORD:Need 'Private Key' password" && !keySent && !string.IsNullOrEmpty(settings.KeyPassword))
            {
                keySent = true;
                await WriteAndClearAsync(network, EncodeCredential("password \"Private Key\" ", settings.KeyPassword), token);
                continue;
            }
            if (Classify(line) is { } failure)
                return failure;
            if (!line.StartsWith(">STATE:", StringComparison.Ordinal))
                continue;

            string[] fields = line[7..].Split(',');
            if (fields.Length < 3)
                continue;
            if (fields[1] == "CONNECTED" && fields[2] == "SUCCESS")
            {
                var addresses = ParseTunnelAddresses(fields);
                if (addresses.Length == 0)
                    return new("OpenVPN reported a tunnel without a usable address", FailureKind.ActionRequired);
                established = true;
                startup.CancelAfter(Timeout.Infinite);
                changed(new Tunnel(addresses));
            }
            else if (established && fields[1] is "RECONNECTING" or "EXITING")
                return new("VPN tunnel dropped");
        }
        return new(established ? "VPN tunnel dropped" : "VPN connection ended before the tunnel was ready");
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
        if (line.StartsWith(">PASSWORD:", StringComparison.Ordinal) && line.Contains("'Private Key'", StringComparison.Ordinal))
            return new("VPN private key passphrase required or rejected", FailureKind.ActionRequired);
        if (line.StartsWith(">PASSWORD:Verification Failed:", StringComparison.Ordinal))
            return AuthFailure(line);
        if (line.StartsWith(">PASSWORD:Need ", StringComparison.Ordinal) ||
            line.StartsWith(">NEED-", StringComparison.Ordinal) ||
            line.StartsWith(">INFOMSG:WEB_AUTH:", StringComparison.Ordinal) ||
            line.StartsWith(">INFOMSG:CR_TEXT:", StringComparison.Ordinal) ||
            line.StartsWith(">STATE:", StringComparison.Ordinal) && line.Contains(",AUTH_PENDING,", StringComparison.Ordinal))
            return new("The VPN server requires an unsupported sign-in challenge", FailureKind.ActionRequired);
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
            return new("VPN server certificate validation failed", FailureKind.ActionRequired);
        int authFailure = line.IndexOf("AUTH_FAILED", StringComparison.Ordinal);
        if (authFailure >= 0)
            return AuthFailure(line[(authFailure + "AUTH_FAILED".Length)..]);
        if (line.StartsWith(">FATAL:", StringComparison.Ordinal))
        {
            if (line.Contains("connect-retry-max", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("RESOLVE", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("network", StringComparison.OrdinalIgnoreCase))
                return new("VPN server unavailable or timed out");
            return new("OpenVPN could not use the profile or Windows tunnel driver", FailureKind.ActionRequired);
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
            throw new SetupException("Review the VPN credentials in Setup.");
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
