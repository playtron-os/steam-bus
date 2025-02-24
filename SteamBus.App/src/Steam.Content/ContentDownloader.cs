// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using Steam.Session;
using SteamKit2.CDN;
using SteamKit2;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Threading;
using System;
using Playtron.Plugin;
using System.ComponentModel;
using Tmds.DBus;
using Steam.Config;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace Steam.Content;

public class RequiredDepot
{
  public uint DepotId;
  public ulong ManifestId;
  public uint DepotAppId;
  public bool IsSharedDepot;
  public bool IsDlc;

  public RequiredDepot(uint depotId, ulong manifestId, uint depotAppId, bool isSharedDepot = false, bool isDlc = false)
  {
    DepotId = depotId;
    ManifestId = manifestId;
    DepotAppId = depotAppId;
    IsSharedDepot = isSharedDepot;
    IsDlc = isDlc;
  }
}

public class ContentDownloader
{
  public const uint INVALID_DEPOT_ID = uint.MaxValue;
  public const ulong INVALID_MANIFEST_ID = ulong.MaxValue;
  private const string DEFAULT_DOWNLOAD_DIR = "depots";
  private const string CONFIG_DIR = ".DepotDownloader";
  private static readonly string STAGING_DIR = Path.Combine(CONFIG_DIR, "staging");
  private static readonly string DOWNLOAD_STATUS_DIR = Path.Combine(CONFIG_DIR, "status");

  private SteamSession session;
  private DepotConfigStore depotConfigStore;
  private static CDNClientPool? cdnPool;
  private AppDownloadOptions? options;

  private event Action<InstallStartedDescription>? OnInstallStarted;
  private event Action<InstallProgressedDescription>? OnInstallProgressed;
  private event Action<string>? OnInstallCompleted;
  private event Action<(string appId, string error)>? OnInstallFailed;

  private static TaskCompletionSource? currentDownload;

  private sealed class DepotDownloadInfo(
      RequiredDepot requiredDepot, string branch, uint version,
      string installDir, byte[] depotKey)
  {
    public uint DepotId { get; } = requiredDepot.DepotId;
    public uint AppId { get; } = requiredDepot.DepotAppId;
    public ulong ManifestId { get; } = requiredDepot.ManifestId;
    public bool IsSharedDepot { get; } = requiredDepot.IsSharedDepot;
    public bool IsDlc { get; } = requiredDepot.IsDlc;
    public uint Version { get; } = version;
    public string Branch { get; } = branch;
    public string InstallDir { get; } = installDir;
    public byte[] DepotKey { get; } = depotKey;
  }


  private class ChunkMatch(DepotManifest.ChunkData oldChunk, DepotManifest.ChunkData newChunk)
  {
    public DepotManifest.ChunkData OldChunk { get; } = oldChunk;
    public DepotManifest.ChunkData NewChunk { get; } = newChunk;
  }

  private class DepotFilesData
  {
    public required DepotDownloadInfo depotDownloadInfo;
    public required DepotDownloadCounter depotCounter;
    public required string stagingDir;
    public required DepotManifest manifest;
    public required DepotManifest? previousManifest;
    public required List<DepotManifest.FileData> filteredFiles;
    public required HashSet<string> allFileNames;
    public required DownloadFileConfig downloadFileConfig;
  }

  private class FileStreamData : IDisposable
  {
    public required FileStream? fileStream;
    public required SemaphoreSlim fileLock;
    public int chunksToDownload;

    public void Dispose()
    {
      fileLock.Dispose();
      fileStream?.Dispose();
    }
  }

  private class ChunkQueueEntry
  {
    public FileStreamData fileStreamData;
    public DepotManifest.FileData fileData;
    public DepotManifest.ChunkData chunk;

    public ChunkQueueEntry(FileStreamData fileStreamData, DepotManifest.FileData fileData, DepotManifest.ChunkData chunk)
    {
      this.fileStreamData = fileStreamData;
      this.fileData = fileData;
      this.chunk = chunk;
    }
  }


  private class GlobalDownloadCounter : INotifyPropertyChanged
  {
    private ulong _sizeDownloaded;
    private ulong _sizeAllocated;

    public ulong previousDownloadSize;
    public ulong completeDownloadSize;
    public ulong totalBytesCompressed;
    public ulong totalBytesUncompressed;

    public ulong sizeDownloaded
    {
      get => _sizeDownloaded;
      set
      {
        if (_sizeDownloaded != value)
        {
          _sizeDownloaded = value;
          OnPropertyChanged(nameof(sizeDownloaded));
        }
      }
    }

    public ulong sizeAllocated
    {
      get => _sizeAllocated;
      set
      {
        if (_sizeAllocated != value)
        {
          _sizeAllocated = value;
          OnPropertyChanged(nameof(sizeAllocated));
        }
      }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
  }


  private class DepotDownloadCounter
  {
    public ulong completeDownloadSize;
    public ulong sizeDownloaded;
    public ulong depotBytesCompressed;
    public ulong depotBytesUncompressed;
  }


  public ContentDownloader(SteamSession steamSession, DepotConfigStore depotConfigStore)
  {
    this.session = steamSession;
    this.depotConfigStore = depotConfigStore;
  }

  public static async Task PauseInstall()
  {
    if (cdnPool != null)
    {
      if (cdnPool.ExhaustedToken != null)
        await cdnPool.ExhaustedToken.CancelAsync();
      cdnPool.Shutdown();
      cdnPool = null;

      if (currentDownload != null)
        await currentDownload!.Task;
    }
  }

  public async Task<InstallOption[]> GetInstallOptions(uint appId)
  {
    await session.RequestAppInfo(appId);

    var depots = session.GetSteam3AppSection(appId, EAppInfoSection.Depots);
    if (depots == null) return [];
    var common = session.GetSteam3AppSection(appId, EAppInfoSection.Common);
    if (common == null) return [];

    // TODO: Handle user generated content

    List<string> branchOptions = [];
    List<string> versionOptions = [];
    List<string> languageOptions = [];
    List<string> osOptions = [];
    List<string> archOptions = [];

    // Get languages
    var supportedLanguages = common["supported_languages"];
    if (supportedLanguages != null)
    {
      foreach (var languageSection in supportedLanguages.Children)
      {
        if (languageSection.Children.Count == 0)
          continue;

        var language = languageSection.Name;
        if (language == null || language == "")
          continue;

        var supported = languageSection["supported"];
        if (supported == null || supported.AsString() != "true")
          continue;

        languageOptions.Add(language);
      }
    }

    // Get OS
    var oslist = common["oslist"];
    if (oslist != null)
    {
      foreach (var os in oslist.AsString()?.Split(",") ?? [])
      {
        if (os != "")
          osOptions.Add(os);
      }
    }
    if (osOptions.Count() == 0)
      osOptions.Add("windows");

    // Get arch
    var osarch = common["osarch"];
    if (osarch != null)
    {
      foreach (var arch in osarch.AsString()?.Split(",") ?? [])
      {
        if (arch != "")
          archOptions.Add(arch);
      }
    }

    foreach (var depotSection in depots.Children)
    {
      if (depotSection.Children.Count == 0)
        continue;

      // Get branches and versions
      if (depotSection.Name == "branches")
      {
        foreach (var branchSection in depotSection.Children)
        {
          if (branchSection.Children.Count == 0)
            continue;

          var branch = branchSection.Name;
          if (branch == null || branch == "")
            continue;

          var version = branchSection["buildid"]?.AsString();
          if (version == null || version == "")
            continue;

          var pwdRequired = branchSection["pwdrequired"]?.AsString();
          if (pwdRequired == "1")
            continue;

          branchOptions.Add(branch);
          versionOptions.Add(version);
        }

        continue;
      }
    }

    return [
      new InstallOption(InstallOptionType.Version.ToString().ToLowerInvariant(), InstallOptionType.Version.GetDescription(), versionOptions.ToArray()),
      new InstallOption(InstallOptionType.Branch.ToString().ToLowerInvariant(), InstallOptionType.Branch.GetDescription(), branchOptions.ToArray()),
      new InstallOption(InstallOptionType.Language.ToString().ToLowerInvariant(), InstallOptionType.Language.GetDescription(), languageOptions.ToArray()),
      new InstallOption(InstallOptionType.OS.ToString().ToLowerInvariant(), InstallOptionType.OS.GetDescription(), osOptions.ToArray()),
      new InstallOption(InstallOptionType.Architecture.ToString().ToLowerInvariant(), InstallOptionType.Architecture.GetDescription(), archOptions.ToArray()),
    ];
  }


  public async Task DownloadAppAsync(uint appId, Action<InstallStartedDescription>? onInstallStarted, Action<InstallProgressedDescription>? onInstallProgressed,
    Action<string>? onInstallCompleted, Action<(string appId, string error)>? onInstallFailed)
  {
    var options = await AppDownloadOptions.CreateAsync(await GetAppInstallDir(appId));
    await DownloadAppAsync(appId, options, onInstallStarted, onInstallProgressed, onInstallCompleted, onInstallFailed);
  }


