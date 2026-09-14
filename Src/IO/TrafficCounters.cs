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
