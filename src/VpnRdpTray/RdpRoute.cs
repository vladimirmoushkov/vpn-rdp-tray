using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VpnRdpTray;

internal sealed record RdpEndpoint(string Server, IPAddress[] Addresses, int Port);

internal static class RdpRoute
{
    internal enum PortCheck { NoVpnRoute, LocalSocketError, NetworkUnreachable, HostUnreachable, TimedOut, Reset, Refused, Connected }

    internal static async Task<(RdpEndpoint? Endpoint, Failure? Failure, bool MayNeedTunnelRefresh)> FindAsync(RdpSettings rdp, Tunnel tunnel,
        CancellationToken cancellationToken, Action<string, string>? progress = null)
    {
        if (!MachineSetup.HostsMatch(rdp))
            return (null, new("The Windows hosts entry differs from the saved configuration. Open Settings → Remote Desktop and save to apply the mapping.",
                FailureKind.ActionRequired), false);

        progress?.Invoke("RDP resolving hostname", "Looking up the saved Remote Desktop hostname with Windows DNS.");
        IPAddress[] addresses;
        string dnsFailure = "The RDP hostname did not resolve. Check the registered hostname or hosts entry in Settings.";
        bool dnsUnavailable = false;
        using (var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            dnsTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            try { addresses = await Dns.GetHostAddressesAsync(rdp.Host, dnsTimeout.Token); }
            catch (SocketException ex)
            {
                addresses = [];
                if (ex.SocketErrorCode is SocketError.TryAgain or SocketError.TimedOut or SocketError.NetworkDown or SocketError.NetworkUnreachable)
                {
                    dnsUnavailable = true;
                    dnsFailure = "Windows reported a temporary DNS failure for the RDP hostname. Check VPN DNS and the saved hostname.";
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                addresses = [];
                dnsUnavailable = true;
                dnsFailure = "The RDP hostname lookup timed out. Check VPN DNS and the saved hostname.";
            }
        }

        bool mappingMismatch = rdp.WindowsHosts && addresses.Length > 0 &&
            (addresses.Length != 1 || !addresses[0].Equals(IPAddress.Parse(rdp.Ip!)));
        bool safeHostname = !mappingMismatch && UsesTunnel(addresses, tunnel);
        var hostnamePort = PortCheck.NoVpnRoute;
        if (safeHostname)
        {
            progress?.Invoke("RDP testing port", $"Opening a TCP connection to port {rdp.Port} through the VPN.");
            hostnamePort = await CheckPortAsync(addresses, rdp.Port, tunnel, cancellationToken);
            if (hostnamePort == PortCheck.Connected)
                return (new(rdp.Host, addresses, rdp.Port), null, false);
        }

        PortCheck? savedIpPort = null;
        if (rdp.Ip is { } configured)
        {
            var address = IPAddress.Parse(configured);
            // A resolved address has already been tested under the same VPN route check.
            if (!safeHostname || !addresses.Contains(address))
            {
                if (UsesTunnel([address], tunnel))
                {
                    progress?.Invoke("RDP testing saved IP", $"Opening a TCP connection to port {rdp.Port} at the saved IP through the VPN.");
                    savedIpPort = await CheckPortAsync([address], rdp.Port, tunnel, cancellationToken);
                }
                else
                    savedIpPort = PortCheck.NoVpnRoute;
            }

            if (savedIpPort == PortCheck.Connected)
            {
                if (rdp.Mode == RdpMode.Classic && !rdp.WindowsHosts)
                    return (new(address.ToString(), [address], rdp.Port), null, false);
                return (null, new("The saved RDP IP answered on the TCP port. Check that the registered hostname or Windows hosts entry points to that address."), false);
            }
        }

        var (reason, mayNeedTunnelRefresh) = safeHostname ? DescribePortCheck(hostnamePort) : mappingMismatch
            ? ("The resolved RDP address differs from the saved Windows hosts entry. Review the mapping in Settings.", false)
            : addresses.Length != 0
                ? ("The RDP hostname resolves outside the VPN route. Check VPN DNS and routing.", true)
                : (dnsFailure, dnsUnavailable);

        if (savedIpPort is { } outcome)
        {
            var (savedReason, savedMayNeedTunnelRefresh) = DescribePortCheck(outcome);
            if (savedReason != reason)
                reason += $" The saved IP check also failed. {savedReason}";
            bool sawReply = outcome is PortCheck.Refused or PortCheck.Reset ||
                hostnamePort is PortCheck.Refused or PortCheck.Reset;
            mayNeedTunnelRefresh = !sawReply && !mappingMismatch && (mayNeedTunnelRefresh || savedMayNeedTunnelRefresh);
        }
        return (null, new(reason), mayNeedTunnelRefresh);
    }

    internal static async Task<(Failure? Failure, bool MayNeedTunnelRefresh)> CheckEndpointAsync(RdpEndpoint endpoint,
        Tunnel tunnel, CancellationToken cancellationToken)
    {
        var outcome = await CheckPortAsync(endpoint.Addresses, endpoint.Port, tunnel, cancellationToken);
        if (outcome == PortCheck.Connected)
            return (null, false);
        var (reason, mayNeedTunnelRefresh) = DescribePortCheck(outcome);
        return (new(reason), mayNeedTunnelRefresh);
    }

    internal static (string Reason, bool MayNeedTunnelRefresh) DescribePortCheck(PortCheck outcome) => outcome switch
    {
        PortCheck.Refused => ("The RDP TCP port refused the connection. Check that Remote Desktop is enabled and the saved port is correct.", false),
        PortCheck.Reset => ("The RDP TCP connection was reset. Check the Remote Desktop service and the remote firewall.", false),
        PortCheck.TimedOut => ("The RDP TCP port did not reply in time. The remote PC may be asleep or stalled, or the VPN path or firewall may be blocking it.", true),
        PortCheck.HostUnreachable => ("Windows reported the remote PC unreachable over the VPN. Check whether the PC is awake and reachable from the VPN.", true),
        PortCheck.NetworkUnreachable => ("Windows could not reach the RDP network through the VPN. Check the VPN route and local network connection.", true),
        PortCheck.NoVpnRoute => ("The RDP address does not have a verified VPN route. Check the tunnel and VPN routing.", true),
        PortCheck.LocalSocketError => ("Windows could not complete the RDP TCP test. Check local network settings and try again.", false),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    internal static PortCheck ClassifySocketError(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => PortCheck.Refused,
        SocketError.ConnectionReset => PortCheck.Reset,
        SocketError.TimedOut => PortCheck.TimedOut,
        SocketError.HostDown or SocketError.HostUnreachable => PortCheck.HostUnreachable,
        SocketError.NetworkDown or SocketError.NetworkUnreachable => PortCheck.NetworkUnreachable,
        SocketError.AddressNotAvailable => PortCheck.NoVpnRoute,
        _ => PortCheck.LocalSocketError
    };

    internal static bool UsesTunnel(IPAddress[] destinations, Tunnel tunnel)
    {
        if (destinations.Length == 0)
            return false;
        var interfaces = new HashSet<(AddressFamily, int)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var properties = nic.GetIPProperties();
            foreach (var local in properties.UnicastAddresses)
            {
                if (!tunnel.Addresses.Contains(local.Address))
                    continue;
                int? index = local.Address.AddressFamily == AddressFamily.InterNetwork
                    ? properties.GetIPv4Properties()?.Index : properties.GetIPv6Properties()?.Index;
                if (index > 0)
                    interfaces.Add((local.Address.AddressFamily, index.Value));
            }
        }
        return destinations.All(address => BestInterface(address) is int index && interfaces.Contains((address.AddressFamily, index)));
    }

    private static int? BestInterface(IPAddress address)
    {
        var socketAddress = new byte[28];
        BinaryPrimitives.WriteUInt16LittleEndian(socketAddress, (ushort)address.AddressFamily);
        if (address.AddressFamily == AddressFamily.InterNetwork)
            address.TryWriteBytes(socketAddress.AsSpan(4, 4), out _);
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            address.TryWriteBytes(socketAddress.AsSpan(8, 16), out _);
            BinaryPrimitives.WriteUInt32LittleEndian(socketAddress.AsSpan(24), (uint)address.ScopeId);
        }
        else
            return null;
        return GetBestInterfaceEx(socketAddress, out uint index) == 0 ? checked((int)index) : null;
    }

