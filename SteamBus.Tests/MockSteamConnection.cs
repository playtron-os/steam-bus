using Steam.Session;

namespace SteamBus.Tests;

/// <summary>
/// Test double for <see cref="ISteamConnection"/>.
/// Records calls and lets tests control <see cref="IsConnected"/> state directly.
/// Also enables simulating immediate-fail connects by firing a disconnect callback.
/// </summary>
public class MockSteamConnection : ISteamConnection
{
  public bool IsConnected { get; set; }
  public int ConnectCallCount { get; private set; }
  public int DisconnectCallCount { get; private set; }

  /// <summary>
  /// When set, calling <see cref="Connect"/> will invoke this action after
  /// recording the call. Use this to simulate immediate connect failures by
  /// firing <c>csm.OnDisconnected(false)</c> from the callback.
  /// </summary>
  public Action? OnConnectCalled { get; set; }

  /// <summary>
  /// When set, calling <see cref="Disconnect"/> will invoke this action after
  /// recording the call and clearing <see cref="IsConnected"/>.
  /// Use this to simulate the disconnect callback.
  /// </summary>
  public Action? OnDisconnectCalled { get; set; }

  public void Connect()
  {
    ConnectCallCount++;
    // By default, mark as connected (optimistic)
    IsConnected = true;
    OnConnectCalled?.Invoke();
  }

  public void Disconnect()
  {
    DisconnectCallCount++;
    IsConnected = false;
    OnDisconnectCalled?.Invoke();
  }

  public void Reset()
  {
    ConnectCallCount = 0;
    DisconnectCallCount = 0;
    IsConnected = false;
    OnConnectCalled = null;
    OnDisconnectCalled = null;
  }
}
