using Playtron.Plugin;
using Tmds.DBus;

namespace Steam.Cloud;

public static class CloudSyncCompletion
{
  // Byte progress is not terminal: the upload batch and local cache still need committing.
  public static double TransferProgress(ulong completed, ulong total) =>
    total == 0 ? 0 : Math.Min(99, (double)completed / total * 100);

  public static DBusException Failure(string appId, Action<CloudSyncFailure> failed, SyncState operation = SyncState.Upload)
  {
    var error = operation == SyncState.Upload ? DbusErrors.CloudFileUpload : DbusErrors.CloudFileDownload;
    failed(new CloudSyncFailure
    {
      AppdId = appId,
      Error = error,
      SyncState = (uint)operation,
    });
    return new DBusException(error, "Cloud save synchronization did not complete");
  }

  // Steam answers Busy, for uploads and downloads alike, while an earlier upload of the file was begun and never committed.
  public static string FileError(Exception exception, string? current, SyncState operation) =>
    exception is CloudRefusedException { Result: SteamKit2.EResult.Busy }
      ? DbusErrors.CloudBusy
      : current ?? (operation == SyncState.Upload ? DbusErrors.CloudFileUpload : DbusErrors.CloudFileDownload);

  static DBusException Busy(string appId, Action<CloudSyncFailure> failed, SyncState operation)
  {
    failed(new CloudSyncFailure
    {
      AppdId = appId,
      Error = DbusErrors.CloudBusy,
      SyncState = (uint)operation,
    });
    return new DBusException(DbusErrors.CloudBusy, "Steam still holds an unfinished earlier upload of these saves");
  }

  public static Task CompleteUploadAsync(
    string appId,
    string? transferError,
    Func<Task<bool>> commit,
    Action persist,
    Action<CloudSyncProgress> progressed,
    Action<CloudSyncFailure> failed) =>
    CompleteAsync(appId, SyncState.Upload, transferError, commit, persist, progressed, failed);

  public static Task CompleteDownloadAsync(
    string appId, string? transferError, Action persist,
    Action<CloudSyncProgress> progressed, Action<CloudSyncFailure> failed) =>
    CompleteAsync(appId, SyncState.Download, transferError, () => Task.FromResult(true), persist, progressed, failed);

  static async Task CompleteAsync(
    string appId, SyncState operation, string? transferError, Func<Task<bool>> commit,
    Action persist, Action<CloudSyncProgress> progressed, Action<CloudSyncFailure> failed)
  {
    try
    {
      if (!await commit()) throw new IOException("Steam did not acknowledge the upload batch");
      if (transferError != null) throw new IOException(transferError);
      persist();
    }
    catch (Exception)
    {
      if (transferError == DbusErrors.CloudBusy) throw Busy(appId, failed, operation);
      throw Failure(appId, failed, operation);
    }
    progressed(new CloudSyncProgress
    {
      AppdId = appId,
      Progress = 100,
      SyncState = (uint)operation,
    });
  }
}
