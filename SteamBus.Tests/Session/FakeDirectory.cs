using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SteamBus.Tests.Session;

// The Steam Web API's ISteamDirectory on localhost: GetCMListForConnect returns Servers.
sealed class FakeDirectory : IDisposable
{
  readonly TcpListener listener = new(IPAddress.Loopback, 0);
  readonly CancellationTokenSource stop = new();
  int requests;

  public FakeDirectory()
  {
    listener.Start();
    Address = new Uri($"http://{listener.LocalEndpoint}/");
    _ = Task.Run(AcceptLoop);
  }

  public Uri Address { get; }
  public volatile IPEndPoint[] Servers = [];
  // Answer with success=0, as the Web API does when it can't list servers
  public volatile bool Fails;

  public int Requests => Volatile.Read(ref requests);

  public void Dispose()
  {
    stop.Cancel();
    listener.Stop();
  }

  async Task AcceptLoop()
  {
    while (!stop.IsCancellationRequested)
    {
      TcpClient client;
      try
      {
        client = await listener.AcceptTcpClientAsync(stop.Token);
      }
      catch (Exception)
      {
        return;
      }
      _ = Task.Run(() => Answer(client));
    }
  }

  async Task Answer(TcpClient client)
  {
    using (client)
    {
      try
      {
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
        {
        }

        Interlocked.Increment(ref requests);
        var body = Encoding.UTF8.GetBytes(Body());
        var head = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
      }
      catch (Exception)
      {
        // The client gave up
      }
    }
  }

  string Body()
  {
    var list = new StringBuilder();
    var servers = Servers;
    for (var i = 0; i < servers.Length; i++)
      list.Append($"\t\t\"{i}\"\n\t\t{{\n\t\t\t\"endpoint\"\t\"{servers[i]}\"\n\t\t\t\"type\"\t\"netfilter\"\n\t\t}}\n");

    return $"\"response\"\n{{\n\t\"serverlist\"\n\t{{\n{list}\t}}\n\t\"success\"\t\"{(Fails ? 0 : 1)}\"\n\t\"message\"\t\"\"\n}}\n";
  }
}
