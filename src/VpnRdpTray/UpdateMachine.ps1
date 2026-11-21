$ErrorActionPreference = 'Stop'

try {
    if ($sid -notmatch '^S-1-(\d+-){1,14}\d+$' -or $action -notin @('provision','hosts','remove')) { exit 1 }
    if ($hostName) {
        if ($hostName -notmatch '^[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*$') { exit 1 }
        $address = $null
        if (-not [System.Net.IPAddress]::TryParse($ip, [ref]$address)) { exit 1 }
        $ip = $address.ToString()
    }

    if ($action -in @('provision','remove')) {
        $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine','Registry64').OpenSubKey('SOFTWARE\OpenVPN')
        $root = if ($registry) { [string]$registry.GetValue('') } else { '' }
        $config = if ($registry) { [string]$registry.GetValue('config_dir') } else { '' }
        if ($registry) { $registry.Dispose() }
        if (-not $config -and $root) { $config = [IO.Path]::Combine($root, 'config') }
        if ($config -and $config -match '^[A-Za-z]:\\' -and [IO.Directory]::Exists($config)) {
            $configItem = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($config))
            for ($parent = $configItem; $parent; $parent = $parent.Parent) {
                if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 2 }
            }
            $profile = [IO.Path]::Combine($configItem.FullName, 'VpnRdpTray-' + $sid + '.ovpn')
            $pipe = '\\.\pipe\VpnRdpTray.Profile.' + $sid
            $expected = 'config "' + $pipe.Replace('\','\\') + '"' + [char]10
            if ([IO.File]::Exists($profile)) {
                $profileItem = [IO.FileInfo]::new($profile)
                if ($profileItem.Attributes -band [IO.FileAttributes]::ReparsePoint -or
                    $profileItem.Length -ne $expected.Length -or
                    [IO.File]::ReadAllText($profile,[Text.Encoding]::ASCII) -cne $expected) { exit 4 }
                if ($action -eq 'remove') { [IO.File]::Delete($profile) }
            } elseif ($action -eq 'provision') {
                $handle = [IO.File]::Open($profile, 'CreateNew', 'Write', 'None')
                try {
                    $bytes = [Text.Encoding]::ASCII.GetBytes($expected)
                    $handle.Write($bytes,0,$bytes.Length)
                    $handle.Flush($true)
                } finally { $handle.Dispose() }
            }
        } elseif ($action -eq 'provision') { exit 2 }
        if ($action -eq 'provision') {
            $service = [ServiceProcess.ServiceController]::new('OpenVPNServiceInteractive')
            try {
                if ($service.Status -eq 'Stopped') { $service.Start() }
                $service.WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
            } finally { $service.Dispose() }
        }
    }

    if ($action -in @('hosts','remove')) {
        $path = [IO.Path]::Combine([Environment]::SystemDirectory, 'drivers\etc\hosts')
        if (-not [IO.File]::Exists($path)) { exit 1 }
        $file = [IO.FileInfo]::new($path)
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint -or $file.Length -gt 1048576) { exit 1 }
        $encoding = [Text.Encoding]::GetEncoding(28591)
        $original = $encoding.GetString([IO.File]::ReadAllBytes($path))
        if ($original.Contains([string][char]0)) { exit 1 }
        $tag = '# VpnRdpTray ' + $sid
        $pattern = '(?m)^[^\r\n#]*[ \t]' + [regex]::Escape($tag) + '[ \t]*(?:\r?\n|$)'
        $updated = [regex]::Replace($original,$pattern,'')
        if ($hostName -and $action -eq 'hosts') {
            foreach ($line in ($updated -split '\r?\n')) {
                $tokens = (($line -split '#',2)[0].Trim().TrimStart([char]239,[char]187,[char]191)) -split '\s+'
                if ($tokens.Length -gt 1 -and $tokens[1..($tokens.Length-1)] -contains $hostName) { exit 3 }
            }
            if ($updated.Length -gt 0 -and -not $updated.EndsWith([string][char]10)) { $updated += [Environment]::NewLine }
            $updated += $ip + [char]9 + $hostName + [char]9 + $tag + [Environment]::NewLine
        }
        if ($updated -cne $original) {
            $temporary = $path + '.VpnRdpTray-' + [guid]::NewGuid().ToString('N')
            try {
                [IO.File]::WriteAllBytes($temporary,$encoding.GetBytes($updated))
                if ($original -cne $encoding.GetString([IO.File]::ReadAllBytes($path))) { exit 1 }
                [IO.File]::Replace($temporary,$path,[NullString]::Value)
            } finally {
                if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
            }
            & ([IO.Path]::Combine([Environment]::SystemDirectory,'ipconfig.exe')) /flushdns | Out-Null
        }
    }
    exit 0
} catch { exit 1 }
