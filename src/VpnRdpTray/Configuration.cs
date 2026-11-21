using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace VpnRdpTray;

internal enum RdpMode { Classic, Entra }
internal enum TotpAlgorithm { Sha1, Sha256, Sha512 }

internal sealed record VpnSettings(string Profile, string Username, string? Password, int RejectLimit, string? KeyPassword)
{
    public byte[]? ProtectedTotpSeed { get; init; }
    public TotpAlgorithm TotpAlgorithm { get; init; } = TotpAlgorithm.Sha1;
    public int TotpPeriod { get; init; } = 30;

    public override string ToString() => nameof(VpnSettings);
}

internal sealed record RdpSettings(string Host, string? Ip, int Port, string Username, RdpMode Mode,
    string? Password, bool WindowsHosts, bool EntraActiveX)
{
    public bool MultiMonitor { get; init; } = true;
    public bool SimulateActivity { get; init; }
    public bool RedirectSecurityKeys { get; init; } = true;
    public bool RedirectUsbDevices { get; init; } = true;
    public bool RedirectPrinters { get; init; } = true;
    public bool RedirectClipboard { get; init; } = true;
    public bool RedirectDrives { get; init; } = true;

    public override string ToString() => nameof(RdpSettings);
}

internal sealed record Configuration(VpnSettings Vpn, RdpSettings Rdp)
{
    public override string ToString() => nameof(Configuration);
}
internal readonly record struct FullScreenExitPlacement(string Monitor, int RightOffsetDips);

internal sealed class SetupException(string message) : Exception(message);

internal static class Settings
{
    internal const string RegistryPath = @"Software\VpnRdpTray";
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "VpnRdpTray";
    internal static readonly string DirectoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VpnRdpTray");

    internal static string ConfigPath(string directory) => Path.Combine(directory, "setup.dpapi");

    internal static bool Enabled(string option, string preferencePath = RegistryPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(preferencePath);
        return key?.GetValue(option) is not 0;
    }

    internal static void SetEnabled(string option, bool enabled, string preferencePath = RegistryPath, string startupPath = RunPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(preferencePath);
        if (option != "AutoStart")
        {
            key.SetValue(option, enabled ? 1 : 0, RegistryValueKind.DWord);
            return;
        }

        bool previous = Enabled(option, preferencePath);
        SetStartupEntry(enabled, startupPath);
        try { key.SetValue(option, enabled ? 1 : 0, RegistryValueKind.DWord); }
        catch
        {
            SetStartupEntry(previous, startupPath);
            throw;
        }
    }

    internal static void SetStartupEntry(bool enabled, string startupPath = RunPath)
    {
        using var run = Registry.CurrentUser.CreateSubKey(startupPath);
        if (enabled)
            run.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
        else
            run.DeleteValue(RunName, throwOnMissingValue: false);
    }

    internal static int VpnRejections
    {
        get => ReadValue("VpnRejections") is int count ? count : 0;
        set => SaveValue("VpnRejections", value);
    }

    internal static DateTimeOffset? VpnNotBefore
    {
        get => ReadValue("VpnNotBefore") is long ticks && ticks >= 0 && ticks <= DateTimeOffset.MaxValue.Ticks
            ? new(ticks, TimeSpan.Zero) : null;
        set => SaveValue("VpnNotBefore", value?.UtcTicks);
    }

    internal static string? VpnAction
    {
        get => ReadValue("VpnAction") as string;
        set => SaveValue("VpnAction", value);
    }

    internal static string? RdpAction
    {
        get => ReadValue("RdpAction") as string;
        set => SaveValue("RdpAction", value);
    }

