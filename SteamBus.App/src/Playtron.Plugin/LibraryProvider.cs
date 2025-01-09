using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Tmds.DBus;

namespace Playtron.Plugin;

/// Type alias for an install option description tuple.
/// DBus does not actually support structs, so they are instead represented as
/// typed tuples.
using InstallOptionDescription = (string, string, string[]);

[StructLayout(LayoutKind.Sequential)]
public struct InstalledAppDescription
{
  public string AppId { get; set; }
  public string InstalledPath { get; set; }
  public ulong DownloadedBytes { get; set; }
  public ulong TotalDownloadSize { get; set; }
}

public enum DownloadStage
{
  Preallocating,
  Downloading,
  Verifying,
  Installing,
}

public enum InstallOptionType
{
  [Description("Version of the game to install")]
  Version,
  [Description("Branch to install from")]
  Branch,
  [Description("Language of the game to install")]
  Language,
  [Description("OS platform version of the game")]
  OS,
  [Description("Architecture version of the game")]
  Architecture,
}

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

public enum AppType
{
  Game = 0,

  Application = 1,

  Tool = 2,

  Dlc = 3,

  Music = 4,

  Config = 5,

  Demo = 6,

  Beta = 7,
}

[Dictionary]
public class LibraryProviderProperties
{
  public string Name = "Steam";
  public string Provider = "Steam";
}

public struct ProviderItem
{
  public string id;
  public string name;
  public string provider;
  public uint app_type;
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
  Task<int> InstallAsync(string appId, string disk, InstallOptions options);

  // Gets information about installed apps
  Task<InstalledAppDescription[]> GetInstalledAppsAsync();

  // Pauses the current install that is in progress
  Task PauseInstallAsync();

  //Task Update(appId);
  //Task Uninstall(appId);

  Task<InstallOptionDescription[]> GetInstallOptionsAsync(string appId);

  Task<ProviderItem> GetProviderItemAsync(string appId);
  Task<ProviderItem[]> GetProviderItemsAsync();
  Task RefreshAsync();
  //Task DiskAdded(diskPath); // We could just listen to udisks2 directly
  //Task DiskRemoved(diskPath);
  Task<CloudPathObject[]> GetSavePathPatternsAsync(string appId, string platform);

  // Signals
  Task<IDisposable> WatchLibraryUpdatedAsync(Action<ProviderItem[]> reply);
  Task<IDisposable> WatchInstallProgressedAsync(Action<(string appId, double progress, DownloadStage stage)> reply);
  Task<IDisposable> WatchInstallCompletedAsync(Action<string> reply);
  Task<IDisposable> WatchInstallFailedAsync(Action<(string appId, string error)> reply);
}


