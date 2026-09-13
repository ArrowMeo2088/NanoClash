using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

using Clash.ProxyNet.Reality;
using Clash.ProxyNet.Vless;

using Clash.ProxyNet;

namespace Clash.ProxyNet.Vision;

/// <summary>
/// The <c>xtls-rprx-vision</c> framing that sits between the VLESS response header and the
/// payload: a stream that strips the server's padding frames and pads its own first write.
/// </summary>
/// <remarks>
/// <para>
/// A server whose user is configured with <c>flow=xtls-rprx-vision</c> does not answer in plain
/// VLESS. After the two-byte response header it sends the user's UUID once, then a run of
/// frames — <c>command(1) + contentLen(2) + paddingLen(2)</c>, content, padding — until a frame
/// arrives with the <i>end</i> command, after which the connection is raw. Reading such a stream
/// as if it were plain VLESS appears to work: the first response usually still contains a
/// recognisable <c>HTTP/1.1 200</c> a few bytes in. It is the reads after it that are quietly
/// corrupted, which is exactly the failure that looks like a server problem.
/// </para>
/// <para>
/// <b>What this implements and what it does not.</b> The padding protocol, in both directions.
/// Not the other half of Vision — the TLS-in-TLS detection that lets Xray splice a connection
/// into a raw copy after a few packets. That is a throughput optimisation with no effect on the
/// wire format either peer must accept, and leaving it out costs nothing but the optimisation.
/// </para>
/// <para>
/// Uplink (browser CONNECT): <c>Continue</c> until TLS ApplicationData (<c>0x17</c>), then
/// <c>End</c> and stay on the REALITY stream (no client-side Direct splice — matches QPN).
/// Downlink: when the server sends <c>Direct</c>, splice reads to the raw TCP under REALITY
/// (mihomo/Xray <c>netConn</c>); <c>End</c> stays on REALITY. Missing Direct splice was the
/// browser <c>RealityHandshakeException</c> / <c>ERR_SSL_BAD_RECORD_MAC_ALERT</c> failure.
/// </para>
/// </remarks>
internal sealed class VisionStream : Stream
{
    /// <summary>The flow identifier this stream implements. Any other flow is not this class's.</summary>
    public const string FlowName = "xtls-rprx-vision";

    private const int UuidSize = 16;
    private const int HeaderSize = 5;

    private const byte CommandPaddingContinue = 0x00;
    private const byte CommandPaddingEnd = 0x01;

    /// <summary>
    /// "Stop padding, the rest of this connection is a direct copy." Xray sends it instead of
    /// <see cref="CommandPaddingEnd"/> once it has decided the connection carries TLS, and real
    /// servers reach that decision far more often than they send the end command — a client that
    /// only honours <c>end</c> stays in framed mode forever and dies on the next header.
    /// </summary>
    private const byte CommandPaddingDirect = 0x02;

    /// <summary>Xray's <c>buf.Size</c>, which bounds one padded frame.</summary>
    private const int MaxFrame = 8192;

    /// <summary>
    /// Consecutive frames carrying padding and no content before the stream is declared
    /// broken. Xray sends one or two; a peer sending them without end would otherwise keep a
    /// read from ever returning.
    /// </summary>
    private const int MaxPaddingOnlyFrames = 64;

    private enum Mode
    {
        /// <summary>Nothing read yet: the leading UUID decides whether this stream is framed.</summary>
        Undecided,

        /// <summary>Inside the padded frames.</summary>
        Framed,

        /// <summary>Past the closing frame — everything from here is payload.</summary>
        Raw
    }

    private readonly Stream _session;
    private readonly bool _leaveInnerOpen;
    private readonly byte[] _uuid;
    private readonly RealityTlsStream? _reality;

    /// <summary>Where framed Vision bytes are read from (REALITY), until Direct splice.</summary>
    private Stream _readSource;

    /// <summary>Where uplink frames / post-End payload are written (stays on REALITY for End).</summary>
    private Stream _writeTarget;

    private Stream? _netConn;
    private bool _ownsNetConn;

    private byte[] _buffer;
    private int _start;
    private int _end;

    private Mode _mode = Mode.Undecided;
    private byte _command = CommandPaddingContinue;
    private int _remainingContent;
    private int _remainingPadding;
    private int _paddingOnlyFrames;

    private bool _uuidSent;
    private bool _uplinkRaw;
    private bool _disposed;

    private const byte TlsApplicationData = 0x17;

