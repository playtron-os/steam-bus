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


namespace Steam.Content;

class ContentDownloaderException(string value) : Exception(value)
{
}

class ContentDownloader
{
  public const uint INVALID_APP_ID = uint.MaxValue;
  public const uint INVALID_DEPOT_ID = uint.MaxValue;
  public const ulong INVALID_MANIFEST_ID = ulong.MaxValue;
  private const string DEFAULT_DOWNLOAD_DIR = "depots";
  private const string CONFIG_DIR = ".DepotDownloader";
  private static readonly string STAGING_DIR = Path.Combine(CONFIG_DIR, "staging");

  private SteamSession session;
  private CDNClientPool? cdnPool;
  private AppDownloadOptions? options;

  private event Action<(string, double)>? OnInstallProgressed;

  private sealed class DepotDownloadInfo(
      uint depotid, uint appId, ulong manifestId, string branch,
      string installDir, byte[] depotKey)
  {
    public uint DepotId { get; } = depotid;
    public uint AppId { get; } = appId;
    public ulong ManifestId { get; } = manifestId;
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
    public required DepotManifest previousManifest;
    public required List<DepotManifest.FileData> filteredFiles;
    public required HashSet<string> allFileNames;
  }

  private class FileStreamData
  {
    public required FileStream fileStream;
    public required SemaphoreSlim fileLock;
    public int chunksToDownload;
  }


  private class GlobalDownloadCounter
  {
    public ulong completeDownloadSize;
    public ulong totalBytesCompressed;
    public ulong totalBytesUncompressed;
  }


  private class DepotDownloadCounter
  {
    public ulong completeDownloadSize;
    public ulong sizeDownloaded;
    public ulong depotBytesCompressed;
    public ulong depotBytesUncompressed;
  }


  public ContentDownloader(SteamSession steamSession)
  {
    this.session = steamSession;
  }


  public async Task DownloadAppAsync(uint appId, Action<(string, double)> onInstallProgressed)
  {
    var options = new AppDownloadOptions();
    await DownloadAppAsync(appId, options, onInstallProgressed);
  }


