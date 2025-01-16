using System.Text.Json;
using Tmds.DBus;
using SteamKit2;
using SteamKit2.Authentication;
using Playtron.Plugin;
using Steam.Content;
using Steam.Session;
using SteamBus.Auth;
using System.Collections.ObjectModel;
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
  private Dictionary<uint, SteamApps.LicenseListCallback.License> licenses = new Dictionary<uint, SteamApps.LicenseListCallback.License>();
  private Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> packages = new Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo>();
  private Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> apps = new Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo>();

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
  public event Action? InstalledAppsUpdated;
  public event Action<PropertyChanges>? OnUserPropsChanged;
  public event Action<string>? OnAuthError;
  public event Action<(bool previousCodeWasIncorrect, string message)>? OnTwoFactorRequired;
  public event Action<(string email, bool previousCodeWasIncorrect, string message)>? OnEmailTwoFactorRequired;
  public event Action<string>? OnConfirmationRequired;
  public event Action<string>? OnQrCodeUpdated;
  public event Action<ProviderItem[]>? OnLibraryUpdated;

  private SteamClientApp steamClientApp;
  public event Action<InstallStartedDescription>? OnDependencyInstallStarted;
  public event Action<InstallProgressedDescription>? OnDependencyInstallProgressed;
  public event Action<string>? OnDependencyInstallCompleted;
  public event Action<(string appId, string error)>? OnDependencyInstallFailed;
  public event Action<(string appId, string version)>? OnDependencyAppNewVersionFound;


  private DepotConfigStore dependenciesStore;


  // Creates a new DBusSteamClient instance with the given DBus path
  public DBusSteamClient(ObjectPath path, DepotConfigStore depotConfigStore, DepotConfigStore dependenciesStore, DisplayManager displayManager)
  {
    steamClientApp = new SteamClientApp(displayManager, dependenciesStore);

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
    // Empty array here since no tool is really required for this plugin
    // the download for steam client happens async during game launch and not before using the plugin
    return Task.FromResult<ProviderItem[]>([]);
  }

  // Starts installation of all the required dependencies
  Task IPluginDependencies.InstallAllRequiredDependenciesAsync()
  {
    // Does nothing for the same reason as GetRequiredDependenciesAsync
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
    if (!EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();
    if (ParseAppId(appIdString) is not uint appId) throw DbusExceptionHelper.ThrowInvalidAppId();
    await this.session!.WaitForLibrary();
    if (!this.session.AppInfo.TryGetValue(appId, out var appinfo)) throw DbusExceptionHelper.ThrowInvalidAppId();
    return SteamSession.GetProviderItem(appIdString, appinfo.KeyValues);
  }

  async Task<ProviderItem[]> IPluginLibraryProvider.GetProviderItemsAsync()
  {
    if (this.session is null) return [];
    await this.session.WaitForLibrary();
    List<ProviderItem> providerItems = new(session.AppInfo.Count);
    foreach (var app in this.session.AppInfo)
    {
      providerItems.Add(SteamSession.GetProviderItem(app.Key.ToString(), app.Value.KeyValues));
    }
    return providerItems.ToArray();
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

    var installScript = await InstallScript.CreateAsync(appId, installDirectory!);

    var options = new JsonSerializerOptions
    {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    return JsonSerializer.Serialize(installScript.scripts, options);
  }

  Task<InstalledAppDescription[]> IPluginLibraryProvider.GetInstalledAppsAsync()
  {
    return Task.FromResult(depotConfigStore.GetInstalledAppInfo());
  }

  async Task<int> IPluginLibraryProvider.InstallAsync(string appIdString, string disk, InstallOptions options)
  {
    Console.WriteLine($"Installing app: {appIdString}");
    if (!EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();
    if (ParseAppId(appIdString) is not uint appId) throw DbusExceptionHelper.ThrowInvalidAppId();

    // Create a content downloader for the given app
    var downloader = new ContentDownloader(session!, depotConfigStore);

    // Configure the download options
    var installdir = await downloader.GetAppInstallDir(appId);
    var downloadOptions = new AppDownloadOptions(options, await Disk.GetInstallRootFromDevice(disk, installdir));

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

  async Task<string> IPluginLibraryProvider.MoveItemAsync(string appIdString, string disk)
  {
    Console.WriteLine($"Uninstalling app: {appIdString}");
    if (ParseAppId(appIdString) is not uint appId) throw DbusExceptionHelper.ThrowInvalidAppId();

    var downloader = new ContentDownloader(session!, depotConfigStore);
    var installdir = await downloader.GetAppInstallDir(appId);
    var newInstallDirectory = await Disk.GetInstallRootFromDevice(disk, installdir);

    return await depotConfigStore.MoveInstalledApp(appId, newInstallDirectory, OnMoveItemProgressed);
  }

  async Task IPluginLibraryProvider.PauseInstallAsync()
  {
    Console.WriteLine("Pausing current install");
    await ContentDownloader.PauseInstall();
  }

  async Task<string[]> IPluginLibraryProvider.PreLaunchHookAsync(string appId, bool wantsOfflineMode)
  {
    if (!EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();
    session!.UpdateConfigFiles(wantsOfflineMode);

    try
    {
      await steamClientApp.Start(session!.GetLogonDetails().Username!);
    }
    catch (DBusException exception)
    {
      if (exception.ErrorName == DbusErrors.DependencyUpdateRequired)
        return [SteamClientApp.STEAM_CLIENT_APP_ID.ToString()];
    }

    return [];
  }

  async Task IPluginLibraryProvider.PostLaunchHookAsync(string appId)
  {
    await steamClientApp.ShutdownSteamWithTimeoutAsync(TimeSpan.FromSeconds(5));
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

  // InstalledAppsUpdated Signal
  Task<IDisposable> IPluginLibraryProvider.WatchInstalledAppsUpdatedAsync(Action reply)
  {
    return SignalWatcher.AddAsync(this, nameof(InstalledAppsUpdated), reply);
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
    session.OnAuthError = OnAuthError;

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
    int status = isLoggedOn ? 2 : 0;
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
    await this.session.Login();
    if (this.session.IsLoggedOn)
    {
      OnUserPropsChanged?.Invoke(new PropertyChanges([], ["Avatar", "Username", "Identifier", "Status"]));
    }
    return this.session.IsLoggedOn;
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
      Console.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);
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
  }

  void OnLoggedOff(SteamUser.LoggedOffCallback callback)
  {
    Console.WriteLine("Logged off of Steam: {0}", callback.Result);
    OnUserPropsChanged?.Invoke(new PropertyChanges([new KeyValuePair<string, object>("Identifier", ""), new KeyValuePair<string, object>("Status", 0)], ["Avatar", "Username", "Identifier", "Status"]));
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
    }
    else
    {
      Console.WriteLine("No login session in progress");
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

  Task<CloudPathObject[]> IPluginLibraryProvider.GetSavePathPatternsAsync(string appid, string platform)
  {
    if (!EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();

    uint appidParsed = uint.Parse(appid);
    Console.WriteLine("Getting paths for {0}", appid);
    var appInfo = this.apps[appidParsed];
    if (appInfo == null)
    {
      Console.WriteLine("Unable to load appInfo");
      return Task.FromResult(Array.Empty<CloudPathObject>());
    }
    List<CloudPathObject> results = [];
    List<KeyValue> overrides = [];
    var ufs = appInfo.KeyValues["ufs"];

    if (ufs.Children.Count == 0)
    {
      Console.WriteLine("Steam Cloud is not configured for this game.");
      return Task.FromResult(Array.Empty<CloudPathObject>());
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
    var defaultPath = RemoteCache.GetRemoteSavePath(this.session!.SteamUser!.SteamID!.AccountID, appidParsed);

    results.Add(new CloudPathObject { alias = "", path = defaultPath, recursive = true });


    foreach (var location in savefiles)
    {
      if (location["platforms"].Children.Count != 0)
      {
        foreach (var locPlatform in location["platforms"].Children)
        {
          var platformToCheck = locPlatform.AsString()?.ToLower();
          if (platformToCheck == "all" || platformToCheck == platform) goto Matched;

        }
        continue;
      }
    Matched:
      var root = location["root"].AsString()!;
      var path = location["path"].AsString()!;
      var pattern = location["pattern"].AsString()!;
      var recursive = location["recursive"].AsBoolean(defaultValue: false);
      path = path.Replace("{64BitSteamID}", this.session.SteamUser.SteamID.ConvertToUInt64().ToString());
      path = path.Replace("{Steam3AccountID}", this.session.SteamUser.SteamID.AccountID.ToString());

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
            path.Replace(find, replace);
          }
        }
      }

      var mappedroot = root;

      var home = Environment.GetEnvironmentVariable("HOME") ?? "~";
      switch (root)
      {
        case "GameInstall":
          Console.WriteLine("FIXME: Game install path in save location IS NOT replaced with actual install location.");
          mappedroot = "{INSTALL}"; // FIXME: Replace it with actual install path if available
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
      Console.WriteLine("Appending new path {0} - {1}", alias, newPath);
      results.Add(new CloudPathObject { alias = alias, path = newPath, recursive = recursive, pattern = pattern });
    }
    return Task.FromResult(results.ToArray());
  }

  async Task ICloudSaveProvider.CloudSaveDownloadAsync(string appid, CloudPathObject[] paths)
  {
    if (!EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();

    if (this.session?.steamCloud == null)
    {
      Console.WriteLine("Steam Cloud not initialized");
      return;
    }
    uint appidParsed = uint.Parse(appid);
    ERemoteStoragePlatform platformToSync = ERemoteStoragePlatform.Windows; // Decide what platform we sync
    var localFiles = Steam.Cloud.SteamCloud.MapFilePaths(paths);

    Console.WriteLine("CloudDownload for {0}", appidParsed);
    var remoteCacheFile = new RemoteCache(this.session.SteamUser!.SteamID!.AccountID, appidParsed);
    var changeNumber = remoteCacheFile.GetChangeNumber();
    Console.WriteLine("Cached change number {0}", changeNumber);
    // Request changelist
    var changelist = await this.session.steamCloud.GetFilesChangelistAsync(appidParsed, changeNumber);
    if (changelist == null)
    {
      return;
    }
    if (changelist.files.Count == 0)
    {
      Console.WriteLine("Up to date");
      return;
    }
    Console.WriteLine("Current change: {0}", changelist.current_change_number);
    var cachedFiles = remoteCacheFile.MapRemoteCacheFiles();

    var client = new HttpClient();
    // FIXME: Concurrency is appreciated here
    foreach (var file in changelist.files)
    {
      if (((ERemoteStoragePlatform)file.platforms_to_sync & platformToSync) == 0) continue;
      var path = "";
      if (changelist.path_prefixes.Count > 0)
      {
        path = changelist.path_prefixes[(int)file.path_prefix_index];
      }
      var cloudpath = $"{path}{file.file_name}";
      var fspath = cloudpath;
      var response = await this.session.steamCloud.DownloadFileAsync(appidParsed, cloudpath);
      if (response == null)
      {
        Console.WriteLine("Unable to get file {0}", file.file_name);
        return;
      }
      // Prepare get request for actual file
      var http = response.use_https ? "https" : "http";
      var url = $"{http}://{response.url_host}{response.url_path}";
      Console.WriteLine("Writing files to appropriate directories is not implemented yet");
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
      Console.WriteLine("{0} - {1}", cloudpath, fspath);
      continue;
      var request = new HttpRequestMessage(HttpMethod.Get, url);
      foreach (var header in response.request_headers)
      {
        request.Headers.Add(header.name, header.value); // Set headers that steam expects us to send to the CDN
      }
      // We may also want to get files concurrently
      try
      {
        // We can also make it return after headers were read, unsure how big files can get.
        var fileRes = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
        Console.WriteLine("Response for {0} received", cloudpath);
        fileRes.EnsureSuccessStatusCode();
        var fileData = await fileRes.Content.ReadAsByteArrayAsync();
        // FIXME: Where to write it???
      }
      catch (Exception e)
      {
        Console.WriteLine("Failed to get a file {0}: {1}", cloudpath, e);
        return;
      }
    }
    return;
  }
  async Task ICloudSaveProvider.CloudSaveUploadAsync(string appid, CloudPathObject[] paths)
  {
    if (!EnsureConnected()) throw DbusExceptionHelper.ThrowNotLoggedIn();

    if (this.session?.steamCloud == null)
    {
      Console.WriteLine("Steam Cloud not initialized");
      return;
    }
    uint appidParsed = uint.Parse(appid);
    var localFiles = Steam.Cloud.SteamCloud.MapFilePaths(paths);
    Console.WriteLine("CloudUpload for {0}", appidParsed);
    var remoteCacheFile = new RemoteCache(this.session.SteamUser!.SteamID!.AccountID, appidParsed);
    var changeNumber = remoteCacheFile.GetChangeNumber();
    var changelist = await this.session.steamCloud.GetFilesChangelistAsync(appidParsed, changeNumber);
    if (changelist == null)
    {
      return;
    }
    if (changelist.current_change_number != changeNumber)
    {
      Console.WriteLine("Potential conflict, different change numbers detected");
      return;
    }
    Console.WriteLine("Uploading is not implemented yet");
  }
}
