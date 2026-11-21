# Version history

The [Releases page](../../releases) contains the current release, the recovered 1.0.20 installer and source, the 1.0.14 source snapshot, and the preserved 1.0.11 release candidate. Each archival tag points to its own recovered source. The notes also cover builds whose release files could no longer be verified.

| Version | Changes |
| --- | --- |
| **1.0.22** | Fixed the activity log at its 200-entry limit. It now removes expired rows and adds new ones while preserving the reader's scroll position and selection. |
| **1.0.21** | Added VPN recovery after Windows wakes. The app tests the configured Remote Desktop TCP port through the existing tunnel and refreshes the tunnel once if the path appears stale. Added distinct RDP port and VPN certificate diagnostics. |
| **1.0.20** | Replaced the duplicate status box and button row with VPN and RDP controls above the preview. RDP can be turned off without interrupting the VPN. Clicking the preview opens the existing session. |
| **1.0.19** | Replaced the optional keep-awake Shift tap with a one-pixel movement inside the embedded desktop. Updated the setup, security, and publishing guides. |
| **1.0.18** | Made optional activity simulation wait at least a minute after sign-in or a focus change. A focused desktop also had to be idle before sending a Shift tap. |
| **1.0.17** | Shortened the status window and preview while retaining readable log text. Added a shared app and installer icon and made the next available action easier to find. |
| **1.0.16** | Made OpenVPN acknowledge each local management command before the next command is sent. Added a distinct failure reason when local credential delivery stalls. |
| **1.0.15** | Renamed Setup to Settings and introduced the two-pane status window with a timestamped log, animated connection stages, and a scaled preview of the embedded desktop. |
| **1.0.14** | Added phase-specific OpenVPN timeouts, separate VPN and RDP progress, and clearer diagnostics for retries and failures. |
| **1.0.13** | Restored optional automatic TOTP generation from a separately encrypted enrollment seed. Added an opt-in activity timer for the embedded desktop. |
| **1.0.12** | Used manually entered TOTP codes and removed any previously saved seed from the active settings file. Made saving the VPN password, private-key passphrase, and classic RDP password separate opt-ins. |
| **1.0.11** | Prepared a release candidate for GitHub. Kept saved values out of the Settings fields, cleared temporary credential buffers, and required an exact VPN server certificate name or fingerprint. |
| **1.0.10** | Made Ctrl+Shift+N minimize the embedded desktop in windowed and full-screen modes. Improved the click-through full-screen edge glow and its scaling. |
| **1.0.9** | Added a five-pixel click-through glow on the inside edges in full screen. Set the windowed desktop's minimum size to two-thirds of the monitor width and height. |
| **1.0.8** | Added separate options for security keys, USB, printers, clipboard, and drive redirection. Added an installer export, made startup and OpenVPN preparation controls visible, and introduced Ctrl+Shift+N for full screen. |
| **1.0.7** | Made Maximize enter full screen, added an option to use all monitors, and made the full-screen exit control draggable along the top edge. |
| **1.0.5** | Added a status window accessible from the tray, Start menu, and Ctrl+Shift+F11. Added Ctrl+Shift+F for the embedded desktop and made the reconnect after a user closes RDP visible. |
| **1.0.2** | First retained release notes for the Windows tray app. Included independent startup and connection switches, DPAPI-protected settings, OpenVPN Interactive Service integration, and embedded and native Remote Desktop paths. |
