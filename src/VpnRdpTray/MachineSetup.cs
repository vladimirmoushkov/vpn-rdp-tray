using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace VpnRdpTray;

internal sealed record OpenVpnInstall(string Executable, string Directory)
{
    internal string BootstrapPath => Path.Combine(Directory, $"VpnRdpTray-{MachineSetup.UserSid}.ovpn");

    internal bool IsProvisioned =>
        File.Exists(BootstrapPath) &&
        (File.GetAttributes(BootstrapPath) & FileAttributes.ReparsePoint) == 0 &&
        new FileInfo(BootstrapPath).Length == Encoding.ASCII.GetByteCount(MachineSetup.Bootstrap) &&
        File.ReadAllText(BootstrapPath, Encoding.ASCII) == MachineSetup.Bootstrap;

    internal static OpenVpnInstall Get()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey("SOFTWARE\\OpenVPN");
        var root = key?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(root))
            throw Missing();
        var exeValue = key?.GetValue("exe_path") as string;
        var dirValue = key?.GetValue("config_dir") as string;
        var executable = string.IsNullOrWhiteSpace(exeValue) ? Path.Combine(root, "bin", "openvpn.exe") : exeValue;
        var directory = string.IsNullOrWhiteSpace(dirValue) ? Path.Combine(root, "config") : dirValue;
        if (!Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(directory) ||
            !File.Exists(executable) || !System.IO.Directory.Exists(directory))
            throw Missing();

        var version = FileVersionInfo.GetVersionInfo(executable);
        if (version.FileMajorPart != 2 || version.FileMinorPart != 7 || version.FileBuildPart < 9)
            throw Missing();
        return new(Path.GetFullPath(executable), Path.GetFullPath(directory));
    }

    private static SetupException Missing() =>
        new("Install OpenVPN Community 2.7.9 or a later 2.7 release with the Interactive Service and Windows drivers.");
}

internal static class MachineSetup
{
    internal static readonly string UserSid = ReadUserSid();
    internal static string ProfilePipe => $"VpnRdpTray.Profile.{UserSid}";
    internal static string Bootstrap => "config \"\\\\\\\\.\\\\pipe\\\\" + ProfilePipe + "\"\n";
    private static string HostsPath => Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
    private static string HostsTag => "# VpnRdpTray " + UserSid;

    private static string ReadUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    internal static bool HostsMatch(RdpSettings rdp)
    {
        string[] tagged = ReadHostsEntries();
        return rdp.WindowsHosts
            ? tagged.Length == 1 && tagged[0].Equals($"{rdp.Ip}\t{rdp.Host}\t{HostsTag}", StringComparison.OrdinalIgnoreCase)
            : tagged.Length == 0;
    }

    private static string[] ReadHostsEntries() =>
        File.Exists(HostsPath)
            ? File.ReadLines(HostsPath).Where(line => line.TrimEnd().EndsWith(HostsTag, StringComparison.Ordinal)).ToArray()
            : [];

    internal static Task ProvisionAsync()
    {
        var install = OpenVpnInstall.Get();
        return install.IsProvisioned && OpenVpnSession.ServiceProcessId() != 0
            ? Task.CompletedTask : RunAdminAsync("provision");
    }

    internal static async Task InstallOpenVpnAsync(CancellationToken token = default)
    {
        const string url = "https://swupdate.openvpn.org/community/releases/OpenVPN-2.7.9-I002-amd64.msi";
        const string hash = "D050EFADC6CABC4E304EB810DD7FEDFC54B859D34730AB9CDCB22D61EB86AB7A";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2), MaxResponseContentBufferSize = 32 * 1024 * 1024 };
        string directory = Path.Combine(Settings.DirectoryPath, "OpenVPN");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"openvpn-{Guid.NewGuid():N}.msi");
        try
        {
            byte[] data = await http.GetByteArrayAsync(url, token);
            if (Convert.ToHexString(SHA256.HashData(data)) != hash)
                throw new SetupException("The OpenVPN download did not match the verified release package.");
            await File.WriteAllBytesAsync(path, data, token);
            using var package = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(SHA256.HashData(package)) != hash)
                throw new SetupException("The OpenVPN package changed before Windows Installer opened it.");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                Arguments = $"/i \"{path}\" /passive /norestart ADDLOCAL=OpenVPN,OpenVPN.Service,Drivers,Drivers.TAPWindows6,Drivers.OvpnDco",
                WorkingDirectory = Environment.SystemDirectory,
                Verb = "runas",
                UseShellExecute = true
            }) ?? throw new SetupException("The OpenVPN installer could not be opened.");
            await process.WaitForExitAsync();
            if (process.ExitCode is 1641 or 3010)
                throw new SetupException("OpenVPN was installed. Restart Windows, then finish provisioning in Setup.");
            if (process.ExitCode != 0)
                throw new SetupException($"OpenVPN installer exited with code {process.ExitCode}.");
        }
        catch (HttpRequestException)
        {
            throw new SetupException("The official OpenVPN installer could not be downloaded.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new SetupException("OpenVPN installation needs administrator approval. Run this step again with an administrator.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 5 or 740 or 1314 or 1326)
        {
            throw new SetupException("Windows requires an administrator to install OpenVPN. Ask an administrator to approve the installation.");
        }
        finally { File.Delete(path); }
    }

    internal static Task SetHostsAsync(RdpSettings rdp) =>
        HostsMatch(rdp) ? Task.CompletedTask : rdp.WindowsHosts
            ? RunAdminAsync("hosts", SetupInput.Host(rdp.Host), SetupInput.Ip(rdp.Ip ?? "") ??
                throw new SetupException("Enter the IP address to add to the Windows hosts file."))
            : RunAdminAsync("hosts");

    internal static Task RemoveAsync()
    {
        bool bootstrapExists;
        try { bootstrapExists = OpenVpnInstall.Get().IsProvisioned; }
        catch (SetupException) { bootstrapExists = false; }
        return !bootstrapExists && ReadHostsEntries().Length == 0
            ? Task.CompletedTask
            : RunAdminAsync("remove");
    }

    private static async Task RunAdminAsync(string operation, string host = "", string ip = "")
    {
        using var resource = typeof(MachineSetup).Assembly.GetManifestResourceStream("VpnRdpTray.UpdateMachine.ps1")!;
        using var reader = new StreamReader(resource);
        // Each interpolated value has a restricted alphabet before it can reach PowerShell.
        var script = $"$action='{operation}';$sid='{UserSid}';$hostName='{host}';$ip='{ip}';\n" + reader.ReadToEnd();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded,
                WorkingDirectory = Environment.SystemDirectory,
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new SetupException("Windows could not start the administrator step.");
            await process.WaitForExitAsync();
            switch (process.ExitCode)
            {
                case 0: return;
                case 2: throw new SetupException("The OpenVPN service configuration directory is missing or unavailable.");
                case 3: throw new SetupException("A conflicting entry already exists in the Windows hosts file. Ask your administrator to review it.");
                case 4: throw new SetupException("An existing OpenVPN launcher occupies the app’s profile path. Ask your administrator to review it.");
                default: throw new SetupException("Windows could not apply the administrator change. Check local policy or ask your administrator.");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new SetupException("Windows did not receive administrator approval. Repeat this step with an administrator.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 5 or 740 or 1314 or 1326)
        {
            throw new SetupException("Windows requires administrator approval for this change. Ask an administrator to approve it.");
        }
    }
}
