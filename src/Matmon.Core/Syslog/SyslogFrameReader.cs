namespace Matmon.Core.Syslog;

/// <summary>
/// Cuts a TCP byte stream into syslog messages (RFC 6587). Two framings exist in the wild and both are read:
/// octet-counting (<c>"120 &lt;34&gt;1 ..."</c> - a length, a space, then exactly that many bytes) and the
/// non-transparent kind, one message per line. Which one a connection speaks is decided by its first message and then
/// kept: a digit followed by more digits and a space can only be a length, and everything else starts with
/// <c>&lt;</c> or text.
///
/// Pure and incremental so it can be tested without a socket: bytes go in as they arrive, whole messages come out.
/// </summary>
public sealed class SyslogFrameReader
{
    public const int DefaultMaxFrameBytes = 64 * 1024;
    private const int MaxLengthDigits = 6;

    private enum Mode
    {
        Undecided,
        Lines,
        OctetCounting
    }

    private readonly int _maxFrameBytes;
    private byte[] _buffer = new byte[4096];
    private int _start;
    private int _end;
    private Mode _mode = Mode.Undecided;

    public SyslogFrameReader(int maxFrameBytes = DefaultMaxFrameBytes) =>
        _maxFrameBytes = Math.Max(16, maxFrameBytes);

    /// <summary>True once the stream broke the framing (a nonsense length, a frame beyond the limit): nothing after it
    /// can be trusted to line up, so the connection should be closed.</summary>
    public bool Faulted { get; private set; }

    /// <summary>Adds received bytes; returns every message that is now complete.</summary>
    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> data)
    {
        Append(data);
        var frames = new List<byte[]>();
        ReadFrames(frames);
        Compact();
        return frames;
    }

    /// <summary>The stream ended: a last line without its new line is still a message.</summary>
    public IReadOnlyList<byte[]> Complete()
    {
        var frames = new List<byte[]>();
        if (!Faulted && _mode != Mode.OctetCounting && _end > _start)
        {
            var length = _end - _start;
            if (_buffer[_end - 1] == (byte)'\r')
            {
                length--;
            }

            if (length > 0)
            {
                frames.Add(Copy(_start, Math.Min(length, _maxFrameBytes)));
            }
        }

        _start = _end = 0;
        return frames;
    }

    private void ReadFrames(List<byte[]> frames)
    {
        while (!Faulted)
        {
            if (_mode == Mode.Undecided)
            {
                SkipLineBreaks();
                if (_start == _end)
                {
                    return;
                }

                if (IsDigit(_buffer[_start]))
                {
                    var index = _start;
                    while (index < _end && index - _start <= MaxLengthDigits && IsDigit(_buffer[index]))
                    {
                        index++;
                    }

                    if (index == _end)
                    {
                        return; // only digits so far: cannot tell a length from the start of a line yet
                    }

                    _mode = _buffer[index] == (byte)' ' && index - _start <= MaxLengthDigits ? Mode.OctetCounting : Mode.Lines;
                }
                else
                {
                    _mode = Mode.Lines;
                }
            }

            if (_mode == Mode.OctetCounting)
            {
                if (!ReadOctetCounted(frames))
                {
                    return;
                }
            }
            else if (!ReadLine(frames))
            {
                return;
            }
        }
    }

    private bool ReadOctetCounted(List<byte[]> frames)
    {
        SkipLineBreaks(); // some senders put a new line after each counted frame
        if (_start == _end)
        {
            return false;
        }

        var index = _start;
        var length = 0;
        var digits = 0;
        while (index < _end && IsDigit(_buffer[index]) && digits <= MaxLengthDigits)
        {
            length = (length * 10) + (_buffer[index] - (byte)'0');
            index++;
            digits++;
        }

        if (index == _end)
        {
            if (digits > MaxLengthDigits)
            {
                Faulted = true;
            }

            return false;
        }

        if (_buffer[index] != (byte)' ' || digits is 0 or > MaxLengthDigits || length <= 0 || length > _maxFrameBytes)
        {
            Faulted = true;
            return false;
        }

        var bodyStart = index + 1;
        if (_end - bodyStart < length)
        {
            return false; // the rest of the message is still on its way
        }

        frames.Add(Copy(bodyStart, length));
        _start = bodyStart + length;
        return true;
    }

    private bool ReadLine(List<byte[]> frames)
    {
        var newLine = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
        if (newLine >= 0)
        {
            var length = newLine - _start;
            if (length > 0 && _buffer[newLine - 1] == (byte)'\r')
            {
                length--;
            }

            if (length > 0)
            {
                frames.Add(Copy(_start, Math.Min(length, _maxFrameBytes)));
            }

            _start = newLine + 1;
            return true;
        }

        if (_end - _start > _maxFrameBytes)
        {
            // No end of line within the limit: take what fits as a message and carry on after it, rather than growing
            // a buffer for a sender that is not really sending lines.
            frames.Add(Copy(_start, _maxFrameBytes));
            _start += _maxFrameBytes;
            return true;
        }

        return false;
    }

    private void SkipLineBreaks()
    {
        while (_start < _end && _buffer[_start] is (byte)'\r' or (byte)'\n')
        {
            _start++;
        }
    }

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    private byte[] Copy(int start, int length)
    {
        var result = new byte[length];
        Buffer.BlockCopy(_buffer, start, result, 0, length);
        return result;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if (_end + data.Length > _buffer.Length)
        {
            Compact();
        }

        if (_end + data.Length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _end + data.Length));
        }

        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;
    }

    private void Compact()
    {
        if (_start == 0)
        {
            return;
        }

        if (_start == _end)
        {
            _start = _end = 0;
            return;
        }

        Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
        _end -= _start;
        _start = 0;
    }
}
