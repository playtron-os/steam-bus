using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SteamKit2;
using SteamKit2.Internal;

namespace SteamBus.Tests.Session;

// A Steam CM on localhost speaking SteamKit's TCP protocol up to the encrypted channel.
// It can't read or answer encrypted traffic, so tests post logon results as callbacks.
sealed class FakeCmServer : IDisposable
{
  public enum Mode
  {
    // Complete the channel handshake, then read and ignore everything
    Accept,
    // Accept the TCP connection and never speak
    Silent,
  }

  const uint Magic = 0x31305456; // "VT01"

  readonly object gate = new();
  readonly List<TcpClient> clients = [];
  TcpListener? listener;
  CancellationTokenSource? acceptLoop;
  int connections;
  int handshakes;
  int logons;

  public FakeCmServer()
  {
    Start(0);
    EndPoint = (IPEndPoint)listener!.LocalEndpoint;
  }

  public IPEndPoint EndPoint { get; }
  public volatile Mode Behaviour = Mode.Accept;

  // Accepted TCP connections
  public int Connections => Volatile.Read(ref connections);

  // Connections that completed the channel handshake
  public int Handshakes => Volatile.Read(ref handshakes);

  // Handshaked connections that then sent their first packet, the logon
  public int Logons => Volatile.Read(ref logons);

  public int OpenConnections
  {
    get { lock (gate) return clients.Count; }
  }

  public bool IsListening => listener != null;

  public void Start() => Start(EndPoint.Port);

  // Refuse new connections; open ones stay up.
  public void Stop()
  {
    acceptLoop?.Cancel();
    listener?.Stop();
    listener = null;
  }

  // Close every open connection, as a CM restart or a network drop would.
  public void DropAll() => Drop(Open());

  public List<TcpClient> Open()
  {
    lock (gate) return [.. clients];
  }

  public void Drop(IEnumerable<TcpClient> connections)
  {
    foreach (var client in connections)
    {
      lock (gate) clients.Remove(client);
      try
      {
        client.Client.LingerState = new LingerOption(true, 0);
        client.Close();
      }
      catch (Exception)
      {
        // Already closed by the client
      }
    }
  }

  public void Dispose()
  {
    Stop();
    DropAll();
  }

  void Start(int port)
  {
    var next = new TcpListener(IPAddress.Loopback, port);
    next.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    next.Start();
    listener = next;
    acceptLoop = new CancellationTokenSource();
    var token = acceptLoop.Token;
    _ = Task.Run(() => AcceptLoop(next, token));
  }

  async Task AcceptLoop(TcpListener server, CancellationToken token)
  {
    while (!token.IsCancellationRequested)
    {
      TcpClient client;
      try
      {
        client = await server.AcceptTcpClientAsync(token);
      }
      catch (Exception)
      {
        return;
      }

      Interlocked.Increment(ref connections);
      lock (gate) clients.Add(client);
      _ = Task.Run(() => Serve(client, Behaviour));
    }
  }

  async Task Serve(TcpClient client, Mode mode)
  {
    try
    {
      var stream = client.GetStream();
      if (mode == Mode.Accept)
      {
        // Network latency on a real CM: SteamKit drops a request that beats its own channel setup.
        await Task.Delay(50);
        await WriteFrame(stream, EncryptRequest());
        await ReadFrame(stream);
        await WriteFrame(stream, EncryptResult());
        Interlocked.Increment(ref handshakes);

        await ReadFrame(stream);
        Interlocked.Increment(ref logons);
      }

      while (await ReadFrame(stream) != null)
      {
      }
    }
    catch (Exception)
    {
      // The client or a test closed the connection
    }
    finally
    {
      lock (gate) clients.Remove(client);
      client.Dispose();
    }
  }

  static byte[] EncryptRequest()
  {
    var msg = new Msg<MsgChannelEncryptRequest>();
    msg.Body.ProtocolVersion = 1;
    msg.Body.Universe = EUniverse.Public;
    msg.Write(RandomNumberGenerator.GetBytes(16));
    return msg.Serialize();
  }

  static byte[] EncryptResult()
  {
    var msg = new Msg<MsgChannelEncryptResult>();
    msg.Body.Result = EResult.OK;
    return msg.Serialize();
  }

  static async Task WriteFrame(NetworkStream stream, byte[] payload)
  {
    var header = new byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), Magic);
    await stream.WriteAsync(header);
    await stream.WriteAsync(payload);
  }

  static async Task<byte[]?> ReadFrame(NetworkStream stream)
  {
    var header = new byte[8];
    if (!await ReadExactly(stream, header))
      return null;

    var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
    var payload = new byte[length];
    return await ReadExactly(stream, payload) ? payload : null;
  }

  static async Task<bool> ReadExactly(NetworkStream stream, byte[] buffer)
  {
    var read = 0;
    while (read < buffer.Length)
    {
      var n = await stream.ReadAsync(buffer.AsMemory(read));
      if (n == 0)
        return false;
      read += n;
    }
    return true;
  }
}
