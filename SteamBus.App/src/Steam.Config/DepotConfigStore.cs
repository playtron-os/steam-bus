using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using Playtron.Plugin;
using Steam.Config;
using Steam.Content;
using Steam.Session;
using SteamKit2;
using Tmds.DBus;

public class InstallOptionsExtended : InstallOptions
{
    public uint appId = 0;
    public string installDir = "";
    public (uint DepotId, ulong ManifestId)[] depotIds = [];
    public bool isUpdatePending = false;
}

public enum Universe
{
    Individual = 0,
    Public = 1,
    Beta = 2,
    Internal = 3,
    Dev = 4,
    Rc = 5,
}

public enum StateFlags
{
    Invalid = 0,
    Uninstalled = 1 << 0,            // 1
    UpdateRequired = 1 << 1,         // 2
    FullyInstalled = 1 << 2,         // 4
    Encrypted = 1 << 3,              // 8
    Locked = 1 << 4,                 // 16
    FilesMissing = 1 << 5,           // 32
    AppRunning = 1 << 6,             // 64
    FilesCorrupt = 1 << 7,           // 128
    UpdateRunning = 1 << 8,          // 256
    UpdatePaused = 1 << 9,           // 512
    UpdateStarted = 1 << 10,         // 1024
    Uninstalling = 1 << 11,          // 2048
    BackupRunning = 1 << 12,         // 4096
    Reconfiguring = 1 << 16,         // 65536
    Validating = 1 << 17,            // 131072
    AddingFiles = 1 << 18,           // 262144
    Preallocating = 1 << 19,         // 524288
    Downloading = 1 << 20,           // 1048576
    Staging = 1 << 21,               // 2097152
    Committing = 1 << 22,            // 4194304
    UpdateStopping = 1 << 23         // 8388608
}


/*
"AppState"
{
        "appid"         "394380"
        "Universe"              "1"
        "name"          "BattleStick"
        "StateFlags"            "4"
        "installdir"            "BattleStick"
        "LastUpdated"           "1737493571"
        "LastPlayed"            "0"
        "SizeOnDisk"            "126911410"
        "StagingSize"           "0"
        "buildid"               "1136323"
        "LastOwner"             "76561197999515283"
        "UpdateResult"          "0"
        "BytesToDownload"               "37841104"
        "BytesDownloaded"               "37841104"
        "BytesToStage"          "126911410"
        "BytesStaged"           "126911410"
        "TargetBuildID"         "1136323"
        "AutoUpdateBehavior"            "0"
        "AllowOtherDownloadsWhileRunning"               "0"
        "ScheduledAutoUpdate"           "0"
        "InstalledDepots"
        {
                "394382"
                {
                        "manifest"              "511842974107429179"
                        "size"          "126911410"
                }
        }
        "UserConfig"
        {
                "language"              "english"
        }
        "MountedConfig"
        {
                "language"              "english"
        }
}
*/
public class DepotConfigStore
{
    public const string KEY_APP_STATE = "AppState";
    public const string KEY_APP_ID = "appid";
    public const string KEY_UNIVERSE = "Universe";
    public const string KEY_NAME = "name";
    public const string KEY_STATE_FLAGS = "StateFlags";
    public const string KEY_INSTALL_DIR = "installdir";
    public const string KEY_LAST_UPDATED = "LastUpdated";
    public const string KEY_LAST_PLAYED = "LastPlayed";
    public const string KEY_SIZE_ON_DISK = "SizeOnDisk";
    public const string KEY_BUILD_ID = "buildid";
    public const string KEY_LAST_OWNER = "LastOwner";
    public const string KEY_UPDATE_RESULT = "UpdateResult";
    public const string KEY_BYTES_TO_DOWNLOAD = "BytesToDownload";
    public const string KEY_BYTES_DOWNLOADED = "BytesDownloaded";
    public const string KEY_BYTES_TO_STAGE = "BytesToStage";
    public const string KEY_BYTES_STAGED = "BytesStaged";
    public const string KEY_TARGET_BUILD_ID = "TargetBuildID";
    public const string KEY_AUTO_UPDATE_BEHAVIOR = "AutoUpdateBehavior";
    public const string KEY_ALLOW_OTHER_DOWNLOADS_WHILE_RUNNING = "AllowOtherDownloadsWhileRunning";
    public const string KEY_SCHEDULED_AUTO_UPDATE = "ScheduledAutoUpdate";
    public const string KEY_FULL_VALIDATE_AFTER_NEXT_UPDATE = "FullValidateAfterNextUpdate";
    public const string KEY_INSTALLED_DEPOTS = "InstalledDepots";
    public const string KEY_INSTALLED_DEPOTS_MANIFEST = "manifest";
    public const string KEY_INSTALLED_DEPOTS_SIZE = "size";
    public const string KEY_INSTALLED_DEPOTS_DLC_APP_ID = "dlcappid";
    public const string KEY_USER_CONFIG = "UserConfig";
    public const string KEY_MOUNTED_CONFIG = "MountedConfig";
    public const string KEY_CONFIG_LANGUAGE = "language";
    public const string KEY_CONFIG_BETA_KEY = "BetaKey";
    public const string KEY_CONFIG_DISABLED_DLC = "DisabledDLC";
    public const string KEY_SHARED_DEPOTS = "SharedDepots";
    public const string KEY_INSTALL_SCRIPTS = "InstallScripts";

    public const string EXTRA_KEY_LATEST_BUILD_ID = "LatestBuildID";
    public const string EXTRA_KEY_OS = "os";

    private List<string>? folders;

    private ConcurrentDictionary<uint, string> manifestPathMap = [];
    private ConcurrentDictionary<uint, KeyValue> manifestMap = [];

    // Path to manifest file specific to SteamBus, if this file is present it means the app has been imported to SteamBus
    private ConcurrentDictionary<uint, string> manifestExtraPathMap = [];
    private ConcurrentDictionary<uint, KeyValue> manifestExtraMap = [];

    private ConcurrentDictionary<uint, UserCompatConfig> accountIdToUserCompatConfig = new();
    private AppInfoCache appInfoCache;

    public SteamSession? steamSession;

    private readonly SemaphoreSlim fileLock = new(1, 1);

    private Dictionary<uint, CancellationTokenSource> _moveCancellationTokenMap = [];

    DepotConfigStore()
    {
        appInfoCache = new AppInfoCache(AppInfoCache.DefaultPath());
    }

    /// <summary>
    /// Initializes the DepotConfigStore
    /// </summary>
    static public async Task<DepotConfigStore> CreateAsync(List<string>? folders = null)
    {
        var store = new DepotConfigStore();
        store.folders = folders;
        await store.Reload();
        return store;
    }

