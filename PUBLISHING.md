# Publish on GitHub

## Prepare the repository

1. Start a new private repository from a freshly extracted `VpnRdpTray-Source.zip`. Use a public project identity and your GitHub no-reply commit email before making the first commit. Review author and committer names and emails in every commit and annotated tag. Approve the public repository owner and publisher name. [Commit email privacy](https://docs.github.com/en/account-and-profile/how-tos/email-preferences/setting-your-commit-email-address)
2. Confirm who owns the source and choose a project license. If the intent is open-source distribution, add the approved `LICENSE` and include it in the release. Third-party runtime terms are separate. [Choosing a repository license](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository) · [Third-party notices](THIRD-PARTY-NOTICES.md)
3. Review the entire staged file list and scan it for secrets and internal identifiers. Build only with synthetic fixtures. Keep real VPN profiles, private keys, passwords, enrollment data, user and host identifiers, IP addresses, dumps, screenshots, local build logs, and generated binaries out of Git. Also review the repository description, release notes, issues, wiki, workflow artifacts, and Git history before making anything public. Secret scanning complements that review. [GitHub sensitive-data guidance](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/removing-sensitive-data-from-a-repository)
4. Protect the default branch and release tags. Enable secret scanning and push protection where available. Enable private vulnerability reporting when the repository is public and verify the form is visible. [GitHub private vulnerability reporting](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository)

## Build and review a draft release

1. Build from a fresh checkout of the reviewed, versioned commit on a clean Windows runner. Run the test project and `build.ps1` using the documented .NET and Inno Setup versions. The build stages a clean source snapshot and includes the restored .NET redistribution notices with the installer. Record the exact toolchain versions in the release record.
2. Test the candidate in a clean Windows 11 x64 VM under a standard account. Verify install and uninstall, separate elevation for OpenVPN and hosts changes, credential rejection and certificate failures, a confirmed Entra sign-in and cancellation, VPN reconnect, settings persistence and installer export. Use a dedicated test VPN, account, and RDP target. Compare the installed file list with the release manifest and verify that first launch asks for Setup. [Build and acceptance tests](README.md#build-and-verify)
3. Authenticode-sign and timestamp the final installer using a protected release signing key owned by the approved public publisher. Keep the signing key out of the repository, build archive and ordinary development machines. Verify the signature on a clean system. The local build script produces an unsigned installer. Authenticode signing changes its bytes. Regenerate `SHA256SUMS.txt` after signing. [Windows code signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options) · [SignTool verification](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool)
4. Tag the exact source commit, such as `v1.0.11`. Create a GitHub draft release and attach `VpnRdpTray-Setup-x64.exe`, `VpnRdpTray-Source.zip`, and the matching `SHA256SUMS.txt`. Use GitHub artifact attestations for the final bytes when the build runs on a supported GitHub Actions workflow. Review asset names, scope, hashes, signature and public metadata before publishing the release. [Managing releases](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository) · [Build provenance](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations)
5. Monitor .NET, OpenVPN and Windows security updates. Rebuild installers when the bundled .NET runtime receives security fixes. Review OpenVPN package pins against upstream releases before changing them. [Self-contained .NET security updates](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core#automatic-patching-on-windows-operating-system) · [OpenVPN security advisories](https://openvpn.net/security-advisories/)

To regenerate the manifest after signing, run this from the source root in PowerShell:

~~~powershell
$assets = 'VpnRdpTray-Setup-x64.exe', 'VpnRdpTray-Source.zip'
$checksums = foreach ($name in $assets) {
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path 'artifacts' $name)
    "$($hash.Hash.ToLowerInvariant())  $name"
}
$checksums | Set-Content -LiteralPath 'artifacts/SHA256SUMS.txt' -Encoding ascii
~~~

## Verify a download

Download the assets and manifest from the same GitHub release. Compare the SHA-256 printed below with the line for the installer. Check that Authenticode reports `Valid` and shows the publisher named on the release page. A checksum compares bytes. The signature identifies the publisher.

~~~powershell
Get-FileHash -Algorithm SHA256 -LiteralPath .\VpnRdpTray-Setup-x64.exe
Get-AuthenticodeSignature -LiteralPath .\VpnRdpTray-Setup-x64.exe |
    Select-Object Status, @{Name='Publisher'; Expression={$_.SignerCertificate.Subject}}
~~~
