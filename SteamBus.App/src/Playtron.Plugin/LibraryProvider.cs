using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Tmds.DBus;

namespace Playtron.Plugin;

/// Type alias for an install option description tuple.
/// DBus does not actually support structs, so they are instead represented as
/// typed tuples.
using InstallOptionDescription = (string, string, string[]);

public enum PluginProviderStatus
{
  Unauthorized = 0,
  Requires2fa = 1,
  Authorized = 2,
  Reconnecting = 3,
}

[StructLayout(LayoutKind.Sequential)]
public struct EulaEntry
{
  public string Id { get; set; }
  public string Name { get; set; }
  public int Version { get; set; }
  public string Url { get; set; }
  public string Body { get; set; }
  public string Country { get; set; }
  public string Language { get; set; }
}

[StructLayout(LayoutKind.Sequential)]
public struct InstalledAppDescription
{
  public string AppId { get; set; }
  public string InstalledPath { get; set; }
  public ulong DownloadedBytes { get; set; }
  public ulong TotalDownloadSize { get; set; }
  public ulong DiskSize { get; set; }
  public string Version { get; set; }
  public string LatestVersion { get; set; }
  public bool UpdatePending { get; set; }
  public string Os { get; set; }
  public string Language { get; set; }
  public string[] DisabledDlc { get; set; }
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
  public string Os { get; set; }
  public string Language { get; set; }
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
  public bool verify = false;
  public string[] disabled_dlc = [];
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

public enum ReleaseState : uint
{
  Released = 0,
  PreloadOnly = 1,
  Unreleased = 2,
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
  public ReleaseState release_state;
  public ulong release_date;
}

[StructLayout(LayoutKind.Sequential)]
public struct PlaytronProvider
{
  public string provider { get; set; }
  public string provider_app_id { get; set; }
  public string store_id { get; set; }
  public string? parent_store_id { get; set; }
  public DateTime? last_imported_timestamp { get; set; }
  public string[] known_dlc_store_ids { get; set; }
  public string Namespace { get; set; }
  public string product_store_link { get; set; }
}

[StructLayout(LayoutKind.Sequential)]
public struct PlaytronImage
{
  public string image_type { get; set; }
  public string url { get; set; }
  public string alt { get; set; }
  public string source { get; set; }
}

[StructLayout(LayoutKind.Sequential)]
public struct LogoPosition
{
  public string pinned_position { get; set; }
  public float width_pct { get; set; }
  public float height_pct { get; set; }
}


[StructLayout(LayoutKind.Sequential)]
public struct ItemMetadata
{
  public string Id { get; set; }
  public string Name { get; set; }
  public PlaytronProvider[] Providers { get; set; }
  public string Slug { get; set; }
  public string Summary { get; set; }
  public string Description { get; set; }
  public string[] Tags { get; set; }
  public PlaytronImage[] Images { get; set; }
  public string[] Publishers { get; set; }
  public string[] Developers { get; set; }
  public string app_type { get; set; }
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

  Task<ulong> GetDownloadSizeAsync(string appId, InstallOptions options);

  // Downloads and installs the app with the given app id to the target disk.
  // Available install options can be queried using the `GetInstallOptionsAsync`
  // method.
  Task<int> InstallAsync(string appId, string disk, InstallOptions options);
  Task UninstallAsync(string appId);
  Task CancelMoveItemAsync(string appId);
  Task MoveItemAsync(string appId, string disk);

  Task<EulaEntry[]> GetEulasAsync(string appId, string country, string locale);

  // Gets information about installed apps
  Task<InstalledAppDescription[]> GetInstalledAppsAsync();

  // Syncs installed apps by reading the library folders again
  Task SyncInstalledAppsAsync();

  // Pauses the current install that is in progress
  Task PauseInstallAsync();

  Task<string> GetItemMetadataAsync(string appId);

  Task<InstallOptionDescription[]> GetInstallOptionsAsync(string appId);

  Task<LaunchOption[]> GetLaunchOptionsAsync(string appIdString, InstallOptions extraOptions);

  Task<string> GetPostInstallStepsAsync(string appId);

  Task<ProviderItem> GetProviderItemAsync(string appId);
  Task<ProviderItem[]> GetProviderItemsAsync();
  Task RefreshAsync();

  Task<CloudPathObject[]> GetSavePathPatternsAsync(string appId, string platform);

  // Run necessary pre launch steps and return a list of required dependency ids if any are being updated
  Task<string[]> PreLaunchHookAsync(string appId, bool usingOfflineMode);
  // Handles clean up post launch
  Task PostLaunchHookAsync(string appId);

  // Signals
  Task<IDisposable> WatchLibraryUpdatedAsync(Action<ProviderItem[]> reply);
  Task<IDisposable> WatchInstallStartedAsync(Action<InstallStartedDescription> reply);
  Task<IDisposable> WatchInstallProgressedAsync(Action<InstallProgressedDescription> reply);
  Task<IDisposable> WatchInstallCompletedAsync(Action<string> reply);
  Task<IDisposable> WatchInstallFailedAsync(Action<(string appId, string error)> reply);
  Task<IDisposable> WatchAppNewVersionFoundAsync(Action<(string appId, string version)> reply);
  Task<IDisposable> WatchMoveItemProgressedAsync(Action<(string appId, double progress)> reply);
  Task<IDisposable> WatchMoveItemCompletedAsync(Action<(string appId, string installFolder)> reply);
  Task<IDisposable> WatchMoveItemFailedAsync(Action<(string appId, string error)> reply);
  Task<IDisposable> WatchInstalledAppsUpdatedAsync(Action reply);
  Task<IDisposable> WatchLaunchReadyAsync(Action<string> reply);
  Task<IDisposable> WatchLaunchErrorAsync(Action<(string appId, string error)> reply);
}
