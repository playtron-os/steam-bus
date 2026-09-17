namespace Steam.Session;

/// Computes reconnection delays for the Steam session.
///
/// Valve restarts its CM (connection manager) fleet during weekly maintenance
/// (typically Tuesday afternoons Pacific time), which can keep connections
/// failing for 5-20 minutes. Recovery of an established session must therefore
/// never give up: it retries indefinitely with exponential backoff. Only
/// interactive logins, where a user is waiting at a login screen, are capped.
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

  /// Upper bound for the delay during an interactive login, where a user is
  /// actively waiting at a login screen and minute-long gaps between the
  /// capped attempts would read as a hang
  public static readonly TimeSpan InteractiveMaxDelay = TimeSpan.FromSeconds(10);

  const double BackoffMultiplier = 2.0;

  // Randomizes each delay by up to +/-20% so that a fleet of devices does not
  // hammer Steam in lockstep when connectivity returns after an outage
  const double JitterFraction = 0.2;

  readonly Random random;
  readonly object attemptsLock = new();
  int attempts;

  public ReconnectPolicy(Random? random = null)
  {
    this.random = random ?? Random.Shared;
  }

  /// Number of consecutive failed connection attempts
  public int Attempts
  {
    get { lock (attemptsLock) return attempts; }
  }

  /// True once enough consecutive failures accumulated that an interactive
  /// login should give up and surface an error to the waiting user
  public bool HasExceededInteractiveAttempts
  {
    get { lock (attemptsLock) return attempts >= InteractiveMaxAttempts; }
  }

  /// Records a failed attempt and returns how long to wait before the next
  /// one. Interactive attempts are clamped to InteractiveMaxDelay.
  public TimeSpan RecordFailureAndGetDelay(bool interactive = false)
  {
    lock (attemptsLock)
    {
      attempts += 1;
      var delay = DelayForAttempt(attempts);
      if (interactive && delay > InteractiveMaxDelay)
        delay = InteractiveMaxDelay;
      return delay;
    }
  }

  /// True when enough consecutive failures accumulated that the cached CM
  /// server list should be re-fetched from the Steam Directory Web API. After
  /// Valve restarts its CM fleet, every cached endpoint may be stale.
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
    var exponential = BaseDelay.TotalSeconds * Math.Pow(BackoffMultiplier, attempt - 1);
    var capped = Math.Min(exponential, MaxDelay.TotalSeconds);
    var jitter = 1.0 + JitterFraction * (random.NextDouble() * 2.0 - 1.0);
    return TimeSpan.FromSeconds(Math.Min(capped * jitter, MaxDelay.TotalSeconds));
  }
}
