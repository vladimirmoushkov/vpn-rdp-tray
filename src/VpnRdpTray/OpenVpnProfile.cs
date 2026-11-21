using System.Globalization;
using System.Text;

namespace VpnRdpTray;

internal static class OpenVpnProfile
{
    private const int MaxBytes = 512 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> FileOptions = new(StringComparer.Ordinal)
    {
        "ca", "cert", "key", "extra-certs", "crl-verify", "tls-auth", "tls-crypt", "tls-crypt-v2", "pkcs12"
    };

    private static readonly Dictionary<string, (int Min, int Max)> Options = new(StringComparer.Ordinal)
    {
        ["client"] = (0, 0), ["tls-client"] = (0, 0), ["pull"] = (0, 0),
        ["dev"] = (1, 1), ["dev-type"] = (1, 1), ["windows-driver"] = (1, 1),
        ["proto"] = (1, 1), ["remote"] = (1, 3), ["port"] = (1, 1),
        ["nobind"] = (0, 0), ["remote-random"] = (0, 0), ["remote-random-hostname"] = (0, 0),
        ["persist-key"] = (0, 0), ["persist-tun"] = (0, 0), ["float"] = (0, 0),
        ["ping"] = (1, 1), ["ping-restart"] = (1, 1), ["keepalive"] = (2, 2),
        ["connect-timeout"] = (1, 1), ["server-poll-timeout"] = (1, 1), ["explicit-exit-notify"] = (0, 1),
        ["resolv-retry"] = (1, 1), ["connect-retry"] = (1, 2), ["connect-retry-max"] = (1, 1),
        ["route"] = (1, 4), ["route-ipv6"] = (1, 3), ["route-gateway"] = (1, 1),
        ["route-metric"] = (1, 1), ["route-delay"] = (0, 2), ["route-nopull"] = (0, 0),
        ["redirect-gateway"] = (0, 8), ["redirect-private"] = (0, 8), ["pull-filter"] = (2, 2),
        ["dhcp-option"] = (2, 3), ["register-dns"] = (0, 0), ["block-outside-dns"] = (0, 0),
        ["topology"] = (1, 1), ["tun-mtu"] = (1, 1), ["mssfix"] = (0, 2), ["fragment"] = (1, 2),
        ["tun-mtu-extra"] = (1, 1), ["sndbuf"] = (1, 1), ["rcvbuf"] = (1, 1),
        ["auth"] = (1, 1), ["cipher"] = (1, 1), ["data-ciphers"] = (1, 1),
        ["data-ciphers-fallback"] = (1, 1), ["disable-dco"] = (0, 0),
        ["tls-version-min"] = (1, 2), ["tls-version-max"] = (1, 1), ["tls-cipher"] = (1, 1),
        ["tls-ciphersuites"] = (1, 1), ["tls-groups"] = (1, 1), ["tls-cert-profile"] = (1, 1),
        ["remote-cert-tls"] = (1, 1), ["verify-x509-name"] = (1, 2), ["peer-fingerprint"] = (1, 1),
        ["key-direction"] = (1, 1), ["reneg-sec"] = (1, 1), ["auth-nocache"] = (0, 0),
        ["auth-user-pass"] = (0, 0), ["verb"] = (1, 1), ["mute"] = (1, 1),
        ["allow-compression"] = (1, 1)
    };

    private static readonly HashSet<string> ManagedOptions = new(StringComparer.Ordinal)
    {
        "auth-user-pass", "auth-nocache", "verb", "mute", "resolv-retry", "connect-retry",
        "connect-retry-max", "allow-compression"
    };

    internal static string Import(string file, Func<string, string?, string?> selectFile)
    {
        if (!Path.GetExtension(file).Equals(".ovpn", StringComparison.OrdinalIgnoreCase))
            throw new SetupException("Select an OpenVPN .ovpn file.");
        return Sanitize(ReadText(file), selectFile);
    }

