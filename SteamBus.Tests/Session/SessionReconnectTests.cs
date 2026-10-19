using System.Diagnostics;
using System.Reflection;
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
    var inFlight = h.Server.Connections;

    // A second network change while that attempt still waits for the CM.
    await h.Session.OnOnline();
    await Holds(() => !h.Session.IsAborted && h.Server.Connections == inFlight, "the attempt is kept and the session never aborted", 0.5);

    var logons = h.Server.Logons;
    h.Server.Behaviour = FakeCmServer.Mode.Accept;
    await h.AnswerNextLogon(EResult.OK, logons);
    await WaitUntil(() => h.Session.IsLoggedOn, "it logs back on");
    Assert.That(h.AuthErrors, Is.Empty);
  }

  // SteamKit raises a replaced socket's disconnect twice. When the late one lands after the next Connect(), it
  // drops that new connection and reports it user-initiated. That is a lost connection, not a reason to stop.
  [Test, Timeout(60_000)]
  public async Task LateSteamKitDisconnectFlaggedUserInitiatedReconnects()
  {
    await using var h = await Create();
    await h.LogOn();

    var logons = h.Server.Logons;
    h.Session.OnOffline();
    await h.Session.OnOnline();
    // CMClient's handler for that late event, which takes whichever connection is current.
    var args = Activator.CreateInstance(typeof(SteamKit2.Internal.CMClient).Assembly.GetType("SteamKit2.DisconnectedEventArgs")!, new object[] { true })!;
    typeof(SteamKit2.Internal.CMClient).GetMethod("Disconnected", BindingFlags.NonPublic | BindingFlags.Instance)!
      .Invoke(h.Session.SteamClient, [null, args]);

    await Holds(() => !h.Session.IsAborted, "the session is never aborted", 0.5);
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
    h.Session.SubscribeCallback<Stall>(_ => release.Wait(TimeSpan.FromSeconds(30)));
    h.Post(new Stall());

    var logons = h.Server.Logons;
    h.Session.OnOffline();
    var online = h.Session.OnOnline();
    await WaitUntil(() => h.Server.Logons > logons || h.Server.Handshakes > logons, "the new connection is up");
    release.Set();
    await online.WaitAsync(TimeSpan.FromSeconds(10));

    await Holds(() => !h.Session.IsAborted && !h.Session.IsLoggedOn && h.Session.ReconnectAttempts == 0,
      "the session waits for its logon, and the stale disconnect counts as nothing", 0.5);
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
    // The drop is handled before the retry starts.
    await using var h = await Create(timings: SlowRetry);
    var logons = h.Server.Logons;
    var login = h.Session.Login();

    await WaitUntil(() => h.Server.Logons > logons, "the session sends its logon");
    var failures = h.Session.ReconnectAttempts;
    var connections = h.Server.Connections;
    var rejected = h.Server.Open();
    h.Post(LoggedOn(EResult.TryAnotherCM));
    h.Server.Drop(rejected);
    await WaitUntil(() => !h.Session.SteamClient.IsConnected, "the session sees the drop");
    await Task.Delay(300);
    Assert.That(h.Session.ReconnectAttempts, Is.EqualTo(failures + 1), "the rejection and its drop count once");

    await h.AnswerNextLogon(EResult.OK, logons + 1);
    await login.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(h.Session.IsLoggedOn, Is.True);
    Assert.That(h.Server.Connections, Is.EqualTo(connections + 1), "one retry, not a retry plus a reconnect");
  }

  // Review of #146: AlreadyLoggedInElsewhere looped every ~13 s without backing off.
  [Test, Timeout(60_000)]
  public async Task AlreadyLoggedInElsewhereBacksOff()
  {
    await using var h = await Create();
    var login = h.Session.Login();
    var delays = new List<TimeSpan>();
    await WaitUntil(() => h.Server.Logons > 0, "the session sends its logon");
    var failures = h.Session.ReconnectAttempts;

    for (var rejection = 1; rejection <= 5; rejection++)
    {
      await h.AnswerNextLogon(EResult.AlreadyLoggedInElsewhere, rejection - 1);
      // The retry's logon comes after its delay was chosen.
      await WaitUntil(() => h.Server.Logons > rejection, $"retry #{rejection}");
      delays.Add(h.Session.LastLogOnRetryDelay);
    }

    Assert.That(h.Session.ReconnectAttempts, Is.EqualTo(failures + 5));

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

  // The outage that grew the backoff may be over, so a network change retries at once.
  [Test, Timeout(60_000)]
  public async Task NetworkChangeDuringAnOutageRetriesAtOnce()
  {
    var slowBackoff = SessionHarness.Fast with
    {
      NewReconnectPolicy = () => new ReconnectPolicy(new Random(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1)),
    };
    await using var h = await Create(timings: slowBackoff);
    await h.LogOn();

    h.Server.Stop();
    h.Server.DropAll();
    await WaitUntil(() => h.Session.ReconnectAttempts >= 2, "the backoff grows to 6 s", 15);

    var connections = h.Server.Connections;
    var logons = h.Server.Logons;
    h.Server.Start();
    await h.Session.OnOnline();
    Assert.That(h.Session.ReconnectAttempts, Is.Zero, "the network change restarts the backoff");
    await WaitUntil(() => h.Server.Connections > connections, "it reconnects well before the 6 s backoff ends", 3);

    await h.AnswerNextLogon(EResult.OK, logons);
    await WaitUntil(() => h.Session.IsLoggedOn, "it logs back on");
    Assert.That(h.AuthErrors, Is.Empty);
  }

  [Test, Timeout(60_000)]
  public async Task NetworkChangeWhileLoggedOnKeepsTheConnection()
  {
    await using var h = await Create();
    await h.LogOn();

    var connections = h.Server.Connections;
    await h.Session.OnOnline();
    await Holds(() => h.Session.IsLoggedOn && h.Server.Connections == connections, "the connection stays", 0.5);
  }

  [Test, Timeout(60_000)]
  public async Task DropWhileOfflineWaitsForTheNetwork()
  {
    await using var h = await Create();
    await h.LogOn();

    h.Session.OnOffline();
    var connections = h.Server.Connections;
    h.Server.DropAll();
    await WaitUntil(() => !h.Session.SteamClient.IsConnected, "the session sees the drop");
    await Holds(() => h.Server.Connections == connections, "no attempt while offline", 1);

    var logons = h.Server.Logons;
    await h.Session.OnOnline();
    await h.AnswerNextLogon(EResult.OK, logons);
    await WaitUntil(() => h.Session.IsLoggedOn, "it logs back on");
    Assert.That(h.Session.ReconnectAttempts, Is.Zero);
    Assert.That(h.AuthErrors, Is.Empty);
  }

  // The watchdog's safety net: the network came back but nothing reported it.
  [Test, Timeout(60_000)]
  public async Task WatchdogReconnectsASessionNothingElseRetries()
  {
    await using var h = await Create();
    await h.LogOn();

    h.Session.isOnline = false;
    var connections = h.Server.Connections;
    h.Server.DropAll();
    await Holds(() => h.Server.Connections == connections, "no attempt while offline", 1);

    var logons = h.Server.Logons;
    h.Session.isOnline = true;
    await h.AnswerNextLogon(EResult.OK, logons);
    await WaitUntil(() => h.Session.IsLoggedOn, "it logs back on");
  }

  [Test, Timeout(60_000)]
  public async Task InteractiveLogonRejectionsGiveUpAfterTheCap()
  {
    await using var h = await Create(interactive: true);
    var login = h.Session.Login();

    for (var rejections = 0; h.AuthErrorCount == 0; rejections++)
    {
      await WaitUntil(() => h.Server.Logons > rejections || h.AuthErrorCount > 0, $"logon #{rejections + 1} or the give-up");
      if (h.AuthErrorCount == 0)
        h.Post(LoggedOn(EResult.TryAnotherCM));
    }
    await login.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(h.Session.ReconnectAttempts, Is.EqualTo(ReconnectPolicy.InteractiveMaxAttempts));

    Assert.That(h.AuthErrors, Is.EqualTo(new[] { DbusErrors.Timeout }));
    Assert.That(h.Session.IsAborted, Is.True);
    var connections = h.Server.Connections;
    await Holds(() => h.Server.Connections == connections, "no retry after giving up", 1);
    Assert.That(h.AuthErrors, Is.EqualTo(new[] { DbusErrors.Timeout }), "the error is reported once");
  }

  [Test, Timeout(60_000)]
  public async Task InteractiveLoginGivesUpOnASilentCm()
  {
    await using var h = await Create(interactive: true, timings: SessionHarness.Fast with { StalledAttempt = TimeSpan.FromMilliseconds(300) });
    h.Server.Behaviour = FakeCmServer.Mode.Silent;

    var login = h.Session.Login();
    await WaitUntil(() => h.AuthErrorCount > 0, "the login gives up", 30);
    await login.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.That(h.Server.Connections, Is.EqualTo(ReconnectPolicy.InteractiveMaxAttempts), "each stalled attempt was replaced");
    Assert.That(h.Session.IsAborted, Is.True);
    await Holds(() => h.AuthErrorCount == 1, "the error is reported once", 0.5);
  }

  // Steam being down: a saved session waits it out, a user at the login screen gets an error.
  [TestCase(false), TestCase(true), Timeout(60_000)]
  public async Task ServiceUnavailableIsRetriedOnlyForASavedSession(bool interactive)
  {
    await using var h = await Create(interactive: interactive);
    var login = h.Session.Login();
    await h.AnswerNextLogon(EResult.ServiceUnavailable, 0);

    if (interactive)
    {
      await login.WaitAsync(TimeSpan.FromSeconds(10));
      Assert.That(h.Session.IsAborted, Is.True);
      var connections = h.Server.Connections;
      await Holds(() => h.Server.Connections == connections, "no retry", 1);
      return;
    }

    await h.AnswerNextLogon(EResult.OK, 1);
    await login.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(h.Session.IsLoggedOn, Is.True);
    Assert.That(h.AuthErrors, Is.Empty);
  }

  // A stale rejection from a replaced connection while a retry waits must not schedule a second one.
  [Test, Timeout(60_000)]
  public async Task RejectionWhileARetryWaitsCountsOnce()
  {
    await using var h = await Create(timings: SlowRetry);
    var login = h.Session.Login();
    await WaitUntil(() => h.Server.Logons > 0, "the session sends its logon");
    var failures = h.Session.ReconnectAttempts;
    var connections = h.Server.Connections;
    var logons = h.Server.Logons;

    h.Post(LoggedOn(EResult.TryAnotherCM));
    h.Post(LoggedOn(EResult.TryAnotherCM));
    await Task.Delay(300);
    Assert.That(h.Session.ReconnectAttempts, Is.EqualTo(failures + 1));

    await h.AnswerNextLogon(EResult.OK, logons);
    await login.WaitAsync(TimeSpan.FromSeconds(10));
    await Holds(() => h.Session.IsLoggedOn && h.Server.Connections == connections + 1, "one retry", 1.5);
  }

  [Test, Timeout(60_000)]
  public async Task DisconnectDuringARetryWaitCancelsIt()
  {
    await using var h = await Create(timings: SlowRetry);
    var login = h.Session.Login();
    await h.AnswerNextLogon(EResult.TryAnotherCM, 0);
    await WaitUntil(() => h.Session.LastLogOnRetryDelay > TimeSpan.Zero, "the retry is scheduled");

    await Task.Run(() => h.Session.Disconnect()).WaitAsync(TimeSpan.FromSeconds(5));
    await login.WaitAsync(TimeSpan.FromSeconds(5));
    var connections = h.Server.Connections;
    await Holds(() => h.Server.Connections == connections && !h.Session.IsReconnecting, "the retry never runs", 2);
  }

  [Test, Timeout(60_000)]
  public async Task DisconnectDuringAStalledHandshakeReturns()
  {
    await using var h = await Create();
    h.Server.Behaviour = FakeCmServer.Mode.Silent;
    var login = h.Session.Login();
    await WaitUntil(() => h.Server.Connections > 0, "an attempt is in flight");

    await Task.Run(() => h.Session.Disconnect()).WaitAsync(TimeSpan.FromSeconds(5));
    await login.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.That(h.Session.IsAborted, Is.True);
    var connections = h.Server.Connections;
    await Holds(() => h.Server.Connections == connections, "no attempt after the disconnect", 1);
  }

  // Logout during an outage: Disconnect() then Dispose(), as DBusSteamClient.LogoutAsync does.
  [Test, Timeout(60_000)]
  public async Task LogoutDuringAnOutageStopsEverything()
  {
    await using var h = await Create();
    await h.LogOn();
    h.Server.Stop();
    h.Server.DropAll();
    await WaitUntil(() => h.Session.ReconnectAttempts >= 2, "it is reconnecting");

    await Task.Run(() =>
    {
      h.Session.Disconnect();
      h.Session.Dispose();
    }).WaitAsync(TimeSpan.FromSeconds(5));
    await h.Session.BackgroundWork.WaitAsync(TimeSpan.FromSeconds(5));

    var connections = h.Server.Connections;
    h.Server.Start();
    await Holds(() => h.Server.Connections == connections, "nothing reconnects after logout", 1);
  }

  [Test, Timeout(60_000)]
  public async Task DisposeEndsAPendingLogin()
  {
    await using var h = await Create();
    h.Server.Behaviour = FakeCmServer.Mode.Silent;
    var login = h.Session.Login();
    await WaitUntil(() => h.Server.Connections > 0, "an attempt is in flight");

    await Task.Run(h.Session.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
    await login.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.That(h.Session.IsLoggedOn, Is.False);
  }

  // A connection that drops before its callback is handled gets no logon, which would fail as NoConnection.
  [Test, Timeout(60_000)]
  public async Task ConnectionLostBeforeItsCallbackIsNotLoggedOn()
  {
    await using var h = await Create();
    using var release = new ManualResetEventSlim();
    h.Session.SubscribeCallback<Stall>(_ => release.Wait(TimeSpan.FromSeconds(30)));
    h.Post(new Stall());

    // Login() pumps the first callbacks on its caller's thread, which the stall would hold.
    var login = Task.Run(() => h.Session.Login());
    await WaitUntil(() => h.Session.SteamClient.IsConnected, "the attempt connects");
    h.Server.DropAll();
    await WaitUntil(() => !h.Session.SteamClient.IsConnected, "the session sees the drop");
    var logons = h.Server.Logons;
    release.Set();

    await h.AnswerNextLogon(EResult.OK, logons);
    await login.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(h.Session.IsLoggedOn, Is.True);
    Assert.That(h.Session.LastLogOnRetryDelay, Is.EqualTo(TimeSpan.Zero), "no logon went to the dropped connection");
  }

  // SteamKit releases a dropped connection before it clears IsConnected. A logon sent in between throws
  // instead of reporting NoConnection, and must be retried like one, not reported as a failed sign-in.
  [Test, Timeout(60_000)]
  public async Task LogOnThatSteamKitCannotSendIsRetried()
  {
    await using var h = await Create();
    using var release = new ManualResetEventSlim();
    h.Session.SubscribeCallback<Stall>(_ => release.Wait(TimeSpan.FromSeconds(30)));
    h.Post(new Stall());

    // Login() pumps the first callbacks on its caller's thread, which the stall would hold.
    var login = Task.Run(() => h.Session.Login());
    await WaitUntil(() => h.Session.SteamClient.IsConnected, "the attempt connects");
    h.Server.DropAll();
    await WaitUntil(() => !h.Session.SteamClient.IsConnected, "the session sees the drop");
    typeof(SteamKit2.Internal.CMClient).GetProperty(nameof(SteamClient.IsConnected))!.SetValue(h.Session.SteamClient, true);
    var logons = h.Server.Logons;
    release.Set();

    await h.AnswerNextLogon(EResult.OK, logons);
    await login.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(h.Session.IsLoggedOn, Is.True);
    Assert.That(h.AuthErrors, Is.Empty);
    Assert.That(h.Session.LastLogOnRetryDelay, Is.GreaterThan(TimeSpan.Zero), "the logon took the retry path");
  }

  // A connected attempt whose callback is late is waiting for the pump, not stuck.
  [Test, Timeout(60_000)]
  public async Task ConnectedAttemptAwaitingItsCallbackIsKept()
  {
    await using var h = await Create(timings: SessionHarness.Fast with { StalledAttempt = TimeSpan.FromMilliseconds(300) });
    using var release = new ManualResetEventSlim();
    h.Session.SubscribeCallback<Stall>(_ => release.Wait(TimeSpan.FromSeconds(30)));
    h.Post(new Stall());

    // Login() pumps the first callbacks on its caller's thread, which the stall would hold.
    var login = Task.Run(() => h.Session.Login());
    await WaitUntil(() => h.Session.SteamClient.IsConnected, "the attempt connects");
    var connections = h.Server.Connections;
    await Holds(() => h.Server.Connections == connections, "the watchdog keeps it", 1.5);
    release.Set();

    await h.AnswerNextLogon(EResult.OK, 0);
    await login.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(h.Session.IsLoggedOn, Is.True);
    Assert.That(h.Server.Connections, Is.EqualTo(connections));
  }

  // A CM restart can leave every cached endpoint stale; the refreshed list leads to the new CM.
  [Test, Timeout(60_000)]
  public async Task ServerListRefreshFindsARestartedCm()
  {
    using var directory = new FakeDirectory();
    using var restarted = new FakeCmServer();
    await using var h = await Create(configure: config => config.WithDirectoryFetch(true).WithWebAPIBaseAddress(directory.Address), timings: PatientRefresh);
    directory.Servers = [h.Server.EndPoint];
    await h.LogOn();

    directory.Servers = [restarted.EndPoint];
    h.Server.Stop();
    h.Server.DropAll();
    await WaitUntil(() => restarted.Logons > 0, "it finds the new CM", 15);
    Assert.That(h.Session.ReconnectAttempts, Is.EqualTo(ReconnectPolicy.AttemptsPerServerListRefresh), "the list is refreshed after the fourth failure");

    h.Post(LoggedOn(EResult.OK));
    await WaitUntil(() => h.Session.IsLoggedOn, "it logs on through the new CM");
    Assert.That(h.AuthErrors, Is.Empty);
  }

  [TestCase(false), TestCase(true), Timeout(60_000)]
  public async Task ServerListRefreshKeepsTheCacheOnAnEmptyOrFailedAnswer(bool apiFails)
  {
    using var directory = new FakeDirectory { Fails = apiFails };
    await using var h = await Create(configure: config => config.WithDirectoryFetch(true).WithWebAPIBaseAddress(directory.Address), timings: PatientRefresh);

    var requests = directory.Requests;
    var clock = Stopwatch.StartNew();
    await h.Session.RefreshServerList().WaitAsync(TimeSpan.FromSeconds(20));
    Assert.That(directory.Requests, Is.GreaterThan(requests), "the refresh did ask the Web API");
    Assert.That(clock.Elapsed, Is.LessThan(PatientRefresh.ServerListRefresh), "the refresh had its answer, not a timeout");

    var servers = h.Session.SteamClient.Configuration.ServerList.GetAllEndPoints();
    Assert.That(servers.Select(server => server.EndPoint), Is.EqualTo(new[] { h.Server.EndPoint }));
  }

  // A QR login's rejected logon reconnects as a recovery: no backoff, nothing counted against the cap.
  [Test, Timeout(60_000)]
  public async Task QrLogonErrorReconnectsWithoutBackingOff()
  {
    await using var h = await Create(interactive: true);
    h.SetField("loggingInWithQrCode", true);
    var login = h.Session.Login();

    await WaitUntil(() => h.Server.Logons > 0, "the session sends its logon");
    var failures = h.Session.ReconnectAttempts;
    var logons = h.Server.Logons;
    h.Post(LoggedOn(EResult.Fail));
    await WaitUntil(() => h.Server.Logons > logons, "it reconnects and logs on again");
    Assert.That(h.Session.ReconnectAttempts, Is.EqualTo(failures));

    h.Post(LoggedOn(EResult.OK));
    await login.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(h.Session.IsLoggedOn, Is.True);
    Assert.That(h.AuthErrors, Is.Empty);
  }

  // OnLogIn's Steam Guard path disconnects from inside a callback, then connects again.
  [Test, Timeout(60_000)]
  public async Task SteamGuardRejectionStartsANewAttempt()
  {
    await using var h = await Create();
    var login = h.Session.Login();
    await WaitUntil(() => h.Server.Logons > 0, "the session sends its logon");
    var connections = h.Server.Connections;
    var logons = h.Server.Logons;
    h.Post(LoggedOn(EResult.AccountLogonDenied));

    await h.AnswerNextLogon(EResult.OK, logons);
    await login.WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(h.Session.IsLoggedOn, Is.True);
    Assert.That(h.Server.Connections, Is.EqualTo(connections + 1));
  }

  // Disconnect() is not Dispose(): a session logged on again afterwards still reconnects.
  [Test, Timeout(60_000)]
  public async Task SessionLoggedOnAgainAfterDisconnectKeepsReconnecting()
  {
    await using var h = await Create();
    await h.LogOn();
    await Task.Run(() => h.Session.Disconnect()).WaitAsync(TimeSpan.FromSeconds(5));
    await h.LogOn();

    var logons = h.Server.Logons;
    h.Server.DropAll();
    await h.AnswerNextLogon(EResult.OK, logons);
    await WaitUntil(() => h.Session.IsLoggedOn, "it logs back on");
  }

  [Test, Timeout(60_000)]
  public async Task TokenRejectedDuringAReconnectStopsIt()
  {
    await using var h = await Create();
    await h.LogOn();

    var logons = h.Server.Logons;
    h.Server.DropAll();
    await h.AnswerNextLogon(EResult.Expired, logons);
    await WaitUntil(() => h.Session.IsAborted, "the session is aborted");
    var connections = h.Server.Connections;
    await Holds(() => h.Server.Connections == connections && !h.Session.IsReconnecting, "no retry with an expired token", 1);
  }

  // A disconnect SteamKit never reports must not hang Disconnect(), and with it logout.
  [Test, Timeout(60_000)]
  public async Task DisconnectReturnsWhenItsDisconnectNeverComes()
  {
    await using var h = await Create(online: false);
    h.SetField("liveConnections", 1);

    var clock = Stopwatch.StartNew();
    await Task.Run(() => h.Session.Disconnect()).WaitAsync(TimeSpan.FromSeconds(10));
    Assert.That(clock.Elapsed, Is.GreaterThanOrEqualTo(SessionHarness.Fast.DisconnectWait));
    Assert.That(h.Session.IsAborted, Is.True);
  }

  [Test, Timeout(60_000)]
  public async Task DisposeEndsAServerListRefresh()
  {
    using var silentApi = new TcpListener(IPAddress.Loopback, 0);
    silentApi.Start();
    var api = new Uri($"http://{silentApi.LocalEndpoint}/");
    await using var h = await Create(configure: config => config.WithDirectoryFetch(true).WithWebAPIBaseAddress(api), timings: PatientRefresh);

    var refresh = h.Session.RefreshServerList();
    await Task.Delay(200);
    h.Session.Dispose();
    await refresh.WaitAsync(TimeSpan.FromSeconds(2));
  }

  // A retry well past SteamKit's 100 ms socket poll.
  static readonly SessionTimings SlowRetry = SessionHarness.Fast with
  {
    NewReconnectPolicy = () => new ReconnectPolicy(new Random(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)),
  };

  // Lets a loaded machine finish a refresh the Web API does answer.
  static readonly SessionTimings PatientRefresh = SessionHarness.Fast with { ServerListRefresh = TimeSpan.FromSeconds(10) };
}
