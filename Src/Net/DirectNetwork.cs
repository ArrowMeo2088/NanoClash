using System.Net;

namespace Clash.Net;

/// <summary>
/// Force all BCL HTTP traffic off the system / env proxy so NanoClash never loops
/// through its own :7887 inbound when "代理模式" is on.
/// </summary>
internal static class DirectNetwork
{
    public static void Configure()
    {
        HttpClient.DefaultProxy = new NoProxy();

#pragma warning disable SYSLIB0014
        WebRequest.DefaultWebProxy = null;
#pragma warning restore SYSLIB0014

        foreach (var name in new[]
                 {
                     "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
                     "http_proxy", "https_proxy", "all_proxy", "no_proxy",
                 })
        {
            try
            {
                Environment.SetEnvironmentVariable(name, null);
            }
            catch
            {
                // ignore
            }
        }
    }

    private sealed class NoProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => null;
        public bool IsBypassed(Uri host) => true;
    }
}
