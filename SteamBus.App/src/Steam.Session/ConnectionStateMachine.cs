using System;
using System.Threading.Tasks;

namespace Steam.Session;

/// <summary>
/// Pure state-machine that governs SteamClient connection recovery.
/// All side-effects go through <see cref="ISteamConnection"/> and the
/// event delegates, making the logic fully testable without SteamKit2.
/// </summary>
public class ConnectionStateMachine
{
  // ── Dependencies ──────────────────────────────────────────────────
  private readonly ISteamConnection _connection;

  // ── Observable events ─────────────────────────────────────────────
  /// <summary>Fired whenever the UI should refresh auth state.</summary>
  public Action? OnAuthUpdated;
  /// <summary>Fired on unrecoverable auth failure (carries error id).</summary>
  public Action<string>? OnAuthError;
  /// <summary>
  /// If set, called during non-recovery OnOnline instead of <see cref="Connect"/>.
  /// This allows SteamSession to run its full Login() flow (auth + connect).
  /// When null (e.g. in tests), falls back to <see cref="Connect"/>.
  /// </summary>
  public Func<Task>? OnLoginRequested;

  // ── Public state ──────────────────────────────────────────────────
  public bool IsLoggedOn { get; internal set; }
  public bool IsPendingLogin { get; internal set; }
  public bool IsReconnecting => !bAborted && !IsLoggedOn && _loggingInTask != null;

  // ── Internal flags ────────────────────────────────────────────────
  internal bool bConnecting;
  internal bool bAborted;
  internal bool bExpectingDisconnectRemote;
  internal bool bDidDisconnect;
  internal bool bIsConnectionRecovery;
  internal bool bSuppressReconnect;
  internal bool isOnline;
  internal bool waitingToRetry;
  internal int connectionBackoff;

  private TaskCompletionSource? disconnectedTcs;
  private TaskCompletionSource? _loggingInTask;

  // Settable in tests to avoid actual delays
  internal int ReconnectDelayMs { get; set; } = 3000;

  // ── Constructor ───────────────────────────────────────────────────
  public ConnectionStateMachine(ISteamConnection connection, bool initialOnline = false)
  {
    _connection = connection;
    isOnline = initialOnline;
  }

  // ── Public API ────────────────────────────────────────────────────

  /// <summary>
  /// Called when the network comes back online.
  /// Returns a Task so callers can await the recovery path.
  /// </summary>
  public async Task OnOnline()
  {
    isOnline = true;
    Console.WriteLine($"CSM.OnOnline: IsPendingLogin={IsPendingLogin}, IsLoggedOn={IsLoggedOn}, bIsConnectionRecovery={bIsConnectionRecovery}, bAborted={bAborted}, bExpectingDisconnectRemote={bExpectingDisconnectRemote}, IsConnected={_connection.IsConnected}");

    if (IsPendingLogin)
    {
      _loggingInTask ??= new TaskCompletionSource();
      OnAuthUpdated?.Invoke();

      if (bIsConnectionRecovery)
      {
        Console.WriteLine("CSM.OnOnline: Connection recovery path");
        bExpectingDisconnectRemote = true;
        bSuppressReconnect = true;

        if (_connection.IsConnected)
        {
          Console.WriteLine("CSM.OnOnline: Disconnecting stale connection");
          disconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          _connection.Disconnect();

          var completed = await Task.WhenAny(disconnectedTcs.Task, Task.Delay(TimeSpan.FromSeconds(1)));
          if (completed == disconnectedTcs.Task)
            Console.WriteLine("CSM.OnOnline: Disconnect callback received");
          else
            Console.WriteLine("CSM.OnOnline: Timed out waiting for disconnect callback");

          disconnectedTcs = null;
        }
        else
        {
          Console.WriteLine("CSM.OnOnline: Already disconnected");
        }

        bExpectingDisconnectRemote = false;
        Connect();
        bSuppressReconnect = false;
        return;
      }

      Console.WriteLine("CSM.OnOnline: Previous session exists (not recovery), calling Connect");
      if (OnLoginRequested != null)
        await OnLoginRequested();
      else
        Connect();
    }
  }

  /// <summary>Called when network goes offline.</summary>
  public void OnOffline()
  {
    isOnline = false;
    Console.WriteLine($"CSM.OnOffline: IsLoggedOn={IsLoggedOn}, IsPendingLogin={IsPendingLogin}");

    if (IsLoggedOn)
    {
      Console.WriteLine("CSM.OnOffline: Marking session for reconnection");
      IsLoggedOn = false;
      IsPendingLogin = true;
      bIsConnectionRecovery = true;
    }
  }

  /// <summary>Call when SteamClient fires ConnectedCallback.</summary>
  public void OnConnected()
  {
    Console.WriteLine("CSM.OnConnected");
    bConnecting = false;
    bDidDisconnect = false;
    bSuppressReconnect = false;
  }

