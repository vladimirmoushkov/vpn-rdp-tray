using System.ComponentModel;
using System.Net.NetworkInformation;

namespace VpnRdpTray;

internal sealed record ConnectionStatus(string Name, string? Reason = null, DateTimeOffset? RetryAt = null,
    bool Action = false, bool SignIn = false, string? LastFailure = null);

internal sealed class ConnectionController(VpnSignInPrompt vpnPrompt, Action<ConnectionStatus, ConnectionStatus> display)
{
    internal const string RdpClosedStatus = "RDP window closed";
    internal const string RdpReopenTip = "Choose Open Remote Desktop in the tray or click RDP in the status window.";
    internal const string RdpOffStatus = "RDP off";
    internal const string RdpReadyStatus = "RDP ready";
    internal const string RdpManualWaitingStatus = "RDP needs VPN";
    internal const string VpnSignInStatus = "VPN sign-in required";
    private const string VpnCheckingAfterResume = "VPN checking after wake";
    private const string VpnRefreshingAfterResume = "VPN reconnecting after wake";
    private const long ResumeSettleMilliseconds = 5_000;
    private const long ResumeWindowMilliseconds = 60_000;

    private CancellationTokenSource? lifetime;
    private CancellationTokenSource? vpnAttemptStop;
    private Task? work;
    private IRdpSession? rdpSession;
    private CancellationTokenSource? rdpAttemptStop;
    private TaskCompletionSource? rdpEnabledChanged, rdpWake, vpnWake;
    private Tunnel? activeTunnel;
    private Tunnel? manualRdpRequest;
    private ResumeCheck? resumeCheck;
    private bool foreground, revealNextVpnPrompt, rdpRetryScheduled, rdpDismissed, manualRdp;
    private (bool Internal, bool External) rdpGlow;
    private ConnectionStatus vpn = new("VPN idle");
    private ConnectionStatus rdp = new("RDP waiting for VPN");

    private sealed record ResumeCheck(Tunnel Tunnel, long StartedAt);

    internal bool RdpEnabled { get; private set; } = true;

    internal void Start(Configuration configuration)
    {
        if (lifetime is not null)
            throw new InvalidOperationException("A connection manager is already running.");
        rdpGlow = (configuration.Rdp.InternalGlow, configuration.Rdp.ExternalGlow);
        manualRdp = configuration.Rdp.ManualConnection;
        if (manualRdp)
        {
            RdpEnabled = true;
            rdpDismissed = false;
        }
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
        if (RdpEnabled && rdpAttemptStop is { IsCancellationRequested: false } &&
            rdpSession is { Completion.IsCompleted: false })
        {
            rdpSession.Reveal();
            return true;
        }
        if (lifetime is null || lifetime.IsCancellationRequested || rdp.Name == "RDP paused" || vpn.Action)
            return false;
        if (manualRdp)
        {
            if (activeTunnel is not { } tunnel || vpnAttemptStop is not { IsCancellationRequested: false })
                return false;
            if (rdpAttemptStop is { IsCancellationRequested: false } || ReferenceEquals(manualRdpRequest, tunnel))
                return true;
            manualRdpRequest = tunnel;
            foreground = true;
            Rdp(new("RDP starting"));
            rdpEnabledChanged?.TrySetResult();
            return true;
        }
        foreground = true;
        SetRemoteDesktopEnabled(true);
        if (rdpDismissed)
        {
            rdpDismissed = false;
            Rdp(new(activeTunnel is null ? "RDP waiting for VPN" : "RDP starting"));
            rdpEnabledChanged?.TrySetResult();
        }
        rdpWake?.TrySetResult();
        return true;
    }

    internal void ToggleRemoteDesktop()
    {
        if (manualRdp && (manualRdpRequest is not null || rdpAttemptStop is { IsCancellationRequested: false }))
        {
            manualRdpRequest = null;
            foreground = false;
            rdpAttemptStop?.Cancel();
            CompleteResumeCheck(resumeCheck);
        }
        else if (manualRdp || rdpDismissed)
            OpenRemoteDesktop();
        else
            SetRemoteDesktopEnabled(!RdpEnabled);
    }

    internal void SetRdpGlow(bool internalGlow, bool externalGlow)
    {
        rdpGlow = (internalGlow, externalGlow);
        if (rdpSession is HostedRdpSession hosted)
            hosted.SetGlow(internalGlow, externalGlow);
    }

