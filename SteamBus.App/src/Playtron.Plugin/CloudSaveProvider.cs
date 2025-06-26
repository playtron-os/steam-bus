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
  public string[] platforms { get; set; }
}

[StructLayout(LayoutKind.Sequential)]
public struct CloudSyncProgress
{
  public string AppdId;
  public double Progress;
  public uint SyncState;
}

[StructLayout(LayoutKind.Sequential)]
public struct CloudSyncFailure
{
  public string AppdId;
  public string Error;
  public ulong Local;
  public ulong Remote;
  public ulong QuotaUsage;
  public ulong Quota;
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
  Task CloudSaveDownloadAsync(string appid, string platform, bool force, CloudPathObject[] paths);
  Task CloudSaveUploadAsync(string appid, string platform, bool force, CloudPathObject[] paths);

  Task<IDisposable> WatchCloudSaveSyncProgressedAsync(Action<CloudSyncProgress> reply);
  Task<IDisposable> WatchCloudSaveSyncFailedAsync(Action<CloudSyncFailure> reply);
}