  public async Task DownloadAppAsync(uint appId, AppDownloadOptions options, Action<InstallStartedDescription>? onInstallStarted = null,
    Action<InstallProgressedDescription>? onInstallProgressed = null, Action<string>? onInstallCompleted = null, Action<(string appId, string error)>? onInstallFailed = null)
  {
    await session.WaitForLibrary();

    if (cdnPool != null)
    {
      Console.WriteLine($"Failed download for app: {appId} because a download is already in progress");
      onInstallFailed?.Invoke((appId.ToString(), DbusErrors.DownloadInProgress));
      return;
    }

    try
    {
      var currentOs = GetSteamOS();

      // Set the platform override so steam client won't detect an update when analyzing the game
      if (options.Os != null)
      {
        var userCompatConfig = new UserCompatConfig(UserCompatConfig.DefaultPath(this.session.SteamUser!.SteamID!.AccountID));
        userCompatConfig.SetPlatformOverride(appId, currentOs, options.Os);
        userCompatConfig.Save();

        // Force compat tool
        if (options.Os == "windows" && options.Os != currentOs)
        {
          var globalConfig = new GlobalConfig(GlobalConfig.DefaultPath());
          globalConfig.SetProton9CompatForApp(appId);
          globalConfig.Save();
        }
      }

      this.options = options;
      await Client.DetectLancacheServerAsync();
      if (Client.UseLancacheServer)
      {
        Console.WriteLine("Using LanCache server for downloads");
      }
      cdnPool = new CDNClientPool(this.session.SteamClient, appId, onInstallFailed);
      var cts = new CancellationTokenSource();
      cdnPool!.ExhaustedToken = cts;

      currentDownload = new TaskCompletionSource();

      // Keep track of signal handlers
      this.OnInstallStarted = onInstallStarted;
      this.OnInstallProgressed = onInstallProgressed;
      this.OnInstallCompleted = onInstallCompleted;
      this.OnInstallFailed = onInstallFailed;

      var branch = options.Branch;
      var os = options.Os ?? currentOs;
      var arch = options.Arch;
      var language = options.Language;
      var lv = options.LowViolence;
      var isUgc = options.IsUgc;

      await this.session.RequestAppInfo(appId);

      if (!await AccountHasAccess(appId))
      {
        if (await this.session.RequestFreeAppLicense(appId))
        {
          Console.WriteLine("Obtained FreeOnDemand license for app {0}", appId);

          // Fetch app info again in case we didn't get it fully without a license.
          await this.session.RequestAppInfo(appId, true);
        }
        else
        {
          var contentName = GetAppName(appId);
          Console.Error.WriteLine(string.Format("App {0} ({1}) is not available from this account.", appId, contentName));
          throw DbusExceptionHelper.ThrowAppNotOwned();
        }
      }

      var requiredDepots = await GetAppRequiredDepots(appId, options);
      var requiresInternetConnection = session.GetSteam3AppRequiresInternetConnection(appId);
      var version = session.GetSteam3AppBuildNumber(appId, options.Branch);
      Console.WriteLine($"Downloading version {version}");

      var infos = new List<DepotDownloadInfo>();
      var steamId = session.SteamUser?.SteamID?.ConvertToUInt64();
      var sharedApps = new List<(uint AppId, string installDir)>();

      foreach (var requiredDepot in requiredDepots)
      {
        Console.WriteLine($"Getting info for depotId:{requiredDepot.DepotId}, depotAppId:{requiredDepot.DepotAppId}");
        var info = await GetDepotInfo(requiredDepot, branch, options.InstallDirectory);
        if (info != null)
        {
          Console.WriteLine($"Downloading depotId:{requiredDepot.DepotId}, manifestId:{requiredDepot.ManifestId}");
          infos.Add(info);

          if (info.IsSharedDepot && !sharedApps.Any((s) => s.AppId == info.AppId))
          {
            sharedApps.Add((info.AppId, info.InstallDir));
            depotConfigStore.EnsureEntryExists(info.InstallDir, info.AppId, GetAppName(info.AppId));
            depotConfigStore.SetNewVersion(info.AppId, info.Version, info.Branch, language ?? "", "", steamId.ToString());
          }
        }
      }

      try
      {
        var installStartedData = new InstallStartedDescription
        {
          AppId = appId.ToString(),
          Version = version.ToString(),
          InstallDirectory = options.InstallDirectory,
          RequiresInternetConnection = requiresInternetConnection,
          Os = os,
        };

        depotConfigStore.EnsureEntryExists(options.InstallDirectory, appId, GetAppName(appId));
        depotConfigStore.SetNewVersion(appId, version, branch, language ?? "", os, steamId.ToString());

        await DownloadSteam3Async(appId, infos, cts, installStartedData).ConfigureAwait(false);
        onInstallCompleted?.Invoke(appId.ToString());

        depotConfigStore.SetDownloadStage(appId, null);
        depotConfigStore.UpdateAppSizeOnDisk(appId, await Disk.GetFolderSizeWithDu(installStartedData.InstallDirectory));
        depotConfigStore.Save(appId);

        foreach (var sharedApp in sharedApps)
        {
          depotConfigStore.UpdateAppSizeOnDisk(sharedApp.AppId, await Disk.GetFolderSizeWithDu(sharedApp.installDir));
          depotConfigStore.Save(sharedApp.AppId);
        }
      }
      catch (OperationCanceledException)
      {
        Console.WriteLine("App {0} download has been cancelled.", appId);
        throw;
      }

      Console.WriteLine($"Finished download task for app: {appId}");

      cdnPool = null;
    }
    catch (DBusException exception)
    {
      Console.WriteLine($"Finished download task for app: {appId} with DBUS exception: {exception}");
      cdnPool = null;
      onInstallFailed?.Invoke((appId.ToString(), exception.ErrorName));
      throw;
    }
    catch (OperationCanceledException)
    {
      cdnPool = null;
      throw;
    }
    catch (Exception exception)
    {
      Console.WriteLine($"Finished download task for app: {appId} with exception: {exception}");
      cdnPool = null;
      onInstallFailed?.Invoke((appId.ToString(), DbusErrors.DownloadFailed));
      throw;
    }
    finally
    {
      currentDownload?.TrySetResult();
      currentDownload = null;
    }
  }

