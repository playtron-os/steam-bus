using Playtron.Plugin;
using Steam.Cloud;
using Tmds.DBus;

namespace SteamBus.Tests;

public class CloudSyncCompletionTests
{
  [TestCase(0UL, 0UL)]
  [TestCase(100UL, 100UL)]
  [TestCase(101UL, 100UL)]
  public void ByteProgressNeverClaimsCommittedSuccess(ulong completed, ulong total)
  {
    var progress = CloudSyncCompletion.TransferProgress(completed, total);
    Assert.That(double.IsFinite(progress), Is.True);
    Assert.That(progress, Is.LessThan(100));
  }

  [TestCase("no-op")]
  [TestCase("deletion-only")]
  [TestCase("zero-byte-file")]
  public async Task SuccessDoesNotRequireByteProgress(string scenario)
  {
    List<CloudSyncProgress> progress = [];
    List<CloudSyncFailure> failures = [];
    bool persisted = false;
    await CloudSyncCompletion.CompleteUploadAsync(scenario, null, () => Task.FromResult(true),
      () => persisted = true, progress.Add, failures.Add);
    Assert.That(persisted, Is.True);
    Assert.That(failures, Is.Empty);
    Assert.That(progress.Single().Progress, Is.EqualTo(100));
    Assert.That(progress.Single().AppdId, Is.EqualTo(scenario));
    Assert.That(progress.Single().SyncState, Is.EqualTo((uint)SyncState.Upload));
  }

  [Test]
  public async Task TerminalSignalWaitsForCommitAndPersistence()
  {
    var commit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    List<string> order = [];
    var task = CloudSyncCompletion.CompleteUploadAsync("game", null, () => commit.Task,
      () => order.Add("persist"), _ => order.Add("success"), _ => order.Add("failure"));
    Assert.That(order, Is.Empty);
    Assert.That(task.IsCompleted, Is.False);
    commit.SetResult(true);
    await task;
    Assert.That(order, Is.EqualTo(new[] { "persist", "success" }));
  }

  [TestCase(false, false)]
  [TestCase(true, true)]
  public void RejectedBatchOrFailedTransferEmitsFailureAndThrows(bool committed, bool transferFailed)
  {
    List<CloudSyncProgress> progress = [];
    List<CloudSyncFailure> failures = [];
    bool persisted = false;
    Assert.ThrowsAsync<DBusException>(async () => await CloudSyncCompletion.CompleteUploadAsync("game",
      transferFailed ? DbusErrors.CloudFileUpload : null, () => Task.FromResult(committed),
      () => persisted = true, progress.Add, failures.Add));
    Assert.That(persisted, Is.False);
    Assert.That(progress, Is.Empty);
    Assert.That(failures.Single().Error, Is.EqualTo(DbusErrors.CloudFileUpload));
    Assert.That(failures.Single().SyncState, Is.EqualTo((uint)SyncState.Upload));
  }

  [Test]
  public void LocalPersistenceFailureCannotEmitSuccess()
  {
    List<CloudSyncProgress> progress = [];
    List<CloudSyncFailure> failures = [];
    Assert.ThrowsAsync<DBusException>(async () => await CloudSyncCompletion.CompleteUploadAsync("game", null,
      () => Task.FromResult(true), () => throw new IOException("disk full"), progress.Add, failures.Add));
    Assert.That(progress, Is.Empty);
    Assert.That(failures, Has.Count.EqualTo(1));
  }
  [TestCase("no-op-download")]
  [TestCase("zero-byte-download")]
  public async Task DownloadsEmitTerminalSuccessAfterLocalPersistence(string scenario)
  {
    List<string> order = [];
    CloudSyncProgress? terminal = null;
    await CloudSyncCompletion.CompleteDownloadAsync(scenario, null, () => order.Add("persist"),
      progress => { terminal = progress; order.Add("success"); }, _ => Assert.Fail("Unexpected failure"));
    Assert.That(order, Is.EqualTo(new[] { "persist", "success" }));
    Assert.That(terminal!.Value.Progress, Is.EqualTo(100));
    Assert.That(terminal.Value.SyncState, Is.EqualTo((uint)SyncState.Download));
  }

  [Test]
  public void FailedDownloadDoesNotEmitSuccess()
  {
    List<CloudSyncFailure> failures = [];
    bool persisted = false;
    Assert.ThrowsAsync<DBusException>(async () => await CloudSyncCompletion.CompleteDownloadAsync("game",
      DbusErrors.CloudFileDownload, () => persisted = true, _ => Assert.Fail("Unexpected success"), failures.Add));
    Assert.That(persisted, Is.False);
    Assert.That(failures.Single().Error, Is.EqualTo(DbusErrors.CloudFileDownload));
    Assert.That(failures.Single().SyncState, Is.EqualTo((uint)SyncState.Download));
  }

}
