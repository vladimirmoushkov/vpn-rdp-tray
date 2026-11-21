param(
    [string] $DotNet,
    [string] $Iscc
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if (-not $DotNet) {
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($command) { $DotNet = $command.Source }
    else { $DotNet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
}
if (-not $Iscc) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $Iscc = $command.Source }
    else {
        $Iscc = @(
            (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
        ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (-not $Iscc -or -not (Test-Path -LiteralPath $Iscc)) { throw 'Install Inno Setup 6 or pass -Iscc with its compiler path.' }

function New-SourceArchive([string] $ArchivePath) {
    $files = @(
        '.gitignore', 'global.json', 'build.ps1', 'README.md', 'SECURITY.md', 'PUBLISHING.md',
        'THIRD-PARTY-NOTICES.md', 'installer\VpnRdpTray.iss', 'src\VpnRdpTray\app.manifest', 'src\VpnRdpTray\app.ico',
        'src\VpnRdpTray\UpdateMachine.ps1'
    )
    foreach ($optional in @('LICENSE', '.gitattributes', 'CHANGELOG.md')) {
        if (Test-Path -LiteralPath $optional -PathType Leaf) { $files += $optional }
    }
    foreach ($directory in @('src\VpnRdpTray', 'tests\VpnRdpTray.Tests')) {
        if ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "The source directory cannot be a reparse point: $directory"
        }
        $files += Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Extension -in '.cs', '.csproj' } |
            ForEach-Object { Join-Path $directory $_.Name }
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($ArchivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $timestamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        foreach ($relative in ($files | Sort-Object -Unique)) {
            $file = Get-Item -LiteralPath $relative
            if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Expected a regular source file: $relative"
            }
            if ($file.Extension -ne '.ico' -and (Get-Content -LiteralPath $relative -Raw) -match
                '(?im)^\s*-{5}BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-{5}\s*$|\botpauth://[^\s<>"'']*[?&]secret=[A-Z2-7]{16,}|\b(?:gh[pousr]_|github_pat_)[A-Za-z0-9_]{20,}\b') {
                throw "The source archive scan found a possible credential in $relative. Review the file privately."
            }
            $entry = $archive.CreateEntry($relative.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $timestamp
            $sourceStream = [IO.File]::OpenRead($file.FullName)
            try {
                $archiveStream = $entry.Open()
                try { $sourceStream.CopyTo($archiveStream) }
                finally { $archiveStream.Dispose() }
            } finally { $sourceStream.Dispose() }
        }
    } finally { $archive.Dispose() }
}

function Add-RuntimeNotices([string] $Payload, [string] $ProjectDirectory) {
    $dependencies = Get-Content -LiteralPath (Join-Path $Payload 'VpnRdpTray.deps.json') -Raw | ConvertFrom-Json
    $assets = Get-Content -LiteralPath (Join-Path $ProjectDirectory 'obj\project.assets.json') -Raw | ConvertFrom-Json
    $noticeFiles = [ordered]@{
        'Microsoft.NETCore.App.Runtime.win-x64' = @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')
        'Microsoft.WindowsDesktop.App.Runtime.win-x64' = @('LICENSE')
    }
    $licenses = New-Item -ItemType Directory -Path (Join-Path $Payload 'licenses')
    foreach ($pack in $noticeFiles.Keys) {
        $entries = @($dependencies.libraries.PSObject.Properties.Name -like "runtimepack.$pack/*")
        if ($entries.Count -ne 1) { throw "Could not determine the runtime version for $pack." }
        $version = $entries[0].Split('/')[-1]
        $packages = $assets.packageFolders.PSObject.Properties.Name
        $packDirectory = $packages | ForEach-Object { Join-Path $_ ($pack.ToLowerInvariant() + '\' + $version) } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (-not $packDirectory) { throw "Could not find the restored runtime pack for $pack." }
        foreach ($notice in $noticeFiles[$pack]) {
            Copy-Item -LiteralPath (Join-Path $packDirectory $notice) -Destination (Join-Path $licenses.FullName "$pack-$notice")
        }
    }
    Copy-Item -LiteralPath 'THIRD-PARTY-NOTICES.md' -Destination $licenses.FullName
    if (Test-Path -LiteralPath 'LICENSE' -PathType Leaf) {
        Copy-Item -LiteralPath 'LICENSE' -Destination (Join-Path $licenses.FullName 'PROJECT-LICENSE.txt')
    }
}

function Assert-PublishPayload([string] $Payload) {
    foreach ($item in Get-ChildItem -LiteralPath $Payload -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "The installer staging directory contains a reparse point: $($item.Name)"
        }
        if ($item.PSIsContainer) { continue }
        if ($item.Extension -eq '.dll') { continue }
        if ($item.DirectoryName -eq $Payload -and $item.Name -in @(
            'VpnRdpTray.exe', 'VpnRdpTray.deps.json', 'VpnRdpTray.runtimeconfig.json', 'createdump.exe',
            'LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) { continue }
        throw "Unexpected file in the installer staging directory: $($item.Name)"
    }
}

$artifacts = Join-Path $PSScriptRoot 'artifacts'
$temp = Get-Item -LiteralPath ([IO.Path]::GetTempPath())
$tempPath = $temp.FullName.TrimEnd([IO.Path]::DirectorySeparatorChar)
$stageName = 'VpnRdpTray.Release.' + [Guid]::NewGuid().ToString('N')
$staging = Join-Path $temp.FullName $stageName
$publicSource = Join-Path $staging 'source'
$payload = Join-Path $staging 'payload'
$output = Join-Path $staging 'output'
$projectDirectory = Join-Path $publicSource 'src\VpnRdpTray'
$installerName = 'VpnRdpTray-Setup-x64.exe'
$archiveName = 'VpnRdpTray-Source.zip'
$checksumsName = 'SHA256SUMS.txt'
$artifactNames = @($installerName, $archiveName)

Push-Location $PSScriptRoot
try {
    [void][IO.Directory]::CreateDirectory($output)
    $sourceArchive = Join-Path $output $archiveName
    New-SourceArchive $sourceArchive
    [IO.Compression.ZipFile]::ExtractToDirectory($sourceArchive, $publicSource)
    Push-Location $publicSource
    try {
        $publishArgs = @('publish', (Join-Path $projectDirectory 'VpnRdpTray.csproj'),
            '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $payload,
            '-p:DebugType=None', '-p:DebugSymbols=false', '-p:ContinuousIntegrationBuild=true',
            '-p:Deterministic=true', '-p:UseSharedCompilation=false', "-p:PathMap=$publicSource=/_/")
        & $DotNet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw 'Publishing the self-contained application failed.' }
        Assert-PublishPayload $payload
        Add-RuntimeNotices $payload $projectDirectory
        & $Iscc /Qp "/DPayload=$payload" "/DArtifacts=$output" (Join-Path $publicSource 'installer\VpnRdpTray.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Compiling the per-user installer failed.' }
    } finally { Pop-Location }

    $checksums = foreach ($name in $artifactNames) {
        $hash = Get-FileHash -LiteralPath (Join-Path $output $name) -Algorithm SHA256
        $hash.Hash.ToLowerInvariant() + '  ' + $name
    }
    [IO.File]::WriteAllText((Join-Path $output $checksumsName), (($checksums -join "`n") + "`n"), [Text.Encoding]::ASCII)
    $artifactDirectory = [IO.Directory]::CreateDirectory($artifacts)
    if ($artifactDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'The artifacts directory cannot be a reparse point.'
    }
    foreach ($name in ($artifactNames + $checksumsName)) {
        Move-Item -LiteralPath (Join-Path $output $name) -Destination (Join-Path $artifacts $name) -Force
    }
    $checksums | Write-Output
} finally {
    Pop-Location
    $stageItem = Get-Item -LiteralPath $staging -ErrorAction SilentlyContinue
    if ($stageItem -and $stageItem.PSIsContainer -and
        $stageItem.Parent.FullName.TrimEnd([IO.Path]::DirectorySeparatorChar) -eq $tempPath -and
        $stageItem.Name -eq $stageName -and -not ($stageItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $stageItem.FullName -Recurse -Force
    }
}