  public async Task<List<RequiredDepot>> GetAppRequiredDepots(uint appId, AppDownloadOptions options, bool forceRefreshDepots = true, bool log = true)
  {
    await this.session.RequestAppInfo(appId);

    var os = options.Os ?? GetSteamOS();
    var arch = options.Arch;
    var language = options.Language;
    var lv = options.LowViolence;

    var requiredDepots = options.DepotManifestIds.Select((d) => new RequiredDepot(d.depotId, d.manifestId, appId)).ToList();
    var hasSpecificDepots = requiredDepots.Count > 0;
    var depotIdsFound = new List<uint>();
    var depotIdsExpected = requiredDepots.Select(x => x.DepotId).ToList();
    var depots = session.GetSteam3AppSection(appId, EAppInfoSection.Depots);
    var disabledDlcDepotIds = depotConfigStore.GetDisabledDlcDepotIds(appId);

    if (depots == null)
      throw DbusExceptionHelper.ThrowContentNotFound();

    // If onlymountshareddepots is defined, this is a shared depot and we shouldn't be downloading from it directly
    if (depots["onlymountshareddepots"]?.AsInteger() == 1)
      return [];

    // Handle user generated content
    if (options.IsUgc)
    {
      var workshopDepot = depots!["workshopdepot"].AsUnsignedInteger();
      if (workshopDepot != 0 && !depotIdsExpected.Contains(workshopDepot))
      {
        depotIdsExpected.Add(workshopDepot);
        requiredDepots = requiredDepots.Select(pair => new RequiredDepot(workshopDepot, pair.ManifestId, appId)).ToList();
      }

      depotIdsFound.AddRange(depotIdsExpected);
    }
    else
    {
      if (log)
        Console.WriteLine("Using app branch: '{0}'.", options.Branch);

      foreach (var depotSection in depots!.Children)
      {
        var id = INVALID_DEPOT_ID;
        if (depotSection.Children.Count == 0)
          continue;

        if (!uint.TryParse(depotSection.Name, out id))
          continue;

        if (hasSpecificDepots && !depotIdsExpected.Contains(id))
          continue;

        if (!hasSpecificDepots)
        {
          var depotConfig = depotSection["config"];
          if (depotConfig != KeyValue.Invalid)
          {
            if (!options.DownloadAllPlatforms &&
                depotConfig["oslist"] != KeyValue.Invalid &&
                !string.IsNullOrWhiteSpace(depotConfig["oslist"].Value))
            {
              var oslist = depotConfig["oslist"].Value?.Split(',') ?? [];
              if (Array.IndexOf(oslist, os) == -1)
                continue;
            }

            if (!options.DownloadAllArchs &&
                depotConfig["osarch"] != KeyValue.Invalid &&
                !string.IsNullOrWhiteSpace(depotConfig["osarch"].Value))
            {
              var depotArch = depotConfig["osarch"].Value;
              if (depotArch != (arch ?? GetSteamArch()))
                continue;
            }

            if (!options.DownloadAllLanguages &&
                depotConfig["language"] != KeyValue.Invalid &&
                !string.IsNullOrWhiteSpace(depotConfig["language"].Value))
            {
              var depotLang = depotConfig["language"].Value;
              if (depotLang != (language ?? "english"))
                continue;
            }

            if (!lv &&
                depotConfig["lowviolence"] != KeyValue.Invalid &&
                depotConfig["lowviolence"].AsBoolean())
              continue;
          }
        }

        if (disabledDlcDepotIds.Contains(id))
          continue;

        depotIdsFound.Add(id);

        if (!hasSpecificDepots)
        {
          var depotFromApp = depotSection["depotfromapp"]?.AsUnsignedInteger() ?? 0;

          if (depotSection["sharedinstall"]?.AsString() == "1")
          {
            if (depotFromApp == 0) continue;
            requiredDepots.Add(new RequiredDepot(id, INVALID_MANIFEST_ID, depotFromApp, true, false));
          }
          else
          {
            var dlcAppId = depotSection["dlcappid"]?.AsUnsignedInteger() ?? 0;
            var isDlc = dlcAppId != 0;
            requiredDepots.Add(new RequiredDepot(id, INVALID_MANIFEST_ID, depotFromApp == 0 ? appId : depotFromApp, false, isDlc));
          }
        }
      }

      if (requiredDepots.Count == 0 && !hasSpecificDepots)
      {
        if (log)
          Console.WriteLine(string.Format("Couldn't find any depots to download for app {0}", appId));
        throw DbusExceptionHelper.ThrowContentNotFound();
      }

      if (depotIdsFound.Count < depotIdsExpected.Count)
      {
        var remainingDepotIds = depotIdsExpected.Except(depotIdsFound);
        if (log)
          Console.WriteLine(string.Format("Depot {0} not listed for app {1}", string.Join(", ", remainingDepotIds), appId));
        throw DbusExceptionHelper.ThrowAppNotOwned();
      }
    }

    // Handle DLCs
    var dlcDepotIds = session.GetExtendedDLCs(appId);
    if (dlcDepotIds.Count() > 0)
    {
      dlcDepotIds = dlcDepotIds.Except(disabledDlcDepotIds).ToList();

      foreach (var dlcDepotId in dlcDepotIds)
        if (dlcDepotId != 0 && !requiredDepots.Any((d) => d.DepotId == dlcDepotId))
          requiredDepots.Add(new RequiredDepot(dlcDepotId, INVALID_MANIFEST_ID, dlcDepotId, false, true));
    }

    List<RequiredDepot> validDepotManifestIds = [];

    foreach (var requiredDepot in requiredDepots)
    {
      if (!await AccountHasAccess(requiredDepot.DepotId, forceRefreshDepots))
      {
        if (log)
          Console.WriteLine("Depot {0} is not available from this account.", requiredDepot.DepotId);
        continue;
      }

      var isDlc = dlcDepotIds.Contains(requiredDepot.DepotId);
      var depotFromAppId = isDlc ? requiredDepot.DepotId : appId;

      var manifestId = await GetSteam3DepotManifest(requiredDepot.DepotId, depotFromAppId, options.Branch);

      // If is dlc and manifest was not found in dlc app info, find in parent app
      if (isDlc && manifestId == INVALID_MANIFEST_ID)
      {
        manifestId = await GetSteam3DepotManifest(requiredDepot.DepotId, appId, options.Branch);
      }

      if (manifestId == INVALID_MANIFEST_ID && !string.Equals(options.Branch, AppDownloadOptions.DEFAULT_BRANCH, StringComparison.OrdinalIgnoreCase))
      {
        if (log)
          Console.WriteLine("Warning: Depot {0} does not have branch named \"{1}\". Trying {2} branch.", requiredDepot.DepotId, options.Branch, AppDownloadOptions.DEFAULT_BRANCH);
        manifestId = await GetSteam3DepotManifest(requiredDepot.DepotId, requiredDepot.DepotAppId, AppDownloadOptions.DEFAULT_BRANCH);
      }

      if (manifestId == INVALID_MANIFEST_ID)
      {
        if (log)
          Console.WriteLine("Depot {0} missing public subsection or manifest section.", requiredDepot.DepotId);
        continue;
      }

      requiredDepot.ManifestId = manifestId;
      validDepotManifestIds.Add(requiredDepot);
    }

    return validDepotManifestIds;
  }

  string GetAppName(uint appId)
  {
    var info = session.GetSteam3AppSection(appId, EAppInfoSection.Common);
    if (info == null)
      return string.Empty;

    return info["name"].AsString()!;
  }

  public async Task<string> GetAppInstallDir(uint appId)
  {
    await session!.RequestAppInfo(appId);
    var config = session.GetSteam3AppSection(appId, EAppInfoSection.Config);
    return config?["installdir"]?.AsString() ?? appId.ToString();
  }