  public async Task DownloadAppAsync(uint appId, AppDownloadOptions options, Action<(string, double)>? onInstallProgressed)
  {
    // TODO: Handle download already in progress
    this.options = options;
    this.cdnPool = new CDNClientPool(this.session.SteamClient, appId);

    // Keep track of signal handlers
    this.OnInstallProgressed = onInstallProgressed;

    var depotManifestIds = options.DepotManifestIds;
    var branch = options.Branch;
    var os = options.Os;
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
        throw new ContentDownloaderException(string.Format("App {0} ({1}) is not available from this account.", appId, contentName));
      }
    }

    var hasSpecificDepots = depotManifestIds.Count > 0;
    var depotIdsFound = new List<uint>();
    var depotIdsExpected = depotManifestIds.Select(x => x.depotId).ToList();
    var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);

    // Handle user generated content
    if (isUgc)
    {
      var workshopDepot = depots["workshopdepot"].AsUnsignedInteger();
      if (workshopDepot != 0 && !depotIdsExpected.Contains(workshopDepot))
      {
        depotIdsExpected.Add(workshopDepot);
        depotManifestIds = depotManifestIds.Select(pair => (workshopDepot, pair.manifestId)).ToList();
      }

      depotIdsFound.AddRange(depotIdsExpected);
    }
    else
    {
      Console.WriteLine("Using app branch: '{0}'.", branch);

      if (depots != null)
      {
        foreach (var depotSection in depots.Children)
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
                if (Array.IndexOf(oslist, os ?? GetSteamOS()) == -1)
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

          depotIdsFound.Add(id);

          if (!hasSpecificDepots)
            depotManifestIds.Add((id, INVALID_MANIFEST_ID));
        }
      }

      if (depotManifestIds.Count == 0 && !hasSpecificDepots)
      {
        Console.WriteLine(string.Format("Couldn't find any depots to download for app {0}", appId));
        throw new ContentDownloaderException(string.Format("Couldn't find any depots to download for app {0}", appId));
      }

      if (depotIdsFound.Count < depotIdsExpected.Count)
      {
        var remainingDepotIds = depotIdsExpected.Except(depotIdsFound);
        Console.WriteLine(string.Format("Depot {0} not listed for app {1}", string.Join(", ", remainingDepotIds), appId));
        throw new ContentDownloaderException(string.Format("Depot {0} not listed for app {1}", string.Join(", ", remainingDepotIds), appId));
      }
    }

    var infos = new List<DepotDownloadInfo>();

    foreach (var (depotId, manifestId) in depotManifestIds)
    {
      var info = await GetDepotInfo(depotId, appId, manifestId, branch);
      if (info != null)
      {
        infos.Add(info);
      }
    }

    try
    {
      await DownloadSteam3Async(infos).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      Console.WriteLine("App {0} was not completely downloaded.", appId);
      throw;
    }
  }


  string GetAppName(uint appId)
  {
    var info = GetSteam3AppSection(appId, EAppInfoSection.Common);
    if (info == null)
      return string.Empty;

    return info["name"].AsString();
  }


  internal KeyValue GetSteam3AppSection(uint appId, EAppInfoSection section)
  {
    if (this.session.AppInfo == null)
    {
      return null;
    }

    if (!this.session.AppInfo.TryGetValue(appId, out var app) || app == null)
    {
      return null;
    }

    var appinfo = app.KeyValues;
    var section_key = section switch
    {
      EAppInfoSection.Common => "common",
      EAppInfoSection.Extended => "extended",
      EAppInfoSection.Config => "config",
      EAppInfoSection.Depots => "depots",
      _ => throw new NotImplementedException(),
    };
    var section_kv = appinfo.Children.Where(c => c.Name == section_key).FirstOrDefault();
    return section_kv;
  }


  async Task<bool> AccountHasAccess(uint depotId)
  {
    var steamUser = this.session.SteamClient.GetHandler<SteamUser>();
    if (steamUser == null || steamUser.SteamID == null || (this.session.Licenses == null && steamUser.SteamID.AccountType != EAccountType.AnonUser))
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
      licenseQuery = this.session.Licenses.Select(x => x.PackageID).Distinct();
    }

    await this.session.RequestPackageInfo(licenseQuery);

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


  async Task<DepotDownloadInfo?> GetDepotInfo(uint depotId, uint appId, ulong manifestId, string branch)
  {
    if (appId != INVALID_APP_ID)
    {
      await this.session.RequestAppInfo(appId);
    }

    if (!await AccountHasAccess(depotId))
    {
      Console.WriteLine("Depot {0} is not available from this account.", depotId);

      return null;
    }

    if (manifestId == INVALID_MANIFEST_ID)
    {
      manifestId = await GetSteam3DepotManifest(depotId, appId, branch);
      if (manifestId == INVALID_MANIFEST_ID && !string.Equals(branch, AppDownloadOptions.DEFAULT_BRANCH, StringComparison.OrdinalIgnoreCase))
      {
        Console.WriteLine("Warning: Depot {0} does not have branch named \"{1}\". Trying {2} branch.", depotId, branch, AppDownloadOptions.DEFAULT_BRANCH);
        branch = AppDownloadOptions.DEFAULT_BRANCH;
        manifestId = await GetSteam3DepotManifest(depotId, appId, branch);
      }

      if (manifestId == INVALID_MANIFEST_ID)
      {
        Console.WriteLine("Depot {0} missing public subsection or manifest section.", depotId);
        return null;
      }
    }

    await this.session.RequestDepotKey(depotId, appId);
    if (!this.session.DepotKeys.TryGetValue(depotId, out var depotKey))
    {
      Console.WriteLine("No valid depot key for {0}, unable to download.", depotId);
      return null;
    }

    var uVersion = GetSteam3AppBuildNumber(appId, branch);

    if (!CreateDirectories(depotId, uVersion, out var installDir))
    {
      Console.WriteLine("Error: Unable to create install directories!");
      return null;
    }

    return new DepotDownloadInfo(depotId, appId, manifestId, branch, installDir, depotKey);
  }


  async Task<ulong> GetSteam3DepotManifest(uint depotId, uint appId, string branch)
  {
    var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
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


  uint GetSteam3AppBuildNumber(uint appId, string branch)
  {
    if (appId == INVALID_APP_ID)
      return 0;


    var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
    var branches = depots["branches"];
    var node = branches[branch];

    if (node == KeyValue.Invalid)
      return 0;

    var buildid = node["buildid"];

    if (buildid == KeyValue.Invalid)
      return 0;

    return uint.Parse(buildid.Value);
  }


  private async Task DownloadSteam3Async(List<DepotDownloadInfo> depots)
  {
    if (this.options is null)
    {
      return;
    }
    //Ansi.Progress(Ansi.ProgressState.Indeterminate);

    var cts = new CancellationTokenSource();
    this.cdnPool.ExhaustedToken = cts;

    var downloadCounter = new GlobalDownloadCounter();
    var depotsToDownload = new List<DepotFilesData>(depots.Count);
    var allFileNamesAllDepots = new HashSet<string>();

    // First, fetch all the manifests for each depot (including previous manifests) and perform the initial setup
    foreach (var depot in depots)
    {
      var depotFileData = await ProcessDepotManifestAndFiles(cts, depot, downloadCounter);

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

    foreach (var depotFileData in depotsToDownload)
    {
      await DownloadSteam3AsyncDepotFiles(cts, downloadCounter, depotFileData, allFileNamesAllDepots);
    }

    //Ansi.Progress(Ansi.ProgressState.Hidden);

    Console.WriteLine("Total downloaded: {0} bytes ({1} bytes uncompressed) from {2} depots",
        downloadCounter.totalBytesCompressed, downloadCounter.totalBytesUncompressed, depots.Count);
  }


  private async Task<DepotFilesData> ProcessDepotManifestAndFiles(CancellationTokenSource cts, DepotDownloadInfo depot, GlobalDownloadCounter downloadCounter)
  {
    var depotCounter = new DepotDownloadCounter();

    Console.WriteLine("Processing depot {0}", depot.DepotId);

    DepotManifest? oldManifest = null;
    DepotManifest? newManifest = null;
    var configDir = Path.Combine(depot.InstallDir, CONFIG_DIR);

    var lastManifestId = INVALID_MANIFEST_ID;

    // TODO: This
    //DepotConfigStore.Instance.InstalledManifestIDs.TryGetValue(depot.DepotId, out lastManifestId);

    //// In case we have an early exit, this will force equiv of verifyall next run.
    //DepotConfigStore.Instance.InstalledManifestIDs[depot.DepotId] = INVALID_MANIFEST_ID;
    //DepotConfigStore.Save();

    if (lastManifestId != INVALID_MANIFEST_ID)
    {
      // We only have to show this warning if the old manifest ID was different
      var badHashWarning = (lastManifestId != depot.ManifestId);
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

          Server connection = null;

          try
          {
            connection = cdnPool.GetConnection(cts.Token);

            string cdnToken = null;
            if (this.session.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise))
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
            if (e.StatusCode == HttpStatusCode.Forbidden && !this.session.CDNAuthTokens.ContainsKey((depot.DepotId, connection.Host)))
            {
              await this.session.RequestCDNAuthToken(depot.AppId, depot.DepotId, connection);

              cdnPool.ReturnConnection(connection);

              continue;
            }

            cdnPool.ReturnBrokenConnection(connection);

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
            cdnPool.ReturnBrokenConnection(connection);
            Console.WriteLine("Encountered error downloading manifest for depot {0} {1}: {2}", depot.DepotId, depot.ManifestId, e.Message);
          }
        } while (newManifest == null);

        if (newManifest == null)
        {
          Console.WriteLine("\nUnable to download manifest {0} for depot {1}", depot.ManifestId, depot.DepotId);
          cts.Cancel();
        }

        // Throw the cancellation exception if requested so that this task is marked failed
        cts.Token.ThrowIfCancellationRequested();

        SaveManifestToFile(configDir, newManifest);
        Console.WriteLine(" Done!");
      }
    }

    Console.WriteLine("Manifest {0} ({1})", depot.ManifestId, newManifest.CreationTime);

    if (this.options.DownloadManifestOnly)
    {
      DumpManifestToTextFile(depot, newManifest);
      return null;
    }

    var stagingDir = Path.Combine(depot.InstallDir, STAGING_DIR);

    var filesAfterExclusions = newManifest.Files.AsParallel().Where(f => TestIsFileIncluded(f.FileName)).ToList();
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
        Directory.CreateDirectory(Path.GetDirectoryName(fileFinalPath));
        Directory.CreateDirectory(Path.GetDirectoryName(fileStagingPath));

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
      allFileNames = allFileNames
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
    installDir = null;
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
        Directory.CreateDirectory(baseInstallPath);

        installDir = baseInstallPath;

        Directory.CreateDirectory(Path.Combine(installDir, CONFIG_DIR));
        Directory.CreateDirectory(Path.Combine(installDir, STAGING_DIR));
      }
    }
    catch
    {
      return false;
    }

    return true;
  }

  private async Task DownloadSteam3AsyncDepotFiles(CancellationTokenSource cts,
      GlobalDownloadCounter downloadCounter, DepotFilesData depotFilesData, HashSet<string> allFileNamesAllDepots)
  {
    var depot = depotFilesData.depotDownloadInfo;
    var depotCounter = depotFilesData.depotCounter;

    Console.WriteLine("Downloading depot {0}", depot.DepotId);

    var files = depotFilesData.filteredFiles.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory)).ToArray();
    var networkChunkQueue = new ConcurrentQueue<(FileStreamData fileStreamData, DepotManifest.FileData fileData, DepotManifest.ChunkData chunk)>();

    await InvokeAsync(
        files.Select(file => new Func<Task>(async () =>
            await Task.Run(() => DownloadSteam3AsyncDepotFile(cts, downloadCounter, depotFilesData, file, networkChunkQueue)))),
        maxDegreeOfParallelism: this.options.MaxDownloads
    );

    await InvokeAsync(
        networkChunkQueue.Select(q => new Func<Task>(async () =>
            await Task.Run(() => DownloadSteam3AsyncDepotFileChunk(cts, downloadCounter, depotFilesData,
                q.fileData, q.fileStreamData, q.chunk)))),
        maxDegreeOfParallelism: this.options.MaxDownloads
    );

    // Check for deleted files if updating the depot.
    if (depotFilesData.previousManifest != null)
    {
      var previousFilteredFiles = depotFilesData.previousManifest.Files.AsParallel().Where(f => TestIsFileIncluded(f.FileName)).Select(f => f.FileName).ToHashSet();

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

    // TODO: This
    //DepotConfigStore.Instance.InstalledManifestIDs[depot.DepotId] = depot.ManifestId;
    //DepotConfigStore.Save();

    Console.WriteLine("Depot {0} - Downloaded {1} bytes ({2} bytes uncompressed)", depot.DepotId, depotCounter.depotBytesCompressed, depotCounter.depotBytesUncompressed);
  }

  private void DownloadSteam3AsyncDepotFile(
      CancellationTokenSource cts,
      GlobalDownloadCounter downloadCounter,
      DepotFilesData depotFilesData,
      DepotManifest.FileData file,
      ConcurrentQueue<(FileStreamData, DepotManifest.FileData, DepotManifest.ChunkData)> networkChunkQueue)
  {
    cts.Token.ThrowIfCancellationRequested();

    var depot = depotFilesData.depotDownloadInfo;
    var stagingDir = depotFilesData.stagingDir;
    var depotDownloadCounter = depotFilesData.depotCounter;
    var oldProtoManifest = depotFilesData.previousManifest;
    DepotManifest.FileData oldManifestFile = null;
    if (oldProtoManifest != null)
    {
      oldManifestFile = oldProtoManifest.Files.SingleOrDefault(f => f.FileName == file.FileName);
    }

    var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
    var fileStagingPath = Path.Combine(stagingDir, file.FileName);

    // This may still exist if the previous run exited before cleanup
    if (File.Exists(fileStagingPath))
    {
      File.Delete(fileStagingPath);
    }

    List<DepotManifest.ChunkData> neededChunks;
    var fi = new FileInfo(fileFinalPath);
    var fileDidExist = fi.Exists;
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
        throw new ContentDownloaderException(string.Format("Failed to allocate file {0}: {1}", fileFinalPath, ex.Message));
      }

      neededChunks = new List<DepotManifest.ChunkData>(file.Chunks);
    }
    else
    {
      // open existing
      if (oldManifestFile != null)
      {
        neededChunks = [];

        var hashMatches = oldManifestFile.FileHash.SequenceEqual(file.FileHash);
        if (this.options.VerifyAll || !hashMatches)
        {
          // we have a version of this file, but it doesn't fully match what we want
          if (this.options.VerifyAll)
          {
            Console.WriteLine("Validating {0}", fileFinalPath);
          }

          var matchingChunks = new List<ChunkMatch>();

          foreach (var chunk in file.Chunks)
          {
            var oldChunk = oldManifestFile.Chunks.FirstOrDefault(c => c.ChunkID.SequenceEqual(chunk.ChunkID));
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
                throw new ContentDownloaderException(string.Format("Failed to resize file to expected size {0}: {1}", fileFinalPath, ex.Message));
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
          }
        }
      }
      else
      {
        // No old manifest or file not in old manifest. We must validate.

        using var fs = File.Open(fileFinalPath, FileMode.Open);
        if ((ulong)fi.Length != file.TotalSize)
        {
          try
          {
            fs.SetLength((long)file.TotalSize);
          }
          catch (IOException ex)
          {
            throw new ContentDownloaderException(string.Format("Failed to allocate file {0}: {1}", fileFinalPath, ex.Message));
          }
        }

        Console.WriteLine("Validating {0}", fileFinalPath);
        neededChunks = ValidateSteam3FileChecksums(fs, [.. file.Chunks.OrderBy(x => x.Offset)]);
      }

      if (neededChunks.Count == 0)
      {
        lock (depotDownloadCounter)
        {
          depotDownloadCounter.sizeDownloaded += file.TotalSize;
          // TODO: emit progress signal
          this.OnInstallProgressed?.Invoke(("0", (depotDownloadCounter.sizeDownloaded / (float)depotDownloadCounter.completeDownloadSize) * 100.0f));
          Console.WriteLine("{0,6:#00.00}% {1}", (depotDownloadCounter.sizeDownloaded / (float)depotDownloadCounter.completeDownloadSize) * 100.0f, fileFinalPath);
        }

        lock (downloadCounter)
        {
          downloadCounter.completeDownloadSize -= file.TotalSize;
        }

        return;
      }

      var sizeOnDisk = (file.TotalSize - (ulong)neededChunks.Select(x => (long)x.UncompressedLength).Sum());
      lock (depotDownloadCounter)
      {
        depotDownloadCounter.sizeDownloaded += sizeOnDisk;
      }

      lock (downloadCounter)
      {
        downloadCounter.completeDownloadSize -= sizeOnDisk;
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
      networkChunkQueue.Enqueue((fileStreamData, file, chunk));
    }
  }

  private async Task DownloadSteam3AsyncDepotFileChunk(
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

    var chunkID = Convert.ToHexString(chunk.ChunkID).ToLowerInvariant();

    var written = 0;
    var chunkBuffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);

    try
    {
      do
      {
        cts.Token.ThrowIfCancellationRequested();

        Server connection = null;

        try
        {
          connection = cdnPool.GetConnection(cts.Token);

          string cdnToken = null;
          if (this.session.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise))
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
          if (e.StatusCode == HttpStatusCode.Forbidden &&
              (!this.session.CDNAuthTokens.TryGetValue((depot.DepotId, connection.Host), out var authTokenCallbackPromise) || !authTokenCallbackPromise.Task.IsCompleted))
          {
            await this.session.RequestCDNAuthToken(depot.AppId, depot.DepotId, connection);

            cdnPool.ReturnConnection(connection);

            continue;
          }

          cdnPool.ReturnBrokenConnection(connection);

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
          cdnPool.ReturnBrokenConnection(connection);
          Console.WriteLine("Encountered unexpected error downloading chunk {0}: {1}", chunkID, e.Message);
        }
      } while (written == 0);

      if (written == 0)
      {
        Console.WriteLine("Failed to find any server with chunk {0} for depot {1}. Aborting.", chunkID, depot.DepotId);
        cts.Cancel();
      }

      // Throw the cancellation exception if requested so that this task is marked failed
      cts.Token.ThrowIfCancellationRequested();

      try
      {
        await fileStreamData.fileLock.WaitAsync().ConfigureAwait(false);

        if (fileStreamData.fileStream == null)
        {
          var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
          fileStreamData.fileStream = File.Open(fileFinalPath, FileMode.Open);
        }

        fileStreamData.fileStream.Seek((long)chunk.Offset, SeekOrigin.Begin);
        await fileStreamData.fileStream.WriteAsync(chunkBuffer.AsMemory(0, written), cts.Token);
      }
      finally
      {
        fileStreamData.fileLock.Release();
      }
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
      downloadCounter.totalBytesCompressed += chunk.CompressedLength;
      downloadCounter.totalBytesUncompressed += chunk.UncompressedLength;

      //Ansi.Progress(downloadCounter.totalBytesUncompressed, downloadCounter.completeDownloadSize);
    }

    if (remainingChunks == 0)
    {
      var fileFinalPath = Path.Combine(depot.InstallDir, file.FileName);
      Console.WriteLine("{0,6:#00.00}% {1}", (sizeDownloaded / (float)depotDownloadCounter.completeDownloadSize) * 100.0f, fileFinalPath);
    }
  }

  class ChunkIdComparer : IEqualityComparer<byte[]>
  {
    public bool Equals(byte[] x, byte[] y)
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

    foreach (var file in manifest.Files)
    {
      foreach (var chunk in file.Chunks)
      {
        uniqueChunks.Add(chunk.ChunkID);
      }
    }

    sw.WriteLine($"Total number of files  : {manifest.Files.Count} ");
    sw.WriteLine($"Total number of chunks : {uniqueChunks.Count} ");
    sw.WriteLine($"Total bytes on disk    : {manifest.TotalUncompressedSize} ");
    sw.WriteLine($"Total bytes compressed : {manifest.TotalCompressedSize} ");
    sw.WriteLine();
    sw.WriteLine();
    sw.WriteLine("          Size Chunks File SHA                                 Flags Name");

    foreach (var file in manifest.Files)
    {
      var sha1Hash = Convert.ToHexString(file.FileHash).ToLower();
      sw.WriteLine($"{file.TotalSize,14:d} {file.Chunks.Count,6:d} {sha1Hash} {(int)file.Flags,5:x} {file.FileName}");
    }
  }

  bool TestIsFileIncluded(string filename)
  {
    if (!this.options.UsingFileList)
      return true;

    filename = filename.Replace('\\', '/');

    if (this.options.FilesToDownload.Contains(filename))
    {
      return true;
    }

    foreach (var rgx in this.options.FilesToDownloadRegex)
    {
      var m = rgx.Match(filename);

      if (m.Success)
        return true;
    }

    return false;
  }

  public static DepotManifest LoadManifestFromFile(string directory, uint depotId, ulong manifestId, bool badHashWarning)
  {
    // Try loading Steam format manifest first.
    var filename = Path.Combine(directory, string.Format("{0}_{1}.manifest", depotId, manifestId));

    if (File.Exists(filename))
    {
      byte[] expectedChecksum;

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
      byte[] expectedChecksum;

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
