namespace Steam.Session;

// The session's waits; tests shorten them.
internal sealed record SessionTimings(
  // Interval between reconnect watchdog checks
  TimeSpan Watchdog,
  // Minimum wait before retrying a logon rejected with AlreadyLoggedInElsewhere
  TimeSpan AlreadyLoggedInRetry,
  // Minimum wait before an established session retries a rate-limited logon
  TimeSpan RateLimitRetry,
  // How long OnOnline waits for a re-login; reconnection continues after it
  TimeSpan ReloginWait,
  // How long a CM server list fetch may take before the cached list is kept
  TimeSpan ServerListRefresh,
  // An attempt not connected after this long is presumed stuck and may be replaced
  TimeSpan StalledAttempt,
  // How long Disconnect() pumps callbacks while waiting for its disconnect
  TimeSpan DisconnectWait,
  Func<ReconnectPolicy> NewReconnectPolicy)
{
  public static readonly SessionTimings Default = new(
    Watchdog: TimeSpan.FromSeconds(60),
    AlreadyLoggedInRetry: TimeSpan.FromSeconds(10),
    RateLimitRetry: TimeSpan.FromMinutes(5),
    ReloginWait: TimeSpan.FromSeconds(60),
    ServerListRefresh: TimeSpan.FromSeconds(15),
    StalledAttempt: TimeSpan.FromSeconds(30),
    DisconnectWait: TimeSpan.FromSeconds(5),
    NewReconnectPolicy: () => new ReconnectPolicy());
}