  async Task<bool> AccountHasAccess(uint depotId, bool forceRefresh = false)
  {
    var steamUser = this.session.SteamClient.GetHandler<SteamUser>();
    if (steamUser == null || steamUser.SteamID == null || (this.session.PackageIDs == null && steamUser.SteamID.AccountType != EAccountType.AnonUser))
    {
      return false;
    }

    IEnumerable<uint> licenseQuery;
    if (steamUser.SteamID.AccountType == EAccountType.AnonUser)
    {
      licenseQuery = [17906];
    }
    else
    {
      licenseQuery = this.session.PackageIDs?.Distinct() ?? [];
    }

    await this.session.RequestPackageInfo(licenseQuery, forceRefresh);

    foreach (var license in licenseQuery)
    {
      if (this.session.PackageInfo.TryGetValue(license, out var package) && package != null)
      {
        if (package.KeyValues["appids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
          return true;

        if (package.KeyValues["depotids"].Children.Any(child => child.AsUnsignedInteger() == depotId))
          return true;
      }
    }

    return false;
  }


  async Task<DepotDownloadInfo?> GetDepotInfo(RequiredDepot requiredDepot, string branch, string baseInstallPath)
  {
    if (requiredDepot.IsSharedDepot)
    {
      var folderName = await GetAppInstallDir(requiredDepot.DepotAppId);
      baseInstallPath = await Disk.GetInstallRoot(folderName);
    }

    await this.session.RequestDepotKey(requiredDepot.DepotId, requiredDepot.DepotAppId);
    if (!this.session.DepotKeys.TryGetValue(requiredDepot.DepotId, out var depotKey))
    {
      Console.WriteLine("No valid depot key for {0}, unable to download.", requiredDepot.DepotId);
      return null;
    }

    var uVersion = session.GetSteam3AppBuildNumber(requiredDepot.DepotAppId, branch);

    if (!CreateDirectories(requiredDepot.DepotId, uVersion, out var installDir, baseInstallPath))
    {
      Console.WriteLine("Error: Unable to create install directories!");
      return null;
    }

    return new DepotDownloadInfo(requiredDepot, branch, uVersion, installDir, depotKey);
  }


  async Task<ulong> GetSteam3DepotManifest(uint depotId, uint appId, string branch)
  {
    var depots = session.GetSteam3AppSection(appId, EAppInfoSection.Depots);
    if (depots == null)
      return INVALID_MANIFEST_ID;

    var depotChild = depots[depotId.ToString()];

    if (depotChild == KeyValue.Invalid)
      return INVALID_MANIFEST_ID;

    // Shared depots can either provide manifests, or leave you relying on their parent app.
    // It seems that with the latter, "sharedinstall" will exist (and equals 2 in the one existance I know of).
    // Rather than relay on the unknown sharedinstall key, just look for manifests. Test cases: 111710, 346680.
    if (depotChild["manifests"] == KeyValue.Invalid && depotChild["depotfromapp"] != KeyValue.Invalid)
    {
      var otherAppId = depotChild["depotfromapp"].AsUnsignedInteger();
      if (otherAppId == appId)
      {
        // This shouldn't ever happen, but ya never know with Valve. Don't infinite loop.
        Console.WriteLine("App {0}, Depot {1} has depotfromapp of {2}!",
            appId, depotId, otherAppId);
        return INVALID_MANIFEST_ID;
      }

      await this.session.RequestAppInfo(otherAppId);

      return await GetSteam3DepotManifest(depotId, otherAppId, branch);
    }

    var manifests = depotChild["manifests"];
    var manifests_encrypted = depotChild["encryptedmanifests"];

    if (manifests.Children.Count == 0 && manifests_encrypted.Children.Count == 0)
      return INVALID_MANIFEST_ID;

    var node = manifests[branch]["gid"];

    if (node == KeyValue.Invalid && !string.Equals(branch, AppDownloadOptions.DEFAULT_BRANCH, StringComparison.OrdinalIgnoreCase))
    {
      var node_encrypted = manifests_encrypted[branch];
      if (node_encrypted != KeyValue.Invalid)
      {
        Console.WriteLine("Branch is password protected and not yet supported");

        //var password = Config.BetaPassword;
        //while (string.IsNullOrEmpty(password))
        //{
        //  Console.Write("Please enter the password for branch {0}: ", branch);
        //  Config.BetaPassword = password = Console.ReadLine();
        //}

        //var encrypted_gid = node_encrypted["gid"];

        //if (encrypted_gid != KeyValue.Invalid)
        //{
        //  // Submit the password to Steam now to get encryption keys
        //  await this.session.CheckAppBetaPassword(appId, Config.BetaPassword);

        //  if (!this.session.AppBetaPasswords.TryGetValue(branch, out var appBetaPassword))
        //  {
        //    Console.WriteLine("Password was invalid for branch {0}", branch);
        //    return INVALID_MANIFEST_ID;
        //  }

        //  var input = DecodeHexString(encrypted_gid.Value);
        //  byte[] manifest_bytes;
        //  try
        //  {
        //    manifest_bytes = SymmetricDecryptECB(input, appBetaPassword);
        //  }
        //  catch (Exception e)
        //  {
        //    Console.WriteLine("Failed to decrypt branch {0}: {1}", branch, e.Message);
        //    return INVALID_MANIFEST_ID;
        //  }

        //  return BitConverter.ToUInt64(manifest_bytes, 0);
        //}

        Console.WriteLine("Unhandled depot encryption for depotId {0}", depotId);
        return INVALID_MANIFEST_ID;
      }

      return INVALID_MANIFEST_ID;
    }

    if (node.Value == null)
      return INVALID_MANIFEST_ID;

    return ulong.Parse(node.Value);
  }


  private async Task DownloadSteam3Async(uint appId, List<DepotDownloadInfo> depots, CancellationTokenSource cts, InstallStartedDescription installStartedData)
  {
    if (this.options is null || cdnPool is null)
    {
      return;
    }
    //Ansi.Progress(Ansi.ProgressState.Indeterminate);

    var downloadCounter = new GlobalDownloadCounter();
    var depotsToDownload = new List<DepotFilesData>(depots.Count);
    var allFileNamesAllDepots = new HashSet<string>();
    var downloadStarted = false;

    downloadCounter.PropertyChanged += (sender, args) =>
    {
      if (!downloadStarted) return;

      if (cts.IsCancellationRequested)
      {
        depotConfigStore.Save(appId);
        return;
      }

      DownloadStage? stage = null;
      if (args.PropertyName == nameof(GlobalDownloadCounter.sizeDownloaded))
        stage = DownloadStage.Downloading;
      else if (args.PropertyName == nameof(GlobalDownloadCounter.sizeAllocated) || args.PropertyName == nameof(GlobalDownloadCounter.completeDownloadSize))
        stage = DownloadStage.Preallocating;

      if (stage != null)
      {
        var counter = (GlobalDownloadCounter)sender!;

        if (counter.completeDownloadSize > 0)
        {
          var size = stage == DownloadStage.Preallocating ? counter.sizeAllocated : counter.sizeDownloaded;
          var progress = size / (float)counter.completeDownloadSize * 100.0f;

          this.OnInstallProgressed?.Invoke(new InstallProgressedDescription
          {
            AppId = appId.ToString(),
            Stage = (uint)stage,
            DownloadedBytes = counter.sizeDownloaded,
            TotalDownloadSize = counter.completeDownloadSize,
            Progress = progress,
          });

          depotConfigStore.SetDownloadStage(appId, stage);
          depotConfigStore.SetCurrentSize(appId, counter.sizeDownloaded);
          depotConfigStore.SetTotalSize(appId, counter.completeDownloadSize);
          // No need to save this on every iteration, just when finished or cancelled
        }
      }
    };

    // Get already installed depots
    var oldDepots = depotConfigStore.GetDepots(appId);
    foreach (var depot in depots)
    {
      if (depot.IsSharedDepot)
      {
        var sharedDepots = depotConfigStore.GetDepots(depot.AppId);
        var manifest = sharedDepots.FirstOrDefault(x => x.DepotId == depot.DepotId);

        if (manifest.ManifestId != 0)
          oldDepots.Add((depot.DepotId, manifest.ManifestId, manifest.ManifestSize));
      }
    }

    // First, fetch all the manifests for each depot (including previous manifests) and perform the initial setup
    // also exclude already installed depots
    foreach (var depot in depots)
    {
      var oldDepot = oldDepots.FirstOrDefault((oldDepot) => oldDepot.DepotId == depot.DepotId && oldDepot.ManifestId == depot.ManifestId);
      if (oldDepot.ManifestId != 0)
      {
        downloadCounter.sizeDownloaded += oldDepot.ManifestSize;
        downloadCounter.completeDownloadSize += oldDepot.ManifestSize;
        continue;
      }

      var depotFileData = await ProcessDepotManifestAndFiles(appId, cts, depot, downloadCounter);

      if (depotFileData != null)
      {
        depotsToDownload.Add(depotFileData);
        allFileNamesAllDepots.UnionWith(depotFileData.allFileNames);
      }

      cts.Token.ThrowIfCancellationRequested();
    }

    // If we're about to write all the files to the same directory, we will need to first de-duplicate any files by path
    // This is in last-depot-wins order, from Steam or the list of depots supplied by the user
    if (!string.IsNullOrWhiteSpace(this.options?.InstallDirectory) && depotsToDownload.Count > 0)
    {
      var claimedFileNames = new HashSet<string>();

      for (var i = depotsToDownload.Count - 1; i >= 0; i--)
      {
        // For each depot, remove all files from the list that have been claimed by a later depot
        depotsToDownload[i].filteredFiles.RemoveAll(file => claimedFileNames.Contains(file.FileName));

        claimedFileNames.UnionWith(depotsToDownload[i].allFileNames);
      }
    }

    downloadStarted = true;
    installStartedData.TotalDownloadSize = downloadCounter.completeDownloadSize;
    OnInstallStarted?.Invoke(installStartedData);

    // Clean up old depots
    var oldBranch = depotConfigStore.GetBranch(appId);
    foreach (var (oldDepotId, oldManifestId, _) in oldDepots)
    {
      // If it is a depot that should be downloaded, skip clean up
      if (oldManifestId == INVALID_MANIFEST_ID || depots.Any((d) => d.DepotId == oldDepotId && d.ManifestId == oldManifestId))
        continue;

      var requiredDepot = new RequiredDepot(oldDepotId, oldManifestId, appId);
      var info = await GetDepotInfo(requiredDepot, oldBranch, installStartedData.InstallDirectory);
      if (info != null)
      {
        var depotFileData = await ProcessDepotManifestAndFiles(appId, cts, info, downloadCounter);

        if (depotFileData != null)
        {
          Console.WriteLine($"Cleaning up files for old depot {oldDepotId} with manifest {oldManifestId}");

          foreach (var fileName in depotFileData.allFileNames)
          {
            if (!allFileNamesAllDepots.Contains(fileName))
            {
              var fullPath = Path.Join(installStartedData.InstallDirectory, fileName);

              try
              {
                if (Directory.Exists(fullPath))
                  Directory.Delete(fullPath, true);
                else if (File.Exists(fullPath))
                  File.Delete(fullPath);
              }
              catch (Exception err)
              {
                Console.Error.WriteLine($"Error deleting file/directory at {fullPath} when removing old depot {oldDepotId}, err:{err}");
              }
            }
          }
        }
      }

      depotConfigStore.RemoveDepot(appId, oldDepotId);
      depotConfigStore.RemoveInstallScript(appId, oldDepotId);
    }

    // Pre-allocate / Verify
    List<ConcurrentQueue<ChunkQueueEntry>> networkChunkQueues = [];
    foreach (var depotFileData in depotsToDownload)
      networkChunkQueues.Add(await VerifySteam3AsyncDepotFiles(appId, cts, downloadCounter, depotFileData));

    // Set initial download size
    downloadCounter.sizeDownloaded += downloadCounter.previousDownloadSize;

    // Download depots
    var downloadIndex = 0;
    foreach (var depotFileData in depotsToDownload)
      await DownloadSteam3AsyncDepotFiles(appId, cts, downloadCounter, depotFileData, allFileNamesAllDepots, networkChunkQueues[downloadIndex++]);

    //Ansi.Progress(Ansi.ProgressState.Hidden);

    Console.WriteLine("Total downloaded: {0} bytes ({1} bytes uncompressed) from {2} depots",
        downloadCounter.totalBytesCompressed, downloadCounter.totalBytesUncompressed, depots.Count);
  }


  private async Task<DepotFilesData?> ProcessDepotManifestAndFiles(uint appId, CancellationTokenSource cts, DepotDownloadInfo depot, GlobalDownloadCounter downloadCounter)
  {
    var depotCounter = new DepotDownloadCounter();

    Console.WriteLine("Processing depot {0}", depot.DepotId);

    DepotManifest? oldManifest = null;
    DepotManifest? newManifest = null;
    var configDir = Path.Combine(depot.InstallDir, CONFIG_DIR);

    var lastManifestId = depotConfigStore.GetManifestID(depot.IsSharedDepot ? depot.AppId : appId, depot.DepotId) ?? INVALID_MANIFEST_ID;

    if (lastManifestId != INVALID_MANIFEST_ID)
    {
      // We only have to show this warning if the old manifest ID was different
      var badHashWarning = lastManifestId != depot.ManifestId;
      oldManifest = LoadManifestFromFile(configDir, depot.DepotId, lastManifestId, badHashWarning);
    }

    if (lastManifestId == depot.ManifestId && oldManifest != null)
    {
      newManifest = oldManifest;
      Console.WriteLine("Already have manifest {0} for depot {1}.", depot.ManifestId, depot.DepotId);
    }
    else
    {
      newManifest = LoadManifestFromFile(configDir, depot.DepotId, depot.ManifestId, true);

      if (newManifest != null)
      {
        Console.WriteLine("Already have manifest {0} for depot {1}.", depot.ManifestId, depot.DepotId);
      }
      else
      {
        Console.Write("Downloading depot manifest... ");

        ulong manifestRequestCode = 0;
        var manifestRequestCodeExpiration = DateTime.MinValue;

        do
        {
          cts.Token.ThrowIfCancellationRequested();

          Server? connection = null;

          try
          {
            if (cdnPool == null) throw new TaskCanceledException();
            connection = cdnPool!.GetConnection(cts.Token);

            string? cdnToken = null;
            if (this.session.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host ?? ""), out var authTokenCallbackPromise))
            {
              var result = await authTokenCallbackPromise.Task;
              cdnToken = result.Token;
            }

            var now = DateTime.Now;

            // In order to download this manifest, we need the current manifest request code
            // The manifest request code is only valid for a specific period in time
            if (manifestRequestCode == 0 || now >= manifestRequestCodeExpiration)
            {
              manifestRequestCode = await this.session.GetDepotManifestRequestCodeAsync(
                  depot.DepotId,
                  depot.AppId,
                  depot.ManifestId,
                  depot.Branch);
              // This code will hopefully be valid for one period following the issuing period
              manifestRequestCodeExpiration = now.Add(TimeSpan.FromMinutes(5));

              // If we could not get the manifest code, this is a fatal error
              if (manifestRequestCode == 0)
              {
                Console.WriteLine("No manifest request code was returned for {0} {1}", depot.DepotId, depot.ManifestId);
                cts.Cancel();
                OnInstallFailed?.Invoke((appId.ToString(), DbusErrors.ContentNotFound));
              }
            }

            DebugLog.WriteLine("ContentDownloader",
                "Downloading manifest {0} from {1} with {2}",
                depot.ManifestId,
                connection,
                cdnPool.ProxyServer != null ? cdnPool.ProxyServer : "no proxy");
            newManifest = await cdnPool.CDNClient.DownloadManifestAsync(
                depot.DepotId,
                depot.ManifestId,
                manifestRequestCode,
                connection,
                depot.DepotKey,
                cdnPool.ProxyServer,
                cdnToken).ConfigureAwait(false);

            cdnPool.ReturnConnection(connection);
          }
          catch (TaskCanceledException)
          {
            Console.WriteLine("Connection timeout downloading depot manifest {0} {1}. Retrying.", depot.DepotId, depot.ManifestId);
          }
          catch (SteamKitWebRequestException e)
          {
            // If the CDN returned 403, attempt to get a cdn auth if we didn't yet
            if (e.StatusCode == HttpStatusCode.Forbidden && connection != null && !this.session.CDNAuthTokens.ContainsKey((depot.DepotId, connection!.Host ?? "")))
            {
              await this.session.RequestCDNAuthToken(depot.AppId, depot.DepotId, connection!);

              if (cdnPool == null) throw new TaskCanceledException();
              cdnPool!.ReturnConnection(connection);

              continue;
            }

            if (connection != null)
            {
              if (cdnPool == null) throw new TaskCanceledException();
              cdnPool!.ReturnBrokenConnection(connection);
            }

            if (e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden)
            {
              Console.WriteLine("Encountered {2} for depot manifest {0} {1}. Aborting.", depot.DepotId, depot.ManifestId, (int)e.StatusCode);
              break;
            }

            if (e.StatusCode == HttpStatusCode.NotFound)
            {
              Console.WriteLine("Encountered 404 for depot manifest {0} {1}. Aborting.", depot.DepotId, depot.ManifestId);
              break;
            }

            Console.WriteLine("Encountered error downloading depot manifest {0} {1}: {2}", depot.DepotId, depot.ManifestId, e.StatusCode);
          }
          catch (OperationCanceledException)
          {
            break;
          }
          catch (Exception e)
          {
            if (connection != null)
            {
              if (cdnPool == null) throw new TaskCanceledException();
              cdnPool!.ReturnBrokenConnection(connection);
            }
            Console.WriteLine("Encountered error downloading manifest for depot {0} {1}: {2}", depot.DepotId, depot.ManifestId, e.Message);
          }
        } while (newManifest == null);

        if (newManifest == null)
        {
          Console.WriteLine("\nUnable to download manifest {0} for depot {1}", depot.ManifestId, depot.DepotId);
          cts.Cancel();
          OnInstallFailed?.Invoke((appId.ToString(), DbusErrors.ContentNotFound));
        }

        // Throw the cancellation exception if requested so that this task is marked failed
        cts.Token.ThrowIfCancellationRequested();

        if (newManifest != null)
          SaveManifestToFile(configDir, newManifest);

        Console.WriteLine(" Done!");
      }
    }

    Console.WriteLine("Manifest {0} ({1})", depot.ManifestId, newManifest?.CreationTime);

    if (this.options?.DownloadManifestOnly == true)
    {
      if (newManifest != null)
        DumpManifestToTextFile(depot, newManifest);
      return null;
    }

    if (newManifest == null)
      return null;

    var stagingDir = Path.Combine(depot.InstallDir, STAGING_DIR);

    var filesAfterExclusions = newManifest!.Files?.AsParallel().Where(f => TestIsFileIncluded(f.FileName)).ToList() ?? [];
    var allFileNames = new HashSet<string>(filesAfterExclusions.Count);

    // Pre-process
    filesAfterExclusions.ForEach(file =>
    {
      allFileNames.Add(file.FileName);

      var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
      var fileStagingPath = Path.Combine(stagingDir, file.FileName);

      if (file.Flags.HasFlag(EDepotFileFlag.Directory))
      {
        Directory.CreateDirectory(fileFinalPath);
        Directory.CreateDirectory(fileStagingPath);
      }
      else
      {
        // Some manifests don't explicitly include all necessary directories
        Directory.CreateDirectory(Path.GetDirectoryName(fileFinalPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(fileStagingPath)!);

        downloadCounter.completeDownloadSize += file.TotalSize;
        depotCounter.completeDownloadSize += file.TotalSize;
      }
    });

    return new DepotFilesData
    {
      depotDownloadInfo = depot,
      depotCounter = depotCounter,
      stagingDir = stagingDir,
      manifest = newManifest,
      previousManifest = oldManifest,
      filteredFiles = filesAfterExclusions,
      allFileNames = allFileNames,
      downloadFileConfig = new DownloadFileConfig(Path.Combine(depot.InstallDir, DOWNLOAD_STATUS_DIR, depot.DepotId.ToString()))
    };
  }


  public static string GetSteamOS()
  {
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
      return "windows";
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
      return "macos";
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
      return "linux";
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
    {
      // Return linux as freebsd steam client doesn't exist yet
      return "linux";
    }

    return "unknown";
  }


  public static string GetSteamArch()
  {
    return Environment.Is64BitOperatingSystem ? "64" : "32";
  }


  static bool CreateDirectories(uint depotId, uint depotVersion, out string installDir, string baseInstallPath = "")
  {
    installDir = "";
    try
    {
      if (string.IsNullOrWhiteSpace(baseInstallPath))
      {
        Directory.CreateDirectory(DEFAULT_DOWNLOAD_DIR);

        var depotPath = Path.Combine(DEFAULT_DOWNLOAD_DIR, depotId.ToString());
        Directory.CreateDirectory(depotPath);

        installDir = Path.Combine(depotPath, depotVersion.ToString());
        Directory.CreateDirectory(installDir);

        Directory.CreateDirectory(Path.Combine(installDir, CONFIG_DIR));
        Directory.CreateDirectory(Path.Combine(installDir, STAGING_DIR));
      }
      else
      {
        // Delete if it is a file/symlink
        var info = new FileInfo(baseInstallPath);
        if (info != null && (info.Attributes & FileAttributes.ReparsePoint) != 0)
          File.Delete(baseInstallPath);

        Directory.CreateDirectory(baseInstallPath);

        installDir = baseInstallPath;

        Directory.CreateDirectory(Path.Combine(installDir, CONFIG_DIR));
        Directory.CreateDirectory(Path.Combine(installDir, STAGING_DIR));
      }
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine($"Error creating directories, ex:{exception}");
      return false;
    }

    return true;
  }

  private async Task<ConcurrentQueue<ChunkQueueEntry>> VerifySteam3AsyncDepotFiles(uint appId, CancellationTokenSource cts, GlobalDownloadCounter downloadCounter, DepotFilesData depotFilesData)
  {
    var depot = depotFilesData.depotDownloadInfo;
    var depotCounter = depotFilesData.depotCounter;

    Console.WriteLine("Pre allocating depot {0}", depot.DepotId);

    var files = depotFilesData.filteredFiles.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory)).ToArray();
    var networkChunkQueue = new ConcurrentQueue<ChunkQueueEntry>();

