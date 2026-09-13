using System.Buffers;
using System.Text;

namespace Clash.IO;

internal static class TrafficCounters
{
    private static long _uploadBytes;
    private static long _downloadBytes;
    private static long _windowUpload;
    private static long _windowDownload;
    private static long _windowStartTicks = Environment.TickCount64;
    private static int _uploadLatencyMs;
    private static int _downloadLatencyMs;

    public static void AddUpload(int n)
    {
        Interlocked.Add(ref _uploadBytes, n);
        Interlocked.Add(ref _windowUpload, n);
    }

    public static void AddDownload(int n)
    {
        Interlocked.Add(ref _downloadBytes, n);
        Interlocked.Add(ref _windowDownload, n);
    }

    public static void NoteUploadLatency(int ms) =>
        Interlocked.Exchange(ref _uploadLatencyMs, Math.Max(0, ms));

    public static void NoteDownloadLatency(int ms) =>
        Interlocked.Exchange(ref _downloadLatencyMs, Math.Max(0, ms));

    public static (int UpLat, int DownLat, int UpKBps, int DownKBps, int UpMB, int DownMB, int TotalKBps, int TotalMB) Snapshot()
    {
        var now = Environment.TickCount64;
        var elapsed = Math.Max(1, now - Interlocked.Read(ref _windowStartTicks));
        var upWin = Interlocked.Exchange(ref _windowUpload, 0);
        var downWin = Interlocked.Exchange(ref _windowDownload, 0);
        Interlocked.Exchange(ref _windowStartTicks, now);

        // Decimal KB/MB (1 KB = 1000 B), not KiB.
        var upK = (int)Math.Min(int.MaxValue, upWin * 1000L / elapsed / 1000L);
        var downK = (int)Math.Min(int.MaxValue, downWin * 1000L / elapsed / 1000L);
        var upBytes = Interlocked.Read(ref _uploadBytes);
        var downBytes = Interlocked.Read(ref _downloadBytes);
        var upMb = (int)Math.Min(int.MaxValue, upBytes / 1_000_000L);
        var downMb = (int)Math.Min(int.MaxValue, downBytes / 1_000_000L);
        var totalK = (int)Math.Min(int.MaxValue, (long)upK + downK);
        var totalMb = (int)Math.Min(int.MaxValue, (upBytes + downBytes) / 1_000_000L);
        return (
            Interlocked.CompareExchange(ref _uploadLatencyMs, 0, 0),
            Interlocked.CompareExchange(ref _downloadLatencyMs, 0, 0),
            upK,
            downK,
            upMb,
            downMb,
            totalK,
            totalMb);
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _uploadBytes, 0);
        Interlocked.Exchange(ref _downloadBytes, 0);
        Interlocked.Exchange(ref _windowUpload, 0);
        Interlocked.Exchange(ref _windowDownload, 0);
        Interlocked.Exchange(ref _windowStartTicks, Environment.TickCount64);
        Interlocked.Exchange(ref _uploadLatencyMs, 0);
        Interlocked.Exchange(ref _downloadLatencyMs, 0);
    }
}

internal static class Relay
{
    private const int BufSize = 64 << 10;

    public static async Task CopyBidirectionalAsync(Stream a, Stream b, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var t1 = CopyHalfAsync(a, b, linked, upload: true);
        var t2 = CopyHalfAsync(b, a, linked, upload: false);
        await Task.WhenAny(t1, t2).ConfigureAwait(false);
        linked.Cancel();
        try
        {
            await Task.WhenAll(t1, t2).ConfigureAwait(false);
        }
        catch
        {
            // teardown
        }
    }

    private static async Task CopyHalfAsync(
        Stream src,
        Stream dst,
        CancellationTokenSource linked,
        bool upload)
    {
        var buf = ArrayPool<byte>.Shared.Rent(BufSize);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var n = await src.ReadAsync(buf.AsMemory(0, BufSize), linked.Token).ConfigureAwait(false);
                if (n == 0)
                    break;
                await dst.WriteAsync(buf.AsMemory(0, n), linked.Token).ConfigureAwait(false);
                if (upload)
                    TrafficCounters.AddUpload(n);
                else
                    TrafficCounters.AddDownload(n);
            }
        }
        catch (OperationCanceledException)
        {
            // teardown
        }
        catch
        {
            // ignore copy errors
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            try
            {
                linked.Cancel();
            }
            catch
            {
                // ignore
            }
        }
    }

    public static async Task WriteHttpErrorAsync(Stream client, int status, string reason, CancellationToken ct)
    {
        var body = Encoding.ASCII.GetBytes(reason + "\n");
        var head =
            $"HTTP/1.1 {status} {ReasonPhrase(status)}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";
        await client.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await client.WriteAsync(body, ct).ConfigureAwait(false);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        400 => "Bad Request",
        403 => "Forbidden",
        503 => "Service Unavailable",
        500 => "Internal Server Error",
        _ => "Error",
    };
}