    private static async Task<PortCheck> CheckPortAsync(IPAddress[] addresses, int port, Tunnel tunnel,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var probes = addresses.Select(address => TestAsync(address, port, tunnel, timeout.Token)).ToList();
        var result = PortCheck.NoVpnRoute;
        while (probes.Count != 0)
        {
            var completed = await Task.WhenAny(probes);
            probes.Remove(completed);
            var outcome = await completed;
            if (outcome == PortCheck.Connected)
            {
                result = outcome;
                timeout.Cancel();
                break;
            }
            if (outcome > result)
                result = outcome;
        }
        await Task.WhenAll(probes);
        cancellationToken.ThrowIfCancellationRequested();
        return UsesTunnel(addresses, tunnel) ? result : PortCheck.NoVpnRoute;
    }

    private static async Task<PortCheck> TestAsync(IPAddress destination, int port, Tunnel tunnel, CancellationToken token)
    {
        var source = tunnel.Addresses.FirstOrDefault(address => address.AddressFamily == destination.AddressFamily);
        if (source is null || !UsesTunnel([destination], tunnel))
            return PortCheck.NoVpnRoute;
        try
        {
            using var socket = new Socket(destination.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(source, 0));
            await socket.ConnectAsync(destination, port, token);
            return PortCheck.Connected;
        }
        catch (SocketException ex) when (token.IsCancellationRequested &&
            ex.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted) { return PortCheck.TimedOut; }
        catch (SocketException ex) { return ClassifySocketError(ex.SocketErrorCode); }
        catch (OperationCanceledException) { return PortCheck.TimedOut; }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterfaceEx(byte[] address, out uint index);
}