    try
    {
      await InvokeAsync(
          files.Select(file => new Func<Task>(async () =>
              await Task.Run(() => DownloadSteam3AsyncDepotFile(appId, cts, downloadCounter, depotFilesData, file, networkChunkQueue)))),
          maxDegreeOfParallelism: this.options!.MaxDownloads
      );

      return networkChunkQueue;
    }
    catch (Exception)
    {
      foreach (var entry in networkChunkQueue)
        entry.fileStreamData.Dispose();
      throw;
    }
  }

  private async Task DownloadSteam3AsyncDepotFiles(uint appId, CancellationTokenSource cts,
      GlobalDownloadCounter downloadCounter, DepotFilesData depotFilesData, HashSet<string> allFileNamesAllDepots,
      ConcurrentQueue<ChunkQueueEntry> networkChunkQueue)
  {
    var depot = depotFilesData.depotDownloadInfo;
    var depotCounter = depotFilesData.depotCounter;

    Console.WriteLine("Downloading depot {0}", depot.DepotId);

    try
    {
      await InvokeAsync(
          networkChunkQueue.Select(q => new Func<Task>(async () =>
              await Task.Run(() => DownloadSteam3AsyncDepotFileChunk(appId, cts, downloadCounter, depotFilesData,
                  q.fileData, q.fileStreamData, q.chunk)))),
          maxDegreeOfParallelism: this.options!.MaxDownloads
      );

      // Check for deleted files if updating the depot.
      if (depotFilesData.previousManifest != null)
      {
        var previousFilteredFiles = depotFilesData.previousManifest.Files?.AsParallel().Where(f => TestIsFileIncluded(f.FileName)).Select(f => f.FileName).ToHashSet() ?? [];

        // Check if we are writing to a single output directory. If not, each depot folder is managed independently
        if (string.IsNullOrWhiteSpace(this.options.InstallDirectory))
        {
          // Of the list of files in the previous manifest, remove any file names that exist in the current set of all file names
          previousFilteredFiles.ExceptWith(depotFilesData.allFileNames);
        }
        else
        {
          // Of the list of files in the previous manifest, remove any file names that exist in the current set of all file names across all depots being downloaded
          previousFilteredFiles.ExceptWith(allFileNamesAllDepots);
        }

        foreach (var existingFileName in previousFilteredFiles)
        {
          var fileFinalPath = Path.Combine(depot.InstallDir, existingFileName);

          if (!File.Exists(fileFinalPath))
            continue;

          File.Delete(fileFinalPath);
          Console.WriteLine("Deleted {0}", fileFinalPath);
        }
      }

      // Clean up temp download manifest files
      depotFilesData.downloadFileConfig.Remove();

      if (depot.IsSharedDepot)
      {
        depotConfigStore.SetSharedDepot(appId, depot.AppId, depot.DepotId);

        depotConfigStore.SetManifestID(depot.AppId, depot.DepotId, depot.ManifestId, depotCounter.completeDownloadSize, null);
        depotConfigStore.Save(depot.AppId);
      }
      else
      {
        depotConfigStore.SetManifestID(appId, depot.DepotId, depot.ManifestId, depotCounter.completeDownloadSize, depot.IsDlc ? depot.AppId : null);
      }

      depotConfigStore.Save(appId);

      Console.WriteLine("Depot {0} - Downloaded {1} bytes ({2} bytes uncompressed)", depot.DepotId, depotCounter.depotBytesCompressed, depotCounter.depotBytesUncompressed);
    }
    catch (Exception)
    {
      foreach (var entry in networkChunkQueue)
        entry.fileStreamData.Dispose();
      throw;
    }
  }

