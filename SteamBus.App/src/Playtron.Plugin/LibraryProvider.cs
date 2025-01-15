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
  public string Version { get; set; }
  public string LatestVersion { get; set; }
  public bool UpdatePending { get; set; }
  public string Os { get; set; }
}

[StructLayout(LayoutKind.Sequential)]
public struct InstallProgressedDescription
{
  public string AppId { get; set; }
  public uint Stage { get; set; }
  public ulong DownloadedBytes { get; set; }
  public ulong TotalDownloadSize { get; set; }
  public double Progress { get; set; }
}

[StructLayout(LayoutKind.Sequential)]
public struct InstallStartedDescription
{
  public string AppId { get; set; }
  public string Version { get; set; }
  public string InstallDirectory { get; set; }
  public ulong TotalDownloadSize { get; set; }
  public bool RequiresInternetConnection { get; set; }
}

public enum DownloadStage
{
  Preallocating,
  Downloading,
  Verifying,
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

[StructLayout(LayoutKind.Sequential)]
public struct ItemMetadata
{
  public string Name { get; set; }
  public ulong InstallSize { get; set; }
  public bool RequiresInternetConnection { get; set; }
  public string[] CloudSaveFolders { get; set; }
  public string InstalledVersion { get; set; }
  public string LatestVersion { get; set; }
}

public enum LaunchType
{
  Unknown,
  Launcher,
  Game,
  Tool,
  Document,
  Other,
}

[StructLayout(LayoutKind.Sequential)]
public struct LaunchOption
{
  public string Description { get; set; }
  public string Executable { get; set; }
  public string Arguments { get; set; }
  public string WorkingDirectory { get; set; }
  public (string, string)[] Environment { get; set; }
  public uint LaunchType { get; set; }
  public string[] HardwareTags { get; set; }
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
  Task UninstallAsync(string appId);
  Task<string> MoveItemAsync(string appId, string disk);

  // Gets information about installed apps
  Task<InstalledAppDescription[]> GetInstalledAppsAsync();

  // Pauses the current install that is in progress
  Task PauseInstallAsync();

  Task<ItemMetadata> GetAppMetadataAsync(string appId);

  Task<InstallOptionDescription[]> GetInstallOptionsAsync(string appId);

  Task<LaunchOption[]> GetLaunchOptionsAsync(string appId);

  Task<string> GetPostInstallStepsAsync(string appId);

  Task<ProviderItem> GetProviderItemAsync(string appId);
  Task<ProviderItem[]> GetProviderItemsAsync();
  Task RefreshAsync();

  Task<CloudPathObject[]> GetSavePathPatternsAsync(string appId, string platform);

  Task PreLaunchHookAsync(string appId, bool usingOfflineMode);
  Task PostLaunchHookAsync(string appId);

  // Signals
  Task<IDisposable> WatchLibraryUpdatedAsync(Action<ProviderItem[]> reply);
  Task<IDisposable> WatchInstallStartedAsync(Action<InstallStartedDescription> reply);
  Task<IDisposable> WatchInstallProgressedAsync(Action<InstallProgressedDescription> reply);
  Task<IDisposable> WatchInstallCompletedAsync(Action<string> reply);
  Task<IDisposable> WatchInstallFailedAsync(Action<(string appId, string error)> reply);
  Task<IDisposable> WatchAppNewVersionFoundAsync(Action<(string appId, string version)> reply);
  Task<IDisposable> WatchMoveItemProgressedAsync(Action<(string appId, double progress)> reply);
  Task<IDisposable> WatchInstalledAppsUpdatedAsync(Action reply);
}
