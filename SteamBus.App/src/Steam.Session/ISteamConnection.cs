namespace Steam.Session;

/// <summary>
/// Abstracts the SteamClient Connect/Disconnect/IsConnected surface
/// so the reconnection state machine can be tested without SteamKit2.
/// </summary>
public interface ISteamConnection
{
  bool IsConnected { get; }
  void Connect();
  void Disconnect();
}
