using System.Text.Json;
using Tmds.DBus;
using SteamKit2;
using SteamKit2.Authentication;
using Playtron.Plugin;
using Steam.Content;
using Steam.Session;
using SteamBus.Auth;
using System.Security.Cryptography;
using System.Text;
using Xdg.Directories;
using Steam.Config;
using Steam.Cloud;

namespace SteamBus.DBus;

using InstallOptionDescription = (string, string, string[]);

[Dictionary]
public class SteamClientProperties : IEnumerable<KeyValuePair<string, object>>
{
  public bool NetworkingEnabled;
  public bool WirelessEnabled;

  System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
  {
    return this.GetEnumerator();
  }

  public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
  {
    yield return new KeyValuePair<string, object>(nameof(NetworkingEnabled), NetworkingEnabled);
    yield return new KeyValuePair<string, object>(nameof(WirelessEnabled), WirelessEnabled);
  }
}

[DBusInterface("one.playtron.SteamBus.SteamClient")]
public interface IDBusSteamClient : IDBusObject
{
  //Task<int> LoginAsync(string username, string password);
  Task<SteamClientProperties> GetAllAsync();

  Task<object> GetAsync(string prop);
  Task SetAsync(string prop, object val);
  //Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);

  // Signals functions must be prefixed with 'Watch'
  // Test signal
  Task<IDisposable> WatchPongAsync(Action<string> reply);
  Task<IDisposable> WatchConnectedAsync(Action<ObjectPath> reply);
  //Task<IDisposable> WatchLoggedInAsync(Action<string> reply);
  //Task<IDisposable> WatchLoggedOutAsync(Action<string> reply);
}

class DBusSteamClient : IDBusSteamClient, IPlaytronPlugin, IAuthPasswordFlow, IAuthCryptography, IUser, IAuthTwoFactorFlow, IAuthQrFlow, IPluginLibraryProvider, ICloudSaveProvider, IAuthenticator, IPluginDependencies
{
  // Path to the object on DBus (e.g. "/one/playtron/SteamBus/SteamClient0")
  public ObjectPath Path;
  // Depot Config Store used to retrieve installed apps informations
  private DepotConfigStore depotConfigStore;
  // Unique login ID used to allow multiple active login sessions from the same account
  private uint? loginId;
  // Two factor code task used to login to Steam
  private TaskCompletionSource<string>? tfaCodeTask;
  private bool needsDeviceConfirmation = false;
  // Steam session instance
  private SteamSession? session = null;

  private string authFile = "auth.json";
  private string cacheDir = "cache";

  private PlaytronPluginProperties pluginInfo = new PlaytronPluginProperties();

  // Create an RSA keypair for secure secret sending
  private bool useEncryption = true;
  private RSA rsa = RSA.Create(2048);

  // Signal events
  public event Action<string>? OnPing;
  public event Action<ObjectPath>? OnClientConnected;
  public event Action<InstallStartedDescription>? OnInstallStarted;
  public event Action<InstallProgressedDescription>? OnInstallProgressed;
  public event Action<string>? OnInstallCompleted;
  public event Action<(string appId, string error)>? OnInstallFailed;
  public event Action<(string appId, string version)>? OnAppNewVersionFound;
  public event Action<(string appId, double progress)>? OnMoveItemProgressed;
  public event Action<(string appId, string installFolder)>? OnMoveItemCompleted;
  public event Action<(string appId, string error)>? OnMoveItemFailed;
  public event Action? InstalledAppsUpdated;
  public event Action<PropertyChanges>? OnUserPropsChanged;
  public event Action<string>? OnAuthError;
  public event Action<(bool previousCodeWasIncorrect, string message)>? OnTwoFactorRequired;
  public event Action<(string email, bool previousCodeWasIncorrect, string message)>? OnEmailTwoFactorRequired;
  public event Action<string>? OnConfirmationRequired;
  public event Action<string>? OnQrCodeUpdated;
  public event Action<ProviderItem[]>? OnLibraryUpdated;
  public event Action<CloudSyncProgress>? OnCloudSaveSyncProgressed;
  public event Action<CloudSyncFailure>? OnCloudSyncFailed;

  private SteamClientApp steamClientApp;
  public event Action<InstallStartedDescription>? OnDependencyInstallStarted;
  public event Action<InstallProgressedDescription>? OnDependencyInstallProgressed;
  public event Action<string>? OnDependencyInstallCompleted;
  public event Action<(string appId, string error)>? OnDependencyInstallFailed;
  public event Action<(string appId, string version)>? OnDependencyAppNewVersionFound;
  public event Action<string>? OnLaunchReady;
  public event Action<(string appId, string error)>? OnLaunchError;


  private DepotConfigStore dependenciesStore;

  private bool isOnline = false;

  public static TaskCompletionSource? fetchingSteamClientData;
  public static TaskCompletionSource? steamClientWaiting;


  // Creates a new DBusSteamClient instance with the given DBus path
  public DBusSteamClient(ObjectPath path, DepotConfigStore depotConfigStore, DepotConfigStore dependenciesStore, DisplayManager displayManager, INetworkManager networkManager)
  {
    steamClientApp = new SteamClientApp(displayManager, depotConfigStore, dependenciesStore);

    // DBus path to this Steam Client instance
    this.Path = path;

    // Depot configure store
    this.depotConfigStore = depotConfigStore;
    this.dependenciesStore = dependenciesStore;

    // Create a steam client config
    var config = SteamConfiguration.Create(builder =>
    {
      builder.WithConnectionTimeout(TimeSpan.FromSeconds(30));
      builder.WithDirectoryFetch(true);
    });

    // Create a unique random login ID for this client. This is required to
    // allow multiple active login sessions from the same public IP address.
    // https://github.com/SteamRE/SteamKit/pull/217
    Random random = new Random();
    this.loginId = (uint?)random.Next();

    // Security
    if (!this.useEncryption)
    {
      Console.WriteLine("WARNING: Encryption not being used for secure communication");
    }

    // Determine the file paths based on XDG environment
    this.authFile = $"{BaseDirectory.DataHome}/steambus/auth.json";

    // Ensure that the auth file exists
    if (!File.Exists(authFile))
    {
      Console.WriteLine("Auth file does not exist at '{0}'. Creating it.", authFile);
      Directory.CreateDirectory($"{BaseDirectory.DataHome}/steambus");
      var authSessions = new Dictionary<string, SteamAuthSession>();
      using StreamWriter writer = new StreamWriter(authFile, false);
      string? authSessionsSerialized = JsonSerializer.Serialize(authSessions);
      writer.Write(authSessionsSerialized);
    }

    // Ensure that the cache directory exists
    this.cacheDir = $"{BaseDirectory.CacheHome}/steambus";
    if (!Directory.Exists(cacheDir))
    {
      Console.WriteLine($"Cache directory does not exist at '{cacheDir}'. Creating it.");
      Directory.CreateDirectory(this.cacheDir);
    }

    networkManager.WatchPropertiesAsync((changes) =>
    {
      try
      {
        foreach (var (key, value) in changes.Changed)
        {
          if (key == "Connectivity" && value != null)
          {
            uint valueUint = (uint)value;
            NmConnectivityStatus connectivity = (NmConnectivityStatus)valueUint;
            var wasOnline = isOnline;
            isOnline = connectivity == NmConnectivityStatus.Full;

            if (wasOnline != isOnline)
            {
              Console.WriteLine($"Network online status changed: {isOnline}");

              if (isOnline)
                _ = Task.Run(OnOnline);
            }
          }
        }
      }
      catch (Exception exception)
      {
        Console.Error.WriteLine($"Error occurred when listening to network manager property changes, err:{exception}");
      }
    });

    _ = Task.Run(async () =>
    {
      try
      {
        var connectivity = (NmConnectivityStatus)await networkManager.GetAsync<uint>("Connectivity");
        isOnline = connectivity == NmConnectivityStatus.Full;
        Console.WriteLine($"Initial network online status: {isOnline}");
      }
      catch (Exception exception)
      {
        Console.Error.WriteLine($"Error occurred when getting initial network online status, err:{exception}");
      }
    });
  }

  private async Task OnOnline()
  {
    if (this.session == null) return;

    if (this.session.IsPendingLogin)
    {
      Console.WriteLine("Previous session exists, trying to re-login to steam");

      await this.session.Login();
      if (this.session.IsLoggedOn)
      {
        OnUserPropsChanged?.Invoke(new PropertyChanges([], ["Avatar", "Username", "Identifier", "Status"]));
      }
    }
    else
      await LaunchSteamClientToSyncTokens(session.GetLogonDetails());
  }

  // Decrypt the given base64 encoded string using our private key
  private string Decrypt(string base64EncodedString)
  {
    byte[] base64EncodedBytes = Convert.FromBase64String(base64EncodedString);
    byte[] decrypted = this.rsa.Decrypt(base64EncodedBytes, RSAEncryptionPadding.OaepSHA256);
    return Encoding.UTF8.GetString(decrypted);
  }

  Task<PlaytronPluginProperties> IPlaytronPlugin.GetAllAsync()
  {
    return Task.FromResult(pluginInfo);
  }

