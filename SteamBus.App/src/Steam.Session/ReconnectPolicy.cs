namespace Steam.Session;

/// Reconnection delays for the Steam session. An established session retries forever, since
/// Valve's weekly CM maintenance can last 5-20 minutes; only interactive logins give up.
public sealed class ReconnectPolicy
{
  /// Maximum connection attempts during an interactive login before giving up
  public const int InteractiveMaxAttempts = 12;

  /// A fresh CM server list is fetched every N consecutive failed attempts
  public const int AttemptsPerServerListRefresh = 4;

  /// Delay before the first reconnect attempt, doubled on each failure
  public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(3);

  /// Upper bound for the delay between reconnect attempts
  public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

  /// Upper bound during an interactive login, where longer gaps would read as a hang
  public static readonly TimeSpan InteractiveMaxDelay = TimeSpan.FromSeconds(10);

  const double BackoffMultiplier = 2.0;

  // +/-20%, so a fleet of devices does not reconnect in lockstep after an outage
  const double JitterFraction = 0.2;

  readonly Random random;
  readonly TimeSpan baseDelay;
  readonly TimeSpan maxDelay;
  readonly TimeSpan interactiveMaxDelay;
  readonly object attemptsLock = new();
  int attempts;

  public ReconnectPolicy(Random? random = null)
    : this(random, BaseDelay, MaxDelay, InteractiveMaxDelay)
  {
  }

  // Tests shorten the delays.
  internal ReconnectPolicy(Random? random, TimeSpan baseDelay, TimeSpan maxDelay, TimeSpan interactiveMaxDelay)
  {
    this.random = random ?? Random.Shared;
    this.baseDelay = baseDelay;
    this.maxDelay = maxDelay;
    this.interactiveMaxDelay = interactiveMaxDelay;
  }

  /// Delay before reconnecting a connection that was dropped on purpose rather than lost
  public TimeSpan RecoveryDelay => baseDelay;

  /// Number of consecutive failed connection attempts
  public int Attempts
  {
    get { lock (attemptsLock) return attempts; }
  }

  /// True once an interactive login should give up and show the waiting user an error
  public bool HasExceededInteractiveAttempts
  {
    get { lock (attemptsLock) return attempts >= InteractiveMaxAttempts; }
  }

  /// Records a failed attempt and returns how long to wait before the next one
  public TimeSpan RecordFailureAndGetDelay(bool interactive = false)
  {
    lock (attemptsLock)
    {
      attempts += 1;
      var delay = DelayForAttempt(attempts);
      if (interactive && delay > interactiveMaxDelay)
        delay = interactiveMaxDelay;
      return delay;
    }
  }

  /// True when the cached CM server list should be re-fetched; after a CM restart every cached endpoint may be stale
  public bool ShouldRefreshServerList()
  {
    lock (attemptsLock)
    {
      return attempts > 0 && attempts % AttemptsPerServerListRefresh == 0;
    }
  }

  /// Resets the failure counter, e.g. after a successful logon
  public void Reset()
  {
    lock (attemptsLock) attempts = 0;
  }

  TimeSpan DelayForAttempt(int attempt)
  {
    var exponential = baseDelay.TotalSeconds * Math.Pow(BackoffMultiplier, attempt - 1);
    var capped = Math.Min(exponential, maxDelay.TotalSeconds);
    var jitter = 1.0 + JitterFraction * (random.NextDouble() * 2.0 - 1.0);
    return TimeSpan.FromSeconds(Math.Min(capped * jitter, maxDelay.TotalSeconds));
  }
}
