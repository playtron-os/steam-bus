using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Steam.Session;
using SteamKit2;
using static SteamBus.Tests.Session.SessionHarness;

namespace SteamBus.Tests.Session;

// Drives a real SteamSession and SteamKit client against a local CM through outages,
// network changes and logon rejections.
[NonParallelizable]
public class SessionReconnectTests
{
  sealed class Stall : CallbackMsg
  {
  }

  [Test, Timeout(60_000)]
  public async Task SavedSessionOutlastsAnOutageLongerThanTheInteractiveCap()
  {
    await using var h = await Create();
    await h.LogOn();

    h.Server.Stop();
    h.Server.DropAll();
    await WaitUntil(() => h.Session.ReconnectAttempts > ReconnectPolicy.InteractiveMaxAttempts, "it retries past the interactive cap", 30);

    Assert.That(h.AuthErrors, Is.Empty);
    Assert.That(h.Session.IsReconnecting, Is.True);

    var logons = h.Server.Logons;
    h.Server.Start();
    await h.AnswerNextLogon(EResult.OK, logons);
    await WaitUntil(() => h.Session.IsLoggedOn, "it logs back on");
    Assert.That(h.Session.ReconnectAttempts, Is.Zero);
  }

  [Test, Timeout(60_000)]
  public async Task InteractiveLoginGivesUpAfterTheCap()
  {
    await using var h = await Create(savedToken: false);
    h.Server.Stop();

    var login = h.Session.Login();
    await WaitUntil(() => h.AuthErrorCount > 0, "the login gives up", 30);
    await login.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.That(h.AuthErrors, Is.EqualTo(new[] { DbusErrors.Timeout }));
    Assert.That(h.Session.ReconnectAttempts, Is.EqualTo(ReconnectPolicy.InteractiveMaxAttempts));
    Assert.That(h.Session.IsAborted, Is.True);
    Assert.That(h.Session.IsReconnecting, Is.False);
  }

  // Review of #146: a Connect() over an attempt in flight was reported as a user disconnect and aborted the session.
  [Test, Timeout(60_000)]
  public async Task NetworkChangeDuringAnAttemptDoesNotAbort()
  {
    await using var h = await Create();
    await h.LogOn();

    h.Session.OnOffline();
    h.Server.Behaviour = FakeCmServer.Mode.Silent;
    h.Server.DropAll();
    await WaitUntil(() => h.Server.OpenConnections == 0, "the old connection closes");

    var connections = h.Server.Connections;
    await h.Session.OnOnline();
    await WaitUntil(() => h.Server.Connections > connections, "an attempt is in flight");

    // A second network change while that attempt still waits for the CM.
    await h.Session.OnOnline();
    await Holds(() => !h.Session.IsAborted, "the session is never aborted", 0.5);

    var logons = h.Server.Logons;
    h.Server.Behaviour = FakeCmServer.Mode.Accept;
    await h.AnswerNextLogon(EResult.OK, logons);
    await WaitUntil(() => h.Session.IsLoggedOn, "it logs back on");
    Assert.That(h.AuthErrors, Is.Empty);
  }

  // Review of #146: the replaced connection's disconnect can be handled after the new attempt started.
  [Test, Timeout(60_000)]
  public async Task LateDisconnectOfTheReplacedConnectionIsIgnored()
  {
    await using var h = await Create();
    await h.LogOn();

    // Hold the callback pump so the stale disconnect is handled after OnOnline's new Connect().
    using var release = new ManualResetEventSlim();
    h.Session.SubscribeCallback<Stall>(_ => release.Wait(TimeSpan.FromSeconds(5)));
    h.Post(new Stall());

    var logons = h.Server.Logons;
    h.Session.OnOffline();
    var online = h.Session.OnOnline();
    await WaitUntil(() => h.Server.Logons > logons || h.Server.Handshakes > logons, "the new connection is up");
    release.Set();
    await online.WaitAsync(TimeSpan.FromSeconds(10));

    await Holds(() => !h.Session.IsAborted && !h.Session.IsLoggedOn, "the session waits for its logon, never aborted", 0.5);
    await h.AnswerNextLogon(EResult.OK, logons);
    await WaitUntil(() => h.Session.IsLoggedOn, "it logs back on");
    await Holds(() => h.Server.OpenConnections == 1, "only the new connection stays open", 0.5);
  }

  [Test, Timeout(60_000)]
  public async Task StalledHandshakeIsReplaced()
  {
    await using var h = await Create();
    await h.LogOn();

    h.Server.Behaviour = FakeCmServer.Mode.Silent;
    var connections = h.Server.Connections;
    h.Server.DropAll();
    await WaitUntil(() => h.Server.Connections > connections, "it reconnects to a CM that never answers");

    connections = h.Server.Connections;
    var logons = h.Server.Logons;
    h.Server.Behaviour = FakeCmServer.Mode.Accept;
    await WaitUntil(() => h.Server.Connections > connections, "the stalled attempt is replaced", 10);
    await h.AnswerNextLogon(EResult.OK, logons);
    await WaitUntil(() => h.Session.IsLoggedOn, "it logs back on");
    Assert.That(h.AuthErrors, Is.Empty);
  }