    /// <summary>
    /// Wraps <paramref name="innerStream"/>, which must be positioned where the payload would
    /// begin in a plain VLESS session — that is, after the response header.
    /// </summary>
    public VisionStream(Stream innerStream, ReadOnlySpan<byte> uuid, bool leaveInnerOpen = false)
    {
        ArgumentNullException.ThrowIfNull(innerStream);

        if (uuid.Length != UuidSize)
            throw new ArgumentException($"A VLESS user id is {UuidSize} bytes.", nameof(uuid));

        _session = innerStream;
        _readSource = innerStream;
        _writeTarget = innerStream;
        _uuid = uuid.ToArray();
        _leaveInnerOpen = leaveInnerOpen;
        _buffer = ArrayPool<byte>.Shared.Rent(MaxFrame);
        _reality = FindReality(innerStream);
    }

    private static RealityTlsStream? FindReality(Stream s)
    {
        for (var cur = s; cur is not null;)
        {
            if (cur is RealityTlsStream r)
                return r;
            if (cur is VlessResponseStream v)
            {
                cur = v.Inner;
                continue;
            }

            break;
        }

        return null;
    }

    private int Buffered => _end - _start;

    public override bool CanRead => !_disposed && _readSource.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && _writeTarget.CanWrite;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() => _writeTarget.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _writeTarget.FlushAsync(cancellationToken);

