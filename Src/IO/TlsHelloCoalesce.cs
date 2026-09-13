namespace Clash.IO;

/// <summary>
/// Chrome/Edge PQ ClientHello (~1.5KB+) often arrives fragmented. Vision uplink
/// framing must see a complete first TLS record — coalesce before the first Write.
/// </summary>
internal static class TlsHelloCoalesce
{
    private const byte ContentTypeHandshake = 0x16;
    private const int MaxHelloBytes = 16 * 1024;
    /// <summary>QPN / Xray Vision MaxFrame − UUID − header.</summary>
    public const int VisionMaxContent = 8192 - 16 - 5;
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    public readonly record struct Result(int FirstWriteLen, bool LikelyVisionFramed, bool TimedOut);

    /// <summary>
    /// Read until the first TLS record is complete (or give up), write it as one buffer to
    /// <paramref name="dest"/>, then write any bytes already read past that record.
    /// </summary>
    public static async Task<Result> FlushFirstRecordAsync(
        Stream client,
        Stream dest,
        ReadOnlyMemory<byte> seed,
        CancellationToken ct)
    {
        await using var ms = new MemoryStream();
        if (!seed.IsEmpty)
            ms.Write(seed.Span);

        if (ms.Length > 0 && ms.GetBuffer()[0] != ContentTypeHandshake)
        {
            var n = (int)ms.Length;
            await WriteAllAsync(dest, ms, ct).ConfigureAwait(false);
            await dest.FlushAsync(ct).ConfigureAwait(false);
            return new Result(n, n > 0 && n <= VisionMaxContent, TimedOut: false);
        }

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitCts.CancelAfter(WaitTimeout);
        var buf = new byte[8 * 1024];
        var timedOut = false;

        while (true)
        {
            var span = ms.GetBuffer().AsSpan(0, (int)ms.Length);
            if (TryCompleteRecordLength(span, out var recordLen))
            {
                await dest.WriteAsync(ms.GetBuffer().AsMemory(0, recordLen), ct).ConfigureAwait(false);
                if (ms.Length > recordLen)
                {
                    await dest.WriteAsync(
                            ms.GetBuffer().AsMemory(recordLen, (int)ms.Length - recordLen), ct)
                        .ConfigureAwait(false);
                }

                await dest.FlushAsync(ct).ConfigureAwait(false);
                return new Result(recordLen, recordLen <= VisionMaxContent, timedOut);
            }

            if (ms.Length >= MaxHelloBytes)
            {
                var n = (int)ms.Length;
                await WriteAllAsync(dest, ms, ct).ConfigureAwait(false);
                await dest.FlushAsync(ct).ConfigureAwait(false);
                return new Result(n, n <= VisionMaxContent, timedOut);
            }

            int read;
            try
            {
                read = await client.ReadAsync(buf.AsMemory(), waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                var n = (int)ms.Length;
                await WriteAllAsync(dest, ms, ct).ConfigureAwait(false);
                await dest.FlushAsync(ct).ConfigureAwait(false);
                return new Result(n, n > 0 && n <= VisionMaxContent, TimedOut: true);
            }

            if (read == 0)
            {
                var n = (int)ms.Length;
                await WriteAllAsync(dest, ms, ct).ConfigureAwait(false);
                await dest.FlushAsync(ct).ConfigureAwait(false);
                return new Result(n, n > 0 && n <= VisionMaxContent, timedOut);
            }

            ms.Write(buf, 0, read);

            if (ms.Length > 0 && ms.GetBuffer()[0] != ContentTypeHandshake)
            {
                var n = (int)ms.Length;
                await WriteAllAsync(dest, ms, ct).ConfigureAwait(false);
                await dest.FlushAsync(ct).ConfigureAwait(false);
                return new Result(n, n <= VisionMaxContent, timedOut);
            }
        }
    }

    private static async Task WriteAllAsync(Stream dest, MemoryStream ms, CancellationToken ct)
    {
        if (ms.Length == 0)
            return;
        await dest.WriteAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), ct).ConfigureAwait(false);
    }

    internal static bool TryCompleteRecordLength(ReadOnlySpan<byte> data, out int recordLen)
    {
        recordLen = 0;
        if (data.Length < 5)
            return false;
        if (data[0] != ContentTypeHandshake)
            return false;

        var fragLen = (data[3] << 8) | data[4];
        if (fragLen is < 0 or > 16 * 1024)
            return false;

        recordLen = 5 + fragLen;
        return data.Length >= recordLen;
    }
}
