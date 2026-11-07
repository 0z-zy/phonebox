using System.Net;
using System.Net.Sockets;

/// <summary>A port nothing listens on, from the operating system (random ports collide now and then).</summary>
static class TestPorts
{
    public static int Free()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
