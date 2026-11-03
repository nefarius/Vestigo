namespace Vestigo.Diagnostics;

/// <summary>Reads PE metadata through PeNet from one open of the file.</summary>
public sealed class PeInspector : IPeInspector
{
    public OperationResult<BinaryInspection> Inspect(string fullPath, bool computeSha256, long maxBytes)
    {
        try
        {
            using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            if (length > maxBytes)
            {
                return OperationResult<BinaryInspection>.Fail("file_too_large", "The file exceeds Vestigo:Diagnostics:MaxInspectBytes.");
            }

            using BoundedReadStream bounded = new(stream, length);
            return PeImage.Inspect(bounded, computeSha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<BinaryInspection>.Fail("io_error", "The file could not be read.");
        }
    }
}

/// <summary>A view of an open stream that stops at a length captured before parsing begins.</summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _origin;
    private readonly long _length;

    public BoundedReadStream(Stream inner, long length)
    {
        _inner = inner;
        _origin = inner.Position;
        _length = length;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _inner.Position - _origin;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long remaining = _length - Position;
        if (remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > remaining)
        {
            buffer = buffer[..(int)Math.Min(remaining, int.MaxValue)];
        }

        return _inner.Read(buffer);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0 || target > _length)
        {
            throw new IOException("The read exceeds the captured file size.");
        }

        _inner.Seek(_origin + target, SeekOrigin.Begin);
        return target;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
