using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace VpnRdpTray;

internal sealed class Totp(VpnSettings settings, TimeProvider? clock = null)
{
    internal const int MinSeedBytes = 10, MaxSeedBytes = 128;
    internal const int MinPeriod = 10, MaxPeriod = 300;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private long lastCounter = -1;

    internal async Task<string> FreshCodeAsync(CancellationToken token)
    {
        if (settings.ProtectedTotpSeed is not { Length: > 0 } protectedSeed ||
            settings.TotpPeriod is < MinPeriod or > MaxPeriod || !Enum.IsDefined(settings.TotpAlgorithm))
            throw new SetupException("Check the saved VPN authenticator in Settings.");

        long periodMs = settings.TotpPeriod * 1000L;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            long counter = now / periodMs;
            long remaining = periodMs - now % periodMs;
            if (counter > lastCounter && remaining > 3000)
            {
                string code = CodeFromProtectedSeed(protectedSeed, settings.TotpAlgorithm, counter);
                lastCounter = counter;
                return code;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(remaining + 100), clock, token);
        }
    }

    private static string CodeFromProtectedSeed(byte[] protectedSeed, TotpAlgorithm algorithm, long counter)
    {
        byte[] seed;
        try { seed = ProtectedData.Unprotect(protectedSeed, null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException)
        {
            throw new SetupException("Windows could not open the saved VPN authenticator. Replace or remove it in Settings.");
        }

        try
        {
            if (seed.Length is < MinSeedBytes or > MaxSeedBytes)
                throw new SetupException("Check the saved VPN authenticator in Settings.");
            return Generate(seed, algorithm, counter);
        }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    internal static string Generate(ReadOnlySpan<byte> seed, TotpAlgorithm algorithm, long counter, int digits = 6)
    {
        Span<byte> message = stackalloc byte[8];
        Span<byte> hash = stackalloc byte[64];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);
        try
        {
            int size = algorithm switch
            {
                TotpAlgorithm.Sha1 => HMACSHA1.HashData(seed, message, hash),
                TotpAlgorithm.Sha256 => HMACSHA256.HashData(seed, message, hash),
                TotpAlgorithm.Sha512 => HMACSHA512.HashData(seed, message, hash),
                _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
            };
            int offset = hash[size - 1] & 15;
            uint value = BinaryPrimitives.ReadUInt32BigEndian(hash.Slice(offset, 4)) & 0x7fff_ffff;
            return digits switch
            {
                6 => (value % 1_000_000u).ToString("D6", CultureInfo.InvariantCulture),
                8 => (value % 100_000_000u).ToString("D8", CultureInfo.InvariantCulture),
                _ => throw new ArgumentOutOfRangeException(nameof(digits))
            };
        }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    internal static byte[] NormalizeSeed(string input)
    {
        var normalized = new char[input.Length];
        int length = 0;
        try
        {
            foreach (char c in input)
                if (!char.IsWhiteSpace(c) && c != '-')
                    normalized[length++] = c;

            ReadOnlySpan<char> value = normalized.AsSpan(0, length);
            var seed = value.StartsWith("hex:", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromHexString(value[4..]) : DecodeBase32(value);
            if (seed.Length >= MinSeedBytes && seed.Length <= MaxSeedBytes)
                return seed;

            CryptographicOperations.ZeroMemory(seed);
            throw new SetupException("Check the enrolled TOTP secret length.");
        }
        catch (FormatException)
        {
            throw new SetupException("Enter the enrolled TOTP secret in Base32, or prefix the hexadecimal form with hex:.");
        }
        finally { normalized.AsSpan().Clear(); }
    }

    private static byte[] DecodeBase32(ReadOnlySpan<char> value)
    {
        var data = value.TrimEnd('=');
        int padding = value.Length - data.Length;
        int remainder = data.Length % 8;
        if (remainder is 1 or 3 or 6 || padding != 0 && (padding != 8 - remainder || remainder == 0))
            throw new FormatException();

        var result = new byte[data.Length * 5 / 8];
        int bits = 0, buffer = 0, index = 0;
        try
        {
            foreach (char c in data)
            {
                int digit = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(char.ToUpperInvariant(c));
                if (digit < 0)
                    throw new FormatException();
                buffer = (buffer << 5) | digit;
                bits += 5;
                if (bits < 8)
                    continue;
                bits -= 8;
                result[index++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
            if (buffer != 0)
                throw new FormatException();
            return result;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(result);
            throw;
        }
    }
}
