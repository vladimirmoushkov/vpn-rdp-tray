# VPN Remote Desktop

VPN Remote Desktop is a .NET 11 WinForms notification-area app for Windows 11 x64. It starts OpenVPN through the official Interactive Service, checks that the Remote Desktop destination routes through that specific tunnel, and opens one owned Remote Desktop session.

## Install

Download `VpnRdpTray-Setup-x64.exe` and `SHA256SUMS.txt` from the same project release. Verify the installer as described in [Publishing](PUBLISHING.md#verify-a-download), then run it from your normal Windows account. The tray app installs for that account and includes the .NET Desktop Runtime. The installer lets you choose **Start VPN Remote Desktop at Windows sign-in**. The choice defaults to on for a new installation.

Open **Setup → App and OpenVPN** to see the sign-in switch, the status of the separate OpenVPN Interactive Service, and the administrator steps. [Windows sign-in startup](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys) · [OpenVPN downloads](https://community.openvpn.net/Downloads) · [Interactive Service protocol and profile location](https://github.com/OpenVPN/openvpn/blob/v2.7.9/doc/interactive-service-notes.rst)

1. Choose **Install OpenVPN** if the official OpenVPN Community 2.7.9 x64 client or a newer 2.7 release is missing. The app checks the official package before opening Windows Installer. An administrator must approve installation of the service and tunnel drivers.
2. Choose **Prepare this Windows account** to start the Interactive Service and register the launcher in its approved profile directory. Windows requests administrator approval separately. If Setup shows **OpenVPN is ready**, continue to **VPN Profile**.

The tray icon appears in the notification area or its overflow. Its initial status is **Setup required**. **Connection** and **Auto-start** start on. Use the two tray switches independently. **Exit** closes the app-owned tunnel and desktop while retaining both switch settings. [Windows sign-in startup](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)

Press **Ctrl+Shift+F11**, open **VPN Remote Desktop** from Start, or left-click the tray icon to see the connection window. It shows the VPN and RDP states, any failure reason, and the next retry. Use **Open Remote Desktop** to bring back the session or **Setup** to edit the connection.

To prepare an installer for another Windows 11 x64 PC, choose **Setup → App and OpenVPN → Save installer…**. This exports the original self-contained release installer. The recipient enters their VPN and Remote Desktop configuration on their PC.

## Set up the VPN

1. Open **Setup → VPN Profile** and import the supplied `.ovpn` profile. Resolve each referenced certificate or key using the file picker that opens for it.
2. Enter the VPN username and regular password in their own fields. Copy **Secret (BASE32)** from the VPN enrollment screen into **TOTP secret**. For **Secret (HEX)**, type `hex:` before the hexadecimal value. Select the enrolled algorithm and time step. Defaults are SHA-1 and 30 seconds. Fill in the optional private key passphrase for a password-protected key.
3. Check the confirmation below the secret once you know the server accepts the password immediately followed by the six-digit code. The app generates and appends the code at sign-in. For a server that uses a separate challenge, obtain a compatible configuration or use the administrator's client. [TOTP algorithm](https://www.rfc-editor.org/rfc/rfc6238) · [Separate OpenVPN challenges](https://openvpn.net/as-docs/v3/challenge-response/)
4. Set the VPN **Rejection limit** to the number of automated rejects your administrator permits. The default is two. After a reject, the next eligible retry waits at least 60 seconds, including across app restarts. At the limit or after certificate failure, correct the problem and save Setup to resume.

The importer accepts a TLS client profile with `dev tun`, at least one `remote`, and an administrator-provided SHA-256 peer fingerprint or a trusted CA with an exact `verify-x509-name`. CA-based imports also require the server certificate role. If an older saved profile has only a CA or a certificate-name prefix, ask your administrator for an updated export and import it in Setup. The supported inline and referenced material is `ca`, `cert`, `key`, `extra-certs`, `crl-verify`, `tls-auth`, `tls-crypt`, `tls-crypt-v2` and `pkcs12`. Profiles with nested configurations, scripts, plugins, compression, external credential files, separate challenges or directives outside the allowlist require a compatible export. Prefer an administrator-provided profile with an AEAD data cipher. An explicitly selected AES-CBC `cipher` can provide a compatibility fallback when the export specifies no modern cipher list or fallback. [OpenVPN client TLS and data-channel options](https://openvpn.net/community-docs/community-articles/openvpn-2-7-manual/)

Setup protects the imported configuration, passwords, keys and TOTP seed with Windows DPAPI for the signed-in user. Saved secrets stay blank when Setup opens. A blank field retains the saved value when it still applies, and the optional password and key passphrase can be removed in Setup. Unattended connection gives software running as the same Windows account access to both VPN factors. Read the [security model](SECURITY.md) before enabling it.

## Set up Remote Desktop

- **Microsoft Entra ID.** Use the registered hostname and the Entra UPN. Keep **Remote Desktop Connection (mstsc)** selected for regular use. Windows handles native sign-in, MFA and Conditional Access. The tray shows **Opened in Remote Desktop** while it owns the client. Choose **Open Remote Desktop** to return to that window or reopen it after dismissal. [Direct Entra RDP requirements](https://learn.microsoft.com/en-us/windows-server/remote/remote-desktop-services/remotepc/remote-desktop-connection-single-sign-on)
- **Classic Windows.** Use `DOMAIN\user`, `COMPUTER\user` or the domain UPN. The optional password goes through the Windows RDP control when client policy permits. The control has CredSSP/NLA enabled and requires successful server certificate validation. An optional routed IP can be used as a fallback when the certificate validates for that address. A rejected classic credential requires user action before another automatic sign-in. [RDP certificate authentication level](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientadvancedsettings4-authenticationlevel) · [Windows credential saving policy](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-terminalserver)
- **Hostname mapping.** Leave the port at 3389 unless the target uses a different one. If VPN DNS cannot resolve the Entra hostname, supply its verified VPN IP and select **Add the hostname and IP to Windows hosts**. Saving asks Windows to approve a machine-wide entry for that hostname. The app refuses to overwrite an existing conflicting entry and requires the resolved address to route through the VPN before launching RDP. With the box cleared, the Entra IP field is used for reachability diagnostics.

Maximizing the embedded desktop enters full screen. **Ctrl+Shift+F** switches between full screen and windowed mode. **Ctrl+Shift+N** minimizes the focused embedded desktop in either mode. **Open Remote Desktop** restores the same session in its previous view. The window title and the blue **Exit full screen** button’s tooltip show both shortcuts. **Use all monitors in full screen** in Remote Desktop Setup defaults to on. The app requests the layout of the local displays when a connection starts with multiple eligible monitors. Clear the setting for a single-display session. [Windows RDP display properties](https://learn.microsoft.com/en-us/azure/virtual-desktop/rdp-properties#display-settings)

Drag **Exit full screen** to reposition it along the top edge. The app remembers the docked position. The windowed desktop stays at least two-thirds as wide and two-thirds as tall as its current monitor. A semi-transparent blue glow identifies the embedded desktop. It sits outside the frame in windowed mode and fades inward along the full-screen content edges over five display-scaled pixels. Mouse input passes through the glow.

Under **Setup → Remote Desktop**, choose which local resources to make available to the remote PC. Security keys and smart cards, supported USB devices, printers, the clipboard, and local drives and files each have a separate checkbox. All five default to on. Clear any you do not want to share, especially local drives, clipboard and security keys. Changes take effect on the next connection. Windows and the remote PC’s policies determine which resources can be redirected. If Remote Desktop Connection displays its connection security dialog, select the resources you want Windows to allow for that connection. [Windows RDP redirection settings](https://learn.microsoft.com/en-us/azure/virtual-desktop/rdp-properties#device-redirection) · [Remote Desktop Connection security dialog](https://learn.microsoft.com/en-us/windows-server/remote/remote-desktop-services/remotepc/understanding-security-warnings#the-connection-security-dialog)

For Entra sessions that need in-session FIDO2/WebAuthn or low-level USB redirection, select **Remote Desktop Connection (mstsc)**. The embedded client configures smart cards and supported Plug and Play devices alongside printers, clipboard and drives. [ActiveX device redirection](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientnonscriptable3)

Closing a signed-in embedded desktop schedules a reconnect while **Connection** is on. The status window and tray report the closure and next retry. Turn **Connection Off** when you want to stop automatic reopening.

To test embedding, use a dedicated Entra test target and select **Embedded ActiveX prototype**. Exercise MFA with a policy that requires a fresh prompt. Confirm that **Connected** appears after completing sign-in and that closing or canceling removes the existing desktop. Check failures and a subsequent disconnect. [ActiveX Entra property](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpextendedsettings-property) · [ActiveX sign-in and disconnect events](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscaxevents-interface)

## Build and verify

Use the Windows x64 .NET 11 SDK and Inno Setup 6. Open PowerShell in the source folder and run the commands below. Pass `-DotNet` or `-Iscc` when either tool is outside its usual location. The build writes an unsigned per-user installer, a source archive and `SHA256SUMS.txt` under `artifacts`. See [Publishing](PUBLISHING.md) for the release procedure and [third-party notices](THIRD-PARTY-NOTICES.md) for bundled runtime terms. [Download .NET 11](https://dotnet.microsoft.com/en-us/download/dotnet/11.0) · [Download Inno Setup](https://jrsoftware.org/isdl.php)

~~~powershell
dotnet run --project .\tests\VpnRdpTray.Tests
.\build.ps1
~~~

The test project uses synthetic data to check TOTP rollover, profile import and server identity, rejection handling, DPAPI and switch persistence, Windows peer and route checks, and ActiveX activation and event binding. Add `-- --native-client` to exercise an owned `mstsc.exe` launch against a closed local port. On a dedicated Windows test machine with OpenVPN prepared, `-- --interactive-service` checks service launch, profile streaming, management authentication and shutdown with synthetic credentials.

| Windows acceptance test | Expected behavior |
| --- | --- |
| First run and switches | Setup required appears. Turn Auto-start off, leave Connection on, exit and reopen. Verify the two values, then repeat with the settings reversed. |
| Status with taskbar hidden | Open the connection window with Ctrl+Shift+F11 or the Start shortcut. Verify that the states and actions are available. |
| Shareable installer | Open **App and OpenVPN** and choose **Save installer…**. Verify the saved file matches the release installer. |
| Unreachable RDP target | Use a closed RDP port on a test host. The VPN stays up, no desktop opens, and the tray lists the next RDP retry. Restore the port and verify one desktop opens. |
| Tunnel loss and recovery | Interrupt a test VPN while keeping the local Windows session open. The owned desktop closes, RDP waits, and one desktop opens after the tunnel and route recover. |
| Rejected credentials and certificates | Use a test account, test certificate and a known lockout budget. Confirm one attempt per backoff, a pause at the VPN limit, and immediate pause on certificate or classic sign-in rejection. |
| Entra completion and cancellation | Complete MFA once and cancel it once. Check the embedded sign-in and disconnect events, and check that native mode reports handoff. Use the tray action to return to the same prompt. |
| Embedded resizing and closure | Resize the desktop, maximize it to enter full screen, and use Ctrl+Shift+F to return to windowed mode. Close the signed-in desktop while Connection is on. The tray reports the closure, then one desktop reopens after the retry delay. |
| Minimize the desktop | In each view, focus the embedded desktop and press Ctrl+Shift+N. Open the desktop again from the status window and verify the same session returns to its previous view. |
| Multiple monitors | Connect two displays, leave **Use all monitors in full screen** checked, and start a new session. Verify that the remote Windows display settings show both monitors. Clear the checkbox, save, and verify the next session uses one display. |
| Local resource redirection | On a target that permits redirection, connect with the five checkboxes on. Try a local security key, an eligible USB device, a printer, clipboard text and a local drive. Clear each option in Setup and verify it stays off after a reconnect. |
| Exit and uninstall | With an owned session open, turn Connection off, then repeat with Exit. Uninstall through Windows Apps. Approve cleanup of the tagged hosts entry and service-approved launcher, if present. Verify the app's sign-in registration, saved configuration and owned clients are gone. The shared OpenVPN installation remains installed. |
