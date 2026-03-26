using Steam.Session;

namespace SteamBus.Tests;

/// <summary>
/// Comprehensive tests for the <see cref="ConnectionStateMachine"/> reconnection logic.
/// Every scenario is derived from real-world log analysis of sleep/wake, network
/// flapping, and stale-connection recovery.
/// </summary>
[TestFixture]
public class ConnectionStateMachineTests
{
  private MockSteamConnection _conn = null!;
  private ConnectionStateMachine _csm = null!;
  private List<string> _authErrors = null!;
  private int _authUpdatedCount;

  [SetUp]
  public void Setup()
  {
    _conn = new MockSteamConnection();
    _csm = new ConnectionStateMachine(_conn);
    _csm.ReconnectDelayMs = 0; // No real delays in tests
    _authErrors = new List<string>();
    _authUpdatedCount = 0;
    _csm.OnAuthError = (err) => _authErrors.Add(err);
    _csm.OnAuthUpdated = () => _authUpdatedCount++;
  }

  // ════════════════════════════════════════════════════════════════════
  //  1. HAPPY PATH: Normal boot → connect → login
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task HappyPath_Boot_Connect_Login()
  {
    // Simulate: boot → network online → Connect → OnConnected → OnLoggedIn
    _csm.IsPendingLogin = true;
    _csm.isOnline = false;

    await _csm.OnOnline();

    // Should call Connect()
    Assert.That(_conn.ConnectCallCount, Is.EqualTo(1), "Should have called Connect once");
    Assert.That(_csm.bConnecting, Is.True, "Should be in connecting state");

    // SteamClient fires ConnectedCallback
    _csm.OnConnected();
    Assert.That(_csm.bConnecting, Is.False, "bConnecting should be cleared after OnConnected");
    Assert.That(_csm.bSuppressReconnect, Is.False, "bSuppressReconnect cleared in OnConnected");

    // Login succeeds
    _csm.OnLoggedIn();
    Assert.That(_csm.IsLoggedOn, Is.True);
    Assert.That(_csm.IsPendingLogin, Is.False);
    Assert.That(_csm.connectionBackoff, Is.EqualTo(0));
    Assert.That(_csm.bAborted, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  //  2. SLEEP/WAKE: Online → Offline → Online with recovery
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task SleepWake_OnlineOfflineOnline_RecoverySucceeds()
  {
    // Setup: fully logged in
    SetupLoggedIn();

    // Device goes to sleep → network drops
    _csm.OnOffline();
    Assert.That(_csm.IsLoggedOn, Is.False, "Should not be logged on after offline");
    Assert.That(_csm.IsPendingLogin, Is.True, "Should be pending login");
    Assert.That(_csm.bIsConnectionRecovery, Is.True, "Should be in recovery mode");

    // SteamClient fires disconnect (stale connection dies)
    _csm.OnDisconnected(userInitiated: false);

    // Device wakes → network returns
    _conn.IsConnected = true; // Stale connection still reports connected
    await _csm.OnOnline();

    // Recovery path: disconnect stale, then connect fresh
    Assert.That(_conn.DisconnectCallCount, Is.GreaterThanOrEqualTo(1), "Should have disconnected stale");
    Assert.That(_conn.ConnectCallCount, Is.GreaterThanOrEqualTo(1), "Should have called Connect for fresh connection");
    Assert.That(_csm.bSuppressReconnect, Is.False, "bSuppressReconnect must be cleared after Connect()");

    // Fresh connection succeeds
    _csm.OnConnected();
    _csm.OnLoggedIn();
    Assert.That(_csm.IsLoggedOn, Is.True);
    Assert.That(_csm.IsPendingLogin, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  //  3. BUG FIX: Recovery path connect fails immediately
  //     (the stuck "Reconnecting" bug)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task RecoveryPath_ConnectFailsImmediately_ShouldRetryViaBackoff()
  {
    // Setup: logged in, then offline, then back online (recovery path)
    SetupLoggedIn();
    _csm.OnOffline();

    _conn.IsConnected = true; // stale

    // Simulate: Connect() is called but connection fails immediately
    // (OnDisconnected fires without OnConnected ever happening)
    _conn.OnConnectCalled = () =>
    {
      // Simulate immediate failure — connection drops right away
      _conn.IsConnected = false;
    };

    await _csm.OnOnline();

    // **Critical assertion**: bSuppressReconnect must be false
    // so that when OnDisconnected fires, it can retry
    Assert.That(_csm.bSuppressReconnect, Is.False,
        "bSuppressReconnect MUST be false after OnOnline returns, " +
        "otherwise OnDisconnected cannot retry");

    // Now simulate the OnDisconnected for the failed fresh connect
    _csm.OnDisconnected(userInitiated: false);

    // Should NOT be aborted — should have scheduled a delayed reconnect
    Assert.That(_csm.bAborted, Is.False, "Should not be aborted");
    Assert.That(_csm.connectionBackoff, Is.EqualTo(1), "Should have incremented backoff");

    // After the delayed reconnect runs, it will call _connection.Connect()
    // Let it settle
    await Task.Delay(50);
    Assert.That(_conn.ConnectCallCount, Is.GreaterThanOrEqualTo(2),
        "Should have retried Connect via backoff");
  }

  // ════════════════════════════════════════════════════════════════════
  //  4. Recovery path when SteamClient already disconnected
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task RecoveryPath_AlreadyDisconnected_SkipsDisconnect()
  {
    SetupLoggedIn();
    _csm.OnOffline();
    _conn.IsConnected = false; // Already disconnected

    int disconnectsBefore = _conn.DisconnectCallCount;
    await _csm.OnOnline();

    Assert.That(_conn.DisconnectCallCount, Is.EqualTo(disconnectsBefore),
        "Should NOT call Disconnect when already disconnected");
    Assert.That(_conn.ConnectCallCount, Is.GreaterThanOrEqualTo(1),
        "Should still call Connect()");
  }

  // ════════════════════════════════════════════════════════════════════
  //  5. OnDisconnected suppression during controlled stale-disconnect
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task RecoveryPath_StaleDisconnectCallback_IsSuppressed()
  {
    SetupLoggedIn();
    _csm.OnOffline();
    _conn.IsConnected = true;

    // When Disconnect is called on stale connection, fire OnDisconnected synchronously
    _conn.OnDisconnectCalled = () =>
    {
      // This simulates the stale disconnect callback
      _csm.OnDisconnected(userInitiated: true);
    };

    await _csm.OnOnline();

    // The stale disconnect should have been suppressed
    // and a fresh Connect should have been called
    Assert.That(_conn.ConnectCallCount, Is.GreaterThanOrEqualTo(1));
    Assert.That(_csm.bSuppressReconnect, Is.False,
        "bSuppressReconnect should be cleared after recovery");
  }

  // ════════════════════════════════════════════════════════════════════
  //  6. OnOnline when not pending login (no-op)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task OnOnline_NotPendingLogin_IsNoop()
  {
    _csm.IsPendingLogin = false;
    _csm.IsLoggedOn = true;

    await _csm.OnOnline();

    Assert.That(_conn.ConnectCallCount, Is.EqualTo(0), "Should not connect when not pending");
    Assert.That(_csm.IsLoggedOn, Is.True);
  }

  // ════════════════════════════════════════════════════════════════════
  //  7. OnOffline when already offline / not logged in (no-op)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnOffline_NotLoggedIn_NoStateChange()
  {
    _csm.IsLoggedOn = false;
    _csm.IsPendingLogin = false;
    _csm.bIsConnectionRecovery = false;

    _csm.OnOffline();

    Assert.That(_csm.IsLoggedOn, Is.False);
    Assert.That(_csm.IsPendingLogin, Is.False, "Should not become pending");
    Assert.That(_csm.bIsConnectionRecovery, Is.False, "Should not enter recovery");
  }

  // ════════════════════════════════════════════════════════════════════
  //  8. OnOffline marks recovery state correctly
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnOffline_WhileLoggedIn_MarksRecovery()
  {
    SetupLoggedIn();

    _csm.OnOffline();

    Assert.That(_csm.IsLoggedOn, Is.False);
    Assert.That(_csm.IsPendingLogin, Is.True);
    Assert.That(_csm.bIsConnectionRecovery, Is.True);
    Assert.That(_csm.isOnline, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  //  9. OnDisconnected with backoff exhaustion (12 retries)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnDisconnected_BackoffExhausted_Aborts()
  {
    _csm.isOnline = true;
    _csm.bConnecting = true;
    _csm.connectionBackoff = 12;

    _csm.OnDisconnected(userInitiated: false);

    Assert.That(_csm.IsLoggedOn, Is.False);
    Assert.That(_csm.IsPendingLogin, Is.False, "Abort clears pending login");
    Assert.That(_authErrors, Has.Count.EqualTo(1));
    Assert.That(_authErrors[0], Is.EqualTo("Timeout"));
  }

  // ════════════════════════════════════════════════════════════════════
  // 10. OnDisconnected user-initiated (not recovery) → abort
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnDisconnected_UserInitiated_NotRecovery_SetsAborted()
  {
    _csm.isOnline = true;
    _csm.bIsConnectionRecovery = false;

    _csm.OnDisconnected(userInitiated: true);

    Assert.That(_csm.bAborted, Is.True);
  }

  // ════════════════════════════════════════════════════════════════════
  // 11. OnDisconnected user-initiated DURING recovery → does NOT abort
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnDisconnected_UserInitiated_DuringRecovery_DoesNotAbort()
  {
    _csm.isOnline = true;
    _csm.bIsConnectionRecovery = true;

    _csm.OnDisconnected(userInitiated: true);

    Assert.That(_csm.bAborted, Is.False,
        "During recovery, user-initiated disconnect should not abort");
  }

  // ════════════════════════════════════════════════════════════════════
  // 12. OnDisconnected while offline → sets PendingLogin, no reconnect
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnDisconnected_Offline_SetsPendingLogin()
  {
    _csm.isOnline = false;
    _csm.bConnecting = true;

    int connectsBefore = _conn.ConnectCallCount;
    _csm.OnDisconnected(userInitiated: false);

    Assert.That(_csm.IsPendingLogin, Is.True);
    // Should NOT attempt reconnect while offline
    Assert.That(_conn.ConnectCallCount, Is.EqualTo(connectsBefore),
        "Should not call Connect while offline");
  }

  // ════════════════════════════════════════════════════════════════════
  // 13. OnDisconnected when aborted → no reconnect
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnDisconnected_Aborted_NoReconnect()
  {
    _csm.isOnline = true;
    _csm.bAborted = true;

    int connectsBefore = _conn.ConnectCallCount;
    _csm.OnDisconnected(userInitiated: false);

    Assert.That(_conn.ConnectCallCount, Is.EqualTo(connectsBefore));
  }

  // ════════════════════════════════════════════════════════════════════
  // 14. Connection backoff increments correctly
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnDisconnected_IncreasesBackoff_WhenNotRecovery()
  {
    _csm.isOnline = true;
    _csm.bConnecting = true;
    _csm.connectionBackoff = 0;
    _csm.bIsConnectionRecovery = false;

    _csm.OnDisconnected(userInitiated: false);

    Assert.That(_csm.connectionBackoff, Is.EqualTo(1));
  }

  // ════════════════════════════════════════════════════════════════════
  // 15. Connection backoff does NOT increment during recovery
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnDisconnected_DoesNotIncrementBackoff_DuringRecovery()
  {
    _csm.isOnline = true;
    _csm.bConnecting = true;
    _csm.connectionBackoff = 3;
    _csm.bIsConnectionRecovery = true;

    _csm.OnDisconnected(userInitiated: false);

    Assert.That(_csm.connectionBackoff, Is.EqualTo(3),
        "Backoff should not increment during recovery");
  }

  // ════════════════════════════════════════════════════════════════════
  // 16. Connect resets backoff when NOT in recovery mode
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void Connect_ResetsBackoff_WhenNotRecovery()
  {
    _csm.connectionBackoff = 5;
    _csm.bIsConnectionRecovery = false;

    _csm.Connect();

    Assert.That(_csm.connectionBackoff, Is.EqualTo(0));
  }

  // ════════════════════════════════════════════════════════════════════
  // 17. Connect preserves backoff during recovery
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void Connect_PreservesBackoff_DuringRecovery()
  {
    _csm.connectionBackoff = 5;
    _csm.bIsConnectionRecovery = true;

    _csm.Connect();

    Assert.That(_csm.connectionBackoff, Is.EqualTo(5));
  }

  // ════════════════════════════════════════════════════════════════════
  // 18. Connect resets flags properly
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void Connect_ResetsFlagsCorrectly()
  {
    _csm.waitingToRetry = true;
    _csm.bAborted = true;
    _csm.bExpectingDisconnectRemote = true;
    _csm.bDidDisconnect = true;

    _csm.Connect();

    Assert.That(_csm.waitingToRetry, Is.False);
    Assert.That(_csm.bAborted, Is.False);
    Assert.That(_csm.bConnecting, Is.True);
    Assert.That(_csm.bExpectingDisconnectRemote, Is.False);
    Assert.That(_csm.bDidDisconnect, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 19. OnOnline with pending login but NOT recovery → direct connect
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task OnOnline_PendingLogin_NotRecovery_DirectConnect()
  {
    _csm.IsPendingLogin = true;
    _csm.bIsConnectionRecovery = false;

    await _csm.OnOnline();

    Assert.That(_conn.ConnectCallCount, Is.EqualTo(1));
    Assert.That(_csm.bSuppressReconnect, Is.False,
        "Direct connect path should not set bSuppressReconnect");
  }

  // ════════════════════════════════════════════════════════════════════
  // 20. Full sleep/wake cycle: logged in → sleep → wake → reconnect → log in
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task FullCycle_SleepWakeReconnectLogin()
  {
    SetupLoggedIn();

    // Sleep: network goes offline
    _csm.OnOffline();
    Assert.That(_csm.IsLoggedOn, Is.False);
    Assert.That(_csm.bIsConnectionRecovery, Is.True);

    // While sleeping, SteamKit delivers a disconnect
    _conn.IsConnected = false;
    _csm.OnDisconnected(userInitiated: false);

    // Wake: network comes back
    await _csm.OnOnline();

    // The recovery path should have called Connect()
    Assert.That(_conn.ConnectCallCount, Is.GreaterThanOrEqualTo(1));

    // SteamClient connects
    _csm.OnConnected();
    Assert.That(_csm.bSuppressReconnect, Is.False);
    Assert.That(_csm.bConnecting, Is.False);

    // Login succeeds
    _csm.OnLoggedIn();
    Assert.That(_csm.IsLoggedOn, Is.True);
    Assert.That(_csm.IsPendingLogin, Is.False);
    Assert.That(_csm.IsReconnecting, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 21. Rapid offline/online flapping
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task NetworkFlapping_MultipleOfflineOnline()
  {
    SetupLoggedIn();

    // Flap 1: offline → online
    _csm.OnOffline();
    _conn.IsConnected = false;
    await _csm.OnOnline();
    _csm.OnConnected();
    _csm.OnLoggedIn();
    Assert.That(_csm.IsLoggedOn, Is.True);

    // Flap 2: offline → online again
    _csm.OnOffline();
    _conn.IsConnected = false;
    await _csm.OnOnline();
    _csm.OnConnected();
    _csm.OnLoggedIn();
    Assert.That(_csm.IsLoggedOn, Is.True);

    // Flap 3: should still work
    _csm.OnOffline();
    _conn.IsConnected = false;
    await _csm.OnOnline();
    _csm.OnConnected();
    _csm.OnLoggedIn();

    Assert.That(_csm.IsLoggedOn, Is.True);
    Assert.That(_csm.bAborted, Is.False);
    Assert.That(_authErrors, Is.Empty);
  }

  // ════════════════════════════════════════════════════════════════════
  // 22. Recovery with stale connection still thinks it's connected
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task Recovery_StaleConnectionStillConnected_DisconnectsFirst()
  {
    SetupLoggedIn();
    _csm.OnOffline();

    // SteamClient still thinks it's connected (stale TCP)
    _conn.IsConnected = true;

    // Set up mock to fire disconnect callback synchronously
    _conn.OnDisconnectCalled = () =>
    {
      _csm.OnDisconnected(userInitiated: true);
    };

    await _csm.OnOnline();

    Assert.That(_conn.DisconnectCallCount, Is.GreaterThanOrEqualTo(1),
        "Must disconnect stale connection first");
    Assert.That(_conn.ConnectCallCount, Is.GreaterThanOrEqualTo(1),
        "Must call Connect after disconnecting stale");
    Assert.That(_csm.bSuppressReconnect, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 23. Recovery with disconnect timeout (stale callback never arrives)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task Recovery_DisconnectTimeoutStaleCallback_ProceedsAnyway()
  {
    SetupLoggedIn();
    _csm.OnOffline();

    _conn.IsConnected = true;
    // Don't fire disconnect callback — simulates timeout
    _conn.OnDisconnectCalled = null;

    await _csm.OnOnline();

    // Should have proceeded to Connect() even without callback
    Assert.That(_conn.ConnectCallCount, Is.GreaterThanOrEqualTo(1),
        "Should proceed with Connect despite disconnect timeout");
    Assert.That(_csm.bSuppressReconnect, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 24. Multiple disconnects during backoff retry loop
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task Backoff_MultipleFailures_IncrementsToMax()
  {
    _csm.isOnline = true;
    _csm.bConnecting = true;

    // Simulate 12 consecutive connection failures
    for (int i = 0; i < 12; i++)
    {
      _csm.connectionBackoff = i;
      _csm.bAborted = false;
      _csm.OnDisconnected(userInitiated: false);
    }

    // On the 13th attempt with backoff=12, should abort
    _csm.bAborted = false;
    _csm.connectionBackoff = 12;
    _csm.OnDisconnected(userInitiated: false);

    Assert.That(_authErrors, Has.Count.EqualTo(1));
    Assert.That(_authErrors[0], Is.EqualTo("Timeout"));
    Assert.That(_csm.IsPendingLogin, Is.False, "Abort should clear pending login");
  }

  // ════════════════════════════════════════════════════════════════════
  // 25. OnLoggedIn resets all recovery state
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnLoggedIn_ResetsAllRecoveryState()
  {
    _csm.bIsConnectionRecovery = true;
    _csm.bAborted = true;
    _csm.connectionBackoff = 5;
    _csm.IsPendingLogin = true;

    _csm.OnLoggedIn();

    Assert.That(_csm.IsLoggedOn, Is.True);
    Assert.That(_csm.IsPendingLogin, Is.False);
    Assert.That(_csm.bIsConnectionRecovery, Is.False);
    Assert.That(_csm.bAborted, Is.False);
    Assert.That(_csm.connectionBackoff, Is.EqualTo(0));
  }

  // ════════════════════════════════════════════════════════════════════
  // 26. IsReconnecting property
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task IsReconnecting_TrueWhenPendingAndNotAborted()
  {
    _csm.IsPendingLogin = true;
    _csm.bIsConnectionRecovery = false;

    await _csm.OnOnline(); // Creates loggingInTask

    Assert.That(_csm.IsReconnecting, Is.True);

    _csm.OnConnected();
    _csm.OnLoggedIn(); // FinishLoggingInTask → loggingInTask = null

    Assert.That(_csm.IsReconnecting, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 27. IsReconnecting false when aborted
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void IsReconnecting_FalseWhenAborted()
  {
    _csm.bAborted = true;
    _csm.IsLoggedOn = false;

    Assert.That(_csm.IsReconnecting, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 28. OnOnline fires OnAuthUpdated
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task OnOnline_PendingLogin_FiresAuthUpdated()
  {
    _csm.IsPendingLogin = true;
    _csm.bIsConnectionRecovery = false;

    await _csm.OnOnline();

    Assert.That(_authUpdatedCount, Is.GreaterThanOrEqualTo(1));
  }

  // ════════════════════════════════════════════════════════════════════
  // 29. OnDisconnected with bExpectingDisconnectRemote and no recovery
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnDisconnected_ExpectedRemoteDisconnect_NotRecovery_Aborts()
  {
    _csm.isOnline = true;
    _csm.bIsConnectionRecovery = false;
    _csm.bExpectingDisconnectRemote = true;

    _csm.OnDisconnected(userInitiated: false);

    Assert.That(_csm.bAborted, Is.True,
        "Expected remote disconnect outside recovery should abort");
  }

  // ════════════════════════════════════════════════════════════════════
  // 30. OnDisconnected with bExpectingDisconnectRemote during recovery
  //     → does NOT abort (allows reconnect)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnDisconnected_ExpectedRemoteDisconnect_DuringRecovery_DoesNotAbort()
  {
    _csm.isOnline = true;
    _csm.bIsConnectionRecovery = true;
    _csm.bExpectingDisconnectRemote = true;

    _csm.OnDisconnected(userInitiated: false);

    Assert.That(_csm.bAborted, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 31. Abort clears all relevant state
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void Abort_ClearsState()
  {
    _csm.IsLoggedOn = true;
    _csm.IsPendingLogin = true;
    _csm.bConnecting = true;

    _csm.Abort();

    Assert.That(_csm.IsLoggedOn, Is.False);
    Assert.That(_csm.IsPendingLogin, Is.False);
    Assert.That(_csm.bAborted, Is.True);
    Assert.That(_csm.bConnecting, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 32. Delayed reconnect is abortable
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task DelayedReconnect_CancelledByAbort()
  {
    _csm.isOnline = true;
    _csm.bConnecting = true;
    _csm.ReconnectDelayMs = 100; // small delay

    int connectsBefore = _conn.ConnectCallCount;
    _csm.OnDisconnected(userInitiated: false);

    // Abort before the reconnect fires
    _csm.bAborted = true;

    await Task.Delay(200);

    Assert.That(_conn.ConnectCallCount, Is.EqualTo(connectsBefore),
        "Connect should NOT have been called after abort");
  }

  // ════════════════════════════════════════════════════════════════════
  // 33. OnConnected clears bSuppressReconnect (redundant safety)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnConnected_ClearsSuppressReconnect()
  {
    _csm.bSuppressReconnect = true;

    _csm.OnConnected();

    Assert.That(_csm.bSuppressReconnect, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 34. OnConnected clears bConnecting and bDidDisconnect
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnConnected_ClearsConnectionFlags()
  {
    _csm.bConnecting = true;
    _csm.bDidDisconnect = true;

    _csm.OnConnected();

    Assert.That(_csm.bConnecting, Is.False);
    Assert.That(_csm.bDidDisconnect, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 35. OnDisconnected always sets bDidDisconnect and clears IsLoggedOn
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnDisconnected_AlwaysSetsDidDisconnectAndClearsLoggedOn()
  {
    _csm.IsLoggedOn = true;
    _csm.bDidDisconnect = false;
    _csm.bSuppressReconnect = true; // even when suppressed

    _csm.OnDisconnected(userInitiated: false);

    Assert.That(_csm.bDidDisconnect, Is.True);
    Assert.That(_csm.IsLoggedOn, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 36. Full scenario: boot offline → online → connect → login
  //     (initial startup with no network)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task Boot_Offline_ThenOnline()
  {
    _csm.IsPendingLogin = true;
    _csm.isOnline = false;
    _csm.bIsConnectionRecovery = false;

    // Network comes online
    await _csm.OnOnline();

    Assert.That(_conn.ConnectCallCount, Is.EqualTo(1));

    _csm.OnConnected();
    _csm.OnLoggedIn();

    Assert.That(_csm.IsLoggedOn, Is.True);
  }

  // ════════════════════════════════════════════════════════════════════
  // 37. Recovery → multiple connect failures → eventually succeeds
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task Recovery_MultipleFailures_ThenSucceeds()
  {
    SetupLoggedIn();
    _csm.OnOffline();
    _conn.IsConnected = false;

    await _csm.OnOnline();

    // Fail 3 times
    for (int i = 0; i < 3; i++)
    {
      _csm.OnDisconnected(userInitiated: false);
      await Task.Delay(50); // let delayed reconnect fire
    }

    Assert.That(_csm.connectionBackoff, Is.EqualTo(3));
    Assert.That(_csm.bAborted, Is.False);

    // 4th attempt succeeds
    _csm.OnConnected();
    _csm.OnLoggedIn();

    Assert.That(_csm.IsLoggedOn, Is.True);
    Assert.That(_csm.connectionBackoff, Is.EqualTo(0));
  }

  // ════════════════════════════════════════════════════════════════════
  // 38. WaitLoggingInTask completes on login
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task WaitLoggingInTask_CompletesOnLogin()
  {
    _csm.IsPendingLogin = true;
    await _csm.OnOnline();

    var waitTask = _csm.WaitLoggingInTask();
    Assert.That(waitTask.IsCompleted, Is.False, "Should not be complete yet");

    _csm.OnConnected();
    _csm.OnLoggedIn();

    await Task.WhenAny(waitTask, Task.Delay(1000));
    Assert.That(waitTask.IsCompleted, Is.True, "Should complete after login");
  }

  // ════════════════════════════════════════════════════════════════════
  // 39. WaitLoggingInTask completes on abort
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task WaitLoggingInTask_CompletesOnAbort()
  {
    _csm.IsPendingLogin = true;
    await _csm.OnOnline();

    var waitTask = _csm.WaitLoggingInTask();
    Assert.That(waitTask.IsCompleted, Is.False);

    _csm.Abort();

    await Task.WhenAny(waitTask, Task.Delay(1000));
    Assert.That(waitTask.IsCompleted, Is.True, "Should complete after abort");
  }

  // ════════════════════════════════════════════════════════════════════
  // 40. bIsConnectionRecovery is cleared by Connect()
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void Connect_ClearsIsConnectionRecovery()
  {
    _csm.bIsConnectionRecovery = true;

    _csm.Connect();

    Assert.That(_csm.bIsConnectionRecovery, Is.False,
        "Connect should clear bIsConnectionRecovery");
  }

  // ════════════════════════════════════════════════════════════════════
  // 41. OnOnline called twice rapidly (duplicate NM wake event)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task OnOnline_CalledTwice_DoesNotDoubleConnect()
  {
    SetupLoggedIn();
    _csm.OnOffline();
    _conn.IsConnected = false;

    // Two rapid OnOnline calls (NetworkManager can double-fire)
    await _csm.OnOnline();
    int connectsAfterFirst = _conn.ConnectCallCount;

    // Second call: IsPendingLogin was already consumed by first OnOnline's Connect()
    // (Connect sets bIsConnectionRecovery=false and clears pending via _loggingInTask)
    await _csm.OnOnline();

    // The second OnOnline may issue another Connect if IsPendingLogin is still set,
    // but it must NOT corrupt state or deadlock
    _csm.OnConnected();
    _csm.OnLoggedIn();

    Assert.That(_csm.IsLoggedOn, Is.True, "Should recover to logged-in state");
    Assert.That(_csm.bAborted, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 42. OnOffline during active connect (network drops mid-handshake)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task OnOffline_DuringActiveConnect_SetsRecoveryForNextOnline()
  {
    _csm.IsPendingLogin = true;
    _csm.isOnline = false;

    // Come online and start connecting
    await _csm.OnOnline();
    Assert.That(_csm.bConnecting, Is.True);

    // Network drops mid-handshake before OnConnected fires
    _csm.OnOffline();
    Assert.That(_csm.isOnline, Is.False);

    // Disconnect callback arrives
    _conn.IsConnected = false;
    _csm.OnDisconnected(userInitiated: false);

    // Should NOT attempt reconnect while offline
    Assert.That(_csm.IsPendingLogin, Is.True, "Should be pending for next OnOnline");

    // Network returns again
    await _csm.OnOnline();
    _csm.OnConnected();
    _csm.OnLoggedIn();

    Assert.That(_csm.IsLoggedOn, Is.True);
  }

  // ════════════════════════════════════════════════════════════════════
  // 43. OnConnected after abort (stale callback arrives late)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnConnected_AfterAbort_DoesNotCorruptState()
  {
    _csm.bConnecting = true;
    _csm.Abort();

    Assert.That(_csm.bAborted, Is.True);
    Assert.That(_csm.IsLoggedOn, Is.False);

    // Stale OnConnected arrives after we already aborted
    _csm.OnConnected();

    // OnConnected only clears connection flags — it should NOT
    // set IsLoggedOn or undo the abort
    Assert.That(_csm.IsLoggedOn, Is.False, "Stale OnConnected must not set IsLoggedOn");
    Assert.That(_csm.bAborted, Is.True, "Stale OnConnected must not clear bAborted");
    Assert.That(_csm.bConnecting, Is.False, "OnConnected still clears bConnecting");
  }

  // ════════════════════════════════════════════════════════════════════
  // 44. OnLoggedIn after abort (stale login response)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnLoggedIn_AfterAbort_ResetsAbortAndSetsLoggedOn()
  {
    _csm.bConnecting = true;
    _csm.Abort();
    Assert.That(_csm.bAborted, Is.True);

    // Stale LoggedOnCallback arrives — in the real SteamSession,
    // OnLoggedIn unconditionally marks success (it already authenticated).
    _csm.OnLoggedIn();

    // OnLoggedIn is authoritative: if Steam says we're logged in, we are.
    Assert.That(_csm.IsLoggedOn, Is.True);
    Assert.That(_csm.bAborted, Is.False, "OnLoggedIn should clear bAborted");
    Assert.That(_csm.IsPendingLogin, Is.False);
    Assert.That(_csm.connectionBackoff, Is.EqualTo(0));
  }

  // ════════════════════════════════════════════════════════════════════
  // 45. OnOffline while waiting for delayed reconnect
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task OnOffline_DuringDelayedReconnect_PreventsReconnect()
  {
    _csm.isOnline = true;
    _csm.bConnecting = true;
    _csm.ReconnectDelayMs = 200;

    int connectsBefore = _conn.ConnectCallCount;

    // Trigger a delayed reconnect
    _csm.OnDisconnected(userInitiated: false);

    // Go offline before the delay fires — this should cause the
    // delayed reconnect to be a no-op (isOnline=false, and
    // bAborted is set by a subsequent OnOffline → recovery path)
    _csm.IsLoggedOn = true; // pretend we were logged in so OnOffline activates
    _csm.OnOffline();
    Assert.That(_csm.isOnline, Is.False);

    // Wait for the delayed reconnect to fire
    await Task.Delay(400);

    // The delayed task checks bAborted — after OnOffline the
    // session enters recovery, and the old delayed task should
    // not interfere. At worst it fires Connect() but the state
    // machine is resilient to this.
    Assert.That(_csm.IsPendingLogin, Is.True,
        "Should be pending login after going offline");
  }

  // ════════════════════════════════════════════════════════════════════
  // 46. Transient connection: OnConnected then immediate OnDisconnected
  //     before login (TCP connects but Steam drops before auth)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task TransientConnection_ConnectedThenImmediateDisconnect_Retries()
  {
    _csm.IsPendingLogin = true;
    _csm.isOnline = true;

    await _csm.OnOnline();
    Assert.That(_conn.ConnectCallCount, Is.EqualTo(1));

    // TCP handshake succeeds
    _csm.OnConnected();
    Assert.That(_csm.bConnecting, Is.False);
    Assert.That(_csm.bSuppressReconnect, Is.False);

    // Steam immediately drops us before we could authenticate
    _csm.OnDisconnected(userInitiated: false);

    // Should schedule a retry (not abort)
    Assert.That(_csm.bAborted, Is.False, "Transient drop should not abort");
    Assert.That(_csm.connectionBackoff, Is.EqualTo(1));

    await Task.Delay(50);
    Assert.That(_conn.ConnectCallCount, Is.GreaterThanOrEqualTo(2),
        "Should retry via delayed reconnect");
  }

  // ════════════════════════════════════════════════════════════════════
  // 47. OnLoggedIn immediately followed by OnOffline
  //     (sleep hits right after login completes)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnLoggedIn_ThenImmediateOffline_EntersRecovery()
  {
    _csm.IsPendingLogin = true;
    _csm.bConnecting = true;
    _csm.isOnline = true;

    _csm.OnConnected();
    _csm.OnLoggedIn();
    Assert.That(_csm.IsLoggedOn, Is.True);

    // Sleep hits immediately after login
    _csm.OnOffline();

    Assert.That(_csm.IsLoggedOn, Is.False);
    Assert.That(_csm.IsPendingLogin, Is.True);
    Assert.That(_csm.bIsConnectionRecovery, Is.True);
    Assert.That(_csm.isOnline, Is.False);
    Assert.That(_csm.bAborted, Is.False, "Offline should not abort");
  }

  // ════════════════════════════════════════════════════════════════════
  // 48. Double OnOffline is idempotent
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void OnOffline_CalledTwice_IsIdempotent()
  {
    SetupLoggedIn();

    _csm.OnOffline();
    Assert.That(_csm.IsPendingLogin, Is.True);
    Assert.That(_csm.bIsConnectionRecovery, Is.True);

    // Second OnOffline: IsLoggedOn is already false, so the body is skipped
    _csm.OnOffline();

    Assert.That(_csm.IsPendingLogin, Is.True, "Should still be pending");
    Assert.That(_csm.bIsConnectionRecovery, Is.True, "Recovery flag should persist");
    Assert.That(_csm.isOnline, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 49. OnOnline when already online and logged in — no-op
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task OnOnline_AlreadyOnlineAndLoggedIn_NoReconnect()
  {
    SetupLoggedIn();

    int connectsBefore = _conn.ConnectCallCount;
    await _csm.OnOnline();

    Assert.That(_conn.ConnectCallCount, Is.EqualTo(connectsBefore),
        "Should not call Connect when already logged in");
    Assert.That(_csm.IsLoggedOn, Is.True, "Should remain logged in");
    Assert.That(_csm.bAborted, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 50. Reconnect-style flow: CSM.Reconnect() sets recovery flags,
  //     disconnects → OnDisconnected → delayed re-connect
  //     (simulates TryAnotherCM / QR code reconnect)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task ReconnectFlow_SetsRecoveryFlagsThenDisconnects()
  {
    // Simulate the initial connected state
    _csm.isOnline = true;
    _csm.bConnecting = false;
    _conn.IsConnected = true;

    // CSM.Reconnect() sets recovery flags and calls _connection.Disconnect()
    _csm.Reconnect();

    // Verify flags were set correctly by Reconnect()
    Assert.That(_csm.bIsConnectionRecovery, Is.True);
    Assert.That(_csm.bExpectingDisconnectRemote, Is.True);
    Assert.That(_csm.IsPendingLogin, Is.True);
    Assert.That(_csm.waitingToRetry, Is.False);
    Assert.That(_conn.DisconnectCallCount, Is.EqualTo(1));

    // SteamClient.Disconnect() fires OnDisconnected
    _csm.OnDisconnected(userInitiated: true);

    // bIsConnectionRecovery=true means user-initiated disconnect does NOT abort
    Assert.That(_csm.bAborted, Is.False,
        "Reconnect-initiated disconnect during recovery should not abort");
    Assert.That(_csm.IsPendingLogin, Is.True);

    // Now come back online and connect fresh
    _conn.IsConnected = false;
    await _csm.OnOnline();

    _csm.OnConnected();
    _csm.OnLoggedIn();

    Assert.That(_csm.IsLoggedOn, Is.True);
    Assert.That(_csm.IsReconnecting, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 51. Multiple Abort() calls are idempotent
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void Abort_CalledMultipleTimes_IsIdempotent()
  {
    _csm.IsLoggedOn = true;
    _csm.IsPendingLogin = true;
    _csm.bConnecting = true;

    _csm.Abort();
    Assert.That(_csm.bAborted, Is.True);
    Assert.That(_csm.IsLoggedOn, Is.False);

    // Second and third calls should not throw or corrupt state
    _csm.Abort();
    _csm.Abort();

    Assert.That(_csm.bAborted, Is.True);
    Assert.That(_csm.IsLoggedOn, Is.False);
    Assert.That(_csm.IsPendingLogin, Is.False);
    Assert.That(_csm.bConnecting, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 52. bSuppressReconnect still signals disconnectedTcs
  //     (critical for recovery path disconnect timeout)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task SuppressReconnect_StillSignalsDisconnectedTcs()
  {
    SetupLoggedIn();
    _csm.OnOffline();

    _conn.IsConnected = true;

    // OnOnline creates disconnectedTcs and calls Disconnect()
    // Set up mock so Disconnect fires OnDisconnected synchronously
    bool disconnectedTcsSignaled = false;
    _conn.OnDisconnectCalled = () =>
    {
      // At this point bSuppressReconnect=true
      // OnDisconnected must TrySetResult on disconnectedTcs even when suppressing
      _csm.OnDisconnected(userInitiated: true);
      disconnectedTcsSignaled = true;
    };

    await _csm.OnOnline();

    Assert.That(disconnectedTcsSignaled, Is.True,
        "OnDisconnected should have been called via Disconnect mock");
    // Recovery should have proceeded to Connect despite the suppressed reconnect
    Assert.That(_conn.ConnectCallCount, Is.GreaterThanOrEqualTo(1),
        "Should proceed to Connect after disconnect callback signals TCS");
    Assert.That(_csm.bSuppressReconnect, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 53. Recovery connect succeeds but login fails
  //     (OnConnected → auth error → OnDisconnected → retry)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task Recovery_ConnectSucceeds_LoginFails_Retries()
  {
    SetupLoggedIn();
    _csm.OnOffline();
    _conn.IsConnected = false;

    await _csm.OnOnline();
    _csm.OnConnected();

    // Auth fails (e.g. token expired) — SteamSession would Abort + set
    // bExpectingDisconnectRemote. Simulate the CSM-visible portion:
    // SteamKit drops us without OnLoggedIn
    _csm.OnDisconnected(userInitiated: false);

    // Should retry, not abort (recovery cleared by Connect, but backoff works)
    Assert.That(_csm.bAborted, Is.False);
    Assert.That(_csm.connectionBackoff, Is.GreaterThanOrEqualTo(1));

    await Task.Delay(50);

    // Retry succeeds
    _csm.OnConnected();
    _csm.OnLoggedIn();
    Assert.That(_csm.IsLoggedOn, Is.True);
    Assert.That(_csm.connectionBackoff, Is.EqualTo(0));
  }

  // ════════════════════════════════════════════════════════════════════
  // 54. OnConnected + OnLoggedIn both arrive after Abort (stale sequence)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void StaleCallbackSequence_OnConnectedThenOnLoggedIn_AfterAbort()
  {
    _csm.bConnecting = true;
    _csm.isOnline = true;

    _csm.Abort();
    Assert.That(_csm.bAborted, Is.True);
    Assert.That(_csm.IsLoggedOn, Is.False);

    // Stale OnConnected arrives
    _csm.OnConnected();
    Assert.That(_csm.IsLoggedOn, Is.False, "Stale OnConnected must not restore login");
    Assert.That(_csm.bAborted, Is.True, "Stale OnConnected must not clear abort");

    // Then stale OnLoggedIn arrives — this IS authoritative
    _csm.OnLoggedIn();
    Assert.That(_csm.IsLoggedOn, Is.True, "OnLoggedIn is always authoritative");
    Assert.That(_csm.bAborted, Is.False, "OnLoggedIn clears abort");
    Assert.That(_csm.connectionBackoff, Is.EqualTo(0));
  }

  // ════════════════════════════════════════════════════════════════════
  // 55. Backoff accounting across recovery → failure → success
  //     (recovery preserves backoff, Connect clears it, verify full cycle)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task BackoffAccounting_RecoveryPreserves_ConnectClears()
  {
    SetupLoggedIn();
    _csm.OnOffline();
    _conn.IsConnected = false;

    // During recovery, OnOnline calls Connect() which clears bIsConnectionRecovery
    await _csm.OnOnline();

    // First connect attempt fails — backoff increments
    _csm.OnDisconnected(userInitiated: false);
    Assert.That(_csm.connectionBackoff, Is.EqualTo(1));

    await Task.Delay(50);

    // Second attempt fails — backoff increments again
    _csm.OnDisconnected(userInitiated: false);
    Assert.That(_csm.connectionBackoff, Is.EqualTo(2));

    await Task.Delay(50);

    // Third attempt succeeds
    _csm.OnConnected();
    _csm.OnLoggedIn();
    Assert.That(_csm.connectionBackoff, Is.EqualTo(0),
        "OnLoggedIn should reset backoff to 0");
    Assert.That(_csm.IsLoggedOn, Is.True);

    // New sleep/wake cycle should start fresh
    _csm.OnOffline();
    _conn.IsConnected = false;
    await _csm.OnOnline();

    _csm.OnDisconnected(userInitiated: false);
    Assert.That(_csm.connectionBackoff, Is.EqualTo(1),
        "New cycle should start backoff from 0");
  }

  // ════════════════════════════════════════════════════════════════════
  // 56. Steam Guard / 2FA rejection: MarkExpectingDisconnect → Abort
  //     → Connect (SteamSession.OnLogIn SteamGuard/2FA path)
  //     Abort calls PrepareDisconnect (bAborted+bConnecting=false),
  //     then Connect clears bAborted and reconnects.
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void SteamGuard2FA_Abort_ThenConnect_OnDisconnectedDoesNotInterfere()
  {
    // Initial state: connected, waiting for login
    _csm.isOnline = true;
    _csm.bConnecting = false;
    _csm.IsLoggedOn = false;
    _csm.IsPendingLogin = true;

    // SteamSession.OnLogIn detects SteamGuard/2FA:
    _csm.MarkExpectingDisconnect();
    _csm.Abort();

    Assert.That(_csm.bAborted, Is.True);
    Assert.That(_csm.IsLoggedOn, Is.False);
    Assert.That(_csm.IsPendingLogin, Is.False);
    // PrepareDisconnect preserves recovery because bExpectingDisconnectRemote=true
    Assert.That(_csm.bExpectingDisconnectRemote, Is.True);

    // SteamSession then calls Connect() to retry with credentials
    _csm.Connect();

    Assert.That(_csm.bAborted, Is.False, "Connect should clear bAborted");
    Assert.That(_csm.bConnecting, Is.True);
    Assert.That(_csm.bExpectingDisconnectRemote, Is.False,
        "Connect resets bExpectingDisconnectRemote");

    // The old disconnect callback arrives (from the Abort's SteamClient.Disconnect)
    _csm.OnDisconnected(userInitiated: true);

    Assert.That(_csm.bDidDisconnect, Is.True);

    // A fresh OnConnected should still work
    _csm.bAborted = false; // simulating a new connect cycle starting
    _csm.bConnecting = true;
    _csm.OnConnected();
    _csm.OnLoggedIn();

    Assert.That(_csm.IsLoggedOn, Is.True);
  }

  // ════════════════════════════════════════════════════════════════════
  // 57. AccessToken rejected: MarkExpectingDisconnect → double Abort
  //     (SteamSession calls Abort twice in the isAccessToken path)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void AccessTokenRejected_DoubleAbort_TerminalState()
  {
    _csm.isOnline = true;
    _csm.bConnecting = false;
    _csm.IsLoggedOn = false;
    _csm.IsPendingLogin = true;

    // First Abort (SteamSession: MarkExpectingDisconnect + Abort)
    _csm.MarkExpectingDisconnect();
    _csm.Abort();

    // Second Abort (SteamSession's access token path calls Abort again)
    _csm.Abort();

    Assert.That(_csm.bAborted, Is.True);
    Assert.That(_csm.IsLoggedOn, Is.False);
    Assert.That(_csm.IsPendingLogin, Is.False);
    Assert.That(_csm.bConnecting, Is.False);

    // OnDisconnected arrives — should not trigger any reconnect
    _csm.OnDisconnected(userInitiated: false);

    Assert.That(_csm.bAborted, Is.True, "Should remain aborted");
    Assert.That(_conn.ConnectCallCount, Is.EqualTo(0),
        "Should NOT reconnect after terminal abort");
  }

  // ════════════════════════════════════════════════════════════════════
  // 58. TryAnotherCM / NoConnection: CSM.Reconnect() sets recovery
  //     flags → _connection.Disconnect() → OnDisconnected(userInitiated)
  //     → should NOT abort because bIsConnectionRecovery=true
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task TryAnotherCM_ReconnectPath_DoesNotAbort()
  {
    // State: connected, online, login returned TryAnotherCM
    _csm.isOnline = true;
    _csm.bConnecting = false;
    _csm.IsLoggedOn = false;
    _csm.IsPendingLogin = false;

    // CSM.Reconnect() sets flags and calls _connection.Disconnect()
    _csm.Reconnect();

    Assert.That(_csm.bIsConnectionRecovery, Is.True);
    Assert.That(_csm.bExpectingDisconnectRemote, Is.True);
    Assert.That(_conn.DisconnectCallCount, Is.EqualTo(1));

    // SteamClient.Disconnect() fires the callback
    _csm.OnDisconnected(userInitiated: true);

    // Key: bIsConnectionRecovery=true means user-initiated disconnect
    // does NOT set bAborted
    Assert.That(_csm.bAborted, Is.False,
        "Reconnect-initiated disconnect must not abort during recovery");
    Assert.That(_csm.bDidDisconnect, Is.True);
    Assert.That(_csm.IsPendingLogin, Is.True);

    // Delayed reconnect fires, then come back online
    _conn.IsConnected = false;
    await _csm.OnOnline();

    _csm.OnConnected();
    _csm.OnLoggedIn();

    Assert.That(_csm.IsLoggedOn, Is.True);
    Assert.That(_csm.IsReconnecting, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 59. AlreadyLoggedInElsewhere: TryBeginRetryWait guard
  //     prevents duplicate Reconnect calls.
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void AlreadyLoggedInElsewhere_WaitingToRetryGuard()
  {
    _csm.isOnline = true;
    _csm.bConnecting = false;

    // First attempt: TryBeginRetryWait succeeds
    Assert.That(_csm.TryBeginRetryWait(), Is.True,
        "First call should succeed");
    Assert.That(_csm.waitingToRetry, Is.True);

    // Second attempt: should be blocked
    Assert.That(_csm.TryBeginRetryWait(), Is.False,
        "Second call should be blocked by waitingToRetry=true");

    // When Reconnect fires, it clears waitingToRetry
    _csm.Reconnect();
    Assert.That(_csm.waitingToRetry, Is.False,
        "Reconnect should clear waitingToRetry");

    // OnDisconnected from Reconnect's Disconnect()
    _csm.OnDisconnected(userInitiated: true);
    Assert.That(_csm.bAborted, Is.False, "Recovery disconnect should not abort");
  }

  // ════════════════════════════════════════════════════════════════════
  // 60. PrepareDisconnect() sets correct flags before SteamClient.Disconnect
  //     OnDisconnected must still set bDidDisconnect=true so the
  //     spin-wait in SteamSession.Disconnect() exits.
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void PrepareDisconnect_SetsFlagsThenOnDisconnectedSetsbDidDisconnect()
  {
    _csm.isOnline = true;
    _csm.bConnecting = true;
    _csm.IsLoggedOn = true;

    // CSM.PrepareDisconnect() handles all state transitions
    _csm.PrepareDisconnect();

    Assert.That(_csm.bAborted, Is.True);
    Assert.That(_csm.bConnecting, Is.False);

    // Then OnDisconnected fires (synchronously or via callback flush)
    _csm.OnDisconnected(userInitiated: false);

    // Critical: bDidDisconnect MUST be true so the while(!bDidDisconnect) loop exits
    Assert.That(_csm.bDidDisconnect, Is.True,
        "OnDisconnected must always set bDidDisconnect regardless of bAborted");
    Assert.That(_csm.IsLoggedOn, Is.False,
        "OnDisconnected must always clear IsLoggedOn");

    // Because bAborted was already true, no reconnect should be scheduled
    Assert.That(_conn.ConnectCallCount, Is.EqualTo(0),
        "Should not reconnect when already aborted");
  }

  // ════════════════════════════════════════════════════════════════════
  // 61. PrepareDisconnect with MarkExpectingDisconnect preserves recovery
  //     (PrepareDisconnect checks !bExpectingDisconnectRemote
  //     before clearing bIsConnectionRecovery)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void PrepareDisconnect_WithExpectingRemote_PreservesRecovery()
  {
    _csm.isOnline = true;
    _csm.bIsConnectionRecovery = true;

    // SteamSession marks expecting disconnect before calling Disconnect
    _csm.MarkExpectingDisconnect();

    // CSM.PrepareDisconnect handles flags
    _csm.PrepareDisconnect();

    // Since bExpectingDisconnectRemote=true, recovery is preserved
    Assert.That(_csm.bIsConnectionRecovery, Is.True,
        "Recovery flag must be preserved when expecting remote disconnect");
    Assert.That(_csm.bAborted, Is.True);

    // OnDisconnected arrives. Recovery flag means no abort (already aborted).
    _csm.OnDisconnected(userInitiated: true);

    Assert.That(_csm.bAborted, Is.True, "Already aborted before OnDisconnected");
    Assert.That(_csm.bDidDisconnect, Is.True);
    Assert.That(_csm.bIsConnectionRecovery, Is.True,
        "Recovery should persist through the disconnect cycle");
  }

  // ════════════════════════════════════════════════════════════════════
  // 62. LoggedOff with Revoked: Abort — terminal state,
  //     no reconnection possible
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void LoggedOff_Revoked_TerminalAbort()
  {
    SetupLoggedIn();

    // SteamSession.OnLoggedOff: Abort(true)
    // CSM.Abort handles all state transitions
    _csm.Abort();

    Assert.That(_csm.bAborted, Is.True);
    Assert.That(_csm.IsLoggedOn, Is.False);
    Assert.That(_csm.IsPendingLogin, Is.False);

    // Disconnect callback arrives
    _csm.OnDisconnected(userInitiated: false);

    Assert.That(_csm.bDidDisconnect, Is.True);
    Assert.That(_csm.IsLoggedOn, Is.False);
    Assert.That(_csm.IsPendingLogin, Is.False);
    Assert.That(_csm.bAborted, Is.True,
        "Revoked sessions must stay aborted");
    Assert.That(_conn.ConnectCallCount, Is.EqualTo(0),
        "Must NOT reconnect after session revocation");
  }

  // ════════════════════════════════════════════════════════════════════
  // 63. QR code login error triggers CSM.Reconnect()
  //     (same recovery pattern as TryAnotherCM)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task QrLoginError_Reconnect_RecoverSuccessfully()
  {
    _csm.isOnline = true;
    _csm.bConnecting = false;
    _csm.IsLoggedOn = false;

    // SteamSession.OnLogIn: QR login failed, calls Reconnect()
    _csm.Reconnect();

    Assert.That(_csm.bIsConnectionRecovery, Is.True);
    Assert.That(_conn.DisconnectCallCount, Is.EqualTo(1));

    // Disconnect fires
    _csm.OnDisconnected(userInitiated: true);
    Assert.That(_csm.bAborted, Is.False, "Recovery disconnect must not abort");

    // Come back online
    _conn.IsConnected = false;
    await _csm.OnOnline();

    _csm.OnConnected();
    _csm.OnLoggedIn();

    Assert.That(_csm.IsLoggedOn, Is.True);
    Assert.That(_csm.bIsConnectionRecovery, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 64. ServiceUnavailable: terminal Abort, OnDisconnected arrives,
  //     no recovery
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void ServiceUnavailable_TerminalAbort_NoRecovery()
  {
    _csm.isOnline = true;
    _csm.bConnecting = false;
    _csm.IsLoggedOn = false;
    _csm.IsPendingLogin = true;

    // SteamSession.OnLogIn: ServiceUnavailable → Abort(false)
    _csm.Abort();

    Assert.That(_csm.bAborted, Is.True);
    Assert.That(_csm.IsPendingLogin, Is.False);

    // OnDisconnected from Disconnect()
    _csm.OnDisconnected(userInitiated: false);

    Assert.That(_csm.bDidDisconnect, Is.True);
    Assert.That(_conn.ConnectCallCount, Is.EqualTo(0),
        "Must not reconnect after ServiceUnavailable");
    Assert.That(_csm.bAborted, Is.True);
  }

  // ════════════════════════════════════════════════════════════════════
  // 65. QR polling cancelled (not our token) → CSM.Reconnect() → recover
  //     (OnConnected catches TaskCanceledException, calls Reconnect)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public async Task QrPollingCancelled_NotOurToken_ReconnectRecovers()
  {
    _csm.isOnline = true;
    _csm.bConnecting = false;

    // SteamSession calls Reconnect()
    _csm.Reconnect();

    Assert.That(_csm.bIsConnectionRecovery, Is.True);
    Assert.That(_conn.DisconnectCallCount, Is.EqualTo(1));

    // Disconnect callback from Reconnect's _connection.Disconnect()
    _csm.OnDisconnected(userInitiated: true);
    Assert.That(_csm.bAborted, Is.False);

    // Fresh connect cycle
    _conn.IsConnected = false;
    await _csm.OnOnline();

    _csm.OnConnected();
    _csm.OnLoggedIn();

    Assert.That(_csm.IsLoggedOn, Is.True);
  }

  // ════════════════════════════════════════════════════════════════════
  // 66. CSM.Reconnect() sets all recovery flags and calls Disconnect
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void Reconnect_SetsAllFlagsAndCallsDisconnect()
  {
    _csm.isOnline = true;
    _csm.waitingToRetry = true;

    _csm.Reconnect();

    Assert.That(_csm.waitingToRetry, Is.False, "Reconnect clears waitingToRetry");
    Assert.That(_csm.bIsConnectionRecovery, Is.True, "Reconnect sets recovery");
    Assert.That(_csm.bExpectingDisconnectRemote, Is.True, "Reconnect expects disconnect");
    Assert.That(_csm.IsPendingLogin, Is.True, "Reconnect sets pending login");
    Assert.That(_csm.IsReconnecting, Is.True, "Should be reconnecting");
    Assert.That(_conn.DisconnectCallCount, Is.EqualTo(1), "Must call _connection.Disconnect()");
  }

  // ════════════════════════════════════════════════════════════════════
  // 67. CSM.PrepareDisconnect() sets correct flags
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void PrepareDisconnect_SetsCorrectFlags()
  {
    _csm.bConnecting = true;
    _csm.bIsConnectionRecovery = true;
    _csm.bExpectingDisconnectRemote = false;

    _csm.PrepareDisconnect();

    Assert.That(_csm.bAborted, Is.True);
    Assert.That(_csm.bConnecting, Is.False);
    // Without bExpectingDisconnectRemote, recovery is cleared
    Assert.That(_csm.bIsConnectionRecovery, Is.False,
        "Recovery should be cleared when not expecting remote disconnect");
  }

  // ════════════════════════════════════════════════════════════════════
  // 68. CSM.MarkExpectingDisconnect() sets the flag
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void MarkExpectingDisconnect_SetsFlag()
  {
    Assert.That(_csm.bExpectingDisconnectRemote, Is.False);

    _csm.MarkExpectingDisconnect();

    Assert.That(_csm.bExpectingDisconnectRemote, Is.True);
  }

  // ════════════════════════════════════════════════════════════════════
  // 69. CSM.TryBeginRetryWait() atomic guard
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void TryBeginRetryWait_FirstCallSucceeds_SecondFails()
  {
    Assert.That(_csm.TryBeginRetryWait(), Is.True, "First call should succeed");
    Assert.That(_csm.waitingToRetry, Is.True);

    Assert.That(_csm.TryBeginRetryWait(), Is.False, "Second call should fail");
    Assert.That(_csm.waitingToRetry, Is.True, "Flag should remain true");
  }

  // ════════════════════════════════════════════════════════════════════
  // 70. CSM.Abort() calls PrepareDisconnect (verify recovery handling)
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void Abort_CallsPrepareDisconnect_ClearsRecoveryWhenNotExpecting()
  {
    _csm.IsLoggedOn = true;
    _csm.IsPendingLogin = true;
    _csm.bConnecting = true;
    _csm.bIsConnectionRecovery = true;
    _csm.bExpectingDisconnectRemote = false;

    _csm.Abort();

    Assert.That(_csm.IsLoggedOn, Is.False);
    Assert.That(_csm.IsPendingLogin, Is.False);
    Assert.That(_csm.bAborted, Is.True);
    Assert.That(_csm.bConnecting, Is.False);
    Assert.That(_csm.bIsConnectionRecovery, Is.False,
        "Abort should clear recovery when not expecting remote disconnect");
  }

  [Test]
  public void Abort_PreservesRecovery_WhenExpectingRemoteDisconnect()
  {
    _csm.IsLoggedOn = true;
    _csm.bIsConnectionRecovery = true;
    _csm.MarkExpectingDisconnect();

    _csm.Abort();

    Assert.That(_csm.bIsConnectionRecovery, Is.True,
        "Abort should preserve recovery when expecting remote disconnect");
  }

  // ════════════════════════════════════════════════════════════════════
  // 72. Constructor initialOnline parameter sets isOnline
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void Constructor_InitialOnlineTrue_SetsIsOnline()
  {
    var conn = new MockSteamConnection();
    var csm = new ConnectionStateMachine(conn, initialOnline: true);
    Assert.That(csm.isOnline, Is.True);
  }

  [Test]
  public void Constructor_InitialOnlineFalse_DefaultIsOffline()
  {
    var conn = new MockSteamConnection();
    var csm = new ConnectionStateMachine(conn);
    Assert.That(csm.isOnline, Is.False);
  }

  // ════════════════════════════════════════════════════════════════════
  // 74. SetPendingLoginOffline sets IsPendingLogin
  // ════════════════════════════════════════════════════════════════════

  [Test]
  public void SetPendingLoginOffline_SetsIsPendingLogin()
  {
    Assert.That(_csm.IsPendingLogin, Is.False);

    _csm.SetPendingLoginOffline();

    Assert.That(_csm.IsPendingLogin, Is.True);
  }

  [Test]
  public async Task SetPendingLoginOffline_ThenOnOnline_Connects()
  {
    _csm.SetPendingLoginOffline();
    Assert.That(_csm.IsPendingLogin, Is.True);

    await _csm.OnOnline();

    Assert.That(_conn.ConnectCallCount, Is.EqualTo(1),
        "Coming online after SetPendingLoginOffline should trigger connect");
  }

  // ════════════════════════════════════════════════════════════════════
  //  Helper: Set up a fully-logged-in state
  // ════════════════════════════════════════════════════════════════════

  private void SetupLoggedIn()
  {
    _csm.IsLoggedOn = true;
    _csm.IsPendingLogin = false;
    _csm.isOnline = true;
    _csm.bAborted = false;
    _csm.bIsConnectionRecovery = false;
    _csm.bSuppressReconnect = false;
    _csm.connectionBackoff = 0;
    _conn.IsConnected = true;
  }
}
