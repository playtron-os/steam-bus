using Tmds.DBus;

namespace Playtron.Plugin;

[Dictionary]
public class CloudPathObject
{
  public string alias = "";
  public string path = "";

  public string pattern = "*";

  public bool recursive = false;
}

[DBusInterface("one.playtron.plugin.CloudSaveProvider")]
public interface ICloudSaveProvider : IDBusObject
{
  Task CloudSaveDownloadAsync(string appid, CloudPathObject[] paths);
  Task CloudSaveUploadAsync(string appid, CloudPathObject[] paths);
}