  Task<object> IPlaytronPlugin.GetAsync(string prop)
  {
    switch (prop)
    {
      case "Id":
        return Task.FromResult((object)pluginInfo.Id);
      case "Version":
        return Task.FromResult((object)pluginInfo.Version);
      case "Name":
        return Task.FromResult((object)pluginInfo.Name);
      case "MinimumApiVersion":
        return Task.FromResult((object)pluginInfo.MinimumApiVersion);
      default:
        throw new NotSupportedException();
    }
  }

  // --- PluginDependencies Implementation

  // Gets a list of all installed dependencies
  Task<InstalledAppDescription[]> IPluginDependencies.GetInstalledDependenciesAsync()
  {
    return Task.FromResult(dependenciesStore.GetInstalledAppInfo());
  }

  // Gets info about a single dependency item
  Task<ProviderItem> IPluginDependencies.GetDependencyItemAsync(string appIdString)
  {
    if (ParseAppId(appIdString) is not uint appId || appId != SteamClientApp.STEAM_CLIENT_APP_ID) throw DbusExceptionHelper.ThrowInvalidAppId();

    return Task.FromResult(new ProviderItem
    {
      id = SteamClientApp.STEAM_CLIENT_APP_ID.ToString(),
      app_type = (uint)AppType.Tool,
      name = "Steam",
      provider = "Steam",
    });
  }

  // Gets a list of the dependencies required to run this plugin which need to be installed
  Task<ProviderItem[]> IPluginDependencies.GetRequiredDependenciesAsync()
  {
    return Task.FromResult<ProviderItem[]>([
      new ProviderItem
      {
        id = SteamClientApp.STEAM_CLIENT_APP_ID.ToString(),
        app_type = (uint)AppType.Tool,
        name = "Steam",
        provider = "Steam",
      }
    ]);
  }

  // Starts installation of all the required dependencies
  Task IPluginDependencies.InstallAllRequiredDependenciesAsync()
  {
    steamClientApp.OnUpdateStarted();

    _ = Task.Run(async () =>
    {
      Console.WriteLine("Triggering steam client update");

      try
      {
        try
        {
          await steamClientApp.Start(0, "", "", false);

          if (!steamClientApp.updating)
          {
            Console.WriteLine("No update is needed for steam client");
            OnDependencyInstallCompleted?.Invoke(SteamClientApp.STEAM_CLIENT_APP_ID.ToString());
            steamClientApp.RunSteamShutdown();
            return;
          }
        }
        catch (DBusException exception)
        {
          if (exception.ErrorName != DbusErrors.DependencyUpdateRequired)
            throw;
        }

        if (steamClientApp.updateEndedTask != null) await steamClientApp.updateEndedTask.Task;

        Console.WriteLine("Steam client update finished successfully");
      }
      catch (Exception exception)
      {
        Console.Error.WriteLine($"Error occurred when performing steam client update, err:{exception}");
      }
      finally
      {
        steamClientApp.RunSteamShutdown();
      }
    });

    return Task.CompletedTask;
  }

  Task<IDisposable> IPluginDependencies.WatchInstallStartedAsync(Action<InstallStartedDescription> reply)
  {
    var res = SignalWatcher.AddAsync(this, nameof(OnDependencyInstallStarted), reply);
    steamClientApp.OnDependencyInstallStarted = OnDependencyInstallStarted;
    return res;
  }

  Task<IDisposable> IPluginDependencies.WatchInstallProgressedAsync(Action<InstallProgressedDescription> reply)
  {
    var res = SignalWatcher.AddAsync(this, nameof(OnDependencyInstallProgressed), reply);
    steamClientApp.OnDependencyInstallProgressed = OnDependencyInstallProgressed;
    return res;
  }

  Task<IDisposable> IPluginDependencies.WatchInstallCompletedAsync(Action<string> reply)
  {
    var res = SignalWatcher.AddAsync(this, nameof(OnDependencyInstallCompleted), reply);
    steamClientApp.OnDependencyInstallCompleted = OnDependencyInstallCompleted;
    return res;
  }

  Task<IDisposable> IPluginDependencies.WatchInstallFailedAsync(Action<(string appId, string error)> reply)
  {
    var res = SignalWatcher.AddAsync(this, nameof(OnDependencyInstallFailed), reply);
    steamClientApp.OnDependencyInstallFailed = OnDependencyInstallFailed;
    return res;
  }

  Task<IDisposable> IPluginDependencies.WatchDependencyNewVersionFoundAsync(Action<(string appId, string version)> reply)
  {
    var res = SignalWatcher.AddAsync(this, nameof(OnDependencyAppNewVersionFound), reply);
    steamClientApp.OnDependencyAppNewVersionFound = OnDependencyAppNewVersionFound;
    return res;
  }

  // --- LibraryProvider Implementation

  Task<LibraryProviderProperties> IPluginLibraryProvider.GetAllAsync()
  {
    return Task.FromResult(new LibraryProviderProperties());
  }

  Task<object> IPluginLibraryProvider.GetAsync(string prop)
  {
    var props = new LibraryProviderProperties();
    switch (prop)
    {
      case "Name":
        return Task.FromResult((object)props.Name);
      case "Provider":
        return Task.FromResult((object)props.Provider);
      default:
        throw new NotSupportedException();
    }
  }

  bool EnsureConnected()
  {
    // Ensure that a Steam session exists
    if (this.session is null)
    {
      Console.WriteLine("No active Steam session found");
      return false;
    }
    if (!this.session.IsLoggedOn)
    {
      Console.WriteLine("Not logged in to Steam");
      return false;
    }
    if (this.session.SteamUser?.SteamID == null)
    {
      Console.WriteLine("Steam ID not found in steam connection");
      return false;
    }

    return true;
  }

  uint? ParseAppId(string appIdString)
  {
    // Convert the app id to a numerical id
    try
    {
      return uint.Parse(appIdString);
    }
    catch (Exception exception)
    {
      Console.WriteLine($"Invalid app id '{appIdString}': {exception}");
      return null;
    }
  }