    internal static string Sanitize(string text, Func<string, string?, string?> selectFile)
    {
        var output = new StringBuilder();
        var providedFiles = new HashSet<string>(StringComparer.Ordinal);
        var legacyCipher = (string?)null;
        bool hasDataCiphers = false, hasFallback = false, disableDco = false;
        bool remote = false, tun = false, serverPurpose = false, exactName = false;
        bool pin = false, tlsMinimum = false;
        bool ping = false, pingRestart = false;
        using var reader = new StringReader(text.TrimStart('\uFEFF'));
        int lineNo = 0;
        while (reader.ReadLine() is { } sourceLine)
        {
            lineNo++;
            string line = sourceLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;

            if (line[0] == '<')
            {
                string tag = line.Length >= 3 && line[^1] == '>' ? line[1..^1] : "";
                if (!FileOptions.Contains(tag) && tag != "peer-fingerprint")
                    throw Error(lineNo, "This inline block is unsupported.");
                if (!providedFiles.Add(tag))
                    throw Error(lineNo, "The inline material is specified more than once.");

                var block = new StringBuilder();
                string terminator = $"</{tag}>";
                bool closed = false;
                while (reader.ReadLine() is { } data)
                {
                    lineNo++;
                    if (data.Trim() == terminator)
                    {
                        closed = true;
                        break;
                    }
                    if (data.TrimStart().StartsWith('<'))
                        throw Error(lineNo, "Unexpected markup inside inline material.");
                    block.Append(data).Append('\n');
                }
                if (block.Length == 0 || !closed)
                    throw Error(lineNo, "The inline block is empty or missing its closing tag.");
                if (tag == "peer-fingerprint")
                {
                    ValidateFingerprints(block.ToString(), lineNo);
                    pin = true;
                }
                AppendBlock(output, tag, block.ToString(), lineNo);
                continue;
            }

            var tokens = Tokenize(line, lineNo);
            var name = tokens[0];
            var args = tokens.Skip(1).ToArray();
            if (FileOptions.Contains(name))
            {
                bool tlsDirection = name == "tls-auth" && args.Length == 2 && args[1] is "0" or "1";
                if ((args.Length != 1 && !tlsDirection) || !providedFiles.Add(name))
                    throw Error(lineNo, $"Check the {name} file directive.");
                if (args[0] is "[inline]" or "stdin")
                    throw Error(lineNo, $"Select the {name} file directly or use a complete inline block.");
                var selected = selectFile(name, Path.GetFileName(args[0].Replace('\\', '/')));
                if (selected is null)
                    throw new OperationCanceledException();
                var material = name == "pkcs12" ? ReadPkcs12(selected) : ReadText(selected);
                AppendBlock(output, name, material, lineNo);
                if (tlsDirection)
                    Append(output, "key-direction", [args[1]], lineNo);
                continue;
            }

            if (!Options.TryGetValue(name, out var counts))
                throw Error(lineNo, "This client directive is unsupported.");
            if (args.Length < counts.Min || args.Length > counts.Max ||
                args.Any(value => value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal)))
                throw Error(lineNo, $"Check the arguments for {name}.");

            switch (name)
            {
                case "dev" or "dev-type" when args[0] != "tun":
                    throw Error(lineNo, "The profile must use a layer-3 TUN tunnel.");
                case "dev": tun = true; break;
                case "remote": remote = true; break;
                case "proto" when args[0] is not ("udp" or "udp4" or "udp6" or "tcp" or "tcp-client" or "tcp4-client" or "tcp6-client"):
                    throw Error(lineNo, "Use a UDP or TCP client protocol.");
                case "remote-cert-tls" when args[0] != "server":
                    throw Error(lineNo, "The remote certificate role must be server.");
                case "remote-cert-tls": serverPurpose = true; break;
                case "verify-x509-name" when !IsExactServerName(args):
                    throw Error(lineNo, "Use an exact VPN certificate subject or name supplied by your administrator.");
                case "verify-x509-name": exactName = true; break;
                case "peer-fingerprint":
                    ValidateFingerprints(args[0], lineNo);
                    pin = true;
                    break;
                case "tls-version-min" when args[0] is not ("1.2" or "1.3"):
                    throw Error(lineNo, "TLS 1.2 or higher is required.");
                case "tls-version-min": tlsMinimum = true; break;
                case "tls-version-max" when args[0] is not ("1.2" or "1.3"):
                    throw Error(lineNo, "TLS 1.2 or higher is required.");
                case "tls-cert-profile" when args[0] is not ("preferred" or "suiteb"):
                    throw Error(lineNo, "Use the preferred or suiteb TLS certificate profile.");
                case "allow-compression" when args[0] != "no":
                    throw Error(lineNo, "VPN compression is unsupported.");
                case "cipher": legacyCipher = ValidateCipher(args[0], lineNo); break;
                case "data-ciphers":
                    foreach (var cipher in args[0].Split(':'))
                        ValidateCipher(cipher.TrimStart('?'), lineNo);
                    hasDataCiphers = true;
                    break;
                case "data-ciphers-fallback":
                    ValidateCipher(args[0], lineNo);
                    hasFallback = true;
                    break;
                case "disable-dco": disableDco = true; break;
                case "ping": ping = true; break;
                case "ping-restart": pingRestart = true; break;
                case "keepalive": ping = pingRestart = true; break;
            }

            if (!ManagedOptions.Contains(name))
                Append(output, name, args, lineNo);
        }

