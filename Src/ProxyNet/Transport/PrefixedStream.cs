namespace Clash.ProxyNet;

/// <summary>
/// Serves <paramref name="prefix"/> before delegating to <paramref name="inner"/>.
/// </summary>
internal sealed class PrefixedStream : Stream
{
    private readonly byte[] _prefix;
    private readonly Stream _inner;
    private readonly bool _leaveInnerOpen;
    private int _offset;

    public PrefixedStream(byte[] prefix, Stream inner, bool leaveInnerOpen = false)
    {
        _prefix = prefix;
        _inner = inner;
        _leaveInnerOpen = leaveInnerOpen;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(Span<byte> buffer)
    {
        if (_offset < _prefix.Length)
        {
            int count = Math.Min(buffer.Length, _prefix.Length - _offset);
            _prefix.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        return _inner.Read(buffer);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_offset < _prefix.Length)
        {
            int count = Math.Min(buffer.Length, _prefix.Length - _offset);
            _prefix.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        return await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        _inner.WriteAsync(buffer, ct);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        _inner.WriteAsync(buffer, offset, count, ct);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveInnerOpen)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() =>
        _leaveInnerOpen ? ValueTask.CompletedTask : _inner.DisposeAsync();

    public static Stream WrapIfNeeded(ReadOnlySpan<byte> overread, Stream inner, bool leaveInnerOpen = false) =>
        overread.IsEmpty
            ? inner
            : new PrefixedStream(overread.ToArray(), inner, leaveInnerOpen);
}