  // Review of #146: the CM drops the socket right after a rejection; that is one failure, not two.
  [Test, Timeout(60_000), Repeat(5)]
  public async Task RejectionFollowedByADropCountsOnce()
  {
    // A retry well past SteamKit's 100 ms socket poll, so the drop is handled before the retry starts.
    var slowRetry = SessionHarness.Fast with
    {
      NewReconnectPolicy = () => new ReconnectPolicy(new Random(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)),
    };
    await using var h = await Create(timings: slowRetry);
    var logons = h.Server.Logons;
    var login = h.Session.Login();

    await WaitUntil(() => h.Server.Logons > logons, "the session sends its logon");
    var rejected = h.Server.Open();
    h.Post(LoggedOn(EResult.TryAnotherCM));
    h.Server.Drop(rejected);
    await WaitUntil(() => !h.Session.SteamClient.IsConnected, "the session sees the drop");
    await Task.Delay(300);
    Assert.That(h.Session.ReconnectAttempts, Is.EqualTo(1), "the rejection and its drop count once");

    await h.AnswerNextLogon(EResult.OK, logons + 1);
    await login.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(h.Session.IsLoggedOn, Is.True);
    Assert.That(h.Server.Connections, Is.EqualTo(2), "one retry, not a retry plus a reconnect");
  }

  // Review of #146: AlreadyLoggedInElsewhere looped every ~13 s without backing off.
  [Test, Timeout(60_000)]
  public async Task AlreadyLoggedInElsewhereBacksOff()
  {
    await using var h = await Create();
    var login = h.Session.Login();
    var delays = new List<TimeSpan>();

    for (var rejection = 1; rejection <= 5; rejection++)
    {
      await h.AnswerNextLogon(EResult.AlreadyLoggedInElsewhere, rejection - 1);
      // The retry's logon comes after its delay was chosen.
      await WaitUntil(() => h.Server.Logons > rejection, $"retry #{rejection}");
      delays.Add(h.Session.LastLogOnRetryDelay);
    }

    Assert.That(h.Session.ReconnectAttempts, Is.EqualTo(5));

    // Never sooner than AlreadyLoggedInRetry, and growing with the backoff past it.
    var shown = string.Join(", ", delays.Select(delay => delay.TotalMilliseconds));
    Assert.That(delays, Is.All.GreaterThanOrEqualTo(SessionHarness.Fast.AlreadyLoggedInRetry), shown);
    Assert.That(delays[4], Is.GreaterThan(delays[0] * 2), shown);

    await h.AnswerNextLogon(EResult.OK, 5);
    await login.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(h.Session.ReconnectAttempts, Is.Zero);
  }

  // Review of #146: a Web API that never answers stalled the retry loop for HttpClient's 100 s.
  [Test, Timeout(60_000)]
  public async Task ServerListRefreshGivesUpOnASilentWebApi()
  {
    using var silentApi = new TcpListener(IPAddress.Loopback, 0);
    silentApi.Start();
    var accepted = new System.Collections.Concurrent.ConcurrentQueue<TcpClient>();
    _ = Task.Run(async () =>
    {
      while (true)
        accepted.Enqueue(await silentApi.AcceptTcpClientAsync());
    });

    var api = new Uri($"http://{silentApi.LocalEndpoint}/");
    await using var h = await Create(configure: config => config.WithDirectoryFetch(true).WithWebAPIBaseAddress(api));

    var clock = Stopwatch.StartNew();
    await h.Session.RefreshServerList().WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
    Assert.That(accepted, Is.Not.Empty, "the refresh did ask the Web API");

    // The cached list is kept.
    var servers = h.Session.SteamClient.Configuration.ServerList.GetAllEndPoints();
    Assert.That(servers.Select(server => server.EndPoint), Is.EqualTo(new[] { h.Server.EndPoint }));
    foreach (var client in accepted)
      client.Dispose();
  }

