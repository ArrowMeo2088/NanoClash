using System.Net.Sockets;

namespace Clash.Net;

internal static class SocketUtil
{
    public static void ConfigureNoDelay(Socket socket)
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

    public static void ConfigureNoDelay(TcpClient client)
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
