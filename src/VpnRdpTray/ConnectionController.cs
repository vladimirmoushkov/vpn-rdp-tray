using System.ComponentModel;
using System.Net.NetworkInformation;

namespace VpnRdpTray;

internal sealed record ConnectionStatus(string Name, string? Reason = null, DateTimeOffset? RetryAt = null,
    bool Action = false, bool SignIn = false, string? LastFailure = null);

internal sealed class ConnectionController(VpnSignInPrompt vpnPrompt, Action<ConnectionStatus, ConnectionStatus> display)
{
    internal const string RdpClosedStatus = "RDP window closed";
    internal const string RdpOffStatus = "RDP off";
    internal const string VpnSignInStatus = "VPN sign-in required";

    private CancellationTokenSource? lifetime;
    private Task? work;
    private IRdpSession? rdpSession;
    private CancellationTokenSource? rdpAttemptStop;
    private TaskCompletionSource? rdpEnabledChanged, rdpWake, vpnWake;
    private bool foreground, revealNextVpnPrompt;
    private ConnectionStatus vpn = new("VPN idle");
    private ConnectionStatus rdp = new("RDP waiting for VPN");

    internal bool RdpEnabled { get; private set; } = true;

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
                revealNextVpnPrompt = false;
            }
        }
    }

    internal bool OpenVpnSignIn()
    {
        if (vpnPrompt.Reveal())
            return true;
        if (vpnWake?.TrySetResult() != true)
            return false;
        revealNextVpnPrompt = true;
        return true;
    }

    internal bool OpenRemoteDesktop()
    {
        if (RdpEnabled && rdpAttemptStop is { IsCancellationRequested: false } && rdpSession is not null)
        {
            rdpSession.Reveal();
            return true;
        }
        if (lifetime is null || lifetime.IsCancellationRequested || rdp.Name == "RDP paused" || vpn.Action)
            return false;
        foreground = true;
        SetRemoteDesktopEnabled(true);
        rdpWake?.TrySetResult();
        return true;
    }

    internal void ToggleRemoteDesktop() => SetRemoteDesktopEnabled(!RdpEnabled);

    private void SetRemoteDesktopEnabled(bool enabled)
    {
        if (RdpEnabled == enabled)
            return;

        var changed = rdpEnabledChanged;
        RdpEnabled = enabled;
        if (enabled)
            Rdp(new(changed is null ? "RDP waiting for VPN" : "RDP starting"));
        else
        {
            foreground = false;
            Rdp(new(RdpOffStatus));
            rdpAttemptStop?.Cancel();
        }
        changed?.TrySetResult();
    }

    internal Bitmap? CapturePreview(Size maximumSize) =>
        RdpEnabled && rdpSession is HostedRdpSession hosted && rdp.Name == "RDP connected"
            ? hosted.CapturePreview(maximumSize) : null;

    internal bool IsPreviewMinimized => rdpSession is HostedRdpSession { IsPreviewMinimized: true };

    private void Vpn(ConnectionStatus status)
    {
        vpn = status;
        display(vpn, rdp);
    }

    private void Rdp(ConnectionStatus status)
    {
        if (!RdpEnabled && status.Name != RdpOffStatus)
            return;
        rdp = status;
        display(vpn, rdp);
    }

    private async Task RunAsync(Configuration configuration, CancellationToken token)
    {
        var settings = configuration.Vpn;
        var totp = settings.ProtectedTotpSeed is { Length: > 0 } ? new Totp(settings) : null;
        Task rdpWork = Task.CompletedTask;
        CancellationTokenSource? rdpStop = null;
        DateTimeOffset? connectedAt = null;
        int transientFailures = 0, attempt = 0;
        string? lastFailure = null;
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
            lastFailure = null;
            Settings.VpnRejections = 0;
            Settings.VpnNotBefore = null;
            Vpn(new("VPN connected"));
            rdpStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            rdpWork = RunRdpOnTunnelAsync(configuration.Rdp, tunnel, rdpStop.Token);
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
                Vpn(new("VPN starting", $"Attempt {++attempt}. Checking the saved VPN settings and the Windows service.", LastFailure: lastFailure));
                var failure = await OpenVpnSession.RunAsync(settings,
                    promptToken => GetVpnAuthenticationAsync(totp, string.IsNullOrEmpty(settings.Password),
                        needsPassword => RequestVpnInputAsync("Enter a fresh code from your authenticator.",
                            () => vpnPrompt.RequestAuthenticationAsync(needsPassword, promptToken), promptToken),
                        () => RequestVpnInputAsync("Enter your VPN password.",
                            () => vpnPrompt.RequestPasswordAsync(promptToken), promptToken), promptToken),
                    promptToken => RequestVpnInputAsync("Enter the VPN private key passphrase.",
                        () => vpnPrompt.RequestKeyPassphraseAsync(promptToken), promptToken),
                    TunnelChanged, token,
                    progress: stage => Vpn(new(stage.Name, stage.Detail, LastFailure: lastFailure)));
                await rdpWork;
                rdpStop?.Dispose();
                rdpStop = null;
                rdpWork = Task.CompletedTask;
                token.ThrowIfCancellationRequested();

                if (connectedAt is { } upSince && DateTimeOffset.UtcNow - upSince >= TimeSpan.FromSeconds(30))
                    transientFailures = 0;
                connectedAt = null;
                Rdp(new("RDP waiting for VPN"));

                if (failure.Kind == FailureKind.ManualRetry)
                {
                    await WaitForVpnSignInAsync(failure.Reason, token);
                    continue;
                }

                bool rejection = failure.Kind == FailureKind.CredentialRejected;
                lastFailure = failure.Reason;
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
        catch (SetupException ex)
        {
            if (!token.IsCancellationRequested)
                Vpn(new("VPN paused", ex.Message, Action: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or System.Security.SecurityException)
        {
            if (!token.IsCancellationRequested)
                Vpn(new("VPN paused", "Windows could not read or manage the VPN settings. Open Settings to review the profile and OpenVPN service.",
                    Action: true, LastFailure: lastFailure));
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

    internal static async Task<VpnAuthentication?> GetVpnAuthenticationAsync(Totp? totp, bool needsPassword,
        Func<bool, Task<VpnAuthentication?>> requestCode, Func<Task<string?>> requestPassword, CancellationToken token)
    {
        if (totp is null)
            return await requestCode(needsPassword);

        var password = needsPassword ? await requestPassword() : null;
        if (needsPassword && password is null)
            return null;

        return new VpnAuthentication(password, await totp.FreshCodeAsync(token));
    }

    private async Task<T?> RequestVpnInputAsync<T>(string reason, Func<Task<T?>> request, CancellationToken token) where T : class
    {
        var previous = vpn;
        Vpn(new(VpnSignInStatus, reason, Action: true, SignIn: true, LastFailure: previous.LastFailure));
        try
        {
            var pending = request();
            if (revealNextVpnPrompt)
            {
                revealNextVpnPrompt = false;
                vpnPrompt.Reveal();
            }
            return await pending;
        }
        finally
        {
            if (!token.IsCancellationRequested)
                Vpn(previous);
        }
    }

    private async Task WaitForVpnSignInAsync(string reason, CancellationToken token)
    {
        var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vpnWake = wake;
        try
        {
            Vpn(new("VPN sign-in paused", reason, Action: true, SignIn: true));
            await wake.Task.WaitAsync(token);
        }
        finally { vpnWake = null; }
    }

    private async Task RunRdpOnTunnelAsync(RdpSettings settings, Tunnel tunnel, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                rdpEnabledChanged = changed;
                if (!RdpEnabled)
                {
                    await changed.Task.WaitAsync(token);
                    continue;
                }

                // Finish tearing down the old client before another attempt can start, even after rapid toggles.
                using var attemptStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                rdpAttemptStop = attemptStop;
                try { await RunRdpAsync(settings, tunnel, attemptStop.Token); }
                finally { rdpAttemptStop = null; }

                // A settings or sign-in block stays in place until another deliberate switch change or a new configuration.
                if (!attemptStop.IsCancellationRequested)
                    await changed.Task.WaitAsync(token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { rdpEnabledChanged = null; }
    }

    private async Task RunRdpAsync(RdpSettings settings, Tunnel tunnel, CancellationToken token)
    {
        int failures = 0, attempt = 0;
        string? lastFailure = null;
        bool native = settings.Mode == RdpMode.Entra && !settings.EntraActiveX;
        try
        {
            if (Settings.RdpAction is { } paused)
            {
                Rdp(new("RDP paused", paused, Action: true));
                return;
            }
            while (!token.IsCancellationRequested)
            {
                Rdp(new("RDP checking target", $"Attempt {++attempt}. Checking the saved hostname and VPN route.", LastFailure: lastFailure));
                Failure failure;
                try
                {
                    var (endpoint, checkFailure) = await RdpRoute.FindAsync(settings, tunnel, token, (name, reason) =>
                    {
                        if (!token.IsCancellationRequested)
                            Rdp(new(name, reason, LastFailure: lastFailure));
                    });
                    token.ThrowIfCancellationRequested();
                    if (endpoint is null)
                        failure = checkFailure!;
                    else if (!RdpRoute.UsesTunnel(endpoint.Addresses, tunnel))
                        failure = new("The RDP route changed before connection. Check VPN routing and wait for a retry.");
                    else
                    {
                        var outcome = await StartRdpAsync(endpoint, settings, native, tunnel, lastFailure, token);
                        failure = outcome.Failure;
                        native = outcome.Native;
                        if (outcome.WasStable)
                            failures = 0;
                    }
                }
                catch (SetupException ex)
                {
                    failure = new(ex.Message, FailureKind.ActionRequired);
                }
                catch (Exception ex) when (ex is IOException or NetworkInformationException or UnauthorizedAccessException or Win32Exception)
                {
                    failure = new("Windows could not check the RDP route or start the client. Check the saved hostname and port in Settings.");
                }
                token.ThrowIfCancellationRequested();
                lastFailure = failure.Reason;

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
        RdpSettings settings, bool native, Tunnel tunnel, string? lastFailure, CancellationToken token)
    {
        var requestForeground = foreground;
        foreground = false;
        var started = DateTimeOffset.UtcNow;
        Rdp(new("RDP opening client", "The target responded through the VPN. Starting Remote Desktop.", LastFailure: lastFailure));
        if (!native)
        {
            try
            {
                rdpSession = new HostedRdpSession(endpoint, settings, requestForeground,
                    (message, action) =>
                    {
                        if (!token.IsCancellationRequested)
                            Rdp(new(message, Action: action, LastFailure: message == "RDP connected" ? null : lastFailure));
                    });
            }
            catch (Exception ex) when (HostedRdpSession.IsClientException(ex))
            {
                if (settings.Mode == RdpMode.Classic)
                    return (new($"Windows could not start the RDP control (0x{ex.HResult:X8}). Check Remote Desktop and Windows updates.",
                        FailureKind.ActionRequired), false, false);
                native = true;
            }
        }

        if (native)
        {
            token.ThrowIfCancellationRequested();
            rdpSession = new NativeRdpSession(endpoint, settings, requestForeground, Settings.DirectoryPath);
            if (!token.IsCancellationRequested)
                Rdp(new("Opened in Remote Desktop", "Choose Open Remote Desktop to view Windows sign-in or its connection messages."));
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
                return new("The RDP route no longer uses the VPN. Check VPN routes and hostname resolution.");
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
