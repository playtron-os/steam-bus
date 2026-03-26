using SteamKit2;

namespace Steam.Session;

/// <summary>
/// Wraps the concrete <see cref="SteamKit2.SteamClient"/> behind <see cref="ISteamConnection"/>
/// so that <see cref="ConnectionStateMachine"/> can be tested independently.
/// </summary>
public class SteamClientConnection : ISteamConnection
{
  private readonly SteamClient _client;

  public SteamClientConnection(SteamClient client)
  {
    _client = client;
  }

  public bool IsConnected => _client.IsConnected;
  public void Connect() => _client.Connect();
  public void Disconnect() => _client.Disconnect();
}
