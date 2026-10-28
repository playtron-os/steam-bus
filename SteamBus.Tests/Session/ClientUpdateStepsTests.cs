using System.Runtime.InteropServices;
using Playtron.Plugin;
using Steam.Config;
using Xdg.Directories;

namespace SteamBus.Tests.Session;

// In this namespace for PrivateHome: an update writes the client's depot config.
public class ClientUpdateStepsTests
{
  // A fresh install on a headless display (2026-10-28, bootstrapper from Steam 1.0.0.87), trimmed:
  // the 32-bit client downloads and restarts into the 64-bit one, which downloads again.
  static readonly string[] FreshInstallLines = [
    "[2026-10-28 11:53:17] Verifying installation...",
    "[2026-10-28 11:53:17] Downloading Update...",
    "[2026-10-28 11:53:17] Checking for available update...",
    "[2026-10-28 11:53:17] Downloaded new manifest: /steam_client_ubuntu12 version 1792618285, installed version 0, existing pending version 0",
    "[2026-10-28 11:53:18] Downloading update (3,107 of 504,927 KB)...",
    "[2026-10-28 11:53:18] Set status message: Downloading update (3,107 of 504,927 KB)...",
    "[2026-10-28 11:53:31] Downloading update (252,463 of 504,927 KB)...",
    "[2026-10-28 11:53:46] Downloading update (504,927 of 504,927 KB)...",
    "[2026-10-28 11:53:46] Download Complete.",
    "[2026-10-28 11:53:46] Extracting package...",
    "[2026-10-28 11:53:46] Set status message: Extracting package...",
    "[----] Extracting package...",
    "[2026-10-28 11:54:06] Installing update...",
    "[----] Installing update...",
    "[2026-10-28 11:54:13] Cleaning up...",
    "[2026-10-28 11:54:13] Update complete, launching...",
    "steam.sh[3104769]: Restarting Steam by request...",
    "steam.sh[3104769]: Running Steam on nixos 26.11 64-bit",
    "[2026-10-28 11:54:25] Verifying installation...",
    "[2026-10-28 11:54:25] Downloading update...",
    "[2026-10-28 11:54:25] Checking for available updates...",
    "[2026-10-28 11:54:25] Downloaded new manifest: /steam_client_steamrt64 version 1792618285, installed version 0, existing pending version 0",
    "[2026-10-28 11:54:26] Set status message: Downloading update...",
    "[2026-10-28 11:54:26] Downloading update (9,995 of 424,455 KB)...",
    "[2026-10-28 11:54:36] Downloading update (212,227 of 424,455 KB)...",
    "[2026-10-28 11:54:46] Downloading update (424,455 of 424,455 KB)...",
    "[2026-10-28 11:54:46] Download complete.",
    "[2026-10-28 11:54:47] Extracting package...",
    "[2026-10-28 11:55:08] Installing update...",
    "[2026-10-28 11:55:17] Update complete, launching Steam...",
    "steam.sh[3104769]: Restarting Steam by request...",
    "steam.sh[3104769]: Running Steam on nixos 26.11 64-bit",
    "[2026-10-28 11:55:19] Verifying installation...",
    "[2026-10-28 11:55:20] Verification complete",
    "Steam Runtime Launch Service: steam-runtime-launcher-service is running pid 3329329",
  ];

  sealed record Signal(string Kind, uint Step = 0, uint Steps = 0, uint Stage = 0, double Progress = 0);

  static async Task<List<Signal>> Replay(IEnumerable<string> lines)
  {
    var tools = Directory.CreateDirectory(Path.Join(BaseDirectory.DataHome, "steambus", "tools")).FullName;
    var store = await DepotConfigStore.CreateAsync([]);
    var toolsStore = await DepotConfigStore.CreateAsync([tools]);
    // The display is only used to start the client, which a replay never does
    var app = new SteamClientApp(null!, store, toolsStore) { UpdateSteps = new(Architecture.X64) };
    var signals = new List<Signal>();
    app.OnDependencyInstallStepChanged = step => signals.Add(new Signal("step", step.step, step.steps));
    app.OnDependencyInstallProgressed = progress => signals.Add(new Signal("progress", Stage: progress.Stage, Progress: progress.Progress));
    app.OnDependencyInstallCompleted = _ => signals.Add(new Signal("completed"));
    app.OnDependencyInstallFailed = _ => signals.Add(new Signal("failed"));
    foreach (var line in lines) app.OnClientOutput(line);
    return signals;
  }

  [Test]
  public async Task AFreshInstallReportsBothDownloadsAsStepsOfOneUpdate()
  {
    var signals = await Replay(FreshInstallLines);

    var downloading = (uint)DownloadStage.Downloading;
    var installing = (uint)DownloadStage.Verifying;
    Assert.That(signals.Select(s => s.Kind switch {
      "progress" => $"progress {s.Stage} {s.Progress:F0}",
      "step" => $"step {s.Step}/{s.Steps}",
      _ => s.Kind,
    }), Is.EqualTo(new[] {
      "step 1/2",
      // The line that starts an update reports nothing, its duplicate status line does
      $"progress {downloading} 1",
      $"progress {downloading} 50",
      $"progress {downloading} 100",
      $"progress {installing} 100",
      "step 2/2",
      $"progress {downloading} 2",
      $"progress {downloading} 50",
      $"progress {downloading} 100",
      $"progress {installing} 100",
      "completed",
    }));
  }

  [Test]
  public async Task AnUpdateOfTheSixtyFourBitClientIsOneStep()
  {
    var signals = await Replay(FreshInstallLines.Skip(17));

    Assert.That(signals.Where(s => s.Kind == "step").Select(s => (s.Step, s.Steps)), Is.EqualTo(new[] { (1u, 1u) }));
    Assert.That(signals.Last().Kind, Is.EqualTo("completed"));
  }

  [TestCase("steam_client_ubuntu12", Architecture.X64, 2u)]
  [TestCase("steam_client_ubuntu12", Architecture.Arm64, 1u)]
  [TestCase("steam_client_steamrt64", Architecture.X64, 1u)]
  public void ExpectsTheSixtyFourBitClientAfterTheThirtyTwoBitOne(string manifest, Architecture architecture, uint steps)
  {
    Assert.That(ClientUpdateSteps.ExpectedSteps(manifest, architecture), Is.EqualTo(steps));
  }

  [Test]
  public void ParsesTheManifestName()
  {
    Assert.That(ClientUpdateSteps.ParseManifest(FreshInstallLines[3]), Is.EqualTo("steam_client_ubuntu12"));
    Assert.That(ClientUpdateSteps.ParseManifest(FreshInstallLines[0]), Is.Null);
  }

  [Test]
  public void AManifestRepeatedBeforeAnyDownloadIsTheSameStep()
  {
    var steps = new ClientUpdateSteps(Architecture.X64);
    steps.OnManifest("steam_client_ubuntu12", updating: false);
    steps.Start();
    Assert.That(steps.OnManifest("steam_client_ubuntu12", updating: true), Is.False);
    Assert.That((steps.Step, steps.Steps), Is.EqualTo((1u, 2u)));
  }
}
