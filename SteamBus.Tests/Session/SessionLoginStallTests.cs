using Steam.Session;
using SteamKit2;
using static SteamBus.Tests.Session.SessionHarness;

namespace SteamBus.Tests.Session;

// Logins that Steam turns away for a while, or leaves unanswered.
[NonParallelizable]
public class SessionLoginStallTests
{
  // Giving up would sign the user out over a passing problem on Steam's side.
  [TestCase(EResult.Timeout), TestCase(EResult.Busy), TestCase(EResult.RateLimitExceeded), Timeout(60_000)]
  public async Task SavedSessionWaitsOutAPassingRejection(EResult result)
  {
    await using var h = await Create();
    await h.LogOn();

    var logons = h.Server.Logons;
    h.Server.DropAll();
    await h.AnswerNextLogon(result, logons);
    await h.AnswerNextLogon(EResult.OK, logons + 1);
    await WaitUntil(() => h.Session.IsLoggedOn, "it logs back on");
    Assert.That(h.AuthErrors, Is.Empty);
    if (result == EResult.RateLimitExceeded)
      Assert.That(h.Session.LastLogOnRetryDelay, Is.GreaterThanOrEqualTo(SessionHarness.Fast.RateLimitRetry));
  }

  [Test, Timeout(60_000)]
  public async Task InteractiveRateLimitIsReported()
  {
    await using var h = await Create(interactive: true);
    var login = h.Session.Login();
    await h.AnswerNextLogon(EResult.RateLimitExceeded, 0);
    await login.WaitAsync(TimeSpan.FromSeconds(10));

    Assert.That(h.AuthErrors, Is.EqualTo(new[] { DbusErrors.RateLimitExceeded }));
    Assert.That(h.Session.IsAborted, Is.True);
  }

  // The fake CM never answers the sign-in request, and SteamKit cancels it after about 10 s.
  [Test, Timeout(90_000)]
  public async Task PasswordLoginEndsWhenSteamNeverAnswers()
  {
    await using var h = await Create(savedToken: false);
    var login = h.Session.Login();
    await login.WaitAsync(TimeSpan.FromSeconds(45));

    Assert.That(h.AuthErrors, Is.EqualTo(new[] { DbusErrors.Timeout }));
    Assert.That(h.Session.IsAborted, Is.True);
  }

  // A dropped connection cancels the request too; that one is retried, not reported.
  [Test, Timeout(60_000)]
  public async Task PasswordLoginReconnectsWhenItsConnectionDrops()
  {
    await using var h = await Create(savedToken: false);
    var login = h.Session.Login();
    // A password login's first request after the handshake is its sign-in request, counted as a logon.
    await WaitUntil(() => h.Server.Logons > 0, "the sign-in request is sent");

    h.Server.DropAll();
    await WaitUntil(() => h.Server.Logons > 1, "it reconnects and asks again", 10);
    Assert.That(h.AuthErrors, Is.Empty);
  }
}