        if (!remote || !tun)
            throw new SetupException("The VPN profile must contain a remote endpoint and dev tun.");
        if (!pin && !providedFiles.Contains("ca"))
            throw new SetupException("The VPN profile must contain a trusted CA or a SHA-256 peer fingerprint.");
        RequireServerIdentity(pin, exactName);
        if (!pin && !serverPurpose)
            output.AppendLine("remote-cert-tls server");
        if (!tlsMinimum)
            output.AppendLine("tls-version-min 1.2");
        if (!ping)
            output.AppendLine("ping 10");
        if (!pingRestart)
            output.AppendLine("ping-restart 60");

        // In current OpenVPN, cipher by itself no longer supplies a TLS data-channel fallback.
        if (legacyCipher?.EndsWith("-CBC", StringComparison.Ordinal) == true && !hasDataCiphers && !hasFallback)
        {
            output.AppendLine($"data-ciphers AES-256-GCM:AES-128-GCM:{legacyCipher}");
            output.AppendLine($"data-ciphers-fallback {legacyCipher}");
            if (!disableDco)
                output.AppendLine("disable-dco");
        }

        output.Append("\nclient\nauth-user-pass\nauth-nocache\nauth-retry none\n");
        output.Append("allow-compression no\nscript-security 1\nresolv-retry 20\nconnect-retry 2 5\nconnect-retry-max 1\n");
        if (Utf8.GetByteCount(output.ToString()) > MaxBytes)
            throw new SetupException("The imported VPN profile and its files exceed 512 KiB.");
        return output.ToString();
    }

    internal static void VerifySavedServerIdentity(string profile)
    {
        // Older saved profiles may have accepted any server certificate issued by their CA.
        using var reader = new StringReader(profile);
        bool pin = false, exactName = false;
        string? closingTag = null;
        int lineNo = 0;
        while (reader.ReadLine() is { } source)
        {
            lineNo++;
            string line = source.Trim();
            if (closingTag is not null)
            {
                if (line == closingTag)
                {
                    pin |= closingTag == "</peer-fingerprint>";
                    closingTag = null;
                }
                else if (closingTag == "</peer-fingerprint>")
                    ValidateFingerprints(line, lineNo);
                continue;
            }

            if (line.StartsWith('<') && line.EndsWith('>'))
                closingTag = "</" + line[1..];
            else if (line.StartsWith("verify-x509-name ", StringComparison.Ordinal))
                exactName = IsExactServerName(Tokenize(line, lineNo).Skip(1).ToArray());
            else if (line.StartsWith("peer-fingerprint ", StringComparison.Ordinal))
            {
                var tokens = Tokenize(line, lineNo);
                if (tokens.Count != 2)
                    throw Error(lineNo, "Use a SHA-256 VPN peer fingerprint.");
                ValidateFingerprints(tokens[1], lineNo);
                pin = true;
            }
        }

        RequireServerIdentity(pin && closingTag is null, exactName);
    }

    private static bool IsExactServerName(string[] args) =>
        args.Length == 1 && !string.IsNullOrWhiteSpace(args[0]) ||
        args.Length == 2 && !string.IsNullOrWhiteSpace(args[0]) && args[1] is "subject" or "name";

    private static void RequireServerIdentity(bool pinned, bool exactName)
    {
        if (!pinned && !exactName)
            throw new SetupException("Import a VPN profile with an exact certificate name or SHA-256 peer fingerprint from your administrator.");
    }

    private static void ValidateFingerprints(string value, int lineNo)
    {
        var hashes = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (hashes.Length == 0 ||
            hashes.Any(hash => hash.Length != 95 || hash.Split(':').Any(part => part.Length != 2 || !part.All(char.IsAsciiHexDigit))))
            throw Error(lineNo, "Use colon-delimited SHA-256 certificate fingerprints.");
    }

    private static string ValidateCipher(string value, int lineNo) => value switch
    {
        "AES-128-GCM" or "AES-192-GCM" or "AES-256-GCM" or "CHACHA20-POLY1305" or
        "AES-128-CBC" or "AES-192-CBC" or "AES-256-CBC" => value,
        _ => throw Error(lineNo, "This VPN data cipher is unsupported.")
    };

    private static byte[] ReadBytes(string file)
    {
        using var input = File.OpenRead(file);
        if (input.Length > MaxBytes)
            throw new SetupException("Each imported VPN file must be 512 KiB or smaller.");
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static string ReadText(string file)
    {
        var bytes = ReadBytes(file);
        try { return Utf8.GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new SetupException("Use a UTF-8 or ASCII VPN profile and PEM files."); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string ReadPkcs12(string file)
    {
        var bytes = ReadBytes(file);
        try { return Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void AppendBlock(StringBuilder output, string tag, string data, int lineNo)
    {
        var lines = data.ReplaceLineEndings("\n").Trim();
        if (lines.Length == 0 || lines.Any(c => char.IsControl(c) && c is not ('\t' or '\n')) ||
            lines.Split('\n').Any(line => line.TrimStart().StartsWith('<') || Utf8.GetByteCount(line) > 240))
            throw Error(lineNo, $"Invalid inline {tag} data.");
        output.Append('<').Append(tag).Append(">\n").Append(lines).Append("\n</").Append(tag).Append(">\n");
    }

    private static void Append(StringBuilder output, string name, string[] arguments, int lineNo)
    {
        var command = name + string.Concat(arguments.Select(arg => $" \"{arg.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""));
        if (Utf8.GetByteCount(command) > 240)
            throw Error(lineNo, "The directive is too long.");
        output.Append(command).Append('\n');
    }

    private static List<string> Tokenize(string line, int lineNo)
    {
        var result = new List<string>();
        int index = 0;
        while (index < line.Length)
        {
            if (char.IsWhiteSpace(line[index]))
            {
                index++;
                continue;
            }
            if (line[index] is '#' or ';')
                break;
            var token = new StringBuilder();
            char quote = line[index] is '\'' or '"' ? line[index++] : '\0';
            bool closed = quote == '\0';
            while (index < line.Length)
            {
                char c = line[index++];
                if (c == quote && quote != '\0')
                {
                    closed = true;
                    break;
                }
                if (quote == '\0' && char.IsWhiteSpace(c))
                    break;
                if (c == '\\' && quote != '\'')
                {
                    if (index == line.Length || line[index] is not ('\\' or '"' or ' ' or '\t'))
                        throw Error(lineNo, "Use quoted arguments and escape each literal backslash.");
                    c = line[index++];
                }
                if (char.IsControl(c))
                    throw Error(lineNo, "Control characters are unsupported.");
                token.Append(c);
            }
            if (!closed || quote != '\0' && index < line.Length && !char.IsWhiteSpace(line[index]))
                throw Error(lineNo, "Check the quoted argument.");
            result.Add(token.ToString());
        }
        return result;
    }

    private static SetupException Error(int line, string message) =>
        new($"VPN profile line {line.ToString(CultureInfo.InvariantCulture)}. {message}");
}
