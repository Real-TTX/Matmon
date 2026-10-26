namespace Matmon.Probe;

/// <summary>
/// The Windows setup is ONE file: the setup program (<c>MatmonSetup.exe</c>, a single-file .NET app) with the program
/// package (the zip the self-update uses) appended, and a trailer that says where the package starts. The setup reads its
/// own file; the bare program before the package is what it installs as the uninstaller - so the uninstaller is the same
/// program without a 190 MB package inside. A single-file app ignores bytes after its bundle, so appending is safe.
/// Written by <c>scripts/package-windows.cs</c>, read by Matmon.Setup (which links this file).
/// </summary>
public static class SetupPayload
{
    /// <summary>8 bytes magic + 8 bytes offset + 8 bytes length, at the very end of the file.</summary>
    public const int TrailerLength = 24;

    private static ReadOnlySpan<byte> Magic => "MMSETUP1"u8;

    /// <summary>Writes <paramref name="setup"/> followed by <paramref name="package"/> and the trailer.</summary>
    public static void Combine(Stream setup, Stream package, Stream output)
    {
        setup.CopyTo(output);
        var offset = output.Position;
        package.CopyTo(output);
        var length = output.Position - offset;
        output.Write(Magic);
        output.Write(BitConverter.GetBytes(offset));
        output.Write(BitConverter.GetBytes(length));
    }

    /// <summary>Where the package sits in <paramref name="file"/>, or null when the file carries none.</summary>
    public static (long Offset, long Length)? Find(Stream file)
    {
        if (file.Length < TrailerLength)
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
        return offset > 0 && length > 0 && offset + length + TrailerLength == file.Length ? (offset, length) : null;
    }

    /// <summary>A read-only window on the package inside <paramref name="file"/>.</summary>
    public static Stream OpenPackage(Stream file, long offset, long length) => new Window(file, offset, length);

    private sealed class Window(Stream inner, long offset, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, length);
        }

        public override int Read(byte[] buffer, int index, int count)
        {
            var available = (int)Math.Min(count, length - _position);
            if (available <= 0)
            {
                return 0;
            }

            inner.Seek(offset + _position, SeekOrigin.Begin);
            var read = inner.Read(buffer, index, available);
            _position += read;
            return read;
        }

        public override long Seek(long target, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => target,
            SeekOrigin.Current => _position + target,
            _ => length + target
        };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int index, int count) => throw new NotSupportedException();
    }
}
