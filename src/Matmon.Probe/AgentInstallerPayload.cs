using System.Text;
using System.Text.Json;

namespace Matmon.Probe;

/// <summary>
/// The "ready to install" agent download: the plain agent package with the instance address and an enrolment code
/// appended, so a double-click (Windows) or <c>sudo ./matmon-agent install</c> (Linux, macOS) needs nothing typed.
/// The instance appends it while streaming the download (no rebuild, no per-code file); the agent reads its own file.
///
/// Layout: the plain package, then a small JSON body (<see cref="AgentInstallerSettings"/>), then a 24-byte trailer -
/// 8 bytes magic "MMAGENT1" + 8 bytes offset of the body (= the length of the plain package) + 8 bytes body length.
/// The same idea as <see cref="SetupPayload"/> (a single-file bundle ignores bytes after it), with its own magic so the
/// two can never be mistaken for each other.
///
/// The signature and SHA-256 of a package cover the PLAIN file only. An agent therefore never keeps the trailer: when
/// it installs itself it writes <see cref="CopyPlain"/> (the code is single-use and must not linger in the installed
/// binary), and its auto-update always downloads the plain package.
/// </summary>
public static class AgentInstallerPayload
{
    public const int TrailerLength = 24;

    /// <summary>A body is a URL and a code; anything bigger is not ours.</summary>
    public const int MaxBodyLength = 4096;

    private static ReadOnlySpan<byte> Magic => "MMAGENT1"u8;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The bytes to append to a plain package of <paramref name="plainLength"/> bytes (body + trailer).</summary>
    public static byte[] BuildSuffix(long plainLength, AgentInstallerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // Written by hand: the agent may be trimmed one day, and two strings need no reflection.
        var body = Encoding.UTF8.GetBytes(
            $"{{\"url\":{JsonSerializer.Serialize(settings.Url, Json)},\"code\":{JsonSerializer.Serialize(settings.Code, Json)}}}");
        if (body.Length > MaxBodyLength)
        {
            throw new ArgumentException("the installer settings are too long", nameof(settings));
        }

        var suffix = new byte[body.Length + TrailerLength];
        body.CopyTo(suffix, 0);
        Magic.CopyTo(suffix.AsSpan(body.Length));
        BitConverter.TryWriteBytes(suffix.AsSpan(body.Length + 8), plainLength);
        BitConverter.TryWriteBytes(suffix.AsSpan(body.Length + 16), (long)body.Length);
        return suffix;
    }

    /// <summary>Writes <paramref name="plain"/> followed by the settings - what the instance streams (and the tests use).</summary>
    public static void Combine(Stream plain, AgentInstallerSettings settings, Stream output)
    {
        var start = output.Position;
        plain.CopyTo(output);
        output.Write(BuildSuffix(output.Position - start, settings));
    }

    /// <summary>The settings carried by <paramref name="file"/> and the length of the plain package before them, or null
    /// when the file is a plain package (or the trailer does not add up).</summary>
    public static (AgentInstallerSettings Settings, long PlainLength)? Read(Stream file)
    {
        if (!file.CanSeek || file.Length < TrailerLength)
        {
            return null;
        }

        var trailer = new byte[TrailerLength];
        file.Seek(-TrailerLength, SeekOrigin.End);
        file.ReadExactly(trailer);
        if (!trailer.AsSpan(0, 8).SequenceEqual(Magic))
        {
            return null;
        }

        var offset = BitConverter.ToInt64(trailer, 8);
        var length = BitConverter.ToInt64(trailer, 16);
        if (offset <= 0 || length <= 0 || length > MaxBodyLength || offset + length + TrailerLength != file.Length)
        {
            return null;
        }

        var body = new byte[length];
        file.Seek(offset, SeekOrigin.Begin);
        file.ReadExactly(body);
        try
        {
            using var document = JsonDocument.Parse(body);
            var url = document.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
            var code = document.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(code))
            {
                return null;
            }

            return (new AgentInstallerSettings(url, code), offset);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The settings embedded in <paramref name="path"/> (normally the running executable), or null.</summary>
    public static AgentInstallerSettings? ReadFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream)?.Settings;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="target"/> WITHOUT the installer settings: the plain
    /// package, byte for byte - so the installed agent carries no code and matches the package's hash and signature.</summary>
    public static void CopyPlain(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var plainLength = Read(input)?.PlainLength ?? input.Length;
        input.Seek(0, SeekOrigin.Begin);
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
        {
            var buffer = new byte[81920];
            var remaining = plainLength;
            while (remaining > 0)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    throw new EndOfStreamException("the agent file ended early");
                }

                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(target, (UnixFileMode)0b111_101_101);
        }
    }
}

/// <summary>What a ready-to-install download carries: where to enrol and the single-use code to enrol with.</summary>
public sealed record AgentInstallerSettings(string Url, string Code);