  private void DownloadSteam3AsyncDepotFile(
      uint appId,
      CancellationTokenSource cts,
      GlobalDownloadCounter downloadCounter,
      DepotFilesData depotFilesData,
      DepotManifest.FileData file,
      ConcurrentQueue<ChunkQueueEntry> networkChunkQueue)
  {
    cts.Token.ThrowIfCancellationRequested();

    var depot = depotFilesData.depotDownloadInfo;
    var stagingDir = depotFilesData.stagingDir;
    var depotDownloadCounter = depotFilesData.depotCounter;
    var oldProtoManifest = depotFilesData.previousManifest;
    DepotManifest.FileData? oldManifestFile = null;
    if (oldProtoManifest != null)
    {
      oldManifestFile = oldProtoManifest.Files?.SingleOrDefault(f => f.FileName == file.FileName);
    }

    var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
    var fileStagingPath = Path.Combine(stagingDir, file.FileName);

    // This may still exist if the previous run exited before cleanup
    if (File.Exists(fileStagingPath))
      File.Delete(fileStagingPath);

    List<DepotManifest.ChunkData> neededChunks;
    var fi = new FileInfo(fileFinalPath);
    var fileDidExist = fi.Exists;
    var fileVersion = Convert.ToHexString(file.FileHash);
    var fileConfig = depotFilesData.downloadFileConfig.Get(file.FileName);

    if (!fileDidExist)
    {
      Console.WriteLine("Pre-allocating {0}", fileFinalPath);

      // create new file. need all chunks
      using var fs = File.Create(fileFinalPath);
      try
      {
        fs.SetLength((long)file.TotalSize);
      }
      catch (IOException ex)
      {
        Console.Error.WriteLine(string.Format("Failed to allocate file {0}: {1}", fileFinalPath, ex.Message));
        throw DbusExceptionHelper.ThrowNotEnoughSpace();
      }

      neededChunks = [.. file.Chunks.OrderBy(x => x.Offset)];

      lock (downloadCounter)
      {
        downloadCounter.sizeAllocated += file.TotalSize;
      }

      depotFilesData.downloadFileConfig.SetAllocated(file.FileName, fileVersion, file.Chunks.Count);
    }
    else
    {
      if (fileConfig != null && fileConfig.ChunkCount == file.Chunks.Count && fileConfig.Version == fileVersion)
      {
        var filteredChunks = file.Chunks.Where((chunk) => !fileConfig.DownloadedChunks.Contains(Convert.ToHexString(chunk.ChunkID ?? [])));
        neededChunks = [.. filteredChunks.OrderBy(x => x.Offset)];
      }
      // open existing
      else if (oldManifestFile != null)
      {
        neededChunks = [];

        var hashMatches = oldManifestFile.FileHash.SequenceEqual(file.FileHash);
        if (this.options!.VerifyAll || !hashMatches)
        {
          // we have a version of this file, but it doesn't fully match what we want
          if (this.options.VerifyAll)
          {
            Console.WriteLine("Validating {0}", fileFinalPath);
          }

          var matchingChunks = new List<ChunkMatch>();

          foreach (var chunk in file.Chunks)
          {
            var oldChunk = oldManifestFile.Chunks.FirstOrDefault(c => (c.ChunkID ?? []).SequenceEqual(chunk.ChunkID ?? []));
            if (oldChunk != null)
            {
              matchingChunks.Add(new ChunkMatch(oldChunk, chunk));
            }
            else
            {
              neededChunks.Add(chunk);
            }
          }

          var orderedChunks = matchingChunks.OrderBy(x => x.OldChunk.Offset);

          var copyChunks = new List<ChunkMatch>();

          using (var fsOld = File.Open(fileFinalPath, FileMode.Open))
          {
            foreach (var match in orderedChunks)
            {
              fsOld.Seek((long)match.OldChunk.Offset, SeekOrigin.Begin);

              var adler = AdlerHash(fsOld, (int)match.OldChunk.UncompressedLength);
              if (!adler.SequenceEqual(BitConverter.GetBytes(match.OldChunk.Checksum)))
              {
                neededChunks.Add(match.NewChunk);
              }
              else
              {
                copyChunks.Add(match);
              }

            }
          }

          if (!hashMatches || neededChunks.Count > 0)
          {
            File.Move(fileFinalPath, fileStagingPath);

            using (var fsOld = File.Open(fileStagingPath, FileMode.Open))
            {
              using var fs = File.Open(fileFinalPath, FileMode.Create);
              try
              {
                fs.SetLength((long)file.TotalSize);
              }
              catch (IOException ex)
              {
                Console.Error.WriteLine(string.Format("Failed to resize file to expected size {0}: {1}", fileFinalPath, ex.Message));
                throw DbusExceptionHelper.ThrowNotEnoughSpace();
              }

              foreach (var match in copyChunks)
              {
                fsOld.Seek((long)match.OldChunk.Offset, SeekOrigin.Begin);

                var tmp = new byte[match.OldChunk.UncompressedLength];
                fsOld.ReadExactly(tmp);

                fs.Seek((long)match.NewChunk.Offset, SeekOrigin.Begin);
                fs.Write(tmp, 0, tmp.Length);
              }
            }

            File.Delete(fileStagingPath);
            depotFilesData.downloadFileConfig.SetChunksDownloaded(file.FileName, fileVersion, file.Chunks.Count, copyChunks.Select((chunk) => Convert.ToHexString(chunk.NewChunk.ChunkID ?? [])));
          }
        }
      }
      else
      {
        // No old manifest or file not in old manifest. We must validate.

        try
        {
          using var fs = File.Open(fileFinalPath, FileMode.Open);
          if ((ulong)fi.Length != file.TotalSize)
          {
            try
            {
              fs.SetLength((long)file.TotalSize);
            }
            catch (IOException ex)
            {
              Console.Error.WriteLine(string.Format("Failed to allocate file {0}: {1}", fileFinalPath, ex.Message));
              throw DbusExceptionHelper.ThrowNotEnoughSpace();
            }
          }

          // Validate checksums in case file has been written to
          Console.WriteLine("Validating file not found in old manifest {0}, {1}", fileFinalPath, file.Chunks.Count);
          neededChunks = ValidateSteam3FileChecksums(fs, [.. file.Chunks.OrderBy(x => x.Offset)]);
        }
        catch (DBusException)
        {
          throw;
        }
        catch (Exception err)
        {
          Console.Error.WriteLine("Error when allocating file {0}: {1}", fileFinalPath, err.Message);
          throw DbusExceptionHelper.ThrowPermission();
        }
      }

      var downloadedChunks = file.Chunks.Except(neededChunks);
      depotFilesData.downloadFileConfig.SetChunksDownloaded(file.FileName, fileVersion, file.Chunks.Count, downloadedChunks.Select((chunk) => Convert.ToHexString(chunk.ChunkID ?? [])));

      if (neededChunks.Count == 0)
      {
        // Check if install script
        if (InstallScript.IsInstallScript(fileFinalPath))
        {
          var depotAppId = depotFilesData.depotDownloadInfo.IsSharedDepot ? depotFilesData.depotDownloadInfo.AppId : appId;
          depotConfigStore.SetInstallScript(depotAppId, depotFilesData.depotDownloadInfo.DepotId, file.FileName);
          depotConfigStore.Save(depotAppId);
        }

        lock (depotDownloadCounter)
        {
          depotDownloadCounter.sizeDownloaded += file.TotalSize;
          Console.WriteLine("File: {0,6:#00.00}% {1}", (depotDownloadCounter.sizeDownloaded / (float)depotDownloadCounter.completeDownloadSize) * 100.0f, fileFinalPath);
        }

        lock (downloadCounter)
        {
          downloadCounter.previousDownloadSize += file.TotalSize;
          downloadCounter.sizeAllocated += file.TotalSize;
        }

        return;
      }

      var sizeOnDisk = (file.TotalSize - (ulong)neededChunks.Select(x => (long)x.UncompressedLength).Sum());
      if (sizeOnDisk > 0)
      {
        lock (depotDownloadCounter)
        {
          depotDownloadCounter.sizeDownloaded += sizeOnDisk;
        }
      }

      lock (downloadCounter)
      {
        downloadCounter.previousDownloadSize += sizeOnDisk;
        downloadCounter.sizeAllocated += file.TotalSize;
      }
    }

    var fileIsExecutable = file.Flags.HasFlag(EDepotFileFlag.Executable);
    if (fileIsExecutable && (!fileDidExist || oldManifestFile == null || !oldManifestFile.Flags.HasFlag(EDepotFileFlag.Executable)))
    {
      SetExecutable(fileFinalPath, true);
    }
    else if (!fileIsExecutable && oldManifestFile != null && oldManifestFile.Flags.HasFlag(EDepotFileFlag.Executable))
    {
      SetExecutable(fileFinalPath, false);
    }

    var fileStreamData = new FileStreamData
    {
      fileStream = null,
      fileLock = new SemaphoreSlim(1),
      chunksToDownload = neededChunks.Count
    };

    foreach (var chunk in neededChunks)
    {
      networkChunkQueue.Enqueue(new ChunkQueueEntry(fileStreamData, file, chunk));
    }
  }

