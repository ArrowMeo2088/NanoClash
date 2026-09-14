using System.Buffers;
using System.Net.Sockets;
using System.Text;

namespace Clash.IO;

internal static class Relay
{
    private const int BufSize = 64 << 10;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    public static async Task CopyBidirectionalAsync(Stream a, Stream b, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(IdleTimeout);
        var t1 = CopyHalfAsync(a, b, linked, upload: true);
        var t2 = CopyHalfAsync(b, a, linked, upload: false);
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
                {
                    TryShutdownSend(dst);
                    return;
                }

                linked.CancelAfter(IdleTimeout);
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
            try
            {
                linked.Cancel();
            }
            catch
            {
                // ignore
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static void TryShutdownSend(Stream dst)
    {
        try
        {
            if (dst is NetworkStream ns)
                ns.Socket.Shutdown(SocketShutdown.Send);
        }
        catch
        {
            // ignore
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
