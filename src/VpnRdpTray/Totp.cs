using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace VpnRdpTray;

internal sealed class Totp(VpnSettings settings, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private long lastCounter = -1;

    internal async Task<string> FreshCodeAsync(CancellationToken cancellationToken)
    {
        var periodMs = settings.Period * 1000L;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var counter = now / periodMs;
            var remaining = periodMs - now % periodMs;
            if (counter > lastCounter && remaining > 3000)
            {
                var secret = Convert.FromBase64String(settings.TotpSecret);
                try
                {
                    var code = Generate(secret, settings.Algorithm, counter);
                    lastCounter = counter;
                    return code;
                }
                finally { CryptographicOperations.ZeroMemory(secret); }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(remaining + 100), clock, cancellationToken);
        }
    }

    internal static string Generate(ReadOnlySpan<byte> secret, TotpAlgorithm algorithm, long counter, int digits = 6)
    {
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);
        var hash = algorithm switch
        {
            TotpAlgorithm.Sha1 => HMACSHA1.HashData(secret, message),
            TotpAlgorithm.Sha256 => HMACSHA256.HashData(secret, message),
            TotpAlgorithm.Sha512 => HMACSHA512.HashData(secret, message),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
        };

        try
        {
            var offset = hash[^1] & 15;
            var value = BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fff_ffff;
            uint modulus = digits == 6 ? 1_000_000u : 100_000_000u;
            return (value % modulus).ToString($"D{digits}", CultureInfo.InvariantCulture);
        }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    internal static string NormalizeSecret(string input)
    {
        var value = new string(input.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        byte[] bytes;
        try
        {
            bytes = value.StartsWith("hex:", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromHexString(value[4..])
                : DecodeBase32(value);
        }
        catch (FormatException)
        {
            throw new SetupException("Enter the enrolled TOTP secret in Base32, or prefix the hexadecimal form with hex:.");
        }

        try
        {
            if (bytes.Length is < 10 or > 128)
                throw new SetupException("Check the enrolled TOTP secret length.");
            return Convert.ToBase64String(bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] DecodeBase32(string value)
    {
        var data = value.TrimEnd('=');
        int padding = value.Length - data.Length;
        int remainder = data.Length % 8;
        if (remainder is 1 or 3 or 6 || padding != 0 && (padding != 8 - remainder || remainder == 0))
            throw new FormatException();

        var result = new byte[data.Length * 5 / 8];
        var bits = 0;
        var buffer = 0;
        var index = 0;
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
