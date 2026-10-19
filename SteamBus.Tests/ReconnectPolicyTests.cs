using Steam.Session;

namespace SteamBus.Tests;

public class ReconnectPolicyTests
{
  // A fixed seed keeps the jitter deterministic within a single test run
  const int RandomSeed = 1234;

  static ReconnectPolicy NewPolicy() => new(new Random(RandomSeed));

  static (TimeSpan min, TimeSpan max) JitterBounds(TimeSpan nominal)
  {
    var min = TimeSpan.FromSeconds(nominal.TotalSeconds * 0.8);
    var max = nominal < ReconnectPolicy.MaxDelay
      ? TimeSpan.FromSeconds(nominal.TotalSeconds * 1.2)
      : ReconnectPolicy.MaxDelay;
    return (min, max);
  }

  [Test]
  public void TestDelayGrowsExponentially()
  {
    var policy = NewPolicy();

    var expectedNominal = ReconnectPolicy.BaseDelay;
    for (var attempt = 1; attempt <= 8; attempt++)
    {
      var delay = policy.RecordFailureAndGetDelay();
      var (min, max) = JitterBounds(expectedNominal);

      Assert.That(delay, Is.InRange(min, max),
        $"Attempt #{attempt} delay {delay} should be within 20% of {expectedNominal}");
      Assert.That(policy.Attempts, Is.EqualTo(attempt));

      var doubled = TimeSpan.FromSeconds(expectedNominal.TotalSeconds * 2);
      expectedNominal = doubled < ReconnectPolicy.MaxDelay ? doubled : ReconnectPolicy.MaxDelay;
    }
  }

  [Test]
  public void TestDelayNeverExceedsMaxDelay()
  {
    var policy = NewPolicy();

    // Far past the point where the exponential curve exceeds the cap
    for (var attempt = 0; attempt < 50; attempt++)
    {
      var delay = policy.RecordFailureAndGetDelay();
      Assert.That(delay, Is.LessThanOrEqualTo(ReconnectPolicy.MaxDelay));
      Assert.That(delay, Is.GreaterThan(TimeSpan.Zero));
    }
  }

  [Test]
  public void TestInteractiveDelayIsClamped()
  {
    var policy = NewPolicy();

    for (var attempt = 0; attempt < 20; attempt++)
    {
      var delay = policy.RecordFailureAndGetDelay(interactive: true);
      Assert.That(delay, Is.LessThanOrEqualTo(ReconnectPolicy.InteractiveMaxDelay),
        $"Attempt #{policy.Attempts}: interactive delay {delay} should never exceed the clamp");
      Assert.That(delay, Is.GreaterThan(TimeSpan.Zero));
    }
  }

  [Test]
  public void TestInteractiveClampStillRecordsFailures()
  {
    var policy = NewPolicy();

    policy.RecordFailureAndGetDelay(interactive: true);
    policy.RecordFailureAndGetDelay(interactive: true);
    Assert.That(policy.Attempts, Is.EqualTo(2));

    // Switching back to non-interactive continues the escalated curve
    for (var attempt = 0; attempt < 10; attempt++)
      policy.RecordFailureAndGetDelay(interactive: true);
    var delay = policy.RecordFailureAndGetDelay();
    Assert.That(delay, Is.GreaterThan(ReconnectPolicy.InteractiveMaxDelay),
      "Non-interactive delay should follow the full exponential curve");
  }

  [Test]
  public void TestResetRestartsBackoff()
  {
    var policy = NewPolicy();

    for (var attempt = 0; attempt < 10; attempt++)
      policy.RecordFailureAndGetDelay();

    policy.Reset();
    Assert.That(policy.Attempts, Is.EqualTo(0));

    var delay = policy.RecordFailureAndGetDelay();
    var (min, max) = JitterBounds(ReconnectPolicy.BaseDelay);
    Assert.That(delay, Is.InRange(min, max), "Delay after reset should restart from the base delay");
  }

  [Test]
  public void TestShouldRefreshServerListEveryNthAttempt()
  {
    var policy = NewPolicy();

    Assert.That(policy.ShouldRefreshServerList(), Is.False, "No refresh before any failure");

    for (var attempt = 1; attempt <= ReconnectPolicy.AttemptsPerServerListRefresh * 3; attempt++)
    {
      policy.RecordFailureAndGetDelay();
      var expected = attempt % ReconnectPolicy.AttemptsPerServerListRefresh == 0;
      Assert.That(policy.ShouldRefreshServerList(), Is.EqualTo(expected),
        $"Attempt #{attempt}: refresh expected={expected}");
    }
  }

  [Test]
  public void TestInteractiveAttemptsCap()
  {
    var policy = NewPolicy();

    for (var attempt = 1; attempt < ReconnectPolicy.InteractiveMaxAttempts; attempt++)
    {
      policy.RecordFailureAndGetDelay();
      Assert.That(policy.HasExceededInteractiveAttempts, Is.False,
        $"Attempt #{attempt} should not exceed the interactive cap yet");
    }

    policy.RecordFailureAndGetDelay();
    Assert.That(policy.HasExceededInteractiveAttempts, Is.True);

    policy.Reset();
    Assert.That(policy.HasExceededInteractiveAttempts, Is.False);
  }
}
