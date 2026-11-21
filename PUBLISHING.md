# Publish on GitHub

## Prepare a private repository

1. Extract `VpnRdpTray-Source.zip` into a new directory and make the first commit in a private repository. Use the approved public project name and a GitHub no-reply commit address. Review both author and committer names and addresses in the history and in annotated tags. Agree on the public repository owner and the name that will appear on the signing certificate. [Commit email privacy](https://docs.github.com/en/account-and-profile/how-tos/email-preferences/setting-your-commit-email-address)
2. Confirm who owns the source and agree on a project license. For open-source distribution, put the approved license in `LICENSE` so the build includes it. Review the separate terms for the bundled .NET runtime. [Choosing a repository license](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository) · [Third-party notices](THIRD-PARTY-NOTICES.md)
3. Use synthetic accounts, profiles, and endpoints for tests and examples. Review the staged files, Git history, release notes, repository description, issues, wiki, and workflow artifacts for credentials and private identifiers. Keep real profiles, keys, enrollment data, hostnames, user names, IP addresses, dumps, screenshots, local build logs, and generated binaries out of Git. The packager checks for some common secret patterns. Review filenames, content, and public metadata as well. [GitHub sensitive-data guidance](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/removing-sensitive-data-from-a-repository)
4. Enable branch and tag protection, secret scanning, and push protection where available. When the repository is public, enable private vulnerability reporting and check that the form appears. [GitHub private vulnerability reporting](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository)

## Build and test the candidate

1. Check out the reviewed versioned commit on a clean Windows runner. Run the test project and `build.ps1`. Record the exact .NET SDK, restored runtime, and Inno Setup versions so another maintainer can repeat the build. `build.ps1` creates a source ZIP from selected project files, extracts it to a fresh staging directory, and builds the installer from that copy. It stages the runtime notices with the application and outputs the ZIP, installer, and `SHA256SUMS.txt`. [Build steps](README.md#build-and-verify)
2. Install in a clean Windows 11 x64 VM as a standard user. Check first launch and **Settings required**, install and uninstall, and separate elevation for OpenVPN and hosts changes. Use a dedicated test VPN and remote PC to verify settings persistence, recovery after VPN loss, certificate and credential failures, and Entra sign-in and cancellation. Check the installed folder for the app and runtime notices. [Acceptance tests](README.md#build-and-verify)

## Sign and publish

1. Sign and timestamp the reviewed installer with Authenticode using the approved publisher's protected signing key. Keep the key outside the repository, build archive, and everyday development machines. The local build produces an unsigned installer. Verify the signed copy on a clean machine, then regenerate `SHA256SUMS.txt` for the final files. Signing and timestamping change the bytes. [Windows code signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options) · [SignTool verification](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool)
2. Tag the reviewed source commit, for example `v1.0.21`, and create a draft GitHub release. Attach `VpnRdpTray-Setup-x64.exe`, `VpnRdpTray-Source.zip`, and the matching `SHA256SUMS.txt`. When using a supported GitHub Actions workflow, attest the final bytes to record where they were built. Before publishing, review the assets, hashes, publisher identity, and release metadata. [Managing releases](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository) · [Build provenance](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations)
3. Follow .NET, OpenVPN, and Windows security updates. Rebuild when the bundled .NET runtime receives a security fix. Review OpenVPN package pins against official releases before changing them. [Self-contained .NET updates](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core#automatic-patching-on-windows-operating-system) · [OpenVPN security advisories](https://openvpn.net/security-advisories/)

Run this from the source root in PowerShell after signing.

~~~powershell
$assets = 'VpnRdpTray-Setup-x64.exe', 'VpnRdpTray-Source.zip'
$checksums = foreach ($name in $assets) {
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path 'artifacts' $name)
    "$($hash.Hash.ToLowerInvariant())  $name"
}
$checksums | Set-Content -LiteralPath 'artifacts/SHA256SUMS.txt' -Encoding ascii
~~~

## Verify a download

Download the installer and `SHA256SUMS.txt` from the same release. Compare the calculated SHA-256 with the manifest to check the downloaded bytes. For a signed release, check that the Authenticode status is `Valid` and the publisher matches the release page. For a local unsigned build, the checksum only checks the bytes.

Open PowerShell in the download folder and run the following commands.

~~~powershell
Get-FileHash -Algorithm SHA256 -LiteralPath .\VpnRdpTray-Setup-x64.exe
Get-AuthenticodeSignature -LiteralPath .\VpnRdpTray-Setup-x64.exe |
    Select-Object Status, @{Name='Publisher'; Expression={$_.SignerCertificate.Subject}}
~~~
