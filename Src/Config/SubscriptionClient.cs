using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

using Clash.Net;
using Clash.Utils;

namespace Clash.Config;

/// <summary>Parsed subscription-userinfo header / body fields.</summary>
internal sealed class SubscriptionUserInfo
{
    public long? Upload { get; init; }
    public long? Download { get; init; }
    public long? Total { get; init; }
    public long? ExpireUnix { get; init; }
}

internal sealed class SubscriptionFetchResult
{
    public required byte[] Body { get; init; }
    public SubscriptionUserInfo? UserInfo { get; init; }
    public string? SuggestedName { get; init; }
}

/// <summary>Direct (no system proxy) subscription fetch with UA clash.meta.</summary>
internal sealed class SubscriptionClient : IDisposable
{
    public const string UserAgent = "clash.meta";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    private readonly HttpClient _http;

    public SubscriptionClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = Timeout,
            ConnectCallback = async (context, ct) =>
            {
                var ep = context.DnsEndPoint;
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    SocketUtil.ConfigureNoDelay(socket);
                    InterfaceBinder.Bind(socket);
                    if (IPAddress.TryParse(ep.Host, out var ip))
                        await socket.ConnectAsync(new IPEndPoint(ip, ep.Port), ct).ConfigureAwait(false);
                    else
                        await socket.ConnectAsync(ep.Host, ep.Port, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _http = new HttpClient(handler) { Timeout = Timeout };
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
    }

    public async Task<SubscriptionFetchResult> FetchAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var info = ParseUserInfoHeader(resp.Headers) ?? ParseUserInfoFromBody(body);
        var suggested = ParseContentDispositionName(resp.Content.Headers)
            ?? ParseProfileTitle(resp.Headers)
            ?? SuggestNameFromUrl(url);
        return new SubscriptionFetchResult { Body = body, UserInfo = info, SuggestedName = suggested };
    }

    public static string? ParseContentDispositionName(HttpContentHeaders headers)
    {
        if (headers.ContentDisposition?.FileNameStar is { Length: > 0 } star)
            return SanitizeFileName(star.Trim('"'));
        if (headers.ContentDisposition?.FileName is { Length: > 0 } name)
            return SanitizeFileName(name.Trim('"'));

        if (!TryGetHeader(headers, "content-disposition", out var raw))
            return null;
        // filename*=UTF-8''...
        var starIdx = raw.IndexOf("filename*=", StringComparison.OrdinalIgnoreCase);
        if (starIdx >= 0)
        {
            var part = raw[(starIdx + "filename*=".Length)..].Trim();
            var semi = part.IndexOf(';');
            if (semi >= 0)
                part = part[..semi];
            part = part.Trim().Trim('"');
            var ticks = part.IndexOf("''", StringComparison.Ordinal);
            if (ticks >= 0)
                part = Uri.UnescapeDataString(part[(ticks + 2)..]);
            return SanitizeFileName(part);
        }

        var fnIdx = raw.IndexOf("filename=", StringComparison.OrdinalIgnoreCase);
        if (fnIdx < 0)
            return null;
        var fn = raw[(fnIdx + "filename=".Length)..].Trim();
        var end = fn.IndexOf(';');
        if (end >= 0)
            fn = fn[..end];
        return SanitizeFileName(fn.Trim().Trim('"'));
    }

    public static string? ParseProfileTitle(HttpResponseHeaders headers)
    {
        if (!TryGetHeader(headers, "profile-title", out var raw))
            return null;
        raw = raw.Trim();
        if (raw.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = Convert.FromBase64String(raw["base64:".Length..].Trim());
                return SanitizeFileName(Encoding.UTF8.GetString(bytes));
            }
            catch
            {
                return null;
            }
        }

        return SanitizeFileName(raw);
    }

    public static string? SuggestNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var seg = uri.Segments.LastOrDefault()?.Trim('/');
            if (string.IsNullOrWhiteSpace(seg) || seg is "/" or ".")
                return null;
            return SanitizeFileName(Uri.UnescapeDataString(seg));
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeFileName(string name)
    {
        name = Path.GetFileNameWithoutExtension(name.Trim());
        if (string.IsNullOrWhiteSpace(name))
            return "Proxy";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    private static bool TryGetHeader(HttpHeaders headers, string name, out string value)
    {
        value = "";
        if (headers.TryGetValues(name, out var vals))
        {
            value = string.Join(",", vals);
            return !string.IsNullOrWhiteSpace(value);
        }

        foreach (var h in headers)
        {
            if (h.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = string.Join(",", h.Value);
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        return false;
    }

    // Keep old overload for response headers used by userinfo.
    private static bool TryGetHeader(HttpResponseHeaders headers, string name, out string value) =>
        TryGetHeader((HttpHeaders)headers, name, out value);

    public static SubscriptionUserInfo? ParseUserInfoHeader(HttpResponseHeaders headers)
    {
        if (!TryGetHeader(headers, "subscription-userinfo", out var raw))
            return null;
        return ParseUserInfoString(raw);
    }

    public static SubscriptionUserInfo? ParseUserInfoFromBody(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        var lines = text.Split('\n');
        var take = Math.Min(lines.Length, 12);
        for (var i = 0; i < take; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("#subscription-userinfo:", StringComparison.OrdinalIgnoreCase))
                return ParseUserInfoString(line["#subscription-userinfo:".Length..].Trim());
            if (line.StartsWith("# subscription-userinfo:", StringComparison.OrdinalIgnoreCase))
                return ParseUserInfoString(line["# subscription-userinfo:".Length..].Trim());
        }

        return null;
    }

    public static SubscriptionUserInfo? ParseUserInfoString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        long? upload = null, download = null, total = null, expire = null;
        var parts = raw.Replace(" ", "", StringComparison.Ordinal).Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = part[..eq].Trim().ToLowerInvariant();
            var val = part[(eq + 1)..].Trim();
            if (!TryParseInt64Loose(val, out var n))
                continue;
            switch (key)
            {
                case "upload":
                    upload = n;
                    break;
                case "download":
                    download = n;
                    break;
                case "total":
                    total = n;
                    break;
                case "expire":
                    expire = n;
                    break;
            }
        }

        if (upload is null && download is null && total is null && expire is null)
            return null;
        return new SubscriptionUserInfo
        {
            Upload = upload,
            Download = download,
            Total = total,
            ExpireUnix = expire,
        };
    }

    private static bool TryParseInt64Loose(string s, out long n)
    {
        n = 0;
        if (long.TryParse(s, out n))
            return true;
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            n = (long)d;
            return true;
        }

        return false;
    }

    public void Dispose() => _http.Dispose();
}
