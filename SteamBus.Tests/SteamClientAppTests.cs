namespace SteamBus.Tests;

public class SteamClientAppTests
{
  // Output of the restarted client after an update on a headless display with no account logged in
  static readonly string[] HeadlessRestartLines = [
    "steam.sh[7444]: Restarting Steam by request...",
    "steam.sh[7444]: Running Steam on fedora 43 64-bit",
    "steam.sh[7444]: Log already open",
    "steam.sh[7444]: Steam client's requirements are satisfied",
    "[2026-09-14 16:08:53] Startup - updater built Sep  3 2026 01:31:48",
    "[2026-09-14 16:08:53] Verifying installation...",
    "[2026-09-14 16:08:53] Verification complete",
    "[2026-09-14 16:08:53] No more messages are expected - exiting",
    "XOpenIM() failed, LANG = C",
    "Running query: 1 - GpuTopology",
    "sh: line 1: pactl: command not found",
    "Steam Runtime Launch Service: starting steam-runtime-launcher-service",
    "Steam Runtime Launch Service: steam-runtime-launcher-service is running pid 9064",
    "[2026-09-14 16:10:54] Background update loop checking for update. . .",
    "[2026-09-14 16:10:54] Download skipped: /steam_client_ubuntu12 version 1788652215, installed version 1788652215, existing pending version 0",
  ];

  [Test]
  public void HeadlessRestartPrintsRunningLineButNoStartedLine()
  {
    Assert.That(HeadlessRestartLines.Any(SteamClientApp.IsClientRunningLine), Is.True,
      "A line must mark the restarted client as running, otherwise a client update never completes");
    Assert.That(HeadlessRestartLines.Any(SteamClientApp.IsClientStartedLine), Is.False,
      "Headless client without an account never prints a started/UI line");
  }

  [TestCase("Steam Runtime Launch Service: steam-runtime-launcher-service is running pid 9064")]
  [TestCase("Desktop state changed: desktop: { pos:    0,   0 size: 8960,2880 } primary: { pos: 5120, 720 size: 3840,2160 }")]
  [TestCase("reaping pid: 29674 -- steam")]
  [TestCase("Starting steamwebhelper under bootstrap log path")]
  public void RecognisesClientRunningLines(string line)
  {
    Assert.That(SteamClientApp.IsClientRunningLine(line), Is.True);
  }

  [TestCase("Steam Runtime Launch Service: starting steam-runtime-launcher-service")]
  [TestCase("[2026-09-14 16:08:53] Verification complete")]
  [TestCase("[2026-09-14 16:07:37] Downloading update (6,744 of 496,367 KB)...")]
  [TestCase("[----] Installing update...")]
  [TestCase("[2026-09-14 16:08:51] Update complete, launching...")]
  public void IgnoresLinesPrintedBeforeTheClientRuns(string line)
  {
    Assert.That(SteamClientApp.IsClientRunningLine(line), Is.False);
    Assert.That(SteamClientApp.IsClientStartedLine(line), Is.False);
  }

  [TestCase("Desktop state changed: desktop: { pos:    0,   0 size: 7936,2304 } primary: { pos: 4096, 144 size: 3840,2160 }", true)]
  [TestCase("reaping pid: 1399598 -- steam", true)]
  [TestCase("Starting steamwebhelper under bootstrap log path", true)]
  [TestCase("Steam Runtime Launch Service: steam-runtime-launcher-service is running pid 9064", false)]
  public void StartedLinesRequireTheUiOrWebHelper(string line, bool expected)
  {
    Assert.That(SteamClientApp.IsClientStartedLine(line), Is.EqualTo(expected));
  }

  [TestCase("[2026-09-14 16:08:51] Update complete, launching...", true)]
  [TestCase("[2026-07-10 15:36:09] Update complete, launching Steam...", true)]
  [TestCase("[2026-09-14 16:08:51] Set status message: Update complete, launching...", true)]
  [TestCase("[2026-09-14 16:08:50] Cleaning up...", false)]
  [TestCase("[2026-09-14 16:08:50] Set status message: Installing update...", false)]
  [TestCase("[2026-09-14 16:10:54] Checking for available updates...", false)]
  public void RecognisesUpdateInstalledLines(string line, bool expected)
  {
    Assert.That(SteamClientApp.IsUpdateInstalledLine(line), Is.EqualTo(expected));
  }
}
