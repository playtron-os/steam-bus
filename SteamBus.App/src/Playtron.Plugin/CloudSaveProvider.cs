using Tmds.DBus;

using Steam.Cloud;
using System.Runtime.InteropServices;
namespace Playtron.Plugin;

public struct CloudPathObject
{
  public string alias { get; set; }
  public string path { get; set; }
  public string pattern { get; set; }
  public bool recursive { get; set; }
}

[StructLayout(LayoutKind.Sequential)]
public struct CloudSyncProgress
{
  public string AppdId;
  public uint Progress;
  public uint SyncState;
}

public enum SyncState
{
  Download,
  Upload
}

[DBusInterface("one.playtron.plugin.CloudSaveProvider")]
public interface ICloudSaveProvider : IDBusObject
{
  Task CloudSaveDownloadAsync(string appid, string platform, CloudPathObject[] paths);
  Task CloudSaveUploadAsync(string appid, string platform, CloudPathObject[] paths);
  // Task CloudSaveResolveConflict(string appid, string platform, CloudPathObject[] paths, bool keepLocal);

  Task<IDisposable> WatchCloudSaveSyncProgressedAsync(Action<CloudSyncProgress> reply);
  Task<IDisposable> WatchCloudSaveSyncFailedAsync(Action<(string appid, string error, CloudUtils.ConflictDetails? conflictDetails)> reply);
}