    // ================================ reading ================================

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty)
            return 0;

        while (true)
        {
            // Anything already unpadded and waiting is returned before touching the transport.
            if (_mode == Mode.Raw)
                return Buffered > 0 ? DrainInto(buffer.Span) : await _readSource.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (_mode == Mode.Undecided)
            {
                // Decide from as few bytes as settle it. A full first frame header is needed to
                // enter framed mode, but one byte that is not the UUID is enough to know the
                // server is not framing — and waiting for 21 bytes from a server that sent a
                // 5-byte greeting and is now waiting for us would be a deadlock.
                while (!TryDecideMode())
                {
                    if (await FillSomeAsync(cancellationToken).ConfigureAwait(false) == 0)
                    {
                        _mode = Mode.Raw; // ended before a frame could exist: whatever came is payload
                        break;
                    }
                }

                continue;
            }

            if (_remainingContent == 0 && _remainingPadding == 0)
            {
                if (EndsFraming(_command))
                {
                    FinishFraming();
                    continue;
                }

                await FillAsync(HeaderSize, throwOnEof: false, cancellationToken).ConfigureAwait(false);
                if (Buffered == 0)
                    return 0; // a clean close on a frame boundary is the end of the stream

                if (Buffered < HeaderSize)
                    throw new EndOfStreamException("The VLESS server closed the connection inside an xtls-rprx-vision frame header, mid-response.");

                ReadFrameHeader();
                continue;
            }

            if (Buffered == 0 && await FillSomeAsync(cancellationToken).ConfigureAwait(false) == 0)
                throw new EndOfStreamException("The VLESS server closed the connection inside an xtls-rprx-vision frame, mid-response.");

            if (_remainingContent > 0)
            {
                int taken = Math.Min(Math.Min(_remainingContent, Buffered), buffer.Length);
                _buffer.AsSpan(_start, taken).CopyTo(buffer.Span);
                _start += taken;
                _remainingContent -= taken;
                return taken;
            }

            int skipped = Math.Min(_remainingPadding, Buffered);
            _start += skipped;
            _remainingPadding -= skipped;
        }
    }

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty)
            return 0;

        while (true)
        {
            if (_mode == Mode.Raw)
                return Buffered > 0 ? DrainInto(buffer) : _readSource.Read(buffer);

            if (_mode == Mode.Undecided)
            {
                while (!TryDecideMode())
                {
                    if (FillSome() == 0)
                    {
                        _mode = Mode.Raw;
                        break;
                    }
                }

                continue;
            }

            if (_remainingContent == 0 && _remainingPadding == 0)
            {
                if (EndsFraming(_command))
                {
                    FinishFraming();
                    continue;
                }

                Fill(HeaderSize, throwOnEof: false);
                if (Buffered == 0)
                    return 0;

                if (Buffered < HeaderSize)
                    throw new EndOfStreamException("The VLESS server closed the connection inside an xtls-rprx-vision frame header, mid-response.");

                ReadFrameHeader();
                continue;
            }

            if (Buffered == 0 && FillSome() == 0)
                throw new EndOfStreamException("The VLESS server closed the connection inside an xtls-rprx-vision frame, mid-response.");

            if (_remainingContent > 0)
            {
                int taken = Math.Min(Math.Min(_remainingContent, Buffered), buffer.Length);
                _buffer.AsSpan(_start, taken).CopyTo(buffer);
                _start += taken;
                _remainingContent -= taken;
                return taken;
            }

            int skipped = Math.Min(_remainingPadding, Buffered);
            _start += skipped;
            _remainingPadding -= skipped;
        }
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <summary>
    /// Tries to decide, from the bytes buffered so far, whether the peer is speaking Vision
    /// framing. Returns false when more bytes are needed to tell.
    /// </summary>
    private bool TryDecideMode()
    {
        int compared = Math.Min(Buffered, UuidSize);
        if (compared > 0 && !_buffer.AsSpan(_start, compared).SequenceEqual(_uuid.AsSpan(0, compared)))
        {
            // Not our UUID — the server answered in plain VLESS despite the flow, which is what
            // a non-Vision server does. Everything buffered is payload, and nothing more needs
            // to arrive to know that.
            _mode = Mode.Raw;
            return true;
        }

        if (Buffered < UuidSize + HeaderSize)
            return false; // the UUID matches so far; framed mode needs the whole first header

        _start += UuidSize;
        _mode = Mode.Framed;
        return true;
    }

    /// <summary>Whether <paramref name="command"/> was the last framed packet.</summary>
    private static bool EndsFraming(byte command) =>
        command is CommandPaddingEnd or CommandPaddingDirect;

    /// <summary>
    /// After End: keep reading REALITY plaintext. After Direct: splice reads to raw TCP under
    /// REALITY (mihomo ExtendedReader = netConn), draining Reality pending + inbound leftover
    /// first. Uplink stays on REALITY (QPN/Xray Vision) — do not switch writes to raw TCP.
    /// </summary>
    private void FinishFraming()
    {
        if (_command == CommandPaddingDirect && _reality is not null)
        {
            using var ms = new MemoryStream();
            if (Buffered > 0)
            {
                ms.Write(_buffer, _start, Buffered);
                _start = _end = 0;
            }

            var pending = _reality.StealPending();
            if (pending.Length > 0)
                ms.Write(pending);

            var inbound = _reality.StealInboundLeftover();
            if (inbound.Length > 0)
                ms.Write(inbound);

            _netConn = _reality.DetachTransport();
            _ownsNetConn = true;
            var prefix = ms.ToArray();
            _readSource = PrefixedStream.WrapIfNeeded(prefix, _netConn, leaveInnerOpen: true);
        }

        _mode = Mode.Raw;
    }

    private void ReadFrameHeader()
    {
        ReadOnlySpan<byte> header = _buffer.AsSpan(_start, HeaderSize);
        _command = header[0];
        _remainingContent = BinaryPrimitives.ReadUInt16BigEndian(header[1..]);
        _remainingPadding = BinaryPrimitives.ReadUInt16BigEndian(header[3..]);
        _start += HeaderSize;

        if (_remainingContent > 0)
            _paddingOnlyFrames = 0;
        else if (++_paddingOnlyFrames > MaxPaddingOnlyFrames)
            throw new ProxyProtocolException(ProxyErrorCode.InvalidResponse,
                $"The VLESS server sent {MaxPaddingOnlyFrames} consecutive xtls-rprx-vision frames with no content.");
    }

    private int DrainInto(Span<byte> destination)
    {
        int taken = Math.Min(Buffered, destination.Length);
        _buffer.AsSpan(_start, taken).CopyTo(destination);
        _start += taken;
        return taken;
    }

    /// <summary>Buffers at least <paramref name="count"/> bytes, compacting first if needed.</summary>
    private async ValueTask FillAsync(int count, bool throwOnEof, CancellationToken cancellationToken)
    {
        Compact(count);

        while (Buffered < count)
        {
            int read = await _session.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                if (throwOnEof)
                    throw new EndOfStreamException("The VLESS server closed the connection inside an xtls-rprx-vision frame header, mid-response.");
                return;
            }

            _end += read;
        }
    }

    private void Fill(int count, bool throwOnEof)
    {
        Compact(count);

        while (Buffered < count)
        {
            int read = _session.Read(_buffer.AsSpan(_end));
            if (read == 0)
            {
                if (throwOnEof)
                    throw new EndOfStreamException("The VLESS server closed the connection inside an xtls-rprx-vision frame header, mid-response.");
                return;
            }

            _end += read;
        }
    }

    private async ValueTask<int> FillSomeAsync(CancellationToken cancellationToken)
    {
        Compact(1);
        int read = await _session.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), cancellationToken)
            .ConfigureAwait(false);
        _end += read;
        return read;
    }

    private int FillSome()
    {
        Compact(1);
        int read = _session.Read(_buffer.AsSpan(_end));
        _end += read;
        return read;
    }

    /// <summary>Moves what is buffered to the front when <paramref name="count"/> would not fit.</summary>
    private void Compact(int count)
    {
        ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);

        if (_start == _end)
        {
            _start = _end = 0;
            return;
        }

        if (_end + count <= _buffer.Length)
            return;

        _buffer.AsSpan(_start, Buffered).CopyTo(_buffer);
        _end -= _start;
        _start = 0;
    }

    // ================================ writing ================================

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
            return;

        if (_uplinkRaw)
        {
            await _writeTarget.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            return;
        }

        var remaining = buffer;
        while (!remaining.IsEmpty)
        {
            int uuidOverhead = _uuidSent ? 0 : UuidSize;
            // Headroom for short-packet padding (up to ~900).
            int maxContent = MaxFrame - HeaderSize - uuidOverhead - 900;
            if (maxContent < 1)
                maxContent = MaxFrame - HeaderSize - uuidOverhead - 1;

            int chunkLen = Math.Min(remaining.Length, maxContent);
            var chunk = remaining[..chunkLen];
            bool isAppData = chunk.Length > 0 && chunk.Span[0] == TlsApplicationData;
            // End (not Direct): stay on REALITY for uplink. Server may still send Direct on
            // downlink — FinishFraming splices reads to raw TCP in that case.
            byte command = isAppData ? CommandPaddingEnd : CommandPaddingContinue;

            if (!TryRentPaddedFrame(chunk.Span, command, uuidOverhead, out byte[]? frame, out int length))
            {
                _uplinkRaw = true;
                await _writeTarget.WriteAsync(remaining, cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                await _writeTarget.WriteAsync(frame.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame, clearArray: true);
            }

            _uuidSent = true;
            remaining = remaining[chunkLen..];

            if (command == CommandPaddingEnd)
            {
                _uplinkRaw = true;
                if (!remaining.IsEmpty)
                    await _writeTarget.WriteAsync(remaining, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        WriteAsync(buffer.ToArray()).AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <summary>
    /// Builds one Vision uplink frame: optional leading UUID (first frame only), command header,
    /// content, and padding. Returns false if content cannot fit in <see cref="MaxFrame"/>.
    /// </summary>
    private bool TryRentPaddedFrame(
        ReadOnlySpan<byte> content, byte command, int uuidOverhead, out byte[] frame, out int length)
    {
        frame = null!;
        length = 0;

        int overhead = uuidOverhead + HeaderSize;
        if (content.Length > MaxFrame - overhead)
            return false;

        int padding = PaddingLength(content.Length, uuidOverhead);
        length = overhead + content.Length + padding;

        frame = ArrayPool<byte>.Shared.Rent(length);
        Span<byte> span = frame.AsSpan(0, length);

        int o = 0;
        if (uuidOverhead > 0)
        {
            _uuid.CopyTo(span);
            o = UuidSize;
        }

        span[o] = command;
        BinaryPrimitives.WriteUInt16BigEndian(span[(o + 1)..], (ushort)content.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[(o + 3)..], (ushort)padding);
        content.CopyTo(span[(o + HeaderSize)..]);
        span[(o + HeaderSize + content.Length)..].Clear();

        return true;
    }

    /// <summary>
    /// Xray's rule: pad a short packet out past 900 bytes, and give a long one a small random
    /// tail. The point is to blur the length of the first records, so the number has to be
    /// unpredictable — hence the cryptographic RNG rather than <see cref="Random"/>.
    /// </summary>
    private static int PaddingLength(int contentLength, int uuidOverhead)
    {
        int padding = contentLength < 900
            ? RandomNumberGenerator.GetInt32(500) + 900 - contentLength
            : RandomNumberGenerator.GetInt32(256);

        return Math.Min(padding, MaxFrame - uuidOverhead - HeaderSize - contentLength);
    }

    // ================================ disposal ================================

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_readSource is PrefixedStream pref && !ReferenceEquals(_readSource, _session))
            await pref.DisposeAsync().ConfigureAwait(false);

        if (_ownsNetConn && _netConn is not null)
            await _netConn.DisposeAsync().ConfigureAwait(false);

        if (!_leaveInnerOpen)
            await _session.DisposeAsync().ConfigureAwait(false);
        ReturnBuffer();

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            if (_readSource is PrefixedStream pref && !ReferenceEquals(_readSource, _session))
                pref.Dispose();

            if (_ownsNetConn)
                _netConn?.Dispose();

            if (!_leaveInnerOpen)
                _session.Dispose();
            ReturnBuffer();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    private void ReturnBuffer()
    {
        byte[] buffer = _buffer;
        _buffer = [];

        // Cleared: this held decrypted tunnel payload, and the pool hands the array to whoever
        // rents next.
        if (buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
    }
}
