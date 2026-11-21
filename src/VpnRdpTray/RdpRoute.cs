using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VpnRdpTray;

internal sealed record RdpEndpoint(string Server, IPAddress[] Addresses, int Port);

internal static class RdpRoute
{
    private enum PortCheck { NoRoute, TimedOut, Refused, Connected }

    internal static async Task<(RdpEndpoint? Endpoint, Failure? Failure)> FindAsync(RdpSettings rdp, Tunnel tunnel,
        CancellationToken cancellationToken, Action<string, string>? progress = null)
    {
        if (!MachineSetup.HostsMatch(rdp))
            return (null, new("The Windows hosts entry differs from the saved configuration. Open Settings → Remote Desktop and save to apply the mapping.",
                FailureKind.ActionRequired));

        progress?.Invoke("RDP resolving hostname", "Looking up the saved Remote Desktop hostname with Windows DNS.");
        IPAddress[] addresses;
        bool dnsTimedOut = false;
        using (var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            dnsTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            try { addresses = await Dns.GetHostAddressesAsync(rdp.Host, dnsTimeout.Token); }
            catch (SocketException) { addresses = []; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                addresses = [];
                dnsTimedOut = true;
            }
        }

        bool safeHostname = UsesTunnel(addresses, tunnel);
        if (rdp.WindowsHosts)
            safeHostname &= addresses.Length == 1 && addresses[0].Equals(IPAddress.Parse(rdp.Ip!));
        var hostnamePort = PortCheck.NoRoute;
        if (safeHostname)
        {
            progress?.Invoke("RDP testing port", "The hostname resolves through the VPN. Checking whether Remote Desktop answers on the saved port.");
            hostnamePort = await CheckPortAsync(addresses, rdp.Port, tunnel, cancellationToken);
            if (hostnamePort == PortCheck.Connected)
                return (new(rdp.Host, addresses, rdp.Port), null);
        }

        if (rdp.Ip is { } configured)
        {
            var address = IPAddress.Parse(configured);
            if (UsesTunnel([address], tunnel))
            {
                progress?.Invoke("RDP testing saved IP", "Checking whether the optional Remote Desktop address answers through the VPN.");
                if (await CheckPortAsync([address], rdp.Port, tunnel, cancellationToken) == PortCheck.Connected)
                {
                    if (rdp.Mode == RdpMode.Classic && !rdp.WindowsHosts)
                        return (new(address.ToString(), [address], rdp.Port), null);
                    if (!safeHostname)
                        return (null, new("The saved RDP IP answers over VPN. Check the registered hostname or Windows hosts entry in Settings."));
                }
            }
        }

        if (safeHostname)
            return (null, new(hostnamePort switch
            {
                PortCheck.Refused => "The remote PC refused the RDP port. Check that Remote Desktop is enabled and the saved port is correct.",
                PortCheck.TimedOut => "The RDP port did not answer. Check that the PC is on and its firewall allows Remote Desktop from the VPN.",
                _ => "The VPN route changed while testing RDP. Check VPN routing if retries keep failing."
            }));

        if (addresses.Length != 0)
            return (null, new("The RDP hostname resolves to an address outside this VPN route. Check VPN DNS and routing."));
        return (null, new(dnsTimedOut ? "The RDP hostname lookup timed out. Check VPN DNS and the saved hostname."
            : "The RDP hostname did not resolve. Check the registered hostname or hosts entry in Settings."));
    }

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
        var result = PortCheck.NoRoute;
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
        return UsesTunnel(addresses, tunnel) ? result : PortCheck.NoRoute;
    }

    private static async Task<PortCheck> TestAsync(IPAddress destination, int port, Tunnel tunnel, CancellationToken token)
    {
        var source = tunnel.Addresses.FirstOrDefault(address => address.AddressFamily == destination.AddressFamily);
        if (source is null || !UsesTunnel([destination], tunnel))
            return PortCheck.NoRoute;
        using var socket = new Socket(destination.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(source, 0));
            await socket.ConnectAsync(destination, port, token);
            return PortCheck.Connected;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { return PortCheck.Refused; }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.AddressNotAvailable)
        { return PortCheck.NoRoute; }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return PortCheck.TimedOut; }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterfaceEx(byte[] address, out uint index);
}