    internal void NotifySystemResumed()
    {
        // Only verify a tunnel that was already established at wake. A new connection checks its route normally.
        if (activeTunnel is not { } tunnel || vpnAttemptStop is not { IsCancellationRequested: false })
            return;
        resumeCheck = new(tunnel, Environment.TickCount64);
        if (rdpRetryScheduled)
            rdpWake?.TrySetResult();
    }

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
            CompleteResumeCheck(resumeCheck);
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
        if (status.Name != RdpOffStatus && (!RdpEnabled || rdpDismissed && status.Name != RdpClosedStatus))
            return;
        rdp = status;
        display(vpn, rdp);
    }

    private void RdpWaitingForVpn() => Rdp(manualRdp
        ? new(RdpManualWaitingStatus, "Open Remote Desktop after the VPN connects.")
        : new("RDP waiting for VPN"));

    private async Task RunAsync(Configuration configuration, CancellationToken token)
    {
        var settings = configuration.Vpn;
        var totp = settings.ProtectedTotpSeed is { Length: > 0 } ? new Totp(settings) : null;
        Task rdpWork = Task.CompletedTask;
        CancellationTokenSource? rdpStop = null;
        DateTimeOffset? connectedAt = null;
        int transientFailures = 0, attempt = 0;
        string? lastFailure = null;
        RdpWaitingForVpn();

        void TunnelChanged(Tunnel? tunnel)
        {
            if (tunnel is null)
            {
                activeTunnel = null;
                resumeCheck = null;
                rdpStop?.Cancel();
                if (manualRdp)
                {
                    manualRdpRequest = null;
                    foreground = false;
                    if (!token.IsCancellationRequested)
                        RdpWaitingForVpn();
                }
                else if (!token.IsCancellationRequested && connectedAt is not null && vpn.Name != VpnRefreshingAfterResume)
                    Rdp(new("RDP suspended"));
                return;
            }
            if (rdpStop is not null || vpnAttemptStop is not { IsCancellationRequested: false } attemptStop)
                return;
            connectedAt = DateTimeOffset.UtcNow;
            lastFailure = null;
            Settings.VpnRejections = 0;
            Settings.VpnNotBefore = null;
            activeTunnel = tunnel;
            Vpn(new("VPN connected"));
            rdpStop = CancellationTokenSource.CreateLinkedTokenSource(attemptStop.Token);
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
                using var attemptStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                vpnAttemptStop = attemptStop;
                Failure? failure = null;
                try
                {
                    failure = await OpenVpnSession.RunAsync(settings,
                        promptToken => GetVpnAuthenticationAsync(totp, string.IsNullOrEmpty(settings.Password),
                            needsPassword => RequestVpnInputAsync("Enter a fresh code from your authenticator.",
                                () => vpnPrompt.RequestAuthenticationAsync(needsPassword, promptToken), promptToken),
                            () => RequestVpnInputAsync("Enter your VPN password.",
                                () => vpnPrompt.RequestPasswordAsync(promptToken), promptToken), promptToken),
                        promptToken => RequestVpnInputAsync("Enter the VPN private key passphrase.",
                            () => vpnPrompt.RequestKeyPassphraseAsync(promptToken), promptToken),
                        TunnelChanged, attemptStop.Token,
                        progress: stage => Vpn(new(stage.Name, stage.Detail, LastFailure: lastFailure)));
                }
                catch (OperationCanceledException) when (attemptStop.IsCancellationRequested && !token.IsCancellationRequested) { }
                finally { vpnAttemptStop = null; }
                await rdpWork;
                rdpStop?.Dispose();
                rdpStop = null;
                rdpWork = Task.CompletedTask;
                token.ThrowIfCancellationRequested();

                if (connectedAt is { } upSince && DateTimeOffset.UtcNow - upSince >= TimeSpan.FromSeconds(30))
                    transientFailures = 0;
                connectedAt = null;
                RdpWaitingForVpn();

                if (failure is null || attemptStop.IsCancellationRequested && failure.Kind == FailureKind.Transient)
                {
                    transientFailures = 0;
                    lastFailure = null;
                    continue;
                }
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
            activeTunnel = null;
            manualRdpRequest = null;
            resumeCheck = null;
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
                if (!RdpEnabled || rdpDismissed || settings.ManualConnection && !ReferenceEquals(manualRdpRequest, tunnel))
                {
                    if (settings.ManualConnection && rdp.Name is not (RdpReadyStatus or "RDP paused"))
                        Rdp(new(RdpReadyStatus, "Choose Open Remote Desktop to connect."));
                    await changed.Task.WaitAsync(token);
                    continue;
                }

                manualRdpRequest = null;
                // Finish tearing down the old client before another attempt can start, even after rapid toggles.
                using var attemptStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                rdpAttemptStop = attemptStop;
                try { await RunRdpAsync(settings, tunnel, attemptStop.Token); }
                finally
                {
                    rdpAttemptStop = null;
                    if (settings.ManualConnection && manualRdpRequest is null)
                        foreground = false;
                }

                // Hold after dismissal or a Settings block until the user acts or the tunnel closes.
                if (!settings.ManualConnection && !attemptStop.IsCancellationRequested)
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
                var afterResume = await WaitForResumeCheckAsync(tunnel, token);
                Rdp(new("RDP checking target", $"Attempt {++attempt}. Checking the saved hostname and VPN route.", LastFailure: lastFailure));
                Failure failure;
                try
                {
                    var (endpoint, checkFailure, canRefresh) = await RdpRoute.FindAsync(settings, tunnel, token, (name, reason) =>
                    {
                        if (!token.IsCancellationRequested)
                            Rdp(new(name, reason, LastFailure: lastFailure));
                    });
                    token.ThrowIfCancellationRequested();
                    if (CurrentResumeCheck(tunnel) is { } latest && !ReferenceEquals(latest, afterResume))
                    {
                        if (!settings.ManualConnection)
                            continue; // A probe already in progress at wake cannot verify the resumed network.
                        failure = new("Windows woke before the VPN route could be verified. Choose Open Remote Desktop to try again.");
                    }
                    else if (endpoint is null)
                    {
                        failure = checkFailure!;
                        if (canRefresh && TryRefreshVpn(afterResume, failure, token))
                            return;
                    }
                    else if (!RdpRoute.UsesTunnel(endpoint.Addresses, tunnel))
                    {
                        failure = new("The RDP route changed before connection. Check VPN routing and wait for a retry.");
                        if (TryRefreshVpn(afterResume, failure, token))
                            return;
                    }
                    else
                    {
                        CompleteResumeCheck(afterResume);
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
                if (!settings.ManualConnection && failure.Kind == FailureKind.Dismissed)
                {
                    // A tray click during teardown already asked for a new foreground session.
                    if (foreground)
                        continue;
                    CompleteResumeCheck(CurrentResumeCheck(tunnel));
                    rdpDismissed = true;
                    string detail = failure.Reason == RdpClosedStatus ? RdpReopenTip : $"{failure.Reason} {RdpReopenTip}";
                    Rdp(new(RdpClosedStatus, detail));
                    return;
                }
                token.ThrowIfCancellationRequested();
                CompleteResumeCheck(afterResume);
                lastFailure = failure.Reason;

                if (failure.Kind is FailureKind.CredentialRejected or FailureKind.ActionRequired)
                {
                    CompleteResumeCheck(CurrentResumeCheck(tunnel));
                    Settings.RdpAction = failure.Reason;
                    Rdp(new("RDP paused", failure.Reason, Action: true));
                    return;
                }
                if (settings.ManualConnection)
                {
                    CompleteResumeCheck(CurrentResumeCheck(tunnel));
                    Rdp(new(RdpReadyStatus, failure.Reason));
                    return;
                }
                if (failure.Kind == FailureKind.ManualRetry)
                {
                    CompleteResumeCheck(CurrentResumeCheck(tunnel));
                    Rdp(new("RDP stopped", failure.Reason, Action: true));
                    await WaitForRdpAsync(null, token);
                    continue;
                }

                if (CurrentResumeCheck(tunnel) is not null)
                    continue; // An open desktop may have ended before its wake probe finished. Verify before backoff.

                var delay = Backoff(++failures);
                Rdp(new("RDP retrying", failure.Reason, DateTimeOffset.UtcNow + delay));
                await WaitForRdpAsync(delay, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (!token.IsCancellationRequested)
            {
                CompleteResumeCheck(CurrentResumeCheck(tunnel));
                Rdp(new("RDP paused", "Windows could not read or save the RDP settings", Action: true));
            }
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
                rdpSession = new HostedRdpSession(endpoint, settings with
                {
                    InternalGlow = rdpGlow.Internal,
                    ExternalGlow = rdpGlow.External
                }, requestForeground,
                    (message, action) =>
                    {
                        if (!token.IsCancellationRequested)
                            Rdp(new(message, Action: action, LastFailure: message == "RDP connected" ? null : lastFailure));
                    });
            }
            catch (Exception ex) when (HostedRdpSession.IsClientException(ex))
            {
                if (settings.Mode == RdpMode.Classic || settings.ManualConnection)
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
                var session = rdpSession!.Completion;
                var finished = await Task.WhenAny(session, route);
                if (session.IsCompletedSuccessfully && session.Result.Kind == FailureKind.Dismissed)
                    finished = session;
                return (await finished, native, DateTimeOffset.UtcNow - started >= TimeSpan.FromSeconds(30));
            }
            finally
            {
                monitor.Cancel();
                if (!route.IsCompleted)
                {
                    try { await route; }
                    catch (OperationCanceledException) when (monitor.IsCancellationRequested) { }
                }
            }
        }
        finally
        {
            rdpSession?.Dispose();
            rdpSession = null;
        }
    }

    private async Task<Failure> MonitorRouteAsync(RdpEndpoint endpoint, Tunnel tunnel, CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            if (await WaitForResumeCheckAsync(tunnel, token) is { } afterResume)
            {
                var (failure, canRefresh) = await RdpRoute.CheckEndpointAsync(endpoint, tunnel, token);
                token.ThrowIfCancellationRequested();
                if (failure is not null && canRefresh && TryRefreshVpn(afterResume, failure, token))
                    return failure;
                CompleteResumeCheck(afterResume);
                if (CurrentResumeCheck(tunnel) is not null)
                    continue;
            }
            if (!RdpRoute.UsesTunnel(endpoint.Addresses, tunnel))
                return new("The RDP route no longer uses the VPN. Check VPN routes and hostname resolution.");
        }
    }

    private ResumeCheck? CurrentResumeCheck(Tunnel tunnel)
    {
        if (resumeCheck is not { } check || !ReferenceEquals(check.Tunnel, tunnel))
            return null;
        if (Environment.TickCount64 - check.StartedAt <= ResumeWindowMilliseconds)
            return check;
        CompleteResumeCheck(check);
        return null;
    }

    private async Task<ResumeCheck?> WaitForResumeCheckAsync(Tunnel tunnel, CancellationToken token)
    {
        while (CurrentResumeCheck(tunnel) is { } check)
        {
            if (vpn.Name == "VPN connected")
                Vpn(new(VpnCheckingAfterResume, "Windows is active again. Checking the route and Remote Desktop port through the existing tunnel."));
            long wait = ResumeSettleMilliseconds - (Environment.TickCount64 - check.StartedAt);
            if (wait <= 0)
                return check;
            await Task.Delay(TimeSpan.FromMilliseconds(wait), token);
        }
        return null;
    }

    private void CompleteResumeCheck(ResumeCheck? check)
    {
        if (check is null || !ReferenceEquals(resumeCheck, check))
            return;
        resumeCheck = null;
        if (ReferenceEquals(activeTunnel, check.Tunnel) && vpn.Name == VpnCheckingAfterResume)
            Vpn(new("VPN connected"));
    }

    private bool TryRefreshVpn(ResumeCheck? check, Failure failure, CancellationToken token)
    {
        if (check is null || failure.Kind != FailureKind.Transient || token.IsCancellationRequested || !RdpEnabled ||
            !ReferenceEquals(activeTunnel, check.Tunnel) ||
            !ReferenceEquals(CurrentResumeCheck(check.Tunnel), check) || vpnAttemptStop is not { IsCancellationRequested: false } attempt)
            return false;

        resumeCheck = null;
        Vpn(new(VpnRefreshingAfterResume, $"Remote Desktop did not respond through the VPN after wake. {failure.Reason}"));
        if (manualRdp)
            RdpWaitingForVpn();
        else
            Rdp(new("RDP waiting for VPN", "The VPN is reconnecting once before another Remote Desktop check."));
        attempt.Cancel();
        return true;
    }

    private async Task WaitForRdpAsync(TimeSpan? delay, CancellationToken token)
    {
        var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rdpWake = wake;
        rdpRetryScheduled = delay.HasValue;
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
        finally
        {
            rdpRetryScheduled = false;
            rdpWake = null;
        }
    }

    internal static TimeSpan Backoff(int failures)
    {
        int seconds = Math.Min(120, 4 << Math.Clamp(failures - 1, 0, 5));
        return TimeSpan.FromSeconds(seconds * (0.85 + Random.Shared.NextDouble() * 0.15));
    }
}
