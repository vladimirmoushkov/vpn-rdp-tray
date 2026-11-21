using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VpnRdpTray;

internal sealed record RdpEndpoint(string Server, IPAddress[] Addresses, int Port);

internal static class RdpRoute
{
    internal static async Task<(RdpEndpoint? Endpoint, Failure? Failure)> FindAsync(RdpSettings rdp, Tunnel tunnel,
        CancellationToken cancellationToken)
    {
        if (!MachineSetup.HostsMatch(rdp))
            return (null, new("Apply the Windows hosts setting in Setup", FailureKind.ActionRequired));

        IPAddress[] addresses;
        using (var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            dnsTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            try { addresses = await Dns.GetHostAddressesAsync(rdp.Host, dnsTimeout.Token); }
            catch (SocketException) { addresses = []; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { addresses = []; }
        }

        bool safeHostname = UsesTunnel(addresses, tunnel);
        if (rdp.WindowsHosts)
            safeHostname &= addresses.Length == 1 && addresses[0].Equals(IPAddress.Parse(rdp.Ip!));
        if (safeHostname && await AnyReachableAsync(addresses, rdp.Port, tunnel, cancellationToken))
            return (new(rdp.Host, addresses, rdp.Port), null);

        if (rdp.Ip is { } configured)
        {
            var address = IPAddress.Parse(configured);
            if (UsesTunnel([address], tunnel) && await AnyReachableAsync([address], rdp.Port, tunnel, cancellationToken))
            {
                if (rdp.Mode == RdpMode.Classic && !rdp.WindowsHosts)
                    return (new(address.ToString(), [address], rdp.Port), null);
                if (!safeHostname)
                    return (null, new("RDP IP reachable over VPN. Check hostname resolution or the Windows hosts entry"));
            }
        }

        return (null, new(safeHostname ? "RDP timed out or refused the connection"
            : addresses.Length == 0 ? "RDP hostname did not resolve over VPN" : "RDP hostname has no exclusive route through this VPN"));
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

    private static async Task<bool> AnyReachableAsync(IPAddress[] addresses, int port, Tunnel tunnel,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var probes = addresses.Select(address => TestAsync(address, port, tunnel, timeout.Token)).ToList();
        bool reachable = false;
        while (probes.Count != 0)
        {
            var completed = await Task.WhenAny(probes);
            probes.Remove(completed);
            if (await completed)
            {
                reachable = true;
                timeout.Cancel();
                break;
            }
        }
        await Task.WhenAll(probes);
        cancellationToken.ThrowIfCancellationRequested();
        return reachable && UsesTunnel(addresses, tunnel);
    }

    private static async Task<bool> TestAsync(IPAddress destination, int port, Tunnel tunnel, CancellationToken token)
    {
        var source = tunnel.Addresses.FirstOrDefault(address => address.AddressFamily == destination.AddressFamily);
        if (source is null || !UsesTunnel([destination], tunnel))
            return false;
        using var socket = new Socket(destination.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(source, 0));
            await socket.ConnectAsync(destination, port, token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return false; }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterfaceEx(byte[] address, out uint index);
}