  // OnOnline's re-login wait is bounded; reconnection must carry on after it returns.
  [Test, Timeout(60_000)]
  public async Task ReconnectionContinuesAfterTheBoundedReloginWait()
  {
    await using var h = await Create(online: false);
    h.Server.Stop();
    h.Session.IsPendingLogin = true;

    var clock = Stopwatch.StartNew();
    await h.Session.OnOnline();
    Assert.That(h.Session.IsLoggedOn, Is.False);
    Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));

    var logons = h.Server.Logons;
    h.Server.Start();
    await h.AnswerNextLogon(EResult.OK, logons);
    await WaitUntil(() => h.Session.IsLoggedOn, "it logs on once Steam answers");
  }

  // A retry that wakes to find the session already logged on must not block later reconnects.
  [Test, Timeout(60_000)]
  public async Task DropAfterAnOverlappingRetryStillReconnects()
  {
    await using var h = await Create();
    var login = h.Session.Login();
    await WaitUntil(() => h.Server.Logons > 0, "the session sends its logon");
    h.Post(LoggedOn(EResult.AlreadyLoggedInElsewhere));
    h.Post(LoggedOn(EResult.OK));
    await login.WaitAsync(TimeSpan.FromSeconds(10));
    await Task.Delay(SessionHarness.Fast.AlreadyLoggedInRetry * 3);
    Assert.That(h.Session.IsLoggedOn, Is.True);

    var connections = h.Server.Connections;
    h.Server.DropAll();
    await WaitUntil(() => h.Server.Connections > connections, "it reconnects after the drop", 5);
  }

  [Test, Timeout(60_000)]
  public async Task DisconnectWithoutAConnectionReturns()
  {
    await using var h = await Create(online: false);
    await Task.Run(() => h.Session.Disconnect()).WaitAsync(TimeSpan.FromSeconds(5));
    Assert.That(h.Session.IsAborted, Is.True);
  }

  [Test, Timeout(60_000)]
  public async Task DisposedSessionStopsReconnecting()
  {
    await using var h = await Create();
    await h.LogOn();

    await Task.Run(h.Session.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
    var connections = h.Server.Connections;
    h.Server.DropAll();
    await Holds(() => h.Server.Connections == connections, "no reconnect after dispose", 1);
  }

  [Test, Timeout(60_000)]
  public async Task RevokedSessionStopsReconnecting()
  {
    await using var h = await Create();
    await h.LogOn();

    var connections = h.Server.Connections;
    h.Post(LoggedOff(EResult.Revoked));
    await WaitUntil(() => h.Session.IsAborted, "the session is aborted");
    h.Server.DropAll();
    await Holds(() => h.Server.Connections == connections && !h.Session.IsReconnecting, "no reconnect after a revoked token", 1);
  }

  // Steam's maintenance logs clients off before dropping them; only a revoked token ends the session.
  [Test, Timeout(60_000)]
  public async Task ServerLogOffForMaintenanceReconnects()
  {
    await using var h = await Create();
    await h.LogOn();

    var logons = h.Server.Logons;
    h.Post(LoggedOff(EResult.ServiceUnavailable));
    h.Server.DropAll();
    await h.AnswerNextLogon(EResult.OK, logons);
    await WaitUntil(() => h.Session.IsLoggedOn, "it logs back on");
    Assert.That(h.AuthErrors, Is.Empty);
  }

  [Test, Timeout(60_000)]
  public async Task RejectedTokenIsNotRetried()
  {
    await using var h = await Create();
    var login = h.Session.Login();
    await h.AnswerNextLogon(EResult.AccessDenied, 0);
    await login.WaitAsync(TimeSpan.FromSeconds(10));

    Assert.That(h.Session.IsAborted, Is.True);
    var connections = h.Server.Connections;
    await Holds(() => h.Server.Connections == connections, "no retry with a rejected token", 1);
  }

  [Test, Timeout(60_000)]
  public async Task WaitsDuringAnOutageGiveUpOnTime()
  {
    await using var h = await Create();
    await h.LogOn();
    h.Server.Stop();
    h.Server.DropAll();
    await WaitUntil(() => h.Session.IsReconnecting, "it starts reconnecting");

    var clock = Stopwatch.StartNew();
    Assert.That(await h.Session.WaitLoggingInTask(TimeSpan.FromMilliseconds(200)), Is.False);
    Assert.That(await h.Session.WaitForReconnect(TimeSpan.FromMilliseconds(200)), Is.False);
    Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
  }

  // Random outages, drops and network changes; the session must come out logged on, never aborted.
  [Test, Timeout(120_000), Repeat(3)]
  public async Task SurvivesAFlappingNetwork()
  {
    await using var h = await Create();
    await h.LogOn();

    var aborted = false;
    using var stop = new CancellationTokenSource();
    var answered = h.Server.Logons;
    var answering = Task.Run(async () =>
    {
      while (!stop.IsCancellationRequested)
      {
        if (h.Server.Logons > answered)
        {
          answered = h.Server.Logons;
          h.Post(LoggedOn(EResult.OK));
        }
        aborted |= h.Session.IsAborted;
        await Task.Delay(5);
      }
    });

    var random = new Random(TestContext.CurrentContext.CurrentRepeatCount);
    for (var step = 0; step < 60; step++)
    {
      switch (random.Next(6))
      {
        case 0: h.Server.Stop(); break;
        case 1: if (!h.Server.IsListening) h.Server.Start(); break;
        case 2: h.Server.DropAll(); break;
        case 3: h.Session.OnOffline(); break;
        case 4: _ = h.Session.OnOnline(); break;
        case 5: h.Server.Behaviour = random.Next(4) == 0 ? FakeCmServer.Mode.Silent : FakeCmServer.Mode.Accept; break;
      }
      await Task.Delay(random.Next(5, 120));
    }

    h.Server.Behaviour = FakeCmServer.Mode.Accept;
    if (!h.Server.IsListening)
      h.Server.Start();
    await h.Session.OnOnline();
    await WaitUntil(() => h.Session.IsLoggedOn, "it ends logged on", 20);

    stop.Cancel();
    await answering;
    Assert.That(aborted, Is.False, "the session was aborted at some point");
    Assert.That(h.AuthErrors, Is.Empty);
  }
}
