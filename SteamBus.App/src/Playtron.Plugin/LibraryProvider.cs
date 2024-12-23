using System.Collections.Generic;
using Tmds.DBus;

namespace Playtron.Plugin;

/// Type alias for an install option description tuple.
/// DBus does not actually support structs, so they are instead represented as
/// typed tuples.
using InstallOptionDescription = (string, string, string[]);

/// InstallOption describes a single install option that can be used when
/// installing an app.
public class InstallOption
{
  public string name;
  public string description;
  public string[] values = [];

  public InstallOption(string name, string description)
  {
    this.name = name;
    this.description = description;
    this.values = [];
  }

  public InstallOption(string name, string description, string[] values)
  {
    this.name = name;
    this.description = description;
    this.values = values;
  }

  /// Convert the option to a DBus-friendly tuple
  public InstallOptionDescription AsTuple()
  {
    return (this.name, this.description, this.values);
  }
}

/// Structure for de-serializing the install options dictionary.
[Dictionary]
public class InstallOptions
{
  public string branch = "public";
  public string language = "english";
  public string version = "";
  public string os = "";
  public string architecture = "";
}

[Dictionary]
public class LibraryProviderProperties
{
  public string Name = "Steam";
  public string Provider = "Steam";
}

/// Interface definition for a library provider
[DBusInterface("one.playtron.plugin.LibraryProvider")]
public interface IPluginLibraryProvider : IDBusObject
{
  // Properties
  Task<object> GetAsync(string prop);
  Task<LibraryProviderProperties> GetAllAsync();
  // Methods

  // Downloads and installs the app with the given app id to the target disk.
  // Available install options can be queried using the `GetInstallOptionsAsync`
  // method.
  Task InstallAsync(string appId, string disk, InstallOptions options);
  //Task Update(appId);
  //Task Uninstall(appId);
  Task<InstallOptionDescription[]> GetInstallOptionsAsync(string appId);
  //Task GetProviderItem(appId);
  //Task GetProviderItems();
  //Task Refresh();
  //Task DiskAdded(diskPath); // We could just listen to udisks2 directly
  //Task DiskRemoved(diskPath);
  Task<CloudPathObject[]> GetSavePathPatternsAsync(string appId, string platform);

  // Signals
  //Task<IDisposable> WatchInstallProgressedAsync(string appId, float percent);
  Task<IDisposable> WatchInstallProgressedAsync(Action<(string, float)> reply);
  // WatchInstallCompleted(appId)
  // WatchInstallFailed(appId, code, reason)
  // WatchUpdateProgressed(appId, percent)
  // WatchUpdateCompleted(appId)
  // WatchUpdateFailed(appId, code, reason)
}