  async Task<ProviderItem> IPluginLibraryProvider.GetProviderItemAsync(string appIdString)
  {
    if (ParseAppId(appIdString) is not uint appId) throw DbusExceptionHelper.ThrowInvalidAppId();

    try
    {
      if ((session == null || !session.IsPendingLogin) && !EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();
      await this.session!.WaitForLibrary();
      if (!this.session.ProviderItemMap.TryGetValue(appId, out var providerItem)) throw DbusExceptionHelper.ThrowInvalidAppId();
      return providerItem;
    }
    catch (Exception)
    {
      var providerItem = await SteamSession.GetProviderItemRequest(appId);
      if (providerItem != null) return (ProviderItem)providerItem;

      throw;
    }
  }

  async Task<ProviderItem[]> IPluginLibraryProvider.GetProviderItemsAsync()
  {
    if ((session == null || !session.IsPendingLogin) && !EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();
    await this.session!.WaitForLibrary();
    return this.session.GetProviderItems().ToArray();
  }

  Task IPluginLibraryProvider.RefreshAsync()
  {
    Console.WriteLine("Refresh called, this is ignored...");
    return Task.FromResult(0);
  }

  async Task<ItemMetadata> IPluginLibraryProvider.GetAppMetadataAsync(string appIdString)
  {
    if (!EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();
    if (ParseAppId(appIdString) is not uint appId) throw DbusExceptionHelper.ThrowInvalidAppId();

    await session!.RequestAppInfo(appId, true);

    var info = depotConfigStore.GetInstalledAppInfo(appId);
    var name = session!.GetSteam3AppName(appId);
    var requiresInternetConnection = session!.GetSteam3AppRequiresInternetConnection(appId);

    if (info == null)
    {
      return new ItemMetadata
      {
        Name = name,
        InstallSize = 0,
        RequiresInternetConnection = requiresInternetConnection,
        CloudSaveFolders = [],
        InstalledVersion = "",
        LatestVersion = "",
      };
    }

    var latestVersion = session!.GetSteam3AppBuildNumber(appId, info!.Value.Branch);

    return new ItemMetadata
    {
      Name = name,
      InstallSize = info.Value.Info.DownloadedBytes,
      RequiresInternetConnection = requiresInternetConnection,
      // TODO: Implement cloud save folders?
      CloudSaveFolders = [],
      InstalledVersion = info.Value.Info.Version,
      LatestVersion = latestVersion.ToString(),
    };
  }

  async Task<LaunchOption[]> IPluginLibraryProvider.GetLaunchOptionsAsync(string appIdString)
  {
    if (ParseAppId(appIdString) is not uint appId) throw DbusExceptionHelper.ThrowInvalidAppId();
    if ((session == null || !session.IsPendingLogin) && !EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();
    if (fetchingSteamClientData != null) await fetchingSteamClientData.Task;

    if (!session!.IsPendingLogin)
      await session!.RequestAppInfo(appId, false);

    var info = session!.GetSteam3AppSection(appId, EAppInfoSection.Config) ?? throw DbusExceptionHelper.ThrowInvalidAppId();
    var installedInfo = depotConfigStore.GetInstalledAppInfo(appId);
    List<LaunchOption> options = [];

    foreach (var entry in info["launch"].Children)
    {
      if (installedInfo is not null)
      {
        if (entry["config"]?["betakey"]?.Value != null && installedInfo.Value.Branch != entry["config"]["betakey"].Value)
        {
          continue;
        }
        if (entry["config"]?["oslist"]?.Value != null && entry["config"]["oslist"].Value?.IndexOf(installedInfo.Value.Info.Os) == -1)
        {
          continue;
        }
      }

      List<string> HardwareTags = [];
      if (entry["config"]?["steamdeck"]?.Value == "1")
      {
        HardwareTags.Add("steamdeck");
      }

      LaunchOption option = new()
      {
        Description = entry["description"]?.Value ?? "",
        // TODO: consider using description_loc to use localized values of description.
        Executable = entry["executable"]?.Value ?? "",
        Arguments = entry["arguments"]?.Value ?? "",
        Environment = [("SteamAppId", appIdString), ("STEAM_COMPAT_APP_ID", appIdString), ("SteamGameId", appIdString)],
        WorkingDirectory = entry["workingdir"]?.Value ?? "",
        LaunchType = (uint)LaunchType.Unknown,
        HardwareTags = HardwareTags.ToArray()
      };
      // Perform normalization
      option.Executable = option.Executable.Replace('\\', System.IO.Path.DirectorySeparatorChar);
      option.WorkingDirectory = option.WorkingDirectory.Replace('\\', System.IO.Path.DirectorySeparatorChar);
      if (installedInfo != null)
      {
        if (!option.Executable.Contains("://"))
          option.Executable = System.IO.Path.GetFullPath(System.IO.Path.Join(installedInfo.Value.Info.InstalledPath, option.Executable));

        option.WorkingDirectory = System.IO.Path.GetFullPath(System.IO.Path.Join(installedInfo.Value.Info.InstalledPath, option.WorkingDirectory));
      }
      options.Add(option);
    }
    return [.. options];
  }

  async Task<InstallOptionDescription[]> IPluginLibraryProvider.GetInstallOptionsAsync(string appIdString)
  {
    if (!EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();
    if (ParseAppId(appIdString) is not uint appId) throw DbusExceptionHelper.ThrowInvalidAppId();

    var downloader = new ContentDownloader(session!, depotConfigStore);
    var options = await downloader.GetInstallOptions(appId);

    InstallOptionDescription[] res = options.Select((option) => option.AsTuple()).ToArray();

    return res;
  }

  private Dictionary<string, string> MapPostInstallRegistryValueToDict(PostInstallRegistryValue val)
  {
    var dict = new Dictionary<string, string>();
    if (val.Language != null) dict.Add("language", val.Language);
    dict.Add("group", val.Group);
    dict.Add("key", val.Key);
    dict.Add("value", val.Value);
    return dict;
  }

  private Dictionary<string, string> MapPostInstallRunProcessValueToDict(PostInstallRunProcess val)
  {
    var dict = new Dictionary<string, string>();
    if (val.HasRunKey != null) dict.Add("has_run_key", val.HasRunKey);
    if (val.Command != null) dict.Add("command", val.Command);
    if (val.MinimumHasRunValue != null) dict.Add("minimum_has_run_value", val.MinimumHasRunValue);
    if (val.RequirementOs.Is64BitWindows != null) dict.Add("is_64_bit_windows", val.RequirementOs.Is64BitWindows == true ? "true" : "false");
    if (val.RequirementOs.OsType != null) dict.Add("os_type", val.RequirementOs.OsType);
    dict.Add("name", val.Name);
    dict.Add("process", val.Process);
    dict.Add("no_clean_up", val.NoCleanUp ? "true" : "false");
    return dict;
  }

  async Task<string> IPluginLibraryProvider.GetPostInstallStepsAsync(string appIdString)
  {
    if (ParseAppId(appIdString) is not uint appId) throw DbusExceptionHelper.ThrowInvalidAppId();

    var installDirectory = depotConfigStore.GetInstallDirectory(appId);

    if (installDirectory == null || !depotConfigStore.IsAppDownloaded(appId))
      throw DbusExceptionHelper.ThrowAppNotInstalled();

    var installScript = await InstallScript.CreateAsync(depotConfigStore, appId, installDirectory!);

    var options = new JsonSerializerOptions
    {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    return JsonSerializer.Serialize(installScript.scripts, options);
  }

  async Task<InstalledAppDescription[]> IPluginLibraryProvider.GetInstalledAppsAsync()
  {
    if (this.session != null) await this.session.WaitForLibrary();
    return depotConfigStore.GetInstalledAppInfo();
  }

  async Task IPluginLibraryProvider.SyncInstalledAppsAsync()
  {
    var installedAppIds = depotConfigStore.GetInstalledAppInfo().Select((info) => info.AppId).ToHashSet();
    var importedAppIds = new List<string>();

    var libraryFoldersConfig = await LibraryFoldersConfig.CreateAsync();
    var directories = libraryFoldersConfig.GetInstallDirectories();
    var hadImport = false;

    foreach (var dir in directories)
    {
      var steamappsDir = Directory.GetParent(dir)?.FullName;
      if (steamappsDir == null || !Directory.Exists(steamappsDir)) continue;

      var files = Directory.EnumerateFiles(steamappsDir);

      foreach (var file in files)
      {
        if (!file.EndsWith(".acf") || file.EndsWith(".extra.acf"))
          continue;

        var appId = file.Split("_").LastOrDefault()?.Split(".").FirstOrDefault();
        if (appId == null || installedAppIds.Contains(appId))
          continue;

        if (await depotConfigStore.ImportApp(file))
        {
          installedAppIds.Add(appId);
          importedAppIds.Add(appId);
          hadImport = true;
        }
      }
    }

    if (hadImport)
    {
      var globalConfig = new GlobalConfig(GlobalConfig.DefaultPath());
      var userCompatConfig = session == null ? null : new UserCompatConfig(UserCompatConfig.DefaultPath(session.GetLogonDetails().AccountID));
      foreach (var appId in importedAppIds)
        depotConfigStore.VerifyAppsOsConfig(globalConfig, userCompatConfig, uint.Parse(appId));
      globalConfig.Save();
      userCompatConfig?.Save();

      InstalledAppsUpdated?.Invoke();
    }
  }

  async Task<int> IPluginLibraryProvider.InstallAsync(string appIdString, string disk, InstallOptions options)
  {
    Console.WriteLine($"Installing app: {appIdString}");
    if (!EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();
    if (ParseAppId(appIdString) is not uint appId) throw DbusExceptionHelper.ThrowInvalidAppId();
    if (fetchingSteamClientData != null) await fetchingSteamClientData.Task;

    // Create a content downloader for the given app
    var downloader = new ContentDownloader(session!, depotConfigStore);

    // Configure the download options
    var installdir = await downloader.GetAppInstallDir(appId);
    var installFolder = await Disk.GetInstallRootFromDevice(disk, installdir);
    var downloadOptions = new AppDownloadOptions(options, installFolder);

    // Start downloading the app
    try
    {
      // Run this in the background
      _ = Task.Run(() => downloader.DownloadAppAsync(appId, downloadOptions, OnInstallStarted, OnInstallProgressed, OnInstallCompleted, OnInstallFailed));
    }
    catch (Exception exception)
    {
      Console.WriteLine($"Failed to start app download for '{appId}': {exception.ToString()}");
      return 1;
    }

    return 0;
  }

  Task IPluginLibraryProvider.UninstallAsync(string appIdString)
  {
    Console.WriteLine($"Uninstalling app: {appIdString}");
    if (ParseAppId(appIdString) is not uint appId) throw DbusExceptionHelper.ThrowInvalidAppId();
    depotConfigStore.RemoveInstalledApp(appId);
    return Task.CompletedTask;
  }

  async Task IPluginLibraryProvider.MoveItemAsync(string appIdString, string disk)
  {
    Console.WriteLine($"Moving app: {appIdString}");
    if (ParseAppId(appIdString) is not uint appId) throw DbusExceptionHelper.ThrowInvalidAppId();

    var downloader = new ContentDownloader(session!, depotConfigStore);
    var installdir = await downloader.GetAppInstallDir(appId);
    var newInstallDirectory = await Disk.GetInstallRootFromDevice(disk, installdir);

    _ = Task.Run(() => depotConfigStore.MoveInstalledApp(appId, newInstallDirectory, OnMoveItemProgressed, OnMoveItemCompleted, OnMoveItemFailed));
  }

  async Task<EulaEntry[]> IPluginLibraryProvider.GetEulasAsync(string appIdString, string country, string locale)
  {
    if (ParseAppId(appIdString) is not uint appId) throw DbusExceptionHelper.ThrowInvalidAppId();
    if ((session == null || !session.IsPendingLogin) && !EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();

    await this.session!.WaitForLibrary();
    await this.session!.RequestAppInfo(appId);

    var common = session!.GetSteam3AppSection(appId, EAppInfoSection.Common) ?? throw DbusExceptionHelper.ThrowInvalidAppId();
    var eulas = common["eulas"];
    if (eulas == KeyValue.Invalid)
      return [];

    var result = new List<EulaEntry>();

    foreach (var child in eulas.Children)
    {
      var allowedCountries = child["allowed_countries"];
      if (allowedCountries == KeyValue.Invalid)
        allowedCountries = child["countries"];

      if (country != string.Empty && allowedCountries != KeyValue.Invalid)
      {
        var countriesStr = allowedCountries.AsString();

        if (!string.IsNullOrEmpty(countriesStr))
        {
          var delimiter = countriesStr.Contains(",") == true ? "," : " ";
          var countries = countriesStr.Split(delimiter) ?? [];
          if (!countries.Contains(country))
            continue;
        }
      }

      var id = child["id"].AsString();
      var url = child["url"].AsString();
      if (id == null || url == null)
        continue;

      result.Add(new EulaEntry
      {
        Id = id,
        Name = child["name"].AsString() ?? "",
        Version = child["version"].AsInteger(),
        Url = url,
        Body = "",
        Country = allowedCountries?.AsString() ?? "",
        Language = "",
      });
    }

    return result.ToArray();
  }

  async Task IPluginLibraryProvider.PauseInstallAsync()
  {
    Console.WriteLine("Pausing current install");
    await ContentDownloader.PauseInstall();
  }

  async Task<string[]> IPluginLibraryProvider.PreLaunchHookAsync(string appIdString, bool wantsOfflineMode)
  {
    if (ParseAppId(appIdString) is not uint appId) throw DbusExceptionHelper.ThrowInvalidAppId();
    if ((session == null || !session.IsPendingLogin) && !EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();
    if (steamClientApp.updating)
      return [SteamClientApp.STEAM_CLIENT_APP_ID.ToString()];

    var installedApp = depotConfigStore.GetInstalledAppOptions(appId);
    if (installedApp == null) throw DbusExceptionHelper.ThrowAppNotInstalled();

    if (fetchingSteamClientData != null) await fetchingSteamClientData.Task;

    if (!wantsOfflineMode && !isOnline)
      wantsOfflineMode = true;

    // Enforce apps being fully updated
    if (!wantsOfflineMode)
    {
      await session!.VerifyDownloadedApp(new ContentDownloader(session, depotConfigStore), installedApp);

      var installedAppInfo = depotConfigStore.GetInstalledAppInfo(appId);
      if (installedAppInfo?.Info.UpdatePending == true)
      {
        var version = session.GetSteam3AppBuildNumber(appId, installedAppInfo?.Branch ?? AppDownloadOptions.DEFAULT_BRANCH);
        OnAppNewVersionFound?.Invoke((appIdString, version.ToString()));
        throw DbusExceptionHelper.ThrowAppUpdateRequired();
      }
    }

    session!.UpdateConfigFiles(wantsOfflineMode);

    try
    {
      if (!wantsOfflineMode && session.playingBlocked) throw DbusExceptionHelper.ThrowPlayingBlocked();

      // Return early in case steam client is already running and ready
      if (steamClientWaiting != null)
      {
        if (steamClientApp.readyTask != null) await steamClientApp.readyTask.Task;
        steamClientWaiting?.TrySetResult();
        steamClientWaiting = null;
        steamClientApp.forAppId = appIdString;
        OnLaunchReady?.Invoke(appIdString);
        return [];
      }

      var logonDetails = session!.GetLogonDetails();
      await steamClientApp.Start(logonDetails.AccountID, appIdString, logonDetails.Username!, wantsOfflineMode);
    }
    catch (DBusException exception)
    {
      if (exception.ErrorName == DbusErrors.DependencyUpdateRequired)
        return [SteamClientApp.STEAM_CLIENT_APP_ID.ToString()];

      throw;
    }

    return [];
  }

  Task IPluginLibraryProvider.PostLaunchHookAsync(string appId)
  {
    Console.WriteLine($"Running post launch hook for appId:{appId}");
    _ = Task.Run(async () =>
    {
      if (ParseAppId(appId) is uint appIdVal)
      {
        // Wait for Steam to pick up game closing
        await Task.Delay(500);
        // Check if the sync operation has been started
        await steamClientApp.WaitForSteamCloud(session.SteamUser.SteamID.AccountID, appIdVal, TimeSpan.FromSeconds(15));
      }
      // Send shutdown request
      await steamClientApp.ShutdownSteamWithTimeoutAsync(TimeSpan.FromSeconds(8));
    }
    );
    return Task.CompletedTask;
  }

  // InstallStart Signal
  Task<IDisposable> IPluginLibraryProvider.WatchInstallStartedAsync(Action<InstallStartedDescription> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnInstallStarted), reply);
  }

  // InstallProgressed Signal
  Task<IDisposable> IPluginLibraryProvider.WatchInstallProgressedAsync(Action<InstallProgressedDescription> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnInstallProgressed), reply);
  }

  Task<IDisposable> IPluginLibraryProvider.WatchLibraryUpdatedAsync(Action<ProviderItem[]> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnLibraryUpdated), reply);
  }

  // InstallCompleted Signal
  Task<IDisposable> IPluginLibraryProvider.WatchInstallCompletedAsync(Action<string> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnInstallCompleted), reply);
  }

  // InstallFailed Signal
  Task<IDisposable> IPluginLibraryProvider.WatchInstallFailedAsync(Action<(string appId, string error)> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnInstallFailed), reply);
  }

  // AppNewVersionFound Signal
  Task<IDisposable> IPluginLibraryProvider.WatchAppNewVersionFoundAsync(Action<(string appId, string version)> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnAppNewVersionFound), reply);
  }

  // MoveItemProgressed Signal
  Task<IDisposable> IPluginLibraryProvider.WatchMoveItemProgressedAsync(Action<(string appId, double progress)> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnMoveItemProgressed), reply);
  }

  // MoveItemCompleted Signal
  Task<IDisposable> IPluginLibraryProvider.WatchMoveItemCompletedAsync(Action<(string appId, string installFolder)> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnMoveItemCompleted), reply);
  }

  // MoveItemFailed Signal
  Task<IDisposable> IPluginLibraryProvider.WatchMoveItemFailedAsync(Action<(string appId, string error)> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnMoveItemFailed), reply);
  }

  // InstalledAppsUpdated Signal
  Task<IDisposable> IPluginLibraryProvider.WatchInstalledAppsUpdatedAsync(Action reply)
  {
    return SignalWatcher.AddAsync(this, nameof(InstalledAppsUpdated), reply);
  }

  // LaunchReady Signal
  Task<IDisposable> IPluginLibraryProvider.WatchLaunchReadyAsync(Action<string> reply)
  {
    var res = SignalWatcher.AddAsync(this, nameof(OnLaunchReady), reply);
    steamClientApp.OnLaunchReady = OnLaunchReady;
    return res;
  }

  // LaunchError Signal
  Task<IDisposable> IPluginLibraryProvider.WatchLaunchErrorAsync(Action<(string appId, string error)> reply)
  {
    var res = SignalWatcher.AddAsync(this, nameof(OnLaunchError), reply);
    steamClientApp.OnLaunchError = OnLaunchError;
    return res;
  }

  public void EmitInstalledAppsUpdated()
  {
    InstalledAppsUpdated?.Invoke();
  }

  SteamSession InitSession(SteamUser.LogOnDetails login, string? steamGuardData)
  {
    // Create a new Steam session using the given login details and the DBus interface
    // as an authenticator implementation.
    var session = new SteamSession(login, depotConfigStore, steamGuardData, this);
    session.OnLibraryUpdated = OnLibraryUpdated;
    session.OnAppNewVersionFound = OnAppNewVersionFound;
    session.InstalledAppsUpdated = InstalledAppsUpdated;
    session.OnAuthError = OnAuthError;
    session.OnAvatarUpdated = () => OnUserPropsChanged?.Invoke(new PropertyChanges([], ["Avatar"]));

    // Subscribe to client callbacks
    session.Callbacks.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
    session.Callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
    session.Callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
    session.Callbacks.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
    // session.Callbacks.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);

    return session;
  }

  // Gets current status of the auth
  int GetCurrentAuthStatus()
  {
    var isLoggedOn = this.session?.IsLoggedOn ?? false;
    var IsPendingLogin = this.session?.IsPendingLogin ?? false;
    int status = isLoggedOn || IsPendingLogin ? 2 : 0;
    if (this.needsDeviceConfirmation || (this.tfaCodeTask != null))
    {
      status = 1;
    }
    return status;
  }

  // --- Password flow Implementation ---

  // Login using the given credentials. The password string should be encrypted
  // using the provided public key to prevent session bus eavesdropping.
  async Task IAuthPasswordFlow.LoginAsync(string username, string password)
  {
    // If an existing session exists, disconnect from it.
    // TODO: prevent logging in if already logged in?
    if (this.session != null)
    {
      Console.WriteLine("Disconnecting existing Steam session");
      this.session.Disconnect();
      needsDeviceConfirmation = false;
    }

    Console.WriteLine($"Logging in for user: {username}");

    // Decrypt the password using our private key
    if (this.useEncryption && password != "")
    {
      password = this.Decrypt(password);
    }

    // Configure the user/pass for the session
    var login = new SteamUser.LogOnDetails();
    login.Username = username;
    login.Password = password != "" ? password : null;
    login.LoginID = this.loginId;
    string? steamGuardData = null;

    // Initiate the connection
    Console.WriteLine("Initializing Steam Client connection");
    //this.steamClient.Connect();
    this.session = InitSession(login, steamGuardData);
    await this.session.Login();
  }

  Task IAuthQrFlow.BeginAsync()
  {
    Console.WriteLine("Starting new auth session.");
    this.session?.Disconnect();
    var login = new SteamUser.LogOnDetails();
    string? steamGuardData = null;

    this.session = InitSession(login, steamGuardData);
    this.session.OnNewQrCode = OnQrCodeUpdated;
    Console.WriteLine("Connecting to Steam...");
    Task.Run(this.session.Login);
    return Task.FromResult(0);
  }

  Task IAuthQrFlow.CancelAsync()
  {
    if (this.session is not null && this.session.GetLogonDetails().Username is null)
    {
      this.session.Disconnect();
    }
    return Task.FromResult(0);
  }


  Task<IDisposable> IAuthQrFlow.WatchCodeUpdatedAsync(System.Action<string> handler)
  {
    return SignalWatcher.AddAsync(this, nameof(OnQrCodeUpdated), handler);
  }

  async Task<bool> IUser.ChangeUserAsync(string user_id)
  {
    // If the same user is logged in just skip this.
    if (this.session != null)
    {
      var currentDetails = this.session.GetLogonDetails();
      if (this.session.IsLoggedOn && currentDetails.Username?.ToLower() == user_id.ToLower())
      {
        return true;
      }
    }
    // Configure the user/pass for the session
    var login = new SteamUser.LogOnDetails();
    login.Username = user_id;
    login.LoginID = this.loginId;
    string? steamGuardData;
    try
    {
      Console.WriteLine("Checking {0} for exisiting auth session", authFile);
      string authFileJson = File.ReadAllText(authFile);
      Dictionary<string, SteamAuthSession>? authSessions = JsonSerializer.Deserialize<Dictionary<string, SteamAuthSession>>(authFileJson);

      // If a session exists for this user, set the previously stored guard data from it
      if (authSessions != null && authSessions!.ContainsKey(user_id.ToLower()))
      {
        Console.WriteLine("Found saved auth session for user: {0}", user_id);
        // Get the session data
        var authSession = authSessions![user_id.ToLower()];
        //this.accountName = authSession.accountName;
        //this.previouslyStoredGuardData = authSession.steamGuard;
        //this.refreshToken = authSession.refreshToken;
        //this.accessToken = authSession.accessToken;

        login.Password = null;
        login.AccessToken = authSession.refreshToken;
        login.ShouldRememberPassword = true;
        login.AccountID = authSession.accountId;
        steamGuardData = authSession.steamGuard;
      }
      else
      {
        return false;
      }
    }
    catch (Exception e)
    {
      Console.WriteLine("Failed to open auth.json for stored auth sessions: {0}", e);
      return false;
    }
    needsDeviceConfirmation = false;
    this.session?.Disconnect();
    this.session = InitSession(login, steamGuardData);

    if (isOnline)
    {
      await this.session.Login();
      if (this.session.IsLoggedOn)
        OnUserPropsChanged?.Invoke(new PropertyChanges([], ["Avatar", "Username", "Identifier", "Status"]));

      return this.session.IsLoggedOn;
    }

    var isValid = login.AccessToken != null && !Jwt.IsExpired(login.AccessToken);

    if (isValid)
    {
      Console.WriteLine("No internet connection when changing user, skipping login");
      this.session.IsPendingLogin = true;
      OnUserPropsChanged?.Invoke(new PropertyChanges([], ["Avatar", "Username", "Identifier", "Status"]));
      return true;
    }

    Console.WriteLine("Session has expired, disconnecting");
    this.session?.Disconnect();
    this.session = null;
    OnUserPropsChanged?.Invoke(new PropertyChanges([], ["Avatar", "Username", "Identifier", "Status"]));

    return false;
  }

  // Log out of the given account
  Task IUser.LogoutAsync(string userId)
  {
    if (session is not null && session.GetLogonDetails().Username == userId)
    {
      // get the steamuser handler, which is used for logging on after successfully connecting
      session?.Disconnect();
      session = null;
    }

    // This should invalidate all properties essentially making clients reload them
    OnUserPropsChanged?.Invoke(new PropertyChanges([], ["Avatar", "Username", "Identifier", "Status"]));

    return Task.FromResult(0);
  }

  // Returns all properties of the DBusSteamClient
  Task<UserProperties> IUser.GetAllAsync()
  {
    var properties = new UserProperties();
    if (this.session != null)
    {
      var username = this.session.GetLogonDetails().Username;
      properties.Avatar = this.session.AvatarUrl;
      properties.Username = this.session.PersonaName;
      properties.Identifier = username is null ? "" : username;
      properties.Status = GetCurrentAuthStatus();
    }
    return Task.FromResult(properties);
  }

  // Return the value of the given property
  Task<object> IUser.GetAsync(string prop)
  {
    switch (prop)
    {
      case "Username":
        return Task.FromResult((object)(this.session?.PersonaName ?? ""));
      case "Avatar":
        return Task.FromResult((object)(this.session?.AvatarUrl ?? ""));
      case "Identifier":
        var username = this.session?.GetLogonDetails().Username;
        object user = username is null ? "" : username!;
        return Task.FromResult(user);
      case "Status":
        return Task.FromResult((object)GetCurrentAuthStatus());
      default:
        throw new NotImplementedException($"Invalid property: {prop}");
    }
  }

  // Set the value for the given property
  Task IUser.SetAsync(string prop, object val)
  {
    return Task.FromResult(0);
  }

  // Sets up sending signals when properties have changed
  Task<IDisposable> IUser.WatchPropertiesAsync(Action<PropertyChanges> handler)
  {
    return SignalWatcher.AddAsync(this, nameof(OnUserPropsChanged), handler);
  }

  // Sets up sending signals when auth errors have happened
  Task<IDisposable> IUser.WatchAuthErrorAsync(Action<string> handler)
  {
    return SignalWatcher.AddAsync(this, nameof(OnAuthError), handler);
  }


  // Invoked when connected to Steam
  void OnConnected(SteamClient.ConnectedCallback callback)
  {
    if (this.session == null)
    {
      return;
    }
    var logOnDetails = this.session.GetLogonDetails();
    Console.WriteLine("Connected to Steam! Logging in '{0}'...", logOnDetails.Username);

    // Emit DBus signal to inform interested applications
    OnClientConnected?.Invoke(Path);
  }


  // Invoked when the Steam client disconnects
  void OnDisconnected(SteamClient.DisconnectedCallback callback)
  {
    Console.WriteLine("Disconnected from Steam");

    // TODO: Send logout signal
  }


  // Invoked when the Steam client tries to log in
  void OnLoggedOn(SteamUser.LoggedOnCallback callback)
  {
    if (callback.Result != EResult.OK)
    {
      if (!new List<EResult>([EResult.AlreadyLoggedInElsewhere, EResult.TryAnotherCM]).Contains(callback.Result))
      {
        Console.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);
        OnAuthError?.Invoke(DbusErrors.AuthenticationError);
        OnUserPropsChanged?.Invoke(new PropertyChanges([new KeyValuePair<string, object>("Identifier", ""), new KeyValuePair<string, object>("Status", 0)], ["Avatar", "Username", "Identifier", "Status"]));
      }

      return;
    }

    if (this.session == null)
    {
      Console.WriteLine("Successfully logged in, but no session exists!");
      return;
    }

    Console.WriteLine("Successfully logged on!");
    var loginDetails = this.session!.GetLogonDetails()!;

    // Update the saved login sessions
    // Update the saved auth sessions with login details
    Dictionary<string, SteamAuthSession> authSessions;

    // Load or create the existing auth file to update available sessions
    try
    {
      Console.WriteLine("Loading auth sessions from path: {0}", authFile);
      string authFileJson = File.ReadAllText(authFile);
      authSessions = JsonSerializer.Deserialize<Dictionary<string, SteamAuthSession>>(authFileJson)!;
    }
    catch (Exception)
    {
      Console.WriteLine("No auth sessions exist. Creating new session.");
      authSessions = new Dictionary<string, SteamAuthSession>();
    }

    // Create a new auth session for this login
    SteamAuthSession authSession = new SteamAuthSession();
    authSession.steamGuard = this.session.GetSteamGuardData();
    authSession.accountId = 0;
    authSession.accountName = loginDetails.Username;
    authSession.refreshToken = loginDetails.AccessToken;
    var steamUser = this.session.SteamUser;
    if (steamUser != null && steamUser!.SteamID != null)
    {
      authSession.accountId = steamUser!.SteamID!.AccountID;
    }
    Console.WriteLine($"Found steam id: {steamUser?.SteamID}");

    // Store the updated session for this user
    authSessions[authSession.accountName!.ToLower()] = authSession;

    // Update the auth file with the updated session details
    using StreamWriter writer = new StreamWriter(authFile, false);
    string? authSessionsSerialized = JsonSerializer.Serialize(authSessions);
    writer.Write(authSessionsSerialized);

    Console.WriteLine($"Updated saved logins at '{authFile}'");

    // TODO: We need to update all the appropriate VDF files to allow the Steam
    // client to use this session. The refresh token is stored in 'local.vdf'
    // encrypted using AES, with the key being the sha256 hash of the lowercased username.
    // The SymmetricDecrypt function can help with decrypting these values.
    var key = SHA256.HashData(Encoding.UTF8.GetBytes(authSession.accountName!.ToLower()));

    object user = authSession.accountName!;
    needsDeviceConfirmation = false;
    tfaCodeTask = null;
    OnUserPropsChanged?.Invoke(new PropertyChanges([new KeyValuePair<string, object>("Identifier", user), new KeyValuePair<string, object>("Status", 2)], ["Avatar", "Username"]));

    _ = Task.Run(async () =>
    {
      await Task.Delay(TimeSpan.FromSeconds(30));
      await LaunchSteamClientToSyncTokens(loginDetails);
    });
  }

  async Task LaunchSteamClientToSyncTokens(SteamUser.LogOnDetails loginDetails)
  {
    if (loginDetails.Username == null || steamClientApp.running || !isOnline) return;

    if (DateTime.UtcNow - steamClientApp.lastLoggedIn < TimeSpan.FromHours(6))
    {
      Console.WriteLine("Skipping steam client sync: Task ran less than 6 hours ago.");
      return;
    }

    fetchingSteamClientData = new();
    Console.WriteLine("Logging in with steam client to fetch latest data");

    try
    {
      await steamClientApp.Start(loginDetails.AccountID, "", loginDetails.Username, false);
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine($"Failed starting steam client to fetch data post login, err:{exception}");
    }
    finally
    {
      try
      {
        if (steamClientApp.updateEndedTask != null) await steamClientApp.updateEndedTask.Task;
      }
      catch (Exception) { }

      try
      {
        if (steamClientApp.readyTask != null) await steamClientApp.readyTask.Task;
      }
      catch (Exception) { }

      // Mark this process as finished
      fetchingSteamClientData?.TrySetResult();
      fetchingSteamClientData = null;

      // If game launch doesn't happen within 1 minute, close steam client
      steamClientWaiting = new();
      await AsyncUtils.WaitForConditionAsync(() => steamClientWaiting == null || !steamClientApp.running, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

      if (steamClientWaiting != null)
      {
        try
        {
          Console.WriteLine("Shut down steam client after fetching data");
          await steamClientApp.ShutdownSteamWithTimeoutAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception err)
        {
          Console.Error.WriteLine($"Error waiting and shutting down steam client after launching it to fetch configs: {err}");
        }
        finally
        {
          steamClientWaiting?.TrySetResult();
          steamClientWaiting = null;
        }
      }
    }

    fetchingSteamClientData?.TrySetResult();
    fetchingSteamClientData = null;
  }

  void OnLoggedOff(SteamUser.LoggedOffCallback callback)
  {
    Console.WriteLine("Logged off of Steam: {0}", callback.Result);
  }


  // Get the value of the given property
  public Task<object> GetAsync(string prop)
  {
    throw new NotImplementedException();
  }


  // Set the given property to the given value
  public Task SetAsync(string prop, object val)
  {
    throw new NotImplementedException();
  }


  // Returns all properties of the DBusSteamClient
  public Task<SteamClientProperties> GetAllAsync()
  {
    return Task.FromResult(new SteamClientProperties());
  }

  // Test signal
  public Task<IDisposable> WatchPongAsync(Action<string> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnPing), reply);
  }


  // Connected Signal; this method gets invoked once on initialization to create
  // the event action to be invoked when we want to fire a signal over DBus.
  public Task<IDisposable> WatchConnectedAsync(Action<ObjectPath> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnClientConnected), reply);
  }

  async Task<string> WaitForTfaCode()
  {
    if (tfaCodeTask != null)
      tfaCodeTask.TrySetCanceled();
    tfaCodeTask = new();

    var timeoutTask = Task.Delay(60000);
    var completedTask = await Task.WhenAny(tfaCodeTask.Task, timeoutTask);

    if (completedTask == timeoutTask)
    {
      tfaCodeTask.TrySetCanceled();
      throw new TimeoutException("Waiting for 2fa code timed out");
    }

    return await tfaCodeTask.Task;
  }


  // --- IAuthenticator implementation ---

  /// <summary>
  /// This method is called when the account being logged into requires 2-factor authentication using the authenticator app.
  /// </summary>
  /// <param name="previousCodeWasIncorrect">True when previously provided code was incorrect.</param>
  /// <returns>The 2-factor auth code used to login. This is the code that can be received from the authenticator app.</returns>
  async Task<string> IAuthenticator.GetDeviceCodeAsync(bool previousCodeWasIncorrect)
  {
    // Emit a signal that a 2-factor code is required using the authenticator app
    OnTwoFactorRequired?.Invoke((previousCodeWasIncorrect, "Enter the two-factor code from the Steam authenticator app."));

    // Wait for a UI to call SendTwoFactor() with the code
    Console.WriteLine("Waiting for application to send two-factor code");
    string code = await WaitForTfaCode();
    Console.WriteLine("TFA code was sent");

    Console.WriteLine($"Sending 2FA code: {code}");

    // Return the code from this function to continue login flow
    return code;
  }


  /// <summary>
  /// This method is called when the account being logged into uses Steam Guard email authentication. This code is sent to the user's email.
  /// </summary>
  /// <param name="email">The email address that the Steam Guard email was sent to.</param>
  /// <param name="previousCodeWasIncorrect">True when previously provided code was incorrect.</param>
  /// <returns>The Steam Guard auth code used to login.</returns>
  async Task<string> IAuthenticator.GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
  {
    // Emit a signal that a 2-factor code is required using Steam Guard email authentication
    OnEmailTwoFactorRequired?.Invoke((email, previousCodeWasIncorrect, $"Enter the two-factor code sent to {email}"));

    // Wait for a UI to call SendTwoFactor() with the code
    Console.WriteLine("Waiting for application to send two-factor code");
    string code = await WaitForTfaCode();
    Console.WriteLine("TFA code was sent");

    Console.WriteLine($"Sending 2FA code: {code}");

    // Return the code from this function to continue login flow
    return code;
  }


  /// <summary>
  /// This method is called when the account being logged has the Steam Mobile App and accepts authentication notification prompts.
  ///
  /// Return false if you want to fallback to entering a code instead.
  /// </summary>
  /// <returns>Return true to poll until the authentication is accepted, return false to fallback to entering a code.</returns>
  Task<bool> IAuthenticator.AcceptDeviceConfirmationAsync()
  {
    // TODO: Have this be configurable
    Console.WriteLine("Waiting for mobile confirmation...");
    needsDeviceConfirmation = true;
    OnConfirmationRequired?.Invoke("Confirm sign in on the Steam Mobile App");
    return Task.FromResult(true);
  }

  // --- Two-factor Implementation ---

  Task IAuthTwoFactorFlow.SendCodeAsync(string code)
  {
    if (tfaCodeTask != null)
    {
      Console.WriteLine($"Got 2FA code: {code}");
      tfaCodeTask.TrySetResult(code);
      tfaCodeTask = null;
    }
    else
    {
      Console.WriteLine("No login session in progress");
      OnAuthError?.Invoke(DbusErrors.AuthenticationError);
    }

    return Task.CompletedTask;
  }


  Task<IDisposable> IAuthTwoFactorFlow.WatchTwoFactorRequiredAsync(Action<(bool previousCodeWasIncorrect, string message)> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnTwoFactorRequired), reply);
  }


  Task<IDisposable> IAuthTwoFactorFlow.WatchEmailTwoFactorRequiredAsync(Action<(string email, bool previousCodeWasIncorrect, string message)> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnEmailTwoFactorRequired), reply);
  }

  Task<IDisposable> IAuthTwoFactorFlow.WatchConfirmationRequiredAsync(System.Action<string> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnConfirmationRequired), reply);
  }

  // Return the public key to the client to send encrypted messages
  Task<(string keyType, string data)> IAuthCryptography.GetPublicKeyAsync()
  {
    string pubKey = this.rsa.ExportRSAPublicKeyPem();
    return Task.FromResult(("RSA-SHA256", pubKey));
  }

  public ObjectPath ObjectPath { get { return Path; } }

  // -- Cloud saves Implementation --

  async Task<CloudPathObject[]> IPluginLibraryProvider.GetSavePathPatternsAsync(string appIdString, string platform)
  {
    if (ParseAppId(appIdString) is not uint appId) throw DbusExceptionHelper.ThrowInvalidAppId();
    if (!EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();
    await session!.RequestAppInfo(appId, false);

    Console.WriteLine("Getting paths for {0}", appId);
    var appInfo = this.session.AppInfo[appId];
    if (appInfo == null)
    {
      Console.WriteLine("Unable to load appInfo");
      return [];
    }
    var installedInfo = depotConfigStore.GetInstalledAppInfo(appId);
    List<CloudPathObject> results = [];
    List<KeyValue> overrides = [];
    var ufs = appInfo["ufs"];

    if (ufs.Children.Count == 0 || ufs["maxnumfiles"].AsInteger() == 0)
    {
      Console.WriteLine("Steam Cloud is not configured for this game.");
      return [];
    }

    // Get overrides that match this platform
    foreach (var rootoverride in ufs["rootoverrides"].Children)
    {
      var os = rootoverride["os"].AsString()?.ToLower();
      if (os != platform) continue;
      overrides.Add(rootoverride);
    }

    var savefiles = ufs["savefiles"].Children;
    // Unsure if usage of remote directory is bound to the number of savefiles or even its existance.
    var defaultPath = RemoteCache.GetRemoteSavePath(this.session!.SteamUser!.SteamID!.AccountID, appId);

    results.Add(new CloudPathObject { alias = "", path = defaultPath, recursive = true, pattern = "*", platforms = [] });


    foreach (var location in savefiles)
    {
      List<string> platforms = [];
      if (location["platforms"].Children.Count != 0)
      {
        var matched = false;
        foreach (var locPlatform in location["platforms"].Children)
        {
          var platformToCheck = locPlatform.AsString()?.ToLower() ?? "";
          if (platformToCheck != "all") platforms.Add(platformToCheck);
          if (!matched) matched = platformToCheck == "all" || platformToCheck == platform;
        }
        if (!matched) continue;
      }
      var root = location["root"].AsString()!;
      var path = location["path"].AsString()!;
      // Remove . characters from the path
      path = string.Join('/', path.Split(['/', '\\']).Where(path => path.Length != 1 || path[0] != '.'));
      var pattern = location["pattern"].AsString()!;
      var recursive = location["recursive"].AsBoolean(defaultValue: false);
      path = path.Replace("{64BitSteamID}", this.session.SteamUser.SteamID.ConvertToUInt64().ToString(), StringComparison.CurrentCultureIgnoreCase);
      path = path.Replace("{Steam3AccountID}", this.session.SteamUser.SteamID.AccountID.ToString(), StringComparison.CurrentCultureIgnoreCase);

      if (root == "gameinstall")
      {
        root = "GameInstall"; // The GameInstall is used for downloads, not sure if steam has distinction betweeen lower and PascalCase, I don't want to find out about that the hard way
      }
      var alias = $"%{root}%{path}";

      // Apply overrides
      foreach (var rootoverride in overrides)
      {
        if (rootoverride["root"].AsString() != root) continue;
        root = rootoverride["useinstead"].AsString();
        var addpath = rootoverride["addpath"].AsString();
        if (addpath != null)
        {
          path = addpath + path;
        }
        var transforms = rootoverride["pathtransforms"];
        foreach (var transform in transforms.Children)
        {
          var find = transform["find"].AsString();
          var replace = transform["replace"].AsString();
          if (find != null && replace != null)
          {
            path.Replace(find, replace, StringComparison.CurrentCultureIgnoreCase);
          }
        }
      }

      var mappedroot = root;

      var home = Environment.GetEnvironmentVariable("HOME") ?? "~";
      switch (root)
      {
        case "GameInstall":
          mappedroot = "{INSTALL}";
          if (installedInfo != null)
          {
            mappedroot = installedInfo.Value.Info.InstalledPath;
          }
          break;
        case "WinMyDocuments":
          mappedroot = "C:/Users/{USER}/Documents";
          break;
        case "WinAppDataLocal":
          mappedroot = "C:/Users/{USER}/AppData/Local";
          break;
        case "WinAppDataLocalLow":
          mappedroot = "C:/Users/{USER}/AppData/LocalLow";
          break;
        case "WinAppDataRoaming":
          mappedroot = "C:/Users/{USER}/AppData/Roaming";
          break;
        case "WinSavedGames":
          mappedroot = "C:/Users/{USER}/Saved Games";
          break;
        case "MacHome":
        case "LinuxHome":
          mappedroot = home;
          break;
        case "MacAppSupport":
          mappedroot = System.IO.Path.Join(home, "Library/Application Support");
          break;
        case "MacDocuments":
          mappedroot = System.IO.Path.Join(home, "Documents");
          break;
        case "MacCaches":
          mappedroot = System.IO.Path.Join(home, "Library/Caches");
          break;
        case "LinuxXdgDataHome":
          var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? System.IO.Path.Join(home, ".local/share");
          mappedroot = xdgData;
          break;
        case "LinuxXdgConfigHome":
          var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? System.IO.Path.Join(home, ".config");
          mappedroot = xdgConfig;
          break;
        default:
          throw new Exception($"Unknown root name {root}");
      }
      var newPath = System.IO.Path.Join(mappedroot, path);
      results.Add(new CloudPathObject { alias = alias, path = newPath, recursive = recursive, pattern = pattern, platforms = platforms.ToArray() });
    }
    return results.ToArray();
  }

  Task<IDisposable> ICloudSaveProvider.WatchCloudSaveSyncProgressedAsync(Action<CloudSyncProgress> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnCloudSaveSyncProgressed), reply);
  }

  Task<IDisposable> ICloudSaveProvider.WatchCloudSaveSyncFailedAsync(Action<CloudSyncFailure> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnCloudSyncFailed), reply);
  }

  async Task ICloudSaveProvider.CloudSaveDownloadAsync(string appIdString, string platform, bool force, CloudPathObject[] paths)
  {
    if (ParseAppId(appIdString) is not uint appidParsed) throw DbusExceptionHelper.ThrowInvalidAppId();
    if (!EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();
    if (!isOnline) throw DbusExceptionHelper.ThrowNotOnline();
    if (this.session?.steamCloud == null)
    {
      Console.WriteLine("Steam Cloud not initialized");
      return;
    }
    ERemoteStoragePlatform platformToSync = ERemoteStoragePlatform.Windows; // Decide what platform we sync
    switch (platform)
    {
      case "linux":
        platformToSync = ERemoteStoragePlatform.Linux;
        break;
      case "windows":
        platformToSync = ERemoteStoragePlatform.Windows;
        break;
      case "macos":
        platformToSync = ERemoteStoragePlatform.OSX;
        break;
    }
    Console.WriteLine("CloudDownload for {0}", appidParsed);
    var remoteCacheFile = new RemoteCache(this.session.SteamUser!.SteamID!.AccountID, appidParsed);
    var changeNumber = remoteCacheFile.GetChangeNumber();
    Console.WriteLine("Cached change number {0}", changeNumber);
    // Request changelist
    var changelist = await this.session.steamCloud.GetFilesChangelistAsync(appidParsed, changeNumber);
    if (changelist == null)
    {
      Console.WriteLine("Failed to get changelist");
      return;
    }
    Console.WriteLine("Current change: {0}", changelist.current_change_number);

    // Check if there are any changes we need to apply
    var cachedFiles = remoteCacheFile.MapRemoteCacheFiles();
    var localFiles = Steam.Cloud.SteamCloud.MapFilePaths(paths);
    var analysis = CloudUtils.AnalyzeSaves(changelist, cachedFiles, localFiles);
    if (changelist.current_change_number == changeNumber && analysis.missingLocal.Count == 0)
    {
      Console.WriteLine("files are synced");
      return;
    }

    if (!force && changeNumber != null && changelist.current_change_number != changeNumber && analysis.changedLocal.Count > 0)
    {
      var local = analysis.conflictDetails.local;
      var remote = analysis.conflictDetails.remote;
      OnCloudSyncFailed?.Invoke(new CloudSyncFailure { AppdId = appIdString, Error = DbusErrors.CloudConflict, Local = local, Remote = remote });
      throw DbusExceptionHelper.ThrowCloudConflict();
    }

    // Collect files that need to be downloaded/restored
    List<RemoteCacheFile> filesToDownload = [];
    // Files to restore
    foreach (var file in analysis.missingLocal)
    {
      file.SyncState = ERemoteStorageSyncState.inprogress;
      filesToDownload.Add(file);
    }
    // potentially new fiels from the cloud
    foreach (var file in changelist.files)
    {
      if ((file.platforms_to_sync & (uint)platformToSync) == 0) continue;
      var path = "";
      if (changelist.path_prefixes.Count > 0 && file.ShouldSerializepath_prefix_index())
      {
        path = changelist.path_prefixes[(int)file.path_prefix_index];
      }
      var cloudpath = $"{path}{file.file_name}";
      cachedFiles.TryGetValue(cloudpath.ToLower(), out var remoteCacheEntry);
      var currentFile = new RemoteCacheFile(file, cloudpath);
      // compare existing cached entry if we have the same hash
      if (remoteCacheEntry is not null && remoteCacheEntry.Sha1() == currentFile.Sha1()) continue;
      remoteCacheEntry ??= currentFile;
      remoteCacheEntry.RemoteTime = file.time_stamp;
      remoteCacheEntry.PersistState = file.persist_state;
      remoteCacheEntry.SyncState = ERemoteStorageSyncState.inprogress;
      filesToDownload.Add(remoteCacheEntry);
    }

    List<Task<(RemoteCacheFile, Exception?)>> downloadTasks = [];
    var httpClient = new HttpClient();
    SemaphoreSlim semaphore = new(4);
    uint totalSize = 0;
    uint downloaded = 0;
    foreach (var downloadFile in filesToDownload)
    {
      totalSize += downloadFile.Size;
      var fspath = downloadFile.GetRemotePath();
      // Map the path to file system
      foreach (var location in paths)
      {
        // If there is no variable, we want a default location
        if (location.alias.Length == 0)
        {
          if (fspath[0] != '%')
          {
            fspath = System.IO.Path.Join(location.path, fspath);
          }
          continue;
        }
        var newpath = fspath.Replace(location.alias, location.path, StringComparison.CurrentCultureIgnoreCase);
        if (newpath != fspath)
        {
          fspath = newpath;
          break;
        }
      }
      downloadTasks.Add(CloudUtils.DownloadFileAsync(appidParsed, downloadFile, fspath, semaphore, httpClient, this.session.steamCloud));
    }
    string? currentError = null;
    while (downloadTasks.Count != 0)
    {
      var completedTask = await Task.WhenAny(downloadTasks);
      downloadTasks.Remove(completedTask);
      (var file, var exception) = completedTask.Result;
      if (exception is null)
      {
        downloaded += file.Size;
        file.SyncState = ERemoteStorageSyncState.unknown;
        file.LocalTime = file.RemoteTime;
        file.Time = file.RemoteTime;
        OnCloudSaveSyncProgressed?.Invoke(new CloudSyncProgress
        {
          AppdId = appIdString,
          Progress = (double)downloaded / totalSize * 100,
          SyncState = (uint)SyncState.Download
        });
      }
      else
      {
        Console.WriteLine("a file encountered an error {0}", exception);
        file.SyncState = ERemoteStorageSyncState.inprogress;
        file.Time = 0;
        file.LocalTime = 0;
        currentError ??= DbusErrors.CloudFileDownload;
      }
      cachedFiles[file.GetRemotePath().ToLower()] = file;
    }

    if (currentError != null)
    {
      OnCloudSyncFailed?.Invoke(new CloudSyncFailure { AppdId = appIdString, Error = currentError });
    }
    // Set this to unknown for now, this shouldnt break anything afaik
    remoteCacheFile.UpdateLocalCache(changelist.current_change_number, "-1", cachedFiles.Values.ToArray());
    remoteCacheFile.Save();

    KeyValue autocloud = new("steam_autocloud.vdf");
    autocloud.Children.Add(new KeyValue("accountid", this.session.SteamUser.SteamID.AccountID.ToString()));
    foreach (var location in paths)
    {
      if (location.alias.Length == 0) continue;
      var path = System.IO.Path.Join(location.path, "steam_autocloud.vdf");
      Disk.EnsureParentFolderExists(path);
      autocloud.SaveToFileWithAtomicRename(path);
    }
    Console.WriteLine("Download complete");
  }

  async Task ICloudSaveProvider.CloudSaveUploadAsync(string appid, string platform, bool force, CloudPathObject[] paths)
  {
    if (ParseAppId(appid) is not uint appidParsed) throw DbusExceptionHelper.ThrowInvalidAppId();
    if (!EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();
    if (!isOnline) throw DbusExceptionHelper.ThrowNotOnline();
    while (steamClientApp.running)
    {
      await Task.Delay(500);
    }

    if (this.session?.steamCloud == null)
    {
      Console.WriteLine("Steam Cloud not initialized");
      return;
    }
    var localFiles = Steam.Cloud.SteamCloud.MapFilePaths(paths);
    Console.WriteLine("CloudUpload for {0}", appidParsed);
    var remoteCacheFile = new RemoteCache(this.session.SteamUser!.SteamID!.AccountID, appidParsed);
    var changeNumber = remoteCacheFile.GetChangeNumber();
    var changelist = await this.session.steamCloud.GetFilesChangelistAsync(appidParsed, changeNumber);
    if (changelist == null)
    {
      return;
    }
    var cachedFiles = remoteCacheFile.MapRemoteCacheFiles();
    var analysis = CloudUtils.AnalyzeSaves(changelist, cachedFiles, localFiles, true);
    Console.WriteLine("Current change number {0}", changelist.current_change_number);
    Console.WriteLine("Local change number {0}", changeNumber);
    if (changelist.ShouldSerializecurrent_change_number() && changelist.current_change_number != changeNumber && changelist.files.Count > 0)
    {
      if (!force)
      {
        Console.WriteLine("Potential conflict, different change numbers detected");
        var local = analysis.conflictDetails.local;
        var remote = analysis.conflictDetails.remote;
        OnCloudSyncFailed?.Invoke(new CloudSyncFailure { AppdId = appid, Error = DbusErrors.CloudConflict, Local = local, Remote = remote });
        throw DbusExceptionHelper.ThrowCloudConflict();
      }
      Console.WriteLine("Focefully uploading files");
    }
    Console.WriteLine("Before upload analysis: Changed locally: {0} Missing local: {1}", analysis.changedLocal.Count, analysis.missingLocal.Count);
    if (analysis.changedLocal.Count == 0 && analysis.missingLocal.Count == 0)
    {
      Console.WriteLine("Nothing to do");
      OnCloudSaveSyncProgressed?.Invoke(new CloudSyncProgress
      {
        AppdId = appid,
        Progress = 100,
        SyncState = (uint)SyncState.Upload
      });
      return;
    }
    // Begin upload
    List<string> filesToUpload = [];
    List<string> filesToDelete = [];
    foreach (var file in analysis.changedLocal)
    {
      filesToUpload.Add(file.GetRemotePath());
    }
    foreach (var file in analysis.missingLocal)
    {
      filesToDelete.Add(file.GetRemotePath());
    }

    var uploadData = await this.session.steamCloud.BeginAppUploadBatch(appidParsed, filesToUpload.ToArray(), filesToDelete.ToArray());
    if (uploadData == null)
    {
      Console.WriteLine("Failed to initialize upload with steam services");
      OnCloudSyncFailed?.Invoke(new CloudSyncFailure { AppdId = appid, Error = DbusErrors.CloudFileUpload });
      return;
    }

    Console.WriteLine("New change number is {0}", uploadData.app_change_number);
    Console.WriteLine("Uploading files to batch {0}", uploadData.batch_id);

    SemaphoreSlim semaphore = new(4);
    uint totalSize = 0;
    uint uploaded = 0;
    var httpClient = new HttpClient();
    List<Task<(RemoteCacheFile, Exception?)>> uploadTasks = [];

    foreach (var file in analysis.changedLocal)
    {
      cachedFiles.TryGetValue(file.GetRemotePath().ToLower(), out var uploadFile);
      uploadFile ??= new RemoteCacheFile(file);
      uploadFile.SyncState = ERemoteStorageSyncState.inprogress;
      totalSize += uploadFile.Size;
      var fspath = uploadFile.GetRemotePath();
      // Map the path to file system
      foreach (var location in paths)
      {
        // If there is no variable, we want a default location
        if (location.alias.Length == 0)
        {
          if (fspath[0] != '%')
          {
            fspath = System.IO.Path.Join(location.path, fspath);
          }
          continue;
        }
        var newpath = fspath.Replace(location.alias, location.path, StringComparison.CurrentCultureIgnoreCase);
        if (newpath != fspath)
        {
          fspath = newpath;
          break;
        }
      }
      var task = CloudUtils.UploadFileAsync(appidParsed, uploadFile, fspath, uploadData.batch_id, semaphore, httpClient, this.session.steamCloud);
      uploadTasks.Add(task);
    }
    string? currentError = null;
    while (uploadTasks.Count != 0)
    {
      var completedTask = await Task.WhenAny(uploadTasks);
      uploadTasks.Remove(completedTask);
      (var file, var exception) = completedTask.Result;
      if (exception is null)
      {
        uploaded += file.Size;
        file.SyncState = ERemoteStorageSyncState.unknown;
        file.RemoteTime = file.LocalTime;

        OnCloudSaveSyncProgressed?.Invoke(new CloudSyncProgress
        {
          AppdId = appid,
          Progress = (double)uploaded / totalSize * 100,
          SyncState = (uint)SyncState.Upload
        });
      }
      else
      {
        Console.WriteLine("a file encountered an error {0}", exception);
        file.SyncState = ERemoteStorageSyncState.inprogress;
        file.RemoteTime = 0;
        currentError ??= DbusErrors.CloudFileUpload;
      }
      cachedFiles[file.GetRemotePath().ToLower()] = file;
    }
    foreach (var file in analysis.missingLocal)
    {
      cachedFiles.TryGetValue(file.GetRemotePath().ToLower(), out var uploadFile);
      if (uploadFile == null) continue;
      uploadFile.PersistState = SteamKit2.Internal.ECloudStoragePersistState.k_ECloudStoragePersistStateDeleted;
      uploadFile.SyncState = ERemoteStorageSyncState.inprogress;
      var delRes = await this.session.steamCloud.DeleteFileAsync(appidParsed, file.GetRemotePath(), uploadData.batch_id);
      if (delRes == null)
      {
        Console.WriteLine("Failed to call delete on a file");
      }
      cachedFiles.Remove(file.GetRemotePath().ToLower());
    }
    EResult eResult = EResult.OK;
    if (currentError != null)
    {
      OnCloudSyncFailed?.Invoke(new CloudSyncFailure { AppdId = appid, Error = currentError });
      eResult = EResult.Fail;
    }
    await this.session.steamCloud.CompleteAppUploadBatch(appidParsed, uploadData.batch_id, (uint)eResult);

    remoteCacheFile.UpdateLocalCache(uploadData.app_change_number, "-1", cachedFiles.Values.ToArray());
    remoteCacheFile.Save();
    KeyValue autocloud = new("steam_autocloud.vdf");
    autocloud.Children.Add(new KeyValue("accountid", this.session.SteamUser.SteamID.AccountID.ToString()));
    foreach (var location in paths)
    {
      if (location.alias.Length == 0) continue;
      var path = System.IO.Path.Join(location.path, "steam_autocloud.vdf");
      Disk.EnsureParentFolderExists(path);
      autocloud.SaveToFileWithAtomicRename(path);
    }
    Console.WriteLine("Upload complete");
  }
}