  private async Task DownloadSteam3AsyncDepotFileChunk(
      uint appId,
      CancellationTokenSource cts,
      GlobalDownloadCounter downloadCounter,
      DepotFilesData depotFilesData,
      DepotManifest.FileData file,
      FileStreamData fileStreamData,
      DepotManifest.ChunkData chunk)
  {
    cts.Token.ThrowIfCancellationRequested();

    var depot = depotFilesData.depotDownloadInfo;
    var depotDownloadCounter = depotFilesData.depotCounter;

    var chunkID = Convert.ToHexString(chunk.ChunkID ?? []).ToLowerInvariant();

    var written = 0;
    var chunkBuffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);

    var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);

    try
    {
      do
      {
        cts.Token.ThrowIfCancellationRequested();

        Server? connection = null;

        try
        {
          if (cdnPool == null) throw new TaskCanceledException();
          connection = cdnPool!.GetConnection(cts.Token);

          string? cdnToken = null;
          if (this.session.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host ?? ""), out var authTokenCallbackPromise))
          {
            var result = await authTokenCallbackPromise.Task;
            cdnToken = result.Token;
          }

          DebugLog.WriteLine("ContentDownloader", "Downloading chunk {0} from {1} with {2}", chunkID, connection, cdnPool.ProxyServer != null ? cdnPool.ProxyServer : "no proxy");
          written = await cdnPool.CDNClient.DownloadDepotChunkAsync(
              depot.DepotId,
              chunk,
              connection,
              chunkBuffer,
              depot.DepotKey,
              cdnPool.ProxyServer,
              cdnToken).ConfigureAwait(false);

          cdnPool.ReturnConnection(connection);

          break;
        }
        catch (TaskCanceledException)
        {
          Console.WriteLine("Connection timeout downloading chunk {0}", chunkID);
        }
        catch (SteamKitWebRequestException e)
        {
          // If the CDN returned 403, attempt to get a cdn auth if we didn't yet,
          // if auth task already exists, make sure it didn't complete yet, so that it gets awaited above
          if (e.StatusCode == HttpStatusCode.Forbidden && connection != null &&
              (!this.session.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host ?? ""), out var authTokenCallbackPromise) || !authTokenCallbackPromise.Task.IsCompleted))
          {
            await this.session.RequestCDNAuthToken(depot.AppId, depot.DepotId, connection);

            if (connection != null)
            {
              if (cdnPool == null) throw new TaskCanceledException();
              cdnPool!.ReturnConnection(connection);
            }

            continue;
          }

          if (connection != null)
          {
            if (cdnPool == null) throw new TaskCanceledException();
            cdnPool!.ReturnBrokenConnection(connection);
          }

          if (e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden)
          {
            Console.WriteLine("Encountered {1} for chunk {0}. Aborting.", chunkID, (int)e.StatusCode);
            break;
          }

          Console.WriteLine("Encountered error downloading chunk {0}: {1}", chunkID, e.StatusCode);
        }
        catch (OperationCanceledException)
        {
          break;
        }
        catch (Exception e)
        {
          if (connection != null)
          {
            if (cdnPool == null) throw new TaskCanceledException();
            cdnPool!.ReturnBrokenConnection(connection);
          }
          Console.WriteLine("Encountered unexpected error downloading chunk {0}: {1}", chunkID, e.Message);
        }
      } while (written == 0);

      if (written == 0)
      {
        Console.WriteLine("Failed to find any server with chunk {0} for depot {1}. Aborting.", chunkID, depot.DepotId);
        cts.Cancel();
        OnInstallFailed?.Invoke((appId.ToString(), DbusErrors.ContentNotFound));
      }

      // Throw the cancellation exception if requested so that this task is marked failed
      cts.Token.ThrowIfCancellationRequested();

      try
      {
        await fileStreamData.fileLock.WaitAsync().ConfigureAwait(false);

        if (fileStreamData.fileStream == null)
          fileStreamData.fileStream = File.Open(fileFinalPath, FileMode.Open);

        fileStreamData.fileStream.Seek((long)chunk.Offset, SeekOrigin.Begin);
        await fileStreamData.fileStream.WriteAsync(chunkBuffer.AsMemory(0, written), cts.Token);
      }
      finally
      {
        fileStreamData.fileLock.Release();
      }

      depotFilesData.downloadFileConfig.SetChunkDownloaded(file.FileName, Convert.ToHexString(chunk.ChunkID ?? []));
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(chunkBuffer);
    }

    var remainingChunks = Interlocked.Decrement(ref fileStreamData.chunksToDownload);
    if (remainingChunks == 0)
    {
      fileStreamData.fileStream?.Dispose();
      fileStreamData.fileLock.Dispose();

      if (InstallScript.IsInstallScript(fileFinalPath))
      {
        var depotAppId = depotFilesData.depotDownloadInfo.IsSharedDepot ? depotFilesData.depotDownloadInfo.AppId : appId;
        depotConfigStore.SetInstallScript(depotAppId, depotFilesData.depotDownloadInfo.DepotId, file.FileName);
        depotConfigStore.Save(depotAppId);
      }
    }

    ulong sizeDownloaded = 0;
    lock (depotDownloadCounter)
    {
      sizeDownloaded = depotDownloadCounter.sizeDownloaded + (ulong)written;
      depotDownloadCounter.sizeDownloaded = sizeDownloaded;
      depotDownloadCounter.depotBytesCompressed += chunk.CompressedLength;
      depotDownloadCounter.depotBytesUncompressed += chunk.UncompressedLength;
    }

    lock (downloadCounter)
    {
      downloadCounter.sizeDownloaded += (ulong)written;
      downloadCounter.totalBytesCompressed += chunk.CompressedLength;
      downloadCounter.totalBytesUncompressed += chunk.UncompressedLength;
      //Ansi.Progress(downloadCounter.totalBytesUncompressed, downloadCounter.completeDownloadSize);
    }

    if (remainingChunks == 0)
      Console.WriteLine("Chunk: {0,6:#00.00}% {1}", (sizeDownloaded / (float)depotDownloadCounter.completeDownloadSize) * 100.0f, fileFinalPath);
  }

  class ChunkIdComparer : IEqualityComparer<byte[]>
  {
    public bool Equals(byte[]? x, byte[]? y)
    {
      if (ReferenceEquals(x, y)) return true;
      if (x == null || y == null) return false;
      return x.SequenceEqual(y);
    }

    public int GetHashCode(byte[] obj)
    {
      ArgumentNullException.ThrowIfNull(obj);

      // ChunkID is SHA-1, so we can just use the first 4 bytes
      return BitConverter.ToInt32(obj, 0);
    }
  }

  static void DumpManifestToTextFile(DepotDownloadInfo depot, DepotManifest manifest)
  {
    var txtManifest = Path.Combine(depot.InstallDir, $"manifest_{depot.DepotId}_{depot.ManifestId}.txt");
    using var sw = new StreamWriter(txtManifest);

    sw.WriteLine($"Content Manifest for Depot {depot.DepotId} ");
    sw.WriteLine();
    sw.WriteLine($"Manifest ID / date     : {depot.ManifestId} / {manifest.CreationTime} ");

    var uniqueChunks = new HashSet<byte[]>(new ChunkIdComparer());

    foreach (var file in manifest.Files ?? [])
    {
      foreach (var chunk in file.Chunks)
      {
        if (chunk.ChunkID != null)
          uniqueChunks.Add(chunk.ChunkID);
      }
    }

    sw.WriteLine($"Total number of files  : {manifest.Files?.Count} ");
    sw.WriteLine($"Total number of chunks : {uniqueChunks.Count} ");
    sw.WriteLine($"Total bytes on disk    : {manifest.TotalUncompressedSize} ");
    sw.WriteLine($"Total bytes compressed : {manifest.TotalCompressedSize} ");
    sw.WriteLine();
    sw.WriteLine();
    sw.WriteLine("          Size Chunks File SHA                                 Flags Name");

    foreach (var file in manifest.Files ?? [])
    {
      var sha1Hash = Convert.ToHexString(file.FileHash).ToLower();
      sw.WriteLine($"{file.TotalSize,14:d} {file.Chunks.Count,6:d} {sha1Hash} {(int)file.Flags,5:x} {file.FileName}");
    }
  }

  bool TestIsFileIncluded(string filename)
  {
    if (!(this.options?.UsingFileList ?? false))
      return true;

    filename = filename.Replace('\\', '/');

    if (this.options.FilesToDownload?.Contains(filename) == true)
    {
      return true;
    }

    foreach (var rgx in this.options.FilesToDownloadRegex ?? [])
    {
      var m = rgx.Match(filename);

      if (m.Success)
        return true;
    }

    return false;
  }

  public static DepotManifest? LoadManifestFromFile(string directory, uint depotId, ulong manifestId, bool badHashWarning)
  {
    // Try loading Steam format manifest first.
    var filename = Path.Combine(directory, string.Format("{0}_{1}.manifest", depotId, manifestId));

    if (File.Exists(filename))
    {
      byte[]? expectedChecksum;

      try
      {
        expectedChecksum = File.ReadAllBytes(filename + ".sha");
      }
      catch (IOException)
      {
        expectedChecksum = null;
      }

      var currentChecksum = FileSHAHash(filename);

      if (expectedChecksum != null && expectedChecksum.SequenceEqual(currentChecksum))
      {
        return DepotManifest.LoadFromFile(filename);
      }
      else if (badHashWarning)
      {
        Console.WriteLine("Manifest {0} on disk did not match the expected checksum.", manifestId);
      }
    }

    // Try converting legacy manifest format.
    filename = Path.Combine(directory, string.Format("{0}_{1}.bin", depotId, manifestId));

    if (File.Exists(filename))
    {
      byte[]? expectedChecksum;

      try
      {
        expectedChecksum = File.ReadAllBytes(filename + ".sha");
      }
      catch (IOException)
      {
        expectedChecksum = null;
      }

      byte[] currentChecksum;
      var oldManifest = ProtoManifest.LoadFromFile(filename, out currentChecksum);

      if (oldManifest != null && (expectedChecksum == null || !expectedChecksum.SequenceEqual(currentChecksum)))
      {
        oldManifest = null;

        if (badHashWarning)
        {
          Console.WriteLine("Manifest {0} on disk did not match the expected checksum.", manifestId);
        }
      }

      if (oldManifest != null)
      {
        // TODO: This
        //return oldManifest.ConvertToSteamManifest(depotId);
      }
    }

    return null;
  }

  public static bool SaveManifestToFile(string directory, DepotManifest manifest)
  {
    try
    {
      var filename = Path.Combine(directory, string.Format("{0}_{1}.manifest", manifest.DepotID, manifest.ManifestGID));
      manifest.SaveToFile(filename);
      File.WriteAllBytes(filename + ".sha", FileSHAHash(filename));
      return true; // If serialization completes without throwing an exception, return true
    }
    catch (Exception)
    {
      return false; // Return false if an error occurs
    }
  }

  public static async Task InvokeAsync(IEnumerable<Func<Task>> taskFactories, int maxDegreeOfParallelism)
  {
    ArgumentNullException.ThrowIfNull(taskFactories);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDegreeOfParallelism, 0);

    var queue = taskFactories.ToArray();

    if (queue.Length == 0)
    {
      return;
    }

    var tasksInFlight = new List<Task>(maxDegreeOfParallelism);
    var index = 0;

    do
    {
      while (tasksInFlight.Count < maxDegreeOfParallelism && index < queue.Length)
      {
        var taskFactory = queue[index++];

        tasksInFlight.Add(taskFactory());
      }

      var completedTask = await Task.WhenAny(tasksInFlight).ConfigureAwait(false);

      await completedTask.ConfigureAwait(false);

      tasksInFlight.Remove(completedTask);
    } while (index < queue.Length || tasksInFlight.Count != 0);
  }

  // Validate a file against Steam3 Chunk data
  public static List<DepotManifest.ChunkData> ValidateSteam3FileChecksums(FileStream fs, DepotManifest.ChunkData[] chunkdata)
  {
    var neededChunks = new List<DepotManifest.ChunkData>();

    foreach (var data in chunkdata)
    {
      fs.Seek((long)data.Offset, SeekOrigin.Begin);

      var adler = AdlerHash(fs, (int)data.UncompressedLength);
      if (!adler.SequenceEqual(BitConverter.GetBytes(data.Checksum)))
      {
        neededChunks.Add(data);
      }
    }

    return neededChunks;
  }

  public static byte[] AdlerHash(Stream stream, int length)
  {
    uint a = 0, b = 0;
    for (var i = 0; i < length; i++)
    {
      var c = (uint)stream.ReadByte();

      a = (a + c) % 65521;
      b = (b + a) % 65521;
    }

    return BitConverter.GetBytes(a | (b << 16));
  }

  public static byte[] FileSHAHash(string filename)
  {
    using (var fs = File.Open(filename, FileMode.Open))
    using (var sha = SHA1.Create())
    {
      var output = sha.ComputeHash(fs);

      return output;
    }
  }

  public static void SetExecutable(string path, bool value)
  {
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
      return;
    }

    const UnixFileMode ModeExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    var mode = File.GetUnixFileMode(path);
    var hasExecuteMask = (mode & ModeExecute) == ModeExecute;
    if (hasExecuteMask != value)
    {
      File.SetUnixFileMode(path, value
          ? mode | ModeExecute
          : mode & ~ModeExecute);
    }
  }
}