    /// <summary>
    /// Reloads the depot config store data from all the install directories
    /// </summary>
    /// <returns></returns>
    public async Task Reload()
    {
        var appIdsToRemove = manifestPathMap.Where((entry) => !File.Exists(entry.Value)).Select((entry) => entry.Key);
        foreach (var appId in appIdsToRemove)
        {
            if (ContentDownloader.currentAppIdDownloading == appId) continue;

            manifestMap.Remove(appId, out var _);
            manifestPathMap.Remove(appId, out var _);
            manifestExtraMap.Remove(appId, out var _);
            manifestExtraPathMap.Remove(appId, out var _);
        }

        if (folders != null)
        {
            foreach (var dir in folders)
                await ReloadApps(dir);
            return;
        }

        var libraryFoldersConfig = await LibraryFoldersConfig.CreateAsync();
        var directories = libraryFoldersConfig.GetInstallDirectories();

        foreach (var dir in directories)
        {
            var parentDir = Directory.GetParent(dir)!.FullName;
            await ReloadApps(parentDir);
        }
    }

    public async Task ReloadApps(string dir, bool isRecursing = false)
    {
        var commonDir = Path.Join(dir, "common");
        if (!Directory.Exists(dir) || !Directory.Exists(commonDir))
            return;

        try
        {
            var manifestPaths = Directory.EnumerateFiles(dir).ToList();

            foreach (var manifestPath in manifestPaths ?? [])
            {
                if (!manifestPath.EndsWith(".acf") || manifestPath.Contains(".extra.acf"))
                    continue;

                await ImportApp(manifestPath);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Exception when importing apps from dir:{dir}, err:{exception}");

            if (!isRecursing)
            {
                Console.WriteLine($"Attempting to reload apps again for dir:{dir}");
                await ReloadApps(dir, true);
                return;
            }
        }

        Console.WriteLine($"Depot config store loaded {manifestPathMap.Count} installed apps");
    }

    public async Task<bool> ImportApp(string manifestPath)
    {
        await fileLock.WaitAsync();

        uint appId = 0;

        try
        {
            if (manifestPathMap.Any((pair) => pair.Value == manifestPath))
                return false;

            var manifestExtraPath = manifestPath.Replace(".acf", ".extra.acf");

            var manifestData = await File.ReadAllTextAsync(manifestPath);
            if (string.IsNullOrEmpty(manifestData))
                return false;

            var data = KeyValue.LoadFromString(manifestData);
            if (data == null)
                return false;

            appId = data["appid"].AsUnsignedInteger();
            if (appId == 0)
                return false;

            // Check if install dir exists, and if not, delete dangling manifest files
            var installDir = GetInstallDirectory(manifestPath, data);
            if (!Directory.Exists(installDir))
            {
                if (File.Exists(manifestPath))
                    File.Delete(manifestPath);

                if (File.Exists(manifestExtraPath))
                    File.Delete(manifestExtraPath);

                return false;
            }

            if (File.Exists(manifestExtraPath))
            {
                var manifestExtraData = await File.ReadAllTextAsync(manifestExtraPath);
                if (string.IsNullOrEmpty(manifestExtraData))
                    return false;

                var extraData = KeyValue.LoadFromString(manifestExtraData);
                if (extraData == null)
                    return false;

                manifestExtraMap.TryAdd(appId, extraData);
            }
            else
            {
                var extraData = new KeyValue(KEY_APP_STATE);
                extraData.SaveToFileWithAtomicRename(manifestExtraPath);
                manifestExtraMap.TryAdd(appId, extraData);
            }

            manifestMap.TryAdd(appId, data);
            manifestPathMap.TryAdd(appId, manifestPath);
            manifestExtraPathMap.TryAdd(appId, manifestExtraPath);

            return true;
        }
        catch (Exception err)
        {
            Console.Error.WriteLine($"Error when importing app, err:{err}");
            return false;
        }
        finally
        {
            fileLock.Release();

            if (appId != 0)
                VerifyAppsStateFlag(appId);
        }
    }

    /// <summary>
    /// Verifies the app state flags and make sure to normalize it if needed to avoid unwanted flags
    /// </summary>
    public void VerifyAppsStateFlag()
    {
        foreach (var pair in manifestMap)
            VerifyAppsStateFlag(pair.Key, pair.Value);
    }

    /// <summary>
    /// Verifies the app state flags and make sure to normalize it if needed to avoid unwanted flags
    /// </summary>
    public void VerifyAppsStateFlag(uint appId)
    {
        if (manifestMap.TryGetValue(appId, out var data))
            VerifyAppsStateFlag(appId, data);
    }

    /// <summary>
    /// Verifies the app state flags and make sure to normalize it if needed to avoid unwanted flags
    /// </summary>
    public void VerifyAppsStateFlag(uint appId, KeyValue data)
    {
        if (!manifestPathMap.ContainsKey(appId))
            return;

        var normalizedStateFlags = GetNormalizedStateFlags(appId);
        if (data[KEY_STATE_FLAGS]?.AsUnsignedInteger() != normalizedStateFlags)
        {
            WithLock(() =>
            {
                data[KEY_STATE_FLAGS] = new KeyValue(KEY_STATE_FLAGS, normalizedStateFlags.ToString());
                data.SaveToFileWithAtomicRename(manifestPathMap[appId]);
            });
        }
    }

    public void VerifyAppsOsConfig(uint accountId)
    {
        var globalConfig = new GlobalConfig(GlobalConfig.DefaultPath());
        var userCompatConfig = accountId == 0 ? null : new UserCompatConfig(UserCompatConfig.DefaultPath(accountId));

        foreach (var appId in manifestMap.Keys)
            VerifyAppsOsConfig(globalConfig, userCompatConfig, appId);

        globalConfig.Save();
        userCompatConfig?.Save();
    }

    public void VerifyAppsOsConfig(GlobalConfig globalConfig, UserCompatConfig? userCompatConfig, uint appId)
    {
        var installedOs = TryGetOsFromDepots(appId);
        if (installedOs == null && manifestExtraMap.TryGetValue(appId, out var extraData))
            installedOs = extraData[EXTRA_KEY_OS].AsString();

        var defaultOs = ContentDownloader.GetSteamOS();

        if (installedOs != null)
        {
            if (userCompatConfig != null)
                userCompatConfig.SetPlatformOverride(appId, defaultOs, installedOs);

            if (installedOs == "windows" && defaultOs != installedOs)
                globalConfig.SetProton9CompatForApp(appId);
            else
                globalConfig.RemoveCompatForApp(appId);

            if (manifestExtraMap.TryGetValue(appId, out var data) && data[EXTRA_KEY_OS].AsString() != installedOs)
            {
                WithLock(() =>
                {
                    data[EXTRA_KEY_OS] = new KeyValue(EXTRA_KEY_OS, installedOs);

                    if (manifestExtraPathMap.TryGetValue(appId, out var extraPath))
                        data.SaveToFileWithAtomicRename(extraPath);
                });
            }
        }
    }

    /// <summary>
    /// Save the changes to the depot config for an app id
    /// </summary>
    /// <returns></returns>
    public void Save(uint appId)
    {
        manifestMap.TryGetValue(appId, out var manifest);
        manifestPathMap.TryGetValue(appId, out var path);
        manifestExtraMap.TryGetValue(appId, out var extraManifest);
        manifestExtraPathMap.TryGetValue(appId, out var extraPath);

        if (manifest == null && path != null && File.Exists(path))
        {
            try
            {
                File.Delete(path);
                if (extraPath != null)
                    File.Delete(extraPath);
            }
            catch (Exception) { }
        }
        else if (manifest != null && path != null && extraManifest != null && extraPath != null)
        {
            WithLock(() =>
            {
                var stateFlags = GetNormalizedStateFlags(appId);
                manifest[KEY_STATE_FLAGS] = new KeyValue(KEY_STATE_FLAGS, ((int)stateFlags).ToString());

                manifest.SaveToFileWithAtomicRename(path);
                extraManifest.SaveToFileWithAtomicRename(extraPath);
            });
        }
    }

    /// <summary>
    /// Returns the install directory for an app id
    /// </summary>
    /// <param name="appId"></param>
    /// <returns></returns>
    public string? GetInstallDirectory(uint appId)
    {
        if (!manifestMap.TryGetValue(appId, out var manifest)) return null;
        if (!manifestPathMap.TryGetValue(appId, out var manifestPath)) return null;

        return GetInstallDirectory(manifestPath, manifest);
    }

    /// <summary>
    /// Returns the install directory for an app
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    public string? GetInstallDirectory(string manifestPath, KeyValue manifest)
    {
        return Path.Join(Directory.GetParent(manifestPath)?.FullName, "common", manifest[KEY_INSTALL_DIR].Value);
    }

    /// <summary>
    /// Returns the manifest ID installed related to the app id and depot id
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="depotId"></param>
    /// <returns></returns>
    public ulong? GetManifestID(uint appId, uint depotId)
    {
        if (!manifestMap.TryGetValue(appId, out var manifest)) return null;

        var manifestId = manifest[KEY_INSTALLED_DEPOTS]?[depotId.ToString()]?[KEY_INSTALLED_DEPOTS_MANIFEST]?.AsUnsignedLong();
        return manifestId == 0 ? null : manifestId;
    }

    /// <summary>
    /// Get all depots for an app
    /// </summary>
    /// <param name="appId"></param>
    /// <returns></returns>
    public List<(uint DepotId, ulong ManifestId, ulong ManifestSize)> GetDepots(uint appId)
    {
        if (!manifestMap.TryGetValue(appId, out var manifest)) return [];

        var depots = new List<(uint, ulong, ulong)>();

        foreach (var child in manifest[KEY_INSTALLED_DEPOTS]?.Children ?? [])
        {
            if (!uint.TryParse(child.Name, out var depotId)) continue;

            var manifestId = child[KEY_INSTALLED_DEPOTS_MANIFEST]?.AsUnsignedLong();
            if (manifestId == null) continue;

            var manifestSize = child[KEY_INSTALLED_DEPOTS_SIZE]?.AsUnsignedLong();
            if (manifestSize == null) continue;

            depots.Add((depotId, (ulong)manifestId, (ulong)manifestSize));
        }

        return depots;
    }

    /// <summary>
    /// Get all shared depot ids for an app
    /// </summary>
    /// <param name="appId"></param>
    /// <returns></returns>
    public List<uint> GetSharedDepotIds(uint appId)
    {
        if (!manifestMap.TryGetValue(appId, out var manifest)) return [];
        return (manifest[KEY_SHARED_DEPOTS]?.Children ?? []).Where((x) => !string.IsNullOrEmpty(x.Name)).Select((x) => uint.Parse(x.Name!)).ToList();
    }

    /// <summary>
    /// Get all install scripts for an app
    /// </summary>
    /// <param name="appId"></param>
    /// <returns></returns>
    public List<(uint DepotId, string Path)> GetInstallScripts(uint appId)
    {
        if (!manifestMap.TryGetValue(appId, out var manifest)) return [];
        return manifest[KEY_INSTALL_SCRIPTS].Children.Select(x => (uint.Parse(x.Name!), x.AsString()!)).ToList();
    }

    /// <summary>
    /// Get all shared depots for an app
    /// </summary>
    /// <param name="appId"></param>
    /// <returns></returns>
    public List<(uint DepotId, uint DepotAppId)> GetSharedDepots(uint appId)
    {
        if (!manifestMap.TryGetValue(appId, out var manifest)) return [];
        return (manifest[KEY_SHARED_DEPOTS]?.Children ?? []).Select((x) => (uint.Parse(x.Name!), x.AsUnsignedInteger())).ToList();
    }


    /// <summary>
    /// Get all shared depot app ids for an app
    /// </summary>
    /// <param name="appId"></param>
    /// <returns></returns>
    public List<uint> GetSharedDepotAppIds(uint appId)
    {
        if (!manifestMap.TryGetValue(appId, out var manifest)) return [];
        return (manifest[KEY_SHARED_DEPOTS]?.Children ?? []).Select((x) => x.AsUnsignedInteger()).Where((x) => x != 0).ToList();
    }

    /// <summary>
    /// Gets the size in bytes downloaded by an app id
    /// </summary>
    /// <param name="appId"></param>
    /// <returns></returns>
    public uint? GetSizeDownloaded(uint appId)
    {
        if (!manifestMap.TryGetValue(appId, out var manifest)) return null;

        return manifest[KEY_BYTES_DOWNLOADED]?.AsUnsignedInteger();
    }

    /// <summary>
    /// Gets the download stage by an app id
    /// </summary>
    /// <param name="appId"></param>
    /// <returns></returns>
    public DownloadStage? GetDownloadStage(uint appId)
    {
        if (!manifestMap.TryGetValue(appId, out var manifest)) return null;

        var stateFlags = manifest[KEY_STATE_FLAGS]?.AsUnsignedInteger();

        if ((stateFlags & (int)StateFlags.Downloading) != 0
            || (stateFlags & (int)StateFlags.Staging) != 0
            || (stateFlags & (int)StateFlags.UpdateStarted) != 0
            || (stateFlags & (int)StateFlags.UpdateRunning) != 0
            || (stateFlags & (int)StateFlags.Committing) != 0
            || (stateFlags & (int)StateFlags.Staging) != 0)
        {
            return DownloadStage.Downloading;
        }

        if ((stateFlags & (int)StateFlags.Validating) != 0)
        {
            return DownloadStage.Verifying;
        }

        return DownloadStage.Preallocating;
    }

    /// <summary>
    /// Is app downloaded
    /// </summary>
    /// <param name="appId"></param>
    /// <returns></returns>
    public bool IsAppDownloaded(uint appId)
    {
        if (!manifestMap.TryGetValue(appId, out var manifest)) return false;

        var stateFlags = manifest[KEY_STATE_FLAGS]?.AsUnsignedInteger() ?? 0;
        return (stateFlags & (int)StateFlags.FullyInstalled) != 0;
    }

    /// <summary>
    /// Removes a depot ID installed for an app id
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="depotId"></param>
    public void RemoveDepot(uint appId, uint depotId)
    {
        if (!manifestMap.TryGetValue(appId, out var manifest)) return;

        WithLock(() =>
        {
            Console.WriteLine($"Removing depotId:{depotId} from appId:{appId}");

            var depotIdKey = depotId.ToString();
            var depots = manifest[KEY_INSTALLED_DEPOTS];
            var child = depots?.Children.Find((child) => child.Name == depotIdKey);
            if (child != null)
                depots!.Children.Remove(child);

            foreach (var entry in manifest[KEY_SHARED_DEPOTS].Children)
            {
                if (entry.Name == depotIdKey)
                {
                    manifest[KEY_SHARED_DEPOTS].Children.Remove(entry);
                    break;
                }
            }
        });
    }

    /// <summary>
    /// Set install script for depot
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="depotId"></param>
    /// <param name="path"></param>
    public void SetInstallScript(uint appId, uint depotId, string path)
    {
        if (manifestMap.TryGetValue(appId, out var manifest))
        {
            WithLock(() =>
            {
                var key = depotId.ToString();
                if (manifest[KEY_INSTALL_SCRIPTS] == KeyValue.Invalid)
                    manifest[KEY_INSTALL_SCRIPTS] = new KeyValue(key);

                manifest[KEY_INSTALL_SCRIPTS][key] = new KeyValue(key, path);
            });
        }
    }

    /// <summary>
    /// Clear install script for depot id
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="depotId"></param>
    public void RemoveInstallScript(uint appId, uint depotId)
    {
        if (manifestMap.TryGetValue(appId, out var manifest))
        {
            WithLock(() =>
            {
                var key = depotId.ToString();
                var child = manifest[KEY_INSTALL_SCRIPTS].Children.FirstOrDefault((child) => child.Name == key);
                if (child != null) manifest[KEY_INSTALL_SCRIPTS].Children.Remove(child);
            });
        }
    }

    /// <summary>
    /// Sets the manifest ID for an app id and depot id
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="depotId"></param>
    /// <param name="manifestId"></param>
    /// <param name="manifestSize"></param>
    public void SetManifestID(uint appId, uint depotId, ulong manifestId, ulong manifestSize, uint? dlcAppId)
    {
        if (!manifestMap.TryGetValue(appId, out KeyValue? value)) return;

        WithLock(() =>
        {
            var key = depotId.ToString();
            if (value[KEY_INSTALLED_DEPOTS][key] == KeyValue.Invalid)
                value[KEY_INSTALLED_DEPOTS][key] = new KeyValue(key);
            value[KEY_INSTALLED_DEPOTS][key][KEY_INSTALLED_DEPOTS_MANIFEST] = new KeyValue(KEY_INSTALLED_DEPOTS_MANIFEST, manifestId.ToString());
            value[KEY_INSTALLED_DEPOTS][key][KEY_INSTALLED_DEPOTS_SIZE] = new KeyValue(KEY_INSTALLED_DEPOTS_SIZE, manifestSize.ToString());

            if (dlcAppId == null || dlcAppId == appId)
            {
                var child = value[KEY_INSTALLED_DEPOTS][key].Children.FirstOrDefault((x) => x.Name == KEY_INSTALLED_DEPOTS_DLC_APP_ID);
                if (child != null)
                    value[KEY_INSTALLED_DEPOTS][key].Children.Remove(child);
            }
            else
                value[KEY_INSTALLED_DEPOTS][key][KEY_INSTALLED_DEPOTS_DLC_APP_ID] = new KeyValue(KEY_INSTALLED_DEPOTS_DLC_APP_ID, dlcAppId.ToString());
        });
    }

    /// <summary>
    /// Sets the shared depot id
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="depotAppId"></param>
    /// <param name="depotId"></param>
    public void SetSharedDepot(uint appId, uint depotAppId, uint depotId)
    {
        if (!manifestMap.TryGetValue(appId, out KeyValue? value)) return;

        WithLock(() =>
        {
            if (value[KEY_SHARED_DEPOTS] == KeyValue.Invalid)
                value[KEY_SHARED_DEPOTS] = new KeyValue(KEY_SHARED_DEPOTS);

            var key = depotId.ToString();
            value[KEY_SHARED_DEPOTS][key] = new KeyValue(key, depotAppId.ToString());
        });
    }

    /// <summary>
    /// Sets the total download property of the manifest
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="size"></param>
    public void SetTotalSize(uint appId, ulong size)
    {
        if (!manifestMap.TryGetValue(appId, out KeyValue? value)) return;

        WithLock(() =>
        {
            value[KEY_BYTES_TO_DOWNLOAD] = new KeyValue(KEY_BYTES_TO_DOWNLOAD, size.ToString());
        });
    }

    /// <summary>
    /// Sets the downloaded property of the manifest
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="size"></param>
    public void SetCurrentSize(uint appId, ulong size)
    {
        if (!manifestMap.TryGetValue(appId, out KeyValue? value)) return;

        WithLock(() =>
        {
            value[KEY_BYTES_DOWNLOADED] = new KeyValue(KEY_BYTES_DOWNLOADED, size.ToString());
        });
    }

    /// <summary>
    /// Sets the download stage
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="stage"></param>
    public void SetDownloadStage(uint appId, DownloadStage? stage, ulong? sizeOnDisk = null)
    {
        if (!manifestMap.TryGetValue(appId, out KeyValue? value)) return;

        var currentStateFlags = value[KEY_STATE_FLAGS]?.AsUnsignedInteger() ?? 0;

        var stateFlags = StateFlags.FullyInstalled;
        if ((currentStateFlags & (int)StateFlags.UpdateRequired) != 0)
            stateFlags |= StateFlags.UpdateRequired;

        // Set download paused in case of app being mid download so steam client doesn't start any downloads
        if (stage != null)
            stateFlags |= StateFlags.UpdatePaused;

        switch (stage)
        {
            case DownloadStage.Preallocating:
                stateFlags |= StateFlags.Preallocating;
                break;
            case DownloadStage.Downloading:
                stateFlags |= StateFlags.Downloading;
                break;
            case DownloadStage.Verifying:
                stateFlags |= StateFlags.Validating;
                break;
        }

        WithLock(() =>
        {
            if (stage == null)
            {
                value[KEY_LAST_UPDATED] = new KeyValue(KEY_LAST_UPDATED, DateTimeOffset.Now.ToUnixTimeSeconds().ToString());
                stateFlags &= ~StateFlags.UpdateRequired;
            }

            value[KEY_STATE_FLAGS] = new KeyValue(KEY_STATE_FLAGS, ((int)stateFlags).ToString());
        });

        if (sizeOnDisk != null)
            UpdateAppSizeOnDisk(appId, (ulong)sizeOnDisk);
    }

    /// <summary>
    /// Updates the app size on disk
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="sizeOnDisk"></param>
    /// <returns></returns>
    public void UpdateAppSizeOnDisk(uint appId, ulong sizeOnDisk)
    {
        var installDirectory = GetInstallDirectory(appId);
        if (installDirectory == null) return;

        WithLock(() =>
        {
            manifestMap[appId][KEY_SIZE_ON_DISK] = new KeyValue(KEY_SIZE_ON_DISK, sizeOnDisk.ToString());
        });
    }

    /// <summary>
    /// Sets the version/branch and removes the updatepending
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="version"></param>
    /// <param name="branch"></param>
    /// <param name="language"></param>
    /// <param name="os"></param>
    /// <param name="disabledDlc"></param>
    /// <param name="lastOwnedSteamId"></param>
    public void SetNewVersion(uint appId, uint version, string branch, string language, string os, string[] disabledDlc, string? lastOwnedSteamId = null)
    {
        if (!manifestMap.ContainsKey(appId)) return;

        WithLock(() =>
        {
            // TODO: Consider separating MOUNTED_CONFIG changes to when the download completes
            // USER_CONFIG should be a place for "staged" changes 
            string disabledDlcStr = String.Join(',', disabledDlc);
            manifestMap[appId][KEY_UNIVERSE] = new KeyValue(KEY_UNIVERSE, ((int)SteamClientApp.UNIVERSE).ToString());
            manifestMap[appId][KEY_BUILD_ID] = new KeyValue(KEY_BUILD_ID, version.ToString());
            manifestMap[appId][KEY_TARGET_BUILD_ID] = new KeyValue(KEY_TARGET_BUILD_ID, version.ToString());

            if (manifestMap[appId][KEY_USER_CONFIG] == KeyValue.Invalid)
                manifestMap[appId][KEY_USER_CONFIG] = new KeyValue(KEY_USER_CONFIG);
            manifestMap[appId][KEY_USER_CONFIG][KEY_CONFIG_LANGUAGE] = new KeyValue(KEY_CONFIG_LANGUAGE, language);

            if (manifestMap[appId][KEY_MOUNTED_CONFIG] == KeyValue.Invalid)
                manifestMap[appId][KEY_MOUNTED_CONFIG] = new KeyValue(KEY_MOUNTED_CONFIG);
            manifestMap[appId][KEY_MOUNTED_CONFIG][KEY_CONFIG_LANGUAGE] = new KeyValue(KEY_CONFIG_LANGUAGE, language);

            if (lastOwnedSteamId != null)
                manifestMap[appId][KEY_LAST_OWNER] = new KeyValue(KEY_LAST_OWNER, lastOwnedSteamId);

            if (!string.IsNullOrEmpty(branch) && branch != AppDownloadOptions.DEFAULT_BRANCH)
            {
                manifestMap[appId][KEY_MOUNTED_CONFIG][KEY_CONFIG_BETA_KEY] = new KeyValue(KEY_CONFIG_BETA_KEY, branch);
                manifestMap[appId][KEY_USER_CONFIG][KEY_CONFIG_BETA_KEY] = new KeyValue(KEY_CONFIG_BETA_KEY, branch);
            }
            else
            {
                var mountedChild = manifestMap[appId][KEY_MOUNTED_CONFIG]?.Children.FirstOrDefault((child) => child.Name == KEY_CONFIG_BETA_KEY);
                if (mountedChild != null) manifestMap[appId][KEY_MOUNTED_CONFIG].Children.Remove(mountedChild);

                var userConfigChild = manifestMap[appId][KEY_USER_CONFIG]?.Children.FirstOrDefault((child) => child.Name == KEY_CONFIG_BETA_KEY);
                if (userConfigChild != null) manifestMap[appId][KEY_USER_CONFIG].Children.Remove(userConfigChild);
            }

            if (!string.IsNullOrEmpty(disabledDlcStr))
            {
                manifestMap[appId][KEY_USER_CONFIG][KEY_CONFIG_DISABLED_DLC] = new KeyValue(KEY_CONFIG_DISABLED_DLC, disabledDlcStr);
                manifestMap[appId][KEY_MOUNTED_CONFIG][KEY_CONFIG_DISABLED_DLC] = new KeyValue(KEY_CONFIG_DISABLED_DLC, disabledDlcStr);
            }
            else
            {
                var userConfigChild = manifestMap[appId][KEY_USER_CONFIG]?.Children.FirstOrDefault((child) => child.Name == KEY_CONFIG_BETA_KEY);
                var mountedChild = manifestMap[appId][KEY_MOUNTED_CONFIG]?.Children.FirstOrDefault((child) => child.Name == KEY_CONFIG_BETA_KEY);

                if (mountedChild != null) manifestMap[appId][KEY_MOUNTED_CONFIG].Children.Remove(mountedChild);
                if (userConfigChild != null) manifestMap[appId][KEY_USER_CONFIG].Children.Remove(userConfigChild);
            }

            manifestExtraMap[appId][EXTRA_KEY_OS] = new KeyValue(EXTRA_KEY_OS, os);
        });
    }

    /// <summary>
    /// Sets the updatepending
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="latestVersion"></param>
    public void SetUpdatePending(uint appId, string latestVersion)
    {
        if (!manifestMap.ContainsKey(appId)) return;

        WithLock(() =>
        {
            var currentStateFlags = manifestMap[appId][KEY_STATE_FLAGS]?.AsUnsignedInteger() ?? 0;
            manifestMap[appId][KEY_STATE_FLAGS] = new KeyValue(KEY_STATE_FLAGS, (currentStateFlags | (int)StateFlags.UpdateRequired).ToString());
            manifestExtraMap[appId][EXTRA_KEY_LATEST_BUILD_ID] = new KeyValue(EXTRA_KEY_LATEST_BUILD_ID, latestVersion);
        });
    }

    /// <summary>
    /// Sets the app as not update pending
    /// </summary>
    /// <param name="appId"></param>
    public void SetNotUpdatePending(uint appId)
    {
        if (!manifestMap.TryGetValue(appId, out var value)) return;

        WithLock(() =>
        {
            value[KEY_STATE_FLAGS] = new KeyValue(KEY_STATE_FLAGS, ((int)StateFlags.FullyInstalled).ToString());
        });
    }

    /// <summary>
    /// Ensures the necessary entry exists in the manifest map
    /// </summary>
    /// <param name="installDirectory"></param>
    /// <param name="appId"></param>
    /// <param name="name"></param>
    public void EnsureEntryExists(string installDirectory, uint appId, string name)
    {
        if (manifestPathMap.ContainsKey(appId)) return;

        WithLock(() =>
        {
            manifestPathMap.Remove(appId, out var _);
            manifestMap.Remove(appId, out var _);

            var steamappsFolder = Directory.GetParent(Directory.GetParent(installDirectory)!.FullName)!.FullName;
            var manifestPath = Path.Join(steamappsFolder, $"appmanifest_{appId}.acf");
            var manifestExtraPath = manifestPath.Replace(".acf", ".extra.acf");

            if (!manifestPathMap.ContainsKey(appId))
                manifestPathMap.TryAdd(appId, manifestPath);
            else
                manifestPathMap[appId] = manifestPath;

            if (!manifestMap.ContainsKey(appId))
            {
                manifestMap.TryAdd(appId, new KeyValue(KEY_APP_STATE));
                manifestMap[appId][KEY_APP_ID] = new KeyValue(KEY_APP_ID, appId.ToString());
            }

            if (manifestMap[appId][KEY_INSTALLED_DEPOTS] == KeyValue.Invalid)
                manifestMap[appId][KEY_INSTALLED_DEPOTS] = new KeyValue(KEY_INSTALLED_DEPOTS);

            manifestMap[appId][KEY_NAME] = new KeyValue(KEY_NAME, name);
            manifestMap[appId][KEY_INSTALL_DIR] = new KeyValue(KEY_INSTALL_DIR, Path.GetFileName(installDirectory));
            manifestMap[appId][KEY_AUTO_UPDATE_BEHAVIOR] = new KeyValue(KEY_AUTO_UPDATE_BEHAVIOR, "1");
            manifestMap[appId][KEY_ALLOW_OTHER_DOWNLOADS_WHILE_RUNNING] = new KeyValue(KEY_ALLOW_OTHER_DOWNLOADS_WHILE_RUNNING, "0");
            manifestMap[appId][KEY_SCHEDULED_AUTO_UPDATE] = new KeyValue(KEY_SCHEDULED_AUTO_UPDATE, "0");
            manifestMap[appId][KEY_FULL_VALIDATE_AFTER_NEXT_UPDATE] = new KeyValue(KEY_FULL_VALIDATE_AFTER_NEXT_UPDATE, "0");

            // Extra
            if (!manifestExtraMap.ContainsKey(appId))
            {
                manifestExtraMap.TryAdd(appId, new KeyValue(KEY_APP_STATE));
                manifestExtraMap[appId][KEY_APP_ID] = new KeyValue(KEY_APP_ID, appId.ToString());
            }

            if (!manifestExtraPathMap.ContainsKey(appId))
                manifestExtraPathMap.TryAdd(appId, manifestExtraPath);
            else
                manifestExtraPathMap[appId] = manifestExtraPath;
        });
    }

    /// <summary>
    /// List of installed app options
    /// </summary>
    /// <returns></returns>
    public InstallOptionsExtended[] GetInstalledAppOptions()
    {
        var manifests = manifestMap.Where((entry) => manifestPathMap.ContainsKey(entry.Key));
        var infos = new List<InstallOptionsExtended>();

        foreach (var entry in manifests)
        {
            var installedApp = GetInstalledAppOptions(entry.Key);
            if (installedApp == null) continue;
            infos.Add(installedApp);
        }

        return [.. infos];
    }

    /// <summary>
    /// List of installed app options
    /// </summary>
    /// <returns></returns>
    public InstallOptionsExtended? GetInstalledAppOptions(uint appId)
    {
        var installDir = GetInstallDirectory(appId);
        if (installDir == null) return null;
        if (!manifestMap.TryGetValue(appId, out var data)) return null;

        var os = GetOS(appId);

        var branch = GetBranch(appId);
        var depots = GetDepots(appId);

        return new InstallOptionsExtended
        {
            appId = appId,
            installDir = installDir,
            branch = branch,
            language = data[KEY_USER_CONFIG][KEY_CONFIG_LANGUAGE]?.AsString() ?? "english",
            version = data[KEY_BUILD_ID].AsString() ?? "",
            os = os,
            architecture = "",
            depotIds = depots.Select(x => (x.DepotId, x.ManifestId)).ToArray(),
            isUpdatePending = (data[KEY_STATE_FLAGS].AsUnsignedInteger() & (int)StateFlags.UpdateRequired) != 0,
        };
    }

    /// <summary>
    /// Verifies all the installed apps have a size, and if not, get their sizes
    /// </summary>
    /// <returns></returns>
    public async Task<bool> VerifyAppsAreSized()
    {
        var manifests = manifestMap.Where((entry) => manifestPathMap.ContainsKey(entry.Key));
        var appSizesToUpdate = new List<(uint, ulong, string)>();

        foreach (var entry in manifests)
        {
            if (!manifestExtraMap.TryGetValue(entry.Key, out var manifestExtra)) continue;

            var appId = entry.Key;
            var totalDownloadSize = entry.Value[KEY_BYTES_TO_DOWNLOAD].AsUnsignedLong();
            var diskSize = entry.Value[KEY_SIZE_ON_DISK].AsUnsignedLong();

            if (steamSession != null && diskSize == 0 && totalDownloadSize == 0)
            {
                var os = GetOS(appId);
                var installPath = GetInstallDirectory(entry.Key)!;
                var disabledDlc = (entry.Value[KEY_USER_CONFIG]?[KEY_CONFIG_DISABLED_DLC]?.AsString() ?? "").Split(',');
                var branch = entry.Value[KEY_MOUNTED_CONFIG]?[KEY_CONFIG_BETA_KEY]?.AsString() ?? AppDownloadOptions.DEFAULT_BRANCH;
                var language = entry.Value[KEY_MOUNTED_CONFIG]?[KEY_CONFIG_LANGUAGE]?.AsString() ?? "english";

                var contentDownloader = new ContentDownloader(steamSession, this);
                var installOptions = new InstallOptions
                {
                    branch = branch,
                    language = language,
                    os = os,
                    disabled_dlc = disabledDlc
                };
                var options = new AppDownloadOptions(installOptions, installPath);
                totalDownloadSize = await contentDownloader.GetTotalDownloadSizeAsync(appId, options);

                if (totalDownloadSize != 0)
                    appSizesToUpdate.Add((appId, totalDownloadSize, installPath));
            }
        }

        if (appSizesToUpdate.Count > 0)
        {
            foreach (var (appId, size, installPath) in appSizesToUpdate)
            {
                Console.WriteLine($"Updating total sizes which wasn't found for appId:{appId}, size:{size}");
                SetTotalSize(appId, size);
                UpdateAppSizeOnDisk(appId, await Disk.GetFolderSizeWithDu(installPath));
                Save(appId);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// List of installed apps with their install information
    /// </summary>
    /// <returns></returns>
    public InstalledAppDescription[] GetInstalledAppInfo()
    {
        var manifests = manifestMap.Where((entry) => manifestPathMap.ContainsKey(entry.Key));
        var infos = new List<InstalledAppDescription>();

        foreach (var entry in manifests)
        {
            if (!manifestExtraMap.TryGetValue(entry.Key, out var manifestExtra)) continue;

            var appId = entry.Key;
            var os = GetOS(appId);

            infos.Add(new InstalledAppDescription
            {
                AppId = appId.ToString(),
                InstalledPath = GetInstallDirectory(entry.Key)!,
                DownloadedBytes = entry.Value[KEY_BYTES_DOWNLOADED].AsUnsignedLong(),
                TotalDownloadSize = entry.Value[KEY_BYTES_TO_DOWNLOAD].AsUnsignedLong(),
                DiskSize = entry.Value[KEY_SIZE_ON_DISK].AsUnsignedLong(),
                Version = entry.Value[KEY_BUILD_ID].AsString() ?? "",
                LatestVersion = manifestExtra[EXTRA_KEY_LATEST_BUILD_ID].AsString() ?? "",
                UpdatePending = (entry.Value[KEY_STATE_FLAGS].AsUnsignedInteger() & (int)StateFlags.UpdateRequired) != 0,
                Os = os,
                Language = entry.Value[KEY_USER_CONFIG]?[KEY_CONFIG_LANGUAGE]?.AsString() ?? "",
                DisabledDlc = (entry.Value[KEY_USER_CONFIG]?[KEY_CONFIG_DISABLED_DLC]?.AsString() ?? "").Split(',')
            });
        }

        return [.. infos];
    }

    /// <summary>
    /// Installed apps with its install information
    /// </summary>
    /// <returns></returns>
    public (InstalledAppDescription Info, string Branch)? GetInstalledAppInfo(uint appId)
    {
        manifestMap.TryGetValue(appId, out var manifest);
        var installDirectory = GetInstallDirectory(appId);
        if (manifest == null || installDirectory == null) return null;
        if (!manifestExtraMap.TryGetValue(appId, out var manifestExtra)) return null;

        var branch = manifest[KEY_MOUNTED_CONFIG]?[KEY_CONFIG_BETA_KEY]?.AsString() ?? AppDownloadOptions.DEFAULT_BRANCH;
        var os = GetOS(appId);

        return (new InstalledAppDescription
        {
            AppId = appId.ToString(),
            InstalledPath = installDirectory,
            DownloadedBytes = manifest[KEY_BYTES_DOWNLOADED].AsUnsignedLong(),
            TotalDownloadSize = manifest[KEY_BYTES_TO_DOWNLOAD].AsUnsignedLong(),
            Version = manifest[KEY_BUILD_ID].AsString() ?? "",
            LatestVersion = manifestExtra[EXTRA_KEY_LATEST_BUILD_ID].AsString() ?? "",
            UpdatePending = (manifest[KEY_STATE_FLAGS].AsUnsignedInteger() & (int)StateFlags.UpdateRequired) != 0,
            Os = os,
            Language = manifest[KEY_USER_CONFIG]?[KEY_CONFIG_LANGUAGE]?.AsString() ?? "",
            DisabledDlc = (manifest[KEY_USER_CONFIG]?[KEY_CONFIG_DISABLED_DLC]?.AsString() ?? "").Split(',')
        }, branch);
    }

    /// <summary>
    /// Get map of app id to version/branch
    /// </summary>
    /// <returns></returns>
    public Dictionary<string, (string version, string branch)> GetAppIdToVersionBranchMap(bool ignoreUpdatePending = false)
    {
        var map = new Dictionary<string, (string version, string branch)>();

        foreach (var entry in manifestMap)
        {
            var version = entry.Value[KEY_BUILD_ID]?.AsString();
            var needsUpdate = (entry.Value[KEY_STATE_FLAGS].AsUnsignedInteger() & (int)StateFlags.UpdateRequired) != 0;
            var branch = entry.Value[KEY_MOUNTED_CONFIG]?[KEY_CONFIG_BETA_KEY]?.AsString() ?? AppDownloadOptions.DEFAULT_BRANCH;

            if (version != null && branch != null && (!ignoreUpdatePending || !needsUpdate))
                map.Add(entry.Key.ToString(), (version, branch));
        }

        return map;
    }

    /// <summary>
    /// Gets the branch associated to an appId
    /// </summary>
    /// <param name="appId"></param>
    /// <returns></returns>
    public string GetBranch(uint appId)
    {
        return manifestMap[appId]?[KEY_MOUNTED_CONFIG]?[KEY_CONFIG_BETA_KEY]?.AsString() ?? AppDownloadOptions.DEFAULT_BRANCH;
    }

    /// <summary>
    /// Removes the information related to the installed app id
    /// </summary>
    /// <param name="appId"></param>
    public void RemoveInstalledApp(uint appId)
    {
        var installDirectory = GetInstallDirectory(appId);

        if (installDirectory != null)
        {
            if (Directory.Exists(installDirectory))
                Directory.Delete(installDirectory, true);

            if (manifestPathMap.TryGetValue(appId, out var manifestPath) && File.Exists(manifestPath))
                File.Delete(manifestPath);

            if (manifestExtraPathMap.TryGetValue(appId, out var manifestExtraPath) && File.Exists(manifestExtraPath))
                File.Delete(manifestExtraPath);

            WithLock(() =>
            {
                manifestPathMap.Remove(appId, out var _);
                manifestMap.Remove(appId, out var _);
            });
        }
    }

    /// <summary>
    /// Moves the installed app to a new directory
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="newInstallDirectory"></param>
    /// <param name="OnMoveItemProgressed"></param>
    /// <param name="OnMoveItemCompleted"></param>
    /// <param name="OnMoveItemFailed"></param>
    /// <param name="ct"></param>
    public void MoveInstalledApp(
        uint appId,
        string newInstallDirectory,
        Action<(string appId, double progress)>? OnMoveItemProgressed,
        Action<(string appId, string installFolder)>? OnMoveItemCompleted,
        Action<(string appId, string error)>? OnMoveItemFailed)
    {
        if (_moveCancellationTokenMap.ContainsKey(appId)) return;
        var cts = new CancellationTokenSource();
        _moveCancellationTokenMap.Add(appId, cts);

        try
        {
            if (!manifestPathMap.TryGetValue(appId, out var currentManifestPath) ||
                !manifestExtraPathMap.TryGetValue(appId, out var currentExtraManifestPath))
                throw DbusExceptionHelper.ThrowAppNotInstalled();

            var currentInstallDirectory = GetInstallDirectory(appId);
            if (currentInstallDirectory == null) throw DbusExceptionHelper.ThrowAppNotInstalled();

            if (!Directory.Exists(newInstallDirectory))
                Directory.CreateDirectory(newInstallDirectory);

            var files = Directory.GetFiles(currentInstallDirectory, "*", SearchOption.AllDirectories);
            var directories = Directory.GetDirectories(currentInstallDirectory, "*", SearchOption.AllDirectories);
            var totalItems = files.Length;
            int processedItems = 0;

            // Copy directories first
            foreach (var directory in directories)
            {
                cts.Token.ThrowIfCancellationRequested();

                string relativePath = Path.GetRelativePath(currentInstallDirectory, directory);
                string destinationDir = Path.Combine(newInstallDirectory, relativePath);

                if (!Directory.Exists(destinationDir))
                    Directory.CreateDirectory(destinationDir);
            }

            // Copy files
            foreach (var file in files)
            {
                cts.Token.ThrowIfCancellationRequested();

                string relativePath = Path.GetRelativePath(currentInstallDirectory, file);
                string destinationFile = Path.Combine(newInstallDirectory, relativePath);
                var directory = Path.GetDirectoryName(destinationFile)!;

                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                if (!File.Exists(destinationFile))
                    File.Copy(file, destinationFile, true);

                processedItems++;
                int progress = (int)((double)processedItems / totalItems * 100);
                OnMoveItemProgressed?.Invoke((appId.ToString(), progress));
            }

            cts.Token.ThrowIfCancellationRequested();

            WithLock(() =>
            {
                var newManifestPath = Path.Join(Directory.GetParent(Directory.GetParent(newInstallDirectory)!.FullName)!.FullName, $"appmanifest_{appId}.acf");
                var newExtraManifestPath = newManifestPath.Replace(".acf", ".extra.acf");
                File.Copy(currentManifestPath, newManifestPath, true);
                File.Copy(currentExtraManifestPath, newExtraManifestPath, true);

                Directory.Delete(currentInstallDirectory, true);
                File.Delete(currentManifestPath);
                File.Delete(currentExtraManifestPath);

                manifestPathMap[appId] = newManifestPath;
                manifestExtraPathMap[appId] = newExtraManifestPath;

                manifestMap[appId].SaveToFileWithAtomicRename(manifestPathMap[appId]);
                manifestExtraMap[appId].SaveToFileWithAtomicRename(manifestExtraPathMap[appId]);
            });

            OnMoveItemProgressed?.Invoke((appId.ToString(), 100));
            OnMoveItemCompleted?.Invoke((appId.ToString(), newInstallDirectory));
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Operation cancelled, rolling back...");
            OnMoveItemFailed?.Invoke((appId.ToString(), DbusErrors.MoveItemCancelled));
            if (Directory.Exists(newInstallDirectory))
                Directory.Delete(newInstallDirectory, true);
        }
        catch (DBusException err)
        {
            Console.Error.WriteLine($"DBus exception when moving item: {err}");
            OnMoveItemFailed?.Invoke((appId.ToString(), err.ErrorName));
            if (Directory.Exists(newInstallDirectory))
                Directory.Delete(newInstallDirectory, true);
        }
        catch (Exception err)
        {
            Console.Error.WriteLine($"Exception when moving item: {err}");
            OnMoveItemFailed?.Invoke((appId.ToString(), err.Message));
            if (Directory.Exists(newInstallDirectory))
                Directory.Delete(newInstallDirectory, true);
        }
        finally
        {
            _moveCancellationTokenMap.Remove(appId);
        }
    }

    public async Task CancelMoveInstalledApp(uint appId)
    {
        if (_moveCancellationTokenMap.TryGetValue(appId, out var cts))
            await cts.CancelAsync();
    }

    public List<uint> GetDisabledDlcIds(uint appId)
    {
        if (!manifestMap.TryGetValue(appId, out var data)) return [];
        return data[KEY_USER_CONFIG]?[KEY_CONFIG_DISABLED_DLC]?.AsString()?.Split(",").Select(uint.Parse).ToList() ?? [];
    }

    public async Task FininishDownloadAndSave(uint appId)
    {
        var installDir = GetInstallDirectory(appId);
        if (installDir == null) return;

        SetNotUpdatePending(appId);
        SetDownloadStage(appId, null);
        UpdateAppSizeOnDisk(appId, await Disk.GetFolderSizeWithDu(installDir));
        Save(appId);
    }

    private string GetOS(uint appId)
    {
        var globalConfig = new GlobalConfig(GlobalConfig.DefaultPath());
        var compatTool = globalConfig.GetCompatForApp(appId).ToLower();
        if (compatTool.Contains("proton") || compatTool.Contains("wine"))
            return "windows";

        var osFromDepot = TryGetOsFromDepots(appId);

        // Only consider the extra manifest data if the depots are empty
        if (osFromDepot == null && manifestExtraMap.TryGetValue(appId, out var extraData))
        {
            var installedOs = extraData[EXTRA_KEY_OS]?.AsString();
            if (!string.IsNullOrEmpty(installedOs)) return installedOs;
        }

        return osFromDepot ?? "";
    }

    private string? TryGetOsFromDepots(uint appId)
    {
        var depots = GetDepots(appId);
        if (depots.Count == 0) return null;

        var appInfo = appInfoCache.GetCached(appId);
        if (appInfo == null) return null;

        var depotsSection = appInfo["depots"];

        foreach (var (depotId, _, _) in depots)
        {
            var oslist = depotsSection[depotId.ToString()]["config"]["oslist"]?.AsString();

            if (!string.IsNullOrEmpty(oslist))
            {
                var oslistSplit = oslist.Split(",");

                if (oslistSplit.Count() == 1)
                    return oslistSplit.FirstOrDefault();
            }
        }

        var defaultOslist = appInfo["common"]["oslist"].AsString();
        return defaultOslist?.Split(",").FirstOrDefault() ?? "windows";
    }

    private UserCompatConfig GetUserCompatConfig(uint accountId)
    {
        if (accountIdToUserCompatConfig.TryGetValue(accountId, out var cachedConfig)) return cachedConfig;

        var config = new UserCompatConfig(UserCompatConfig.DefaultPath(accountId));
        accountIdToUserCompatConfig[accountId] = config;
        return config;
    }

    private long GetNormalizedStateFlags(uint appId)
    {
        var operation = ~(StateFlags.UpdateStarted | StateFlags.UpdateRunning);
        return (manifestMap[appId][KEY_STATE_FLAGS]?.AsUnsignedInteger() ?? 0) & (int)operation;
    }

    private void WithLock(Action Callback)
    {
        fileLock.Wait();
        try
        {
            Callback();
        }
        finally
        {
            fileLock.Release();
        }
    }
}
