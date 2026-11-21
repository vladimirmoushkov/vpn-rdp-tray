using System.ComponentModel;
using System.Net.NetworkInformation;

namespace VpnRdpTray;

internal sealed record ConnectionStatus(string Name, string? Reason = null, DateTimeOffset? RetryAt = null,
    bool Action = false);

internal sealed class ConnectionController(Action<ConnectionStatus, ConnectionStatus> display)
{
    internal const string RdpClosedStatus = "RDP window closed";

    private CancellationTokenSource? lifetime;
    private Task? work;
    private IRdpSession? rdpSession;
    private TaskCompletionSource? rdpWake;
    private bool foreground;
    private ConnectionStatus vpn = new("VPN idle");
    private ConnectionStatus rdp = new("RDP waiting for VPN");

    internal void Start(Configuration configuration)
    {
        if (lifetime is not null)
            throw new InvalidOperationException("A connection manager is already running.");
        lifetime = new CancellationTokenSource();
        work = RunAsync(configuration, lifetime.Token);
    }

    internal async Task StopAsync()
    {
        var stopping = lifetime;
        if (stopping is null)
            return;
        stopping.Cancel();
        try { if (work is not null) await work; }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(lifetime, stopping))
            {
                stopping.Dispose();
                lifetime = null;
                work = null;
                foreground = false;
            }
        }
    }

    internal bool OpenRemoteDesktop()
    {
        if (rdpSession is not null)
        {
            rdpSession.Reveal();
            return true;
        }
        if (lifetime is null || rdp.Action && Settings.RdpAction is not null || vpn.Action)
            return false;
        foreground = true;
        rdpWake?.TrySetResult();
        return true;
    }

    private void Vpn(ConnectionStatus status)
    {
        vpn = status;
        display(vpn, rdp);
    }

    private void Rdp(ConnectionStatus status)
    {
        rdp = status;
        display(vpn, rdp);
    }

    private async Task RunAsync(Configuration configuration, CancellationToken token)
    {
        var totp = new Totp(configuration.Vpn);
        Task rdpWork = Task.CompletedTask;
        CancellationTokenSource? rdpStop = null;
        DateTimeOffset? connectedAt = null;
        int transientFailures = 0;
        Rdp(new("RDP waiting for VPN"));

        void TunnelChanged(Tunnel? tunnel)
        {
            if (tunnel is null)
            {
                rdpStop?.Cancel();
                if (!token.IsCancellationRequested && connectedAt is not null)
                    Rdp(new("RDP suspended"));
                return;
            }
            if (rdpStop is not null)
                return;
            connectedAt = DateTimeOffset.UtcNow;
            Settings.VpnRejections = 0;
            Settings.VpnNotBefore = null;
            Vpn(new("VPN connected"));
            rdpStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            rdpWork = RunRdpAsync(configuration.Rdp, tunnel, rdpStop.Token);
        }

        try
        {
            if (Settings.VpnAction is { } paused)
            {
                Vpn(new("VPN paused", paused, Action: true));
                return;
            }
            if (Settings.VpnRejections >= configuration.Vpn.RejectLimit)
            {
                Vpn(new("VPN paused", "VPN credential retry limit reached", Action: true));
                return;
            }
            if (Settings.VpnNotBefore is { } notBefore && notBefore > DateTimeOffset.UtcNow)
            {
                Vpn(new("VPN denied", "VPN credentials or one-time code rejected", notBefore));
                var remaining = notBefore - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, token);
            }

            while (!token.IsCancellationRequested)
            {
                Vpn(new("VPN connecting", vpn.RetryAt is null ? null : vpn.Reason));
                var failure = await OpenVpnSession.RunAsync(configuration.Vpn, totp, TunnelChanged, token);
                await rdpWork;
                rdpStop?.Dispose();
                rdpStop = null;
                rdpWork = Task.CompletedTask;
                token.ThrowIfCancellationRequested();

                if (connectedAt is { } upSince && DateTimeOffset.UtcNow - upSince >= TimeSpan.FromSeconds(30))
                    transientFailures = 0;
                connectedAt = null;
                Rdp(new("RDP waiting for VPN"));

                bool rejection = failure.Kind == FailureKind.CredentialRejected;
                if (rejection)
                    Settings.VpnRejections++;
                if (failure.Kind == FailureKind.ActionRequired || rejection && Settings.VpnRejections >= configuration.Vpn.RejectLimit)
                {
                    Settings.VpnAction = failure.Reason;
                    Vpn(new("VPN paused", failure.Reason, Action: true));
                    return;
                }

                var delay = Backoff(++transientFailures);
                if (rejection && delay < TimeSpan.FromSeconds(60))
                    delay = TimeSpan.FromSeconds(60);
                if (failure.MinimumDelay is { } serverDelay && delay < serverDelay)
                    delay = serverDelay;
                DateTimeOffset retry = DateTimeOffset.UtcNow + delay;
                if (rejection)
                    Settings.VpnNotBefore = retry;
                Vpn(new("VPN retrying", failure.Reason, retry));
                await Task.Delay(delay, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or System.Security.SecurityException)
        {
            if (!token.IsCancellationRequested)
                Vpn(new("VPN paused", "Windows could not manage the saved VPN connection. Review Setup.", Action: true));
        }
        finally
        {
            if (rdpStop is not null)
            {
                rdpStop.Cancel();
                await rdpWork;
                rdpStop.Dispose();
            }
        }
    }

    private async Task RunRdpAsync(RdpSettings settings, Tunnel tunnel, CancellationToken token)
    {
        int failures = 0;
        bool native = settings.Mode == RdpMode.Entra && !settings.EntraActiveX;
        if (Settings.RdpAction is { } paused)
        {
            Rdp(new("RDP paused", paused, Action: true));
            return;
        }
        try
        {
            while (!token.IsCancellationRequested)
            {
                Rdp(new("RDP checking target", rdp.RetryAt is null ? null : rdp.Reason));
                Failure failure;
                try
                {
                    var (endpoint, checkFailure) = await RdpRoute.FindAsync(settings, tunnel, token);
                    token.ThrowIfCancellationRequested();
                    if (endpoint is null)
                        failure = checkFailure!;
                    else if (!RdpRoute.UsesTunnel(endpoint.Addresses, tunnel))
                        failure = new("The RDP route changed");
                    else
                    {
                        var outcome = await StartRdpAsync(endpoint, settings, native, tunnel, token);
                        failure = outcome.Failure;
                        native = outcome.Native;
                        if (outcome.WasStable)
                            failures = 0;
                    }
                }
                catch (Exception ex) when (ex is IOException or NetworkInformationException or UnauthorizedAccessException or Win32Exception)
                {
                    failure = new("Windows could not start Remote Desktop or check the VPN route");
                }
                token.ThrowIfCancellationRequested();

                if (failure.Kind is FailureKind.CredentialRejected or FailureKind.ActionRequired)
                {
                    Settings.RdpAction = failure.Reason;
                    Rdp(new("RDP paused", failure.Reason, Action: true));
                    return;
                }
                if (failure.Kind == FailureKind.ManualRetry)
                {
                    Rdp(new(RdpClosedStatus, failure.Reason, Action: true));
                    await WaitForRdpAsync(null, token);
                    continue;
                }

                var delay = Backoff(++failures);
                string status = rdp.Name == "RDP connected" ? RdpClosedStatus : "RDP retrying";
                Rdp(new(status, failure.Reason == status ? null : failure.Reason, DateTimeOffset.UtcNow + delay));
                await WaitForRdpAsync(delay, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (!token.IsCancellationRequested)
                Rdp(new("RDP paused", "Windows could not read or save the RDP settings", Action: true));
        }
    }

    private async Task<(Failure Failure, bool Native, bool WasStable)> StartRdpAsync(RdpEndpoint endpoint,
        RdpSettings settings, bool native, Tunnel tunnel, CancellationToken token)
    {
        var requestForeground = foreground;
        foreground = false;
        var started = DateTimeOffset.UtcNow;
        if (!native)
        {
            try
            {
                rdpSession = new HostedRdpSession(endpoint, settings, requestForeground,
                    (message, action) =>
                    {
                        if (!token.IsCancellationRequested)
                            Rdp(new(message, Action: action));
                    });
            }
            catch (Exception ex) when (HostedRdpSession.IsClientException(ex))
            {
                if (settings.Mode == RdpMode.Classic)
                    return (new($"The Windows RDP control could not start (0x{ex.HResult:X8})", FailureKind.ActionRequired), false, false);
                native = true;
            }
        }

        if (native)
        {
            rdpSession = new NativeRdpSession(endpoint, settings, requestForeground, Settings.DirectoryPath);
            Rdp(new("Opened in Remote Desktop"));
        }
        try
        {
            using var monitor = CancellationTokenSource.CreateLinkedTokenSource(token);
            var route = MonitorRouteAsync(endpoint, tunnel, monitor.Token);
            try
            {
                var finished = await Task.WhenAny(rdpSession!.Completion, route);
                return (await finished, native, DateTimeOffset.UtcNow - started >= TimeSpan.FromSeconds(30));
            }
            finally { monitor.Cancel(); }
        }
        finally
        {
            rdpSession?.Dispose();
            rdpSession = null;
        }
    }

    private static async Task<Failure> MonitorRouteAsync(RdpEndpoint endpoint, Tunnel tunnel, CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            if (!RdpRoute.UsesTunnel(endpoint.Addresses, tunnel))
                return new("The RDP route no longer uses the VPN");
        }
    }

    private async Task WaitForRdpAsync(TimeSpan? delay, CancellationToken token)
    {
        var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rdpWake = wake;
        try
        {
            if (delay is null)
            {
                await wake.Task.WaitAsync(token);
                return;
            }
            try { await wake.Task.WaitAsync(delay.Value, token); }
            catch (TimeoutException) { }
        }
        finally { rdpWake = null; }
    }

    internal static TimeSpan Backoff(int failures)
    {
        int seconds = Math.Min(120, 4 << Math.Clamp(failures - 1, 0, 5));
        return TimeSpan.FromSeconds(seconds * (0.85 + Random.Shared.NextDouble() * 0.15));
    }
}
