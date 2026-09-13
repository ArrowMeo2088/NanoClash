using System.Collections.Concurrent;

namespace Clash.Utils;

/// <summary>Simple TTL cache with approximate LRU eviction.</summary>
internal sealed class TtlLru<T> : IDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _map = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private bool _disposed;

    public TtlLru(int capacity = 4096) => _capacity = Math.Max(16, capacity);

    public bool TryGet(string key, out T value)
    {
        value = default!;
        if (!_map.TryGetValue(key, out var e))
            return false;
        if (Environment.TickCount64 > e.ExpireAt)
        {
            _map.TryRemove(key, out _);
            return false;
        }

        e.LastAccess = Environment.TickCount64;
        value = e.Value;
        return true;
    }

    public void Set(string key, T value, TimeSpan? ttl = null)
    {
        var expire = Environment.TickCount64 + (long)(ttl ?? TimeSpan.FromMinutes(10)).TotalMilliseconds;
        _map[key] = new Entry(value, expire, Environment.TickCount64);
        if (_map.Count <= _capacity)
            return;

        // Best-effort eviction of oldest entries.
        foreach (var old in _map.OrderBy(kv => kv.Value.LastAccess).Take(_map.Count - _capacity))
            _map.TryRemove(old.Key, out _);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _map.Clear();
    }

    private sealed class Entry(T value, long expireAt, long lastAccess)
    {
        public T Value { get; } = value;
        public long ExpireAt { get; } = expireAt;
        public long LastAccess = lastAccess;
    }
}

internal static class SocketUtil
{
    public static void ConfigureNoDelay(System.Net.Sockets.Socket socket)
    {
        try
        {
            socket.NoDelay = true;
        }
        catch
        {
            // ignore
        }
    }

    public static void ConfigureNoDelay(System.Net.Sockets.TcpClient client)
    {
        try
        {
            ConfigureNoDelay(client.Client);
        }
        catch
        {
            // ignore
        }
    }
}
