# Security

## What the connections verify

- **OpenVPN.** Import a profile from your VPN administrator. The app accepts supported client directives, requires TLS 1.2 or later, and rejects compression. It requires a trusted CA and an exact server certificate name with the server certificate role, or a SHA-256 certificate fingerprint that you can verify through a trusted channel. The app pauses on certificate failure. It also stores the credential rejection count across restarts, so set the rejection limit to match the VPN server's lockout policy. [OpenVPN 2.7 manual](https://openvpn.net/community-docs/community-articles/openvpn-2-7-manual/)
- **Remote Desktop.** The app requires server certificate validation and enables NLA/CredSSP. In Microsoft Entra mode, Windows handles sign-in and applies the organization's MFA and Conditional Access policy. The Windows client may keep credentials you choose to remember in its own sign-in dialog. [RDP server authentication](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientadvancedsettings4-authenticationlevel) · [Microsoft Entra RDP requirements](https://learn.microsoft.com/en-us/windows-server/remote/remote-desktop-services/remotepc/remote-desktop-connection-single-sign-on)
- **The path to the remote PC.** The app checks the DNS results and route before opening Remote Desktop. It watches the route and closes the session it opened if the VPN goes down. Windows can resolve the hostname again after the check, and routes can change between checks. Enforce tunnel-only traffic with a firewall or network policy when that guarantee is required.

## Which steps need administrator approval

The tray runs as the signed-in standard user. The official OpenVPN Interactive Service handles privileged tunnel operations. Installing OpenVPN, preparing the service-approved launcher, and changing the optional Windows hosts entry each require administrator approval. The OpenVPN download is pinned to the SHA-256 of the official package. The app verifies the hash again using an open package handle and prevents replacement of the file and its local directory path during launch. [OpenVPN downloads](https://community.openvpn.net/Downloads) · [Interactive Service protocol](https://github.com/OpenVPN/openvpn/blob/v2.7.9/doc/interactive-service-notes.rst)

The launcher on disk contains a named-pipe reference. The tray checks the service pipe against Windows Service Control Manager, then sends the imported profile over a current-user pipe after verifying the OpenVPN child process ID. It also verifies the child process on the loopback management connection before proving possession of a random password generated for that launch. That password travels through the service's standard input, and the VPN password and one-time code travel through the authenticated local connection. [OpenVPN management protocol](https://github.com/OpenVPN/openvpn/blob/v2.7.9/doc/management-notes.txt)

## What the app stores

| Where | What it contains |
| --- | --- |
| `%LOCALAPPDATA%\VpnRdpTray\setup.dpapi` | VPN and RDP settings, imported certificate and private key material, and any passwords or TOTP seed you chose to save. Windows DPAPI `CurrentUser` encrypts the file for the signed-in user. The app saves it by atomically replacing the previous file and removes its active copy on uninstall. |
| `%LOCALAPPDATA%\VpnRdpTray\entra.rdp` | The hostname, account name, and connection options for native Remote Desktop. The app removes this file on normal teardown and cleans up a leftover copy at startup. Windows and Remote Desktop can retain their own history. |
| Windows hosts file | If enabled, the mapping includes the hostname, IP address and a Windows account SID tag. The helper refuses a conflicting mapping. Windows auditing can record the host data during an approved change. |
| Local memory | The latest 200 diagnostic entries use fixed app-written text. The scaled desktop preview is captured from the signed-in embedded session and clears when it disconnects. Both stay in local memory. |

The VPN password, private key passphrase, and classic RDP password each have their own save option. They start off for new users, and upgrades carry forward existing choices. To remove an app-saved value, clear the option and save. Settings leaves saved credential fields blank. Old backups or copies can still contain earlier values. [Windows DPAPI](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata)

For automatic VPN codes, the app separately encrypts the TOTP seed inside the settings file using the same Windows account. It decrypts the seed when generating a code and clears its mutable seed buffer afterward. It also clears mutable buffers holding decrypted settings and encoded authentication commands after use. Recently used values may still exist in managed strings, Windows controls, or OpenVPN memory. Restrict access to the Windows session, process memory, and crash dumps. Upgrades migrate legacy saved seeds that are still present. Clear automatic generation and save to remove the active seed. [TOTP requirements](https://www.rfc-editor.org/rfc/rfc6238) · [Microsoft guidance on handling passwords](https://learn.microsoft.com/en-us/windows/win32/secbp/handling-passwords)

**Decide which factors to save.** DPAPI protects stored data from routine access by other accounts. Software running as you, or an administrator who controls the PC, can still reach your saved credentials. Saving both the VPN password and TOTP seed enables unattended sign-in, and a compromise of your Windows account can expose both. To keep the factors separate, leave password saving off and enter codes from a separate authenticator. Use a managed PC with current patches and disk encryption.

## What a remote session can access

The five redirection options in **Settings → Remote Desktop** start on. These let the remote PC access enabled local drives and files, clipboard, printers, eligible USB devices, and supported security keys or smart cards. Turn off the resources you need to keep local. Windows and the remote host can restrict redirection further. [Microsoft redirection guidance](https://learn.microsoft.com/en-us/windows-server/remote/remote-desktop-services/remotepc/understanding-security-warnings#understanding-redirections)

**Simulate activity** starts off. If enabled, the app can send one tiny pointer movement per minute through its signed-in embedded Remote Desktop control. It uses the last pointer position observed in that desktop and leaves the local cursor in place. It waits at least 60 seconds after sign-in or a focus change. While the desktop is focused, it also waits for at least 60 seconds without local keyboard or mouse input. It skips the movement during authentication, while a modifier or mouse button is held, or if it cannot identify the foreground window or safely place the pointer in the embedded session. Host idle policies still apply. [Windows RDP pointer input](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpinputsink)

## Respond to a disclosure

Tell the issuing administrator if a password, TOTP seed, private key, or profile containing credentials was disclosed. Change exposed passwords, revoke and re-enroll an exposed authenticator, and revoke and reissue compromised client certificates as applicable. Save the replacement details in Settings. Treat the original profiles, enrollment material, and recovery codes as confidential. [GitHub guidance on rotating exposed credentials](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/removing-sensitive-data-from-a-repository)

## Report a vulnerability

Use the repository's **Security → Report a vulnerability** form. Include the app, Windows, and OpenVPN versions, what happened, and a reproduction using synthetic accounts and endpoints. Keep profiles, protected settings files, host mappings, keys, TOTP enrollment material, dumps, and personal or company identifiers out of reports. [GitHub private vulnerability reporting](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository)
