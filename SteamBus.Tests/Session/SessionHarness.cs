using System.Reflection;
using System.Runtime.CompilerServices;
using Steam.Config;
using Steam.Session;
using SteamKit2;
using SteamKit2.Discovery;
using Xdg.Directories;

namespace SteamBus.Tests.Session;

// A SteamSession wired to a FakeCmServer, with short waits and a private home directory.
sealed class SessionHarness : IAsyncDisposable
{
  public static readonly SessionTimings Fast = new(
    Watchdog: TimeSpan.FromMilliseconds(200),
    AlreadyLoggedInRetry: TimeSpan.FromMilliseconds(150),
    RateLimitRetry: TimeSpan.FromMilliseconds(300),
    ReloginWait: TimeSpan.FromSeconds(1),
    ServerListRefresh: TimeSpan.FromMilliseconds(500),
    // Long enough that a loaded machine's slow handshake is not taken for a stuck one
    StalledAttempt: TimeSpan.FromSeconds(3),
    DisconnectWait: TimeSpan.FromSeconds(2),
    NewReconnectPolicy: () => new ReconnectPolicy(new Random(1),
      baseDelay: TimeSpan.FromMilliseconds(50),
      maxDelay: TimeSpan.FromMilliseconds(400),
      interactiveMaxDelay: TimeSpan.FromMilliseconds(100)));

  public FakeCmServer Server { get; } = new();
  public SteamSession Session { get; private set; } = null!;
  public List<string> AuthErrors { get; } = [];

  // interactive: a login still in progress, holding the token its auth flow just returned but never logged on.
  public static async Task<SessionHarness> Create(bool savedToken = true, bool online = true, Action<ISteamConfigurationBuilder>? configure = null, SessionTimings? timings = null, bool interactive = false)
  {
    // STEAMBUS_TEST_TRACE=1 adds SteamKit's own connection log to the test output.
    if (Environment.GetEnvironmentVariable("STEAMBUS_TEST_TRACE") == "1" && !DebugLog.Enabled)
    {
      DebugLog.Enabled = true;
      DebugLog.AddListener((category, message) => Console.WriteLine($"[SteamKit {category}] {message}"));
    }
    var harness = new SessionHarness();
    var library = Directory.CreateDirectory(Path.Join(BaseDirectory.DataHome, "steambus-test-library")).FullName;
    var store = await DepotConfigStore.CreateAsync([library]);

    var servers = new MemoryServerListProvider();
    await servers.UpdateServerListAsync([ServerRecord.CreateSocketServer(harness.Server.EndPoint)]);
    var configuration = SteamConfiguration.Create(config =>
    {
      config
        .WithProtocolTypes(ProtocolTypes.Tcp)
        .WithDirectoryFetch(false)
        .WithServerListProvider(servers)
        .WithConnectionTimeout(TimeSpan.FromSeconds(1));
      configure?.Invoke(config);
    });

    var details = new SteamUser.LogOnDetails { Username = "tester" };
    if (savedToken)
      details.AccessToken = "saved-refresh-token";
    else
      details.Password = "password";

    harness.Session = new SteamSession(details, store, null, null, configuration, timings ?? Fast)
    {
      isOnline = online,
    };
    harness.Session.OnAuthError = error =>
    {
      lock (harness.AuthErrors) harness.AuthErrors.Add(error);
    };
    if (interactive)
      harness.SetField("bSessionEstablished", false);
    return harness;
  }

  public void SetField(string name, object value) =>
    typeof(SteamSession).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Session, value);

  // Log on through the fake CM: the session connects and sends its logon, and the test answers it.
  public async Task LogOn()
  {
    var logons = Server.Logons;
    var login = Session.Login();
    await WaitUntil(() => Server.Logons > logons, "the session sends its logon");
    Post(LoggedOn(EResult.OK));
    await login.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(Session.IsLoggedOn, Is.True);
  }

  // Answer the next logon the session sends.
  public async Task AnswerNextLogon(EResult result, int afterLogons)
  {
    await WaitUntil(() => Server.Logons > afterLogons, $"the session sends logon #{afterLogons + 1}");
    Post(LoggedOn(result));
  }

  public void Post(CallbackMsg callback) => Session.SteamClient.PostCallback(callback);

  public static SteamUser.LoggedOnCallback LoggedOn(EResult result) =>
    (SteamUser.LoggedOnCallback)typeof(SteamUser.LoggedOnCallback)
      .GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, [typeof(EResult)])!
      .Invoke([result]);

  public static SteamUser.LoggedOffCallback LoggedOff(EResult result)
  {
    // Its only constructor parses a packet, so it is built bare and filled in.
    var callback = (SteamUser.LoggedOffCallback)RuntimeHelpers.GetUninitializedObject(typeof(SteamUser.LoggedOffCallback));
    typeof(SteamUser.LoggedOffCallback).GetProperty(nameof(SteamUser.LoggedOffCallback.Result))!.SetValue(callback, result);
    typeof(CallbackMsg).GetProperty(nameof(CallbackMsg.JobID))!.SetValue(callback, JobID.Invalid);
    return callback;
  }

  public int AuthErrorCount
  {
    get { lock (AuthErrors) return AuthErrors.Count; }
  }

  public static async Task WaitUntil(Func<bool> condition, string what, double seconds = 20)
  {
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
    while (!condition())
    {
      if (DateTime.UtcNow > deadline)
        Assert.Fail($"Timed out after {seconds}s waiting until {what}");
      await Task.Delay(10);
    }
  }

  // Holds for the whole span, failing as soon as it doesn't.
  public static async Task Holds(Func<bool> condition, string what, double seconds)
  {
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
    while (DateTime.UtcNow < deadline)
    {
      Assert.That(condition(), Is.True, what);
      await Task.Delay(10);
    }
  }

  public async ValueTask DisposeAsync()
  {
    // A hang here is a bug in itself, so it fails the test instead of the run.
    var dispose = Task.Run(() => Session?.Dispose());
    Server.Dispose();
    if (await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(10))) != dispose)
      Assert.Fail("SteamSession.Dispose() did not return");
  }
}

// Every test gets a throwaway HOME: a logon writes Steam's config files under it.
[SetUpFixture]
public sealed class PrivateHome
{
  static readonly string[] Variables = ["HOME", "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME"];
  readonly Dictionary<string, string?> saved = [];
  string? root;

  [OneTimeSetUp]
  public void Isolate()
  {
    root = Directory.CreateTempSubdirectory("steambus-tests-").FullName;
    var home = Directory.CreateDirectory(Path.Join(root, "home")).FullName;
    foreach (var name in Variables)
      saved[name] = Environment.GetEnvironmentVariable(name);

    Environment.SetEnvironmentVariable("HOME", home);
    Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Join(home, ".local/share"));
    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Join(home, ".config"));
    Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Join(home, ".cache"));
    Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Join(home, ".local/state"));

    // Never let a test write into the real Steam or steambus directories.
    Assert.That(BaseDirectory.DataHome, Does.StartWith(home));
    Assert.That(SteamConfig.GetConfigDirectory(), Does.StartWith(home));
  }

  [OneTimeTearDown]
  public void Restore()
  {
    foreach (var (name, value) in saved)
      Environment.SetEnvironmentVariable(name, value);
    if (root != null)
      Directory.Delete(root, recursive: true);
  }
}
