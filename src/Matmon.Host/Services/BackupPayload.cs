using System.IO.Compression;

namespace Matmon.Host.Services;

/// <summary>
/// A backup package on the wire or on disk: plain JSON, or that JSON gzip-compressed. A package that carries the history
/// (sensor results, events, statistics, syslog) is mostly repetitive JSON and shrinks to a fraction - which is what lets
/// it fit Matmon.Cloud's per-backup limit. Reading accepts both, so every older plain package still restores.
/// </summary>
public static class BackupPayload
{
    public static bool IsCompressed(ReadOnlySpan<byte> bytes) => bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;

    public static byte[] Compress(byte[] json)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(json);
        }

        return output.ToArray();
    }

    /// <summary>The JSON of a package, whether it came compressed or not.</summary>
    public static byte[] Decode(byte[] bytes)
    {
        if (!IsCompressed(bytes))
        {
            // A snapshot file is written with a UTF-8 BOM, which the JSON reader does not take from bytes.
            return bytes is [0xEF, 0xBB, 0xBF, ..] ? bytes[3..] : bytes;
        }

        using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            if (output.Length + read > int.MaxValue - 64)
            {
                throw new InvalidOperationException("The backup is too large to read.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }
}