  /// <summary>
  /// Call when SteamClient fires DisconnectedCallback.
  /// <paramref name="userInitiated"/> mirrors DisconnectedCallback.UserInitiated.
  /// </summary>
  public void OnDisconnected(bool userInitiated)
  {
    bDidDisconnect = true;
    IsLoggedOn = false;

    var suppressReconnect = bSuppressReconnect;
    var isConnectionRecovery = bIsConnectionRecovery;

    Console.WriteLine($"CSM.OnDisconnected: bIsConnectionRecovery={bIsConnectionRecovery}, UserInitiated={userInitiated}, bExpectingDisconnectRemote={bExpectingDisconnectRemote}, bAborted={bAborted}, bSuppressReconnect={bSuppressReconnect}, bConnecting={bConnecting}, isOnline={isOnline}, connectionBackoff={connectionBackoff}");

    disconnectedTcs?.TrySetResult();

    if (suppressReconnect)
    {
      Console.WriteLine("CSM.OnDisconnected: bSuppressReconnect=true, skipping reconnect");
      return;
    }

    if (!isConnectionRecovery && (userInitiated || bExpectingDisconnectRemote))
    {
      Console.WriteLine("CSM.OnDisconnected: User-initiated or expected disconnect - aborting");
      bAborted = true;
    }
    else if (connectionBackoff >= 12)
    {
      Console.WriteLine("CSM.OnDisconnected: Backoff exhausted (12 attempts)");
      Abort();
      OnAuthError?.Invoke("Timeout");
    }
    else if (!bAborted)
    {
      if (!isConnectionRecovery)
        connectionBackoff += 1;

      if (isOnline)
      {
        Console.WriteLine($"CSM.OnDisconnected: Scheduling reconnect (#{connectionBackoff})");
        _loggingInTask ??= new TaskCompletionSource();
        OnAuthUpdated?.Invoke();

        _ = Task.Run(async () =>
        {
          await Task.Delay(ReconnectDelayMs);
          if (bAborted)
          {
            Console.WriteLine("CSM.OnDisconnected: Reconnect cancelled (bAborted)");
            return;
          }

          Console.WriteLine("CSM.OnDisconnected: Executing delayed reconnect");
          ResetConnectionFlags();
          _connection.Connect();
        });
      }
      else
      {
        Console.WriteLine("CSM.OnDisconnected: Offline, skipping reconnect");
        IsPendingLogin = true;
      }
    }
    else
    {
      Console.WriteLine("CSM.OnDisconnected: bAborted=true, skipping");
    }

    if (bAborted)
      FinishLoggingInTask();
  }

  /// <summary>Call when SteamUser fires LoggedOnCallback with EResult.OK.</summary>
  public void OnLoggedIn()
  {
    Console.WriteLine("CSM.OnLoggedIn");
    bIsConnectionRecovery = false;
    bAborted = false;
    connectionBackoff = 0;
    IsLoggedOn = true;
    IsPendingLogin = false;
    FinishLoggingInTask();
  }

  /// <summary>Await until the current login/reconnect cycle finishes.</summary>
  public async Task WaitLoggingInTask()
  {
    if (_loggingInTask != null)
      await _loggingInTask.Task;
  }

  // ── Internals ─────────────────────────────────────────────────────

  internal void Connect()
  {
    Console.WriteLine($"CSM.Connect: bIsConnectionRecovery={bIsConnectionRecovery}, bAborted={bAborted}, bConnecting={bConnecting}, IsConnected={_connection.IsConnected}");

    waitingToRetry = false;
    bAborted = false;
    bConnecting = true;
    _loggingInTask ??= new TaskCompletionSource();

    if (!bIsConnectionRecovery)
      connectionBackoff = 0;

    bIsConnectionRecovery = false;
    ResetConnectionFlags();
    _connection.Connect();
  }

  internal void Abort()
  {
    IsLoggedOn = false;
    IsPendingLogin = false;
    PrepareDisconnect();
  }

  /// <summary>
  /// Prepares state for a controlled disconnect. Sets abort flags and clears
  /// connection state. Called before performing transport-level disconnect.
  /// </summary>
  public void PrepareDisconnect()
  {
    Console.WriteLine($"CSM.PrepareDisconnect: bExpectingDisconnectRemote={bExpectingDisconnectRemote}, bIsConnectionRecovery={bIsConnectionRecovery}");
    bAborted = true;
    bConnecting = false;
    FinishLoggingInTask();

    if (!bExpectingDisconnectRemote)
      bIsConnectionRecovery = false;
  }

  /// <summary>
  /// Called when a recoverable login failure requires reconnecting
  /// (e.g. TryAnotherCM, QR error, AlreadyLoggedInElsewhere).
  /// Sets recovery flags and disconnects the current connection.
  /// The subsequent OnDisconnected will schedule a delayed reconnect.
  /// </summary>
  public void Reconnect()
  {
    Console.WriteLine($"CSM.Reconnect: waitingToRetry={waitingToRetry}, bIsConnectionRecovery={bIsConnectionRecovery}, IsConnected={_connection.IsConnected}");
    waitingToRetry = false;
    bIsConnectionRecovery = true;
    bExpectingDisconnectRemote = true;
    IsPendingLogin = true;
    _loggingInTask ??= new TaskCompletionSource();
    _connection.Disconnect();
  }

  /// <summary>
  /// Marks that we expect an imminent remote disconnect
  /// (e.g. SteamGuard challenge, 2FA required, access token rejected).
  /// </summary>
  public void MarkExpectingDisconnect()
  {
    bExpectingDisconnectRemote = true;
  }

  /// <summary>
  /// Marks the session as pending login while offline.
  /// Used when a user is changed while there's no network connection
  /// but a valid cached token exists.
  /// </summary>
  public void SetPendingLoginOffline()
  {
    IsPendingLogin = true;
  }

  /// <summary>
  /// Atomically begins a retry wait. Returns false if already waiting.
  /// Used to prevent duplicate reconnect attempts (e.g. TryAnotherCM path).
  /// </summary>
  public bool TryBeginRetryWait()
  {
    if (waitingToRetry) return false;
    waitingToRetry = true;
    return true;
  }

  private void ResetConnectionFlags()
  {
    bExpectingDisconnectRemote = false;
    bDidDisconnect = false;
  }

  /// <summary>Ensure loggingInTask is created (for Reconnect paths that need it).</summary>
  internal void EnsureLoggingInTask()
  {
    _loggingInTask ??= new TaskCompletionSource();
  }

  internal void FinishLoggingInTask()
  {
    _loggingInTask?.TrySetResult();
    _loggingInTask = null;
  }
}