    internal static FullScreenExitPlacement? FullScreenExitDock
    {
        get
        {
            try
            {
                if (ReadValue(nameof(FullScreenExitDock)) is string stored)
                {
                    int separator = stored.LastIndexOf('|');
                    if (separator > 0 &&
                        int.TryParse(stored.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int offset) &&
                        offset <= 32766)
                        return new(stored[..separator], offset);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            return null;
        }
        set
        {
            try
            {
                SaveValue(nameof(FullScreenExitDock), value is { } dock
                    ? dock.Monitor + "|" + dock.RightOffsetDips.ToString(CultureInfo.InvariantCulture)
                    : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }

    private static object? ReadValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return key?.GetValue(name);
    }

    private static void SaveValue(string name, object? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        if (value is null)
            key.DeleteValue(name, throwOnMissingValue: false);
        else
            key.SetValue(name, value);
    }

    internal static Configuration? Load(string directory)
    {
        var path = ConfigPath(directory);
        if (!File.Exists(path))
            return null;
        var data = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            var configuration = root.Deserialize<Configuration>();
            if (configuration?.Vpn is null || configuration.Rdp is null)
                throw new SetupException("The saved configuration is incomplete. Import the VPN profile and save again.");
            if (root.TryGetProperty(nameof(Configuration.Vpn), out var vpn) &&
                (vpn.TryGetProperty("TotpSecret", out _) || vpn.TryGetProperty("Algorithm", out _) || vpn.TryGetProperty("Period", out _)))
            {
                configuration = configuration with { Vpn = MigrateTotp(configuration.Vpn, vpn) };
                Save(directory, configuration);
            }
            return configuration;
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    private static VpnSettings MigrateTotp(VpnSettings settings, JsonElement vpn)
    {
        if (settings.ProtectedTotpSeed is not null || !vpn.TryGetProperty("TotpSecret", out var rawSeed) ||
            rawSeed.ValueKind == JsonValueKind.Null)
            return settings;

        if (rawSeed.ValueKind != JsonValueKind.String || !rawSeed.TryGetBytesFromBase64(out byte[]? seed))
            throw InvalidLegacyTotp();

        try
        {
            int algorithm = ReadLegacyTotpOption(vpn, "Algorithm", (int)TotpAlgorithm.Sha1);
            int period = ReadLegacyTotpOption(vpn, "Period", 30);
            if (seed.Length is < Totp.MinSeedBytes or > Totp.MaxSeedBytes ||
                !Enum.IsDefined((TotpAlgorithm)algorithm) || period is < Totp.MinPeriod or > Totp.MaxPeriod)
                throw InvalidLegacyTotp();

            return settings with
            {
                ProtectedTotpSeed = ProtectedData.Protect(seed, null, DataProtectionScope.CurrentUser),
                TotpAlgorithm = (TotpAlgorithm)algorithm,
                TotpPeriod = period
            };
        }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    private static int ReadLegacyTotpOption(JsonElement vpn, string name, int fallback)
    {
        if (!vpn.TryGetProperty(name, out var option))
            return fallback;
        if (option.ValueKind != JsonValueKind.Number || !option.TryGetInt32(out int number))
            throw InvalidLegacyTotp();
        return number;
    }

    private static SetupException InvalidLegacyTotp() =>
        new("The old TOTP settings are invalid. Open Settings to enter a valid seed or use manual codes.");

    internal static void Save(string directory, Configuration configuration)
    {
        Directory.CreateDirectory(directory);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(configuration);
        string temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            var protectedData = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(protectedData);
                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, ConfigPath(directory), overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
            File.Delete(temporary);
        }
    }
}

internal static class SetupInput
{
    internal static string Host(string input)
    {
        string host;
        try { host = new IdnMapping().GetAscii(input.Trim()); }
        catch (ArgumentException) { throw InvalidHost(); }
        if (host.Length > 253 || IPAddress.TryParse(host, out _) ||
            host.Split('.').Any(label => label.Length is < 1 or > 63 ||
                label[0] == '-' || label[^1] == '-' ||
                label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw InvalidHost();
        return host;
    }

    private static SetupException InvalidHost() =>
        new("Enter the Remote Desktop hostname, such as a computer name or DNS name.");

    internal static string? Ip(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;
        if (!IPAddress.TryParse(input.Trim(), out var address) ||
            IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast) ||
            address.IsIPv6Multicast || address.IsIPv6LinkLocal || address.IsIPv4MappedToIPv6 ||
            address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                address.GetAddressBytes()[0] >= 224)
            throw new SetupException("Enter a valid unicast IPv4 or IPv6 address, or leave the IP blank.");
        return address.ToString();
    }

    internal static string Credential(string input, string label, int maximumBytes, bool required = true)
    {
        if ((required && string.IsNullOrWhiteSpace(input)) ||
            input.Any(char.IsControl) || Encoding.UTF8.GetByteCount(input) > maximumBytes)
            throw new SetupException($"Check the {label}. It must fit in {maximumBytes} UTF-8 bytes and contain no control characters.");
        return input;
    }
}
