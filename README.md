# VPN Remote Desktop

If you use OpenVPN to reach the same Windows desktop every day, this app connects the VPN, checks that the desktop is reachable through it, and opens one Remote Desktop window. It runs in the Windows notification area. If a step fails, the status window shows what stopped, what to check, and when the app will try again.

If only Remote Desktop fails, the app keeps the VPN up while it retries. If the VPN drops, it closes the desktop it opened and waits for the tunnel to recover. Windows handles Remote Desktop sign-in and any Microsoft Entra MFA prompt.

See the [version history](CHANGELOG.md) for changes across builds and links to archived releases.

## Install and connect

You need Windows 11 x64, an OpenVPN profile from your VPN administrator, and the hostname and account you normally use for Remote Desktop.

1. Download **VpnRdpTray-Setup-x64.exe** and **SHA256SUMS.txt** from the same release. Verify the download, then run the installer from your usual Windows account. The installer includes the .NET Desktop Runtime and installs for that account. [Verify a download](PUBLISHING.md#verify-a-download)
2. Open **Settings → App and OpenVPN**. If needed, choose **Install OpenVPN…** to install the official OpenVPN Community client and drivers. The app checks the package before Windows Installer runs it. Supported versions are 2.7.9 x64 and later 2.7 releases. [OpenVPN downloads](https://community.openvpn.net/Downloads)
3. Choose **Prepare this Windows account…** to start the Interactive Service and register the account’s VPN launcher. Installing OpenVPN and preparing the account each require administrator approval. Continue when Settings shows **OpenVPN is ready**. [OpenVPN Interactive Service](https://github.com/OpenVPN/openvpn/blob/v2.7.9/doc/interactive-service-notes.rst)
4. Complete **VPN Profile** and **Remote Desktop**, then click **Save**. **Connection** and **Auto-start** default to on. The first switch controls whether the app connects and retries. The second controls whether it starts at Windows sign-in. You can change each switch independently. [Windows sign-in startup](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)

**Settings required** appears in the notification area until you save a configuration. If you cannot see the tray icon, open **VPN Remote Desktop** from Start or press **Ctrl+Shift+F11** to open the status window.

For another PC, use the same release installer and checksum file. The recipient enters their connection details on their own PC. [Releases](../../releases)

### VPN profile and sign-in

1. In **Settings → VPN Profile**, choose **Import .ovpn…**. If the profile refers to certificate or key files, choose the files supplied by your VPN administrator in the file pickers.
2. Enter your VPN username. Confirm with your administrator that the VPN expects your password immediately followed by the six-digit authenticator code, then check the corresponding box. A server that asks for a separate challenge needs a compatible client. [OpenVPN challenge-response flow](https://openvpn.net/as-docs/v3/challenge-response/)
3. With the default settings, choose **Sign in to VPN** from the tray or status window when prompted. Enter your regular password and a fresh code from your authenticator. If the code is about to expire, wait for the next one. Canceling pauses the attempt until you choose **Sign in to VPN** again.
4. Set **Rejection limit** to the number of automated rejects your administrator permits. The default is two. After a rejection, any eligible retry waits at least 60 seconds, even if you restart the app. The app pauses at the limit or after a certificate failure. Correct the cause and save Settings to resume.

You can independently save the VPN password and the private key passphrase. To generate codes on this PC, select **Generate VPN codes automatically using a saved TOTP seed** and enter the Base32 seed or use a `hex:` prefix for a hexadecimal seed. Match your enrollment algorithm and period. The defaults are SHA-1 and 30 seconds. With a saved seed, the prompt only needs the password if you left it unsaved. Saving both allows unattended VPN sign-in. [TOTP algorithm](https://www.rfc-editor.org/rfc/rfc6238)

Clear a save or automatic generation checkbox and click **Save** to remove the app’s saved copy. Upgrading migrates legacy seeds that are still present. Enter the seed again if an earlier version removed it.

<details>
<summary>OpenVPN profile requirements</summary>

Use a TLS client profile with `dev tun`, a `remote` endpoint, and either a trusted CA with an exact `verify-x509-name` or a verified SHA-256 peer fingerprint. For a CA-based profile, the importer also enforces the server certificate role. If an older profile provides only a CA or a certificate-name prefix, ask your administrator for an updated export.

Supported inline or referenced files are `ca`, `cert`, `key`, `extra-certs`, `crl-verify`, `tls-auth`, `tls-crypt`, `tls-crypt-v2` and `pkcs12`. Nested configurations, scripts, plugins, compression, external credential files and directives outside the client allowlist require a compatible profile. Prefer a profile with an AEAD data cipher. An explicit AES-CBC `cipher` can serve as a compatibility fallback when the profile supplies neither a modern cipher list nor a fallback. [OpenVPN TLS and data-channel options](https://openvpn.net/community-docs/community-articles/openvpn-2-7-manual/)

</details>

### Remote Desktop target

Open **Settings → Remote Desktop**. Leave the port at 3389 unless your remote PC uses a different one.

- **Microsoft Entra ID.** Enter the registered hostname and your Entra UPN. Keep **Remote Desktop Connection (mstsc)** selected for regular use. Windows handles sign-in, MFA and Conditional Access. **Opened in Remote Desktop** means the app launched the Windows client. Choose **Open Remote Desktop** to return to its sign-in prompt or reopen it after dismissal. The embedded client can report **Connected** after it confirms sign-in. [Direct Entra RDP requirements](https://learn.microsoft.com/en-us/windows-server/remote/remote-desktop-services/remotepc/remote-desktop-connection-single-sign-on)
- **Classic Windows.** Enter `DOMAIN\user`, `COMPUTER\user` or a domain UPN. Windows prompts for credentials unless you opt in to saving the password and Windows policy permits its use. Clear the checkbox and click **Save** to remove the app’s copy. The embedded control requires NLA/CredSSP and server certificate validation. An optional IP can serve as a fallback when it routes through the VPN and the certificate validates for that IP. [RDP certificate authentication](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientadvancedsettings4-authenticationlevel) · [Windows credential policy](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-terminalserver)
- **Hostname mapping.** If VPN DNS cannot resolve the Entra hostname, supply the verified VPN IP and select **Add the hostname and IP to Windows hosts**. Windows asks for administrator approval. The app checks for conflicts before adding the machine-wide entry and checks that the resulting address routes through the VPN. With the box cleared, the app uses the optional Entra IP for reachability diagnostics.

## Use the connection

Left-click the tray icon or press **Ctrl+Shift+F11** for the status window. The activity log shows connection attempts, failures, and retry times in local time. Use the **VPN** circle to turn Connection on or off or to answer a pending VPN sign-in prompt. **Settings** is at the top right.

Use the **RDP** circle to control Remote Desktop while Connection is enabled. Turning RDP off closes the app’s Remote Desktop window and stops its retries while the VPN stays connected. Closing the desktop yourself also keeps it closed for the current app run, including after VPN recovery. Click the **RDP** circle or select **Open Remote Desktop** from the tray to reconnect when ready. Use **Exit** in the tray menu to close the app and its connections.

For a remote PC that people also use in person, enable **Settings → Remote Desktop → Connect Remote Desktop only when opened manually**. The VPN keeps connecting automatically. Once it is up, choose **Open Remote Desktop** when the PC is available. The setting also applies after the app restarts, Windows wakes, or the VPN reconnects. Each click requests one connection, and the app waits for a new click after it ends. Starting a connection yourself can still switch the Windows session away from the physical console. [Windows console and remote terminals](https://learn.microsoft.com/en-us/windows/win32/termserv/consoles-vs-terminals)

With automatic RDP enabled, the embedded client still retries a network failure. It waits for an explicit reopen if Windows reports the session was replaced or ended by a user or the server. Some hosts send only a generic network error, so use manual connection mode when the PC’s console may be in use. [RDP disconnect reasons](https://learn.microsoft.com/en-us/windows/win32/termserv/extendeddisconnectreasoncode) · [RDP server error reporting](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpbcgr/116829a9-9b6b-4044-9290-e523f20ab787)

The right panel requests a scaled preview of a signed-in embedded desktop about every two seconds while the status window is visible. When a frame appears, the VPN and RDP circles move above it. Click the preview or choose **Open Remote Desktop** from the tray to return to the session. Windows can pause rendering when the desktop is minimized. In that case the panel retains its last available frame. The preview stays in memory and clears on disconnect. Use the tray action to return to a native session or a Windows sign-in prompt. [Windows RDP display updates](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpbcgr/79202daa-76b8-4a09-ac67-fc3cae7d895f)

After wake, an open embedded desktop shows **RDP checking after wake** while the app checks the RDP port through the VPN. When the check finishes, **View desktop** returns to the existing window if it is available. A reply from the port confirms reachability. The existing window may still need a fresh connection. Use **Reconnect now** to replace it or **Connection status** to see progress and retries. If the visible desktop closes during recovery, the status window takes over. With the native Windows client, use the tray status and **Reconnect Remote Desktop** for an owned session. Manual-only RDP still requires an explicit open or reconnect. [ActiveX connection state](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscax-connected)

### Embedded desktop controls

- Maximize the window or press **Ctrl+Shift+F** for full screen. Press **Ctrl+Shift+F** again or click **Exit full screen (Ctrl+Shift+F)** to go back. Drag either button to the left or right edge for a narrow vertical strip, or to the top for a horizontal strip. Slide it along the edge to choose a position. The app remembers the edge and position.
- Press **Ctrl+Shift+N** to minimize the focused desktop in either mode, or click **Minimize (Ctrl+Shift+N)** in full screen. **Open Remote Desktop** restores the same session and view.
- **Use all monitors in full screen** is on by default. The app requests the local display layout at connection time with multiple eligible monitors. Clear the setting for a single-display session. [Windows RDP display properties](https://learn.microsoft.com/en-us/azure/virtual-desktop/rdp-properties#display-settings)

The windowed desktop stays at least two-thirds as wide and two-thirds as tall as its monitor. The embedded desktop has a translucent glow outside the windowed frame and a narrow, click-through glow inside the full-screen edges. On the first Settings tab, **App and OpenVPN**, use **Internal glow (full screen)** and **External glow (windowed)** to control them independently. Both start on. Saving only a glow change updates the open embedded desktop without reconnecting.

Click inside the remote desktop to send Windows shortcuts such as **Alt+Tab** and **Win+Tab** to the remote PC. This applies to the embedded and native clients in windowed and full-screen views. Use **Ctrl+Shift+N** to minimize the embedded desktop and return to the local PC. Nested Remote Desktop sessions have different shortcut behavior. [RDP shortcut routing](https://learn.microsoft.com/en-us/azure/virtual-desktop/rdp-properties#keyboardhook) · [Remote Desktop shortcuts](https://learn.microsoft.com/en-us/windows/win32/termserv/terminal-services-shortcut-keys)

**Simulate activity** is off by default and applies only to a signed-in embedded desktop. Move the pointer over the remote desktop once after signing in so the app has a starting point. It waits at least a minute after sign-in and after the desktop gains or loses focus. With the desktop focused, it also waits for a minute without local keyboard or mouse input. After a minute in the background, other local windows can still be in use. The app attempts one small movement of the remote pointer per minute. It sends movement only and leaves your local pointer in place. It skips the movement during authentication prompts, while a modifier key or mouse button is held, or when it cannot place the pointer reliably. Enable it only where your remote-access policy permits it. The remote host still controls session limits and sign-out. [Windows RDP pointer input](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpinputsink)

### Local resources available to the remote PC

In **Settings → Remote Desktop**, review the separate checkboxes for smart cards and security keys, supported USB devices, printers, the clipboard, and local drives and files. All five default to on. Clear any you want to keep local. Changes apply to the next connection and remain subject to Windows and remote-host policy. If the Windows client shows a security dialog, choose the resources to allow for that connection. [Windows RDP redirection](https://learn.microsoft.com/en-us/azure/virtual-desktop/rdp-properties#device-redirection) · [Remote Desktop security dialog](https://learn.microsoft.com/en-us/windows-server/remote/remote-desktop-services/remotepc/understanding-security-warnings#the-connection-security-dialog)

For Entra sessions that need in-session FIDO2/WebAuthn or low-level USB redirection, select **Remote Desktop Connection (mstsc)**. The embedded client configures smart cards and supported Plug and Play devices alongside printers, clipboard and drives. [ActiveX device redirection](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientnonscriptable3)

## When the connection stops

| Status or symptom | What you can do |
| --- | --- |
| OpenVPN service, profile or local control | Check **Settings → App and OpenVPN**. Choose **Prepare this Windows account…** if prompted. Re-import the profile if the status identifies it as the problem. |
| OpenVPN has yet to acknowledge sign-in | Check the installed OpenVPN version and VPN sign-in settings. This step is the handoff to the local VPN client. |
| VPN is waiting for the server | Check connectivity and DNS. If the server still fails to answer, ask your VPN administrator to verify the listener and port. |
| VPN paused after a credential rejection | Check the account and authenticator settings. Save the corrected Settings to resume. |
| VPN paused after a certificate failure | Read the last VPN error in the activity log and use the checks below. After fixing the cause, save Settings to resume. An existing pause survives an upgrade. |
| VPN checking after wake | Windows has resumed or restored the display. The app gives networking a few seconds to settle, then tries the saved RDP TCP port through the existing tunnel. If the tunnel route is missing or the target does not respond, the app refreshes the VPN once before testing again. |
| RDP TCP port refused, reset, or timed out | A refusal or reset is a reply along the network path. Check the saved RDP port, Remote Desktop service, and firewall. A timeout means no reply arrived before the deadline. The remote PC may be asleep or stalled, or the VPN path or a firewall may be blocking traffic. |
| RDP hostname did not resolve or has no VPN route | Check the registered hostname and VPN DNS. If using a hosts mapping, verify it in **Settings → Remote Desktop**. The app requires a verified VPN route before testing the port. |
| Entra MFA is pending | Choose **Open Remote Desktop** and complete the prompt in Windows. If the Windows client reports an error, use it to identify the account, host or Conditional Access issue. |

If you need help, share the app version, the exact status message and retry time, and whether Windows Remote Desktop was open. The app keeps the latest 200 diagnostic entries in memory for the current run. Scroll to the bottom to follow new events, or scroll up to read and copy earlier entries. Keep VPN profiles, keys, enrollment material and raw client logs private.

### VPN certificates and TLS

When only one PC fails, compare its clock, OpenVPN Community version, and imported profile with a working PC. Under **Windows Settings → Time & language → Date & time**, check the time zone and sync the clock. Use a fresh profile issued for your account or device. [Windows date and time settings](https://support.microsoft.com/en-us/windows/experience/personalization/set-time-date-and-time-zone-settings-in-windows)

| Activity log message | What to check |
| --- | --- |
| Server or CA certificate expired or not yet valid | Sync this PC’s clock. If the date error persists, ask the VPN administrator to check the dates on the server certificate and its issuing chain. |
| Server name or fingerprint does not match | The server may have rotated certificates, or the profile may lead to a different endpoint. Get a fresh profile and verify the exact certificate name or SHA-256 fingerprint through your administrator. A VPN certificate name can differ from the endpoint DNS name. |
| Chain, key usage, certificate subject, or revocation problem | Ask the administrator to check the issuing CA, the intermediates the server sends, Server Authentication usage, an OpenVPN-compatible certificate subject, and current revocation lists. A revoked certificate needs replacement. |
| Client certificate, private key, or PKCS#12 bundle problem | Check the key or bundle passphrase. Ask for a matching client certificate and key, or a fresh PKCS#12 bundle exported with current encryption. The server may also need an updated trust chain for client certificates. |
| TLS version or crypto mismatch | Ask the administrator to check the endpoint, TLS 1.2 or 1.3 support, and compatible ciphers and signatures with the current OpenVPN release. Legacy certificates or key parameters may need to be replaced. |
| Control packet rejected or TLS handshake timed out | Check the network and the VPN endpoint and protocol. Ask the administrator to check the matching control-channel key and the server log at the failure time. A client certificate warning can appear earlier in the log. |

To isolate the issue, try the same administrator-issued profile on the affected PC in the standard OpenVPN Community GUI after turning this app’s **Connection** off. Share the redacted activity log message, local timestamp and OpenVPN version with your administrator. Keep identity checks and revocation settings enabled. [OpenVPN TLS configuration](https://openvpn.net/community-docs/community-articles/openvpn-2-7-manual/) · [OpenSSL PKCS#12 compatibility](https://docs.openssl.org/3.0/man1/openssl-pkcs12/)

## What the app protects

- **Administrator access.** The tray runs as a standard user and uses the official OpenVPN Interactive Service for privileged tunnel work. It verifies the service and OpenVPN process before exchanging data over local channels. Installing OpenVPN, preparing the account, and updating the Windows hosts file are each explicit actions that require administrator approval. [OpenVPN Interactive Service](https://github.com/OpenVPN/openvpn/blob/v2.7.9/doc/interactive-service-notes.rst)
- **Server identity and routing.** OpenVPN uses TLS and validates the configured server identity. The embedded Remote Desktop control requires a valid RDP server certificate and uses NLA. Before starting Remote Desktop, the app checks that the target routes through the tunnel and that its port answers there. Windows can resolve the hostname again or change routes after the check. Use firewall or network policy for enforced tunnel-only traffic. [OpenVPN TLS options](https://openvpn.net/community-docs/community-articles/openvpn-2-7-manual/) · [RDP certificate authentication](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientadvancedsettings4-authenticationlevel)
- **Saved credentials.** Windows DPAPI encrypts the imported VPN profile, private keys and any secrets you opt in to saving in `%LOCALAPPDATA%\VpnRdpTray\setup.dpapi` for your Windows account. The TOTP seed is separately protected inside that file. Saved passwords and the seed stay blank in Settings. Software running as your account can still decrypt those values. Saving both VPN factors on this PC puts both within reach of an account compromise. Typing your password and using codes from a separate authenticator keeps the factors separate. [Windows DPAPI](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata) · [Security model and reporting](SECURITY.md)

## Build and verify

Use the Windows x64 .NET 11 SDK and Inno Setup 6. Open PowerShell in the source folder and run the commands below. Pass `-DotNet` or `-Iscc` when either tool is outside its usual location. The build produces an unsigned per-user installer, a source archive and `SHA256SUMS.txt` under `artifacts`. [Download .NET 11](https://dotnet.microsoft.com/en-us/download/dotnet/11.0) · [Download Inno Setup](https://jrsoftware.org/isdl.php) · [Publishing](PUBLISHING.md) · [Third-party notices](THIRD-PARTY-NOTICES.md)

~~~powershell
dotnet run --project .\tests\VpnRdpTray.Tests
.\build.ps1
~~~

The tests use synthetic data to exercise automatic and manual VPN codes, rollover, DPAPI seed migration, optional password storage, profile import and server identity, rejection handling, switch persistence, Windows peer and route checks, and ActiveX activation and event binding. Add `-- --native-client` to test launch of an owned `mstsc.exe` against a closed local port. On a Windows test machine with OpenVPN prepared, add `-- --interactive-service` to check service launch, profile streaming, management authentication, command acknowledgments and shutdown with synthetic credentials and a loopback listener.

For an Entra embedding test, choose **Embedded ActiveX prototype** with a dedicated test target and an MFA policy that requires a fresh prompt. Check that the sign-in confirmation shows **Connected** and that closing or canceling removes the existing window. [ActiveX Entra property](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpextendedsettings-property) · [ActiveX sign-in and disconnect events](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscaxevents-interface)

| Windows acceptance test | Expected behavior |
| --- | --- |
| First run and switches | **Settings required** appears. Turn Auto-start off, leave Connection on, exit and reopen. Verify both values, then repeat with the switches reversed. |
| Status with taskbar hidden | Use **Ctrl+Shift+F11** or the Start shortcut. Verify the log, preview and actions fit on the current display. |
| Release installer | Verify the downloaded installer against the release checksum file and use it on a clean Windows account. |
| VPN prompts and saved credentials | On a new setup, verify password and seed saving are off. With a saved seed, verify an unsaved password prompts. Save both and verify automatic sign-in. Clear the seed and verify the code prompt returns. Cancel it and verify **Sign in to VPN** opens only one new request. |
| Unreachable RDP target | Use a closed RDP port on a test host. Verify the VPN stays up, the status shows a retry, and one desktop opens after restoring the port. |
| Tunnel loss and recovery | Interrupt a test VPN. Verify the app closes its desktop and waits for the VPN. Enter a fresh code when prompted and verify one desktop opens after the route recovers. |
| Sleep and wake | Sleep with the embedded desktop open. On wake, verify that the panel covers the old frame, the probe result is shown, and the status window continues tracking recovery if the old desktop closes. Verify that **View desktop** keeps the existing session, **Reconnect now** closes it before opening another, and manual-only mode never opens a new desktop by itself. With a stale path, confirm that the VPN refreshes once and a refused port is reported separately. |
| Rejected credentials and certificates | Use a test account, a test certificate and a known lockout budget. Verify the retry delay, the pause at the limit, and immediate pause on certificate or classic sign-in rejection. |
| Entra completion and cancellation | Complete MFA once and cancel it once. Check the embedded sign-in and disconnect events and the native handoff status. Use the tray action to return to the same prompt. |
| Embedded resizing and closure | Resize the desktop and maximize it for full screen. Use **Ctrl+Shift+F** to go back. Close the signed-in desktop. Verify that VPN remains connected, the tray offers to reopen RDP, and VPN recovery leaves RDP closed. Reopen it and check that one window appears. |
| Remote PC console sign-in | On a test target, return to the physical console and sign in while the embedded client is connected. When Windows reports the remote session was replaced or ended, verify RDP stays closed until you choose **Open Remote Desktop**, including after a VPN interruption. |
| Manual RDP on a shared PC | Enable manual connection and restart. Confirm VPN connects and RDP waits. Choose **Open Remote Desktop**, close the desktop, and test wake and VPN recovery. Each new connection should wait for an explicit open. |
| Minimize and preview | Focus the embedded desktop in each view and press **Ctrl+Shift+N**. Also try the full-screen **Minimize** button. Use **Open Remote Desktop** and verify the same session returns to its previous view. |
| Full-screen controls | Drag either button to the left, right, and top edges of the monitor. Check that the buttons stack on the sides and return to a row at the top. Reopen the app and verify the last edge and position are restored. |
| Embedded glow | On the first Settings tab, turn off each glow independently while the desktop is open. Switch between full screen and windowed, then restart the app and verify both choices stayed saved. |
| Windows keyboard shortcuts | With the signed-in desktop focused, try **Alt+Tab** and **Win+Tab** in windowed and full-screen views for each client. Switch focus to a local window and verify the shortcuts act locally again. |
| Multiple monitors | With two displays connected, verify that the remote Windows display settings show both monitors. Clear the setting and verify the next session uses one. |
| Local resource redirection | On a target that permits redirection, try a security key, eligible USB device, printer, clipboard text and local drive. Clear each option in Settings and verify it stays off after reconnecting. |
| Simulate activity | Use an input event viewer on an approved test host. Move the pointer over the embedded desktop after signing in. Verify the 60-second wait after sign-in and focus changes and that focused keyboard or mouse activity postpones simulation. After a minute idle or in the background, check for a small remote pointer movement at most once per minute, with no key or button events and no local pointer movement. Verify that holding a modifier or mouse button suppresses the movement and that the native client receives none. |
| Connection controls | Turn the RDP circle off. Check that the desktop closes and the VPN stays up. Turn it back on and verify that only one desktop opens. Turn VPN off, then repeat with **Exit** in the tray menu. Check that the app’s tunnel and desktop close. |
| Uninstall | Uninstall through Windows Apps and approve removal of the app’s hosts entry and launcher, if present. Check that the app’s sign-in registration and configuration are gone. The shared OpenVPN installation stays installed. |
