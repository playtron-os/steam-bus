using Steam.Session;

namespace SteamBus.Tests;

public class SteamSessionReconnectTests
{
  [Test]
  public void RetriesQuicklyBeforeTheCap([Values] bool hasToken)
  {
    for (var backoff = 0; backoff < SteamSession.QuickReconnectAttempts; backoff++)
      Assert.That(SteamSession.NextReconnectStep(backoff, hasToken), Is.EqualTo(SteamSession.ReconnectStep.Quick));
  }

  [Test]
  public void SavedSessionKeepsRetryingAfterTheCap()
  {
    // An outage longer than the quick retries used to drop the session and report Unauthorized.
    Assert.That(SteamSession.NextReconnectStep(SteamSession.QuickReconnectAttempts, hasToken: true),
      Is.EqualTo(SteamSession.ReconnectStep.Slow));
  }

  [Test]
  public void SessionWithoutTokenStillGivesUp()
  {
    Assert.That(SteamSession.NextReconnectStep(SteamSession.QuickReconnectAttempts, hasToken: false),
      Is.EqualTo(SteamSession.ReconnectStep.GiveUp));
  }

  [TestCase(0, 15)]
  [TestCase(1, 30)]
  [TestCase(2, 60)]
  [TestCase(3, 60)]
  [TestCase(50, 60)]
  public void SlowRetriesBackOffToAMinute(int attempt, int seconds)
  {
    Assert.That(SteamSession.SlowReconnectDelay(attempt), Is.EqualTo(TimeSpan.FromSeconds(seconds)));
  }
}
