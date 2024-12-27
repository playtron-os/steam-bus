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

class DBusSteamClient : IDBusSteamClient, IPlaytronPlugin, IAuthPasswordFlow, IAuthCryptography, IAuthTwoFactorFlow, IPluginLibraryProvider, ICloudSaveProvider, IAuthenticator
{
  // Path to the object on DBus (e.g. "/one/playtron/SteamBus/SteamClient0")
  public ObjectPath Path;
  // Unique login ID used to allow multiple active login sessions from the same account
  private uint? loginId;
  // Two factor code task used to login to Steam
  private TaskCompletionSource<string>? tfaCodeTask;
  // Steam session instance
  private SteamSession? session = null;

  private string authFile = "auth.json";
  private string cacheDir = "cache";
  private Dictionary<uint, SteamApps.LicenseListCallback.License> licenses = new Dictionary<uint, SteamApps.LicenseListCallback.License>();
  private Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> packages = new Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo>();
  private Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> apps = new Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo>();

  private PlaytronPluginProperties pluginInfo = new PlaytronPluginProperties();

  // Create an RSA keypair for secure secret sending
  private bool useEncryption = false;
  private RSA rsa = RSA.Create(2048);

  // Signal events
  public event Action<string>? OnPing;
  public event Action<ObjectPath>? OnClientConnected;
  public event Action<string>? OnLoggedIn;
  public event Action<string>? OnLoggedOut;
  public event Action<(string, double)>? OnInstallProgressed;
  public event Action<PropertyChanges>? OnPasswordPropsChanged;
  public event Action<(bool previousCodeWasIncorrect, string message)>? OnTwoFactorRequired;
  public event Action<(string email, bool previousCodeWasIncorrect, string message)>? OnEmailTwoFactorRequired;


  // Creates a new DBusSteamClient instance with the given DBus path
  public DBusSteamClient(ObjectPath path)
  {
    // DBus path to this Steam Client instance
    this.Path = path;

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
    return Encoding.Unicode.GetString(decrypted);
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

  Task<InstallOptionDescription[]> IPluginLibraryProvider.GetInstallOptionsAsync(string appId)
  {
    // TODO: Query the app for available versions, branches, languages, etc.
    var version = new InstallOption("version", "Version of the game to install");
    var branch = new InstallOption("branch", "Branch to install from", ["public"]);
    var language = new InstallOption("language", "Language of the game to install", ["english"]);
    var os = new InstallOption("os", "OS platform version of the game", ["windows", "macos", "linux"]);
    var arch = new InstallOption("architecture", "Architecture version of the game", ["32", "64"]);

    InstallOptionDescription[] options = [version.AsTuple(), branch.AsTuple(), language.AsTuple(), os.AsTuple(), arch.AsTuple()];

    return Task.FromResult<InstallOptionDescription[]>(options);
  }

  Task IPluginLibraryProvider.InstallAsync(string appId, string disk, InstallOptions options)
  {
    Console.WriteLine($"Installing app: {appId}");

    // Ensure that a Steam session exists
    if (this.session is null)
    {
      Console.WriteLine("No active Steam session found to install app");
      return Task.FromResult(1);
    }
    if (!this.session.IsLoggedOn)
    {
      Console.WriteLine("Not logged in to Steam to install app");
      return Task.FromResult(1);
    }

    // Convert the app id to a numerical id
    uint appIdNumber;
    try
    {
      appIdNumber = UInt32.Parse(appId);
    }
    catch (Exception exception)
    {
      Console.WriteLine($"Invalid app id '{appId}': {exception.ToString()}");
      return Task.FromResult(1);
    }

    // Configure the download options
    var downloadOptions = new AppDownloadOptions(options);

    // TODO: Determine the install path to use based on the block device passed

    // Create a content downloader for the given app
    var downloader = new ContentDownloader(this.session);

    // Start downloading the app
    try
    {
      // Run this in the background
      Task.Run(() => downloader.DownloadAppAsync(appIdNumber, downloadOptions, this.OnInstallProgressed));
    }
    catch (Exception exception)
    {
      Console.WriteLine($"Failed to start app download for '{appId}': {exception.ToString()}");
      return Task.FromResult(1);
    }

    return Task.FromResult(0);
  }

  // InstallProgressed Signal
  Task<IDisposable> IPluginLibraryProvider.WatchInstallProgressedAsync(Action<(string, double)> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnInstallProgressed), reply);
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

    // Look up any previously saved SteamGuard data for this user
    try
    {
      Console.WriteLine("Checking {0} for exisiting auth session", authFile);
      string authFileJson = File.ReadAllText(authFile);
      Dictionary<string, SteamAuthSession>? authSessions = JsonSerializer.Deserialize<Dictionary<string, SteamAuthSession>>(authFileJson);

      // If a session exists for this user, set the previously stored guard data from it
      if (authSessions != null && authSessions!.ContainsKey(username.ToLower()))
      {
        Console.WriteLine("Found saved auth session for user: {0}", username);
        // Get the session data
        var authSession = authSessions![username.ToLower()];
        //this.accountName = authSession.accountName;
        //this.previouslyStoredGuardData = authSession.steamGuard;
        //this.refreshToken = authSession.refreshToken;
        //this.accessToken = authSession.accessToken;

        login.Password = null;
        login.AccessToken = authSession.refreshToken;
        login.ShouldRememberPassword = true;
        steamGuardData = authSession.steamGuard;
      }
    }
    catch (Exception e)
    {
      Console.WriteLine("Failed to open auth.json for stored auth sessions: {0}", e);
    }

    // Initiate the connection
    Console.WriteLine("Initializing Steam Client connection");
    //this.steamClient.Connect();

    // Create a new Steam session using the given login details and the DBus interface
    // as an authenticator implementation.
    this.session = new SteamSession(login, steamGuardData, this);

    // Subscribe to client callbacks
    this.session.Callbacks.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
    this.session.Callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
    this.session.Callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
    this.session.Callbacks.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
    this.session.Callbacks.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);
    //this.session.callbacks.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);

    await this.session.Login();
  }


  // Log out of the given account
  Task IAuthPasswordFlow.LogoutAsync(string username)
  {
    // get the steamuser handler, which is used for logging on after successfully connecting
    this.session?.Disconnect();

    return Task.FromResult(0);
  }


  // Returns all properties of the DBusSteamClient
  Task<PasswordFlowProperties> IAuthPasswordFlow.GetAllAsync()
  {
    var properties = new PasswordFlowProperties();
    if (this.session != null)
    {
      var username = this.session!.GetLogonDetails().Username;
      properties.AuthenticatedUser = username is null ? "" : username;

      if (this.session!.IsLoggedOn)
      {
        properties.Status = 1;
      }
    }
    return Task.FromResult(properties);
  }

  // Return the value of the given property
  Task<object> IAuthPasswordFlow.GetAsync(string prop)
  {
    switch (prop)
    {
      case "AuthenticatedUser":
        var username = this.session?.GetLogonDetails().Username;
        object user = username is null ? "" : username!;
        return Task.FromResult(user);
      case "Status":
        var isLoggedOn = this.session?.IsLoggedOn;
        bool loggedIn = (bool)(isLoggedOn is null ? false : isLoggedOn!);
        object status = loggedIn ? 1 : 0;
        return Task.FromResult(status);
      default:
        throw new NotImplementedException($"Invalid property: {prop}");
    }
  }

  // Set the value for the given property
  Task IAuthPasswordFlow.SetAsync(string prop, object val)
  {
    return Task.FromResult(0);
  }


  // LoggedIn Signal
  Task<IDisposable> IAuthPasswordFlow.WatchLoggedInAsync(Action<string> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnLoggedIn), reply);
  }


  // LoggedOut Signal
  Task<IDisposable> IAuthPasswordFlow.WatchLoggedOutAsync(Action<string> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnLoggedOut), reply);
  }


  // Sets up sending signals when properties have changed
  Task<IDisposable> IAuthPasswordFlow.WatchPropertiesAsync(Action<PropertyChanges> handler)
  {
    return SignalWatcher.AddAsync(this, nameof(OnPasswordPropsChanged), handler);
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


    // Emit dbus signal when logged in successfully
    OnLoggedIn?.Invoke(authSession.accountName is null ? "" : authSession.accountName!);
    object user = authSession.accountName!;
    OnPasswordPropsChanged?.Invoke(new PropertyChanges([new KeyValuePair<string, object>("AuthenticatedUser", user)]));
  }

  void OnLoggedOff(SteamUser.LoggedOffCallback callback)
  {
    Console.WriteLine("Logged off of Steam: {0}", callback.Result);
  }


  // Invoked shortly after login to provide account information
  void OnAccountInfo(SteamUser.AccountInfoCallback callback)
  {
    Console.WriteLine($"Account persona name: {callback.PersonaName}");
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
    return Task.FromResult(true);
  }

  // --- Two-factor Implementation ---

  Task IAuthTwoFactorFlow.SendCodeAsync(string code)
  {
    Task task = Task.Run(() =>
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
    });

    return task;
  }


  Task<IDisposable> IAuthTwoFactorFlow.WatchTwoFactorRequiredAsync(Action<(bool previousCodeWasIncorrect, string message)> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnTwoFactorRequired), reply);
  }


  Task<IDisposable> IAuthTwoFactorFlow.WatchEmailTwoFactorRequiredAsync(Action<(string email, bool previousCodeWasIncorrect, string message)> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnEmailTwoFactorRequired), reply);
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
    uint appidParsed = uint.Parse(appid);
    Console.WriteLine("Getting paths for {0}", appid);
    var appInfo = this.apps[appidParsed];
    if (appInfo == null)
    {
      Console.WriteLine("Unable to load appInfo");
      return Task.FromResult<CloudPathObject[]>([]);
    }
    List<CloudPathObject> results = [];
    List<KeyValue> overrides = [];
    var ufs = appInfo.KeyValues["ufs"];

    if (ufs.Children.Count == 0)
    {
      Console.WriteLine("Steam Cloud is not configured for this game.");
      return Task.FromResult<CloudPathObject[]>([]);
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
    var defaultPath = RemoteCache.GetRemoteSavePath(this.session.SteamUser.SteamID.AccountID, appidParsed);
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
    if (this.session?.steamCloud == null)
    {
      Console.WriteLine("Steam Cloud not initialized");
      return;
    }
    uint appidParsed = uint.Parse(appid);
    ERemoteStoragePlatform platformToSync = ERemoteStoragePlatform.Windows; // Decide what platform we sync
    var localFiles = Steam.Cloud.SteamCloud.MapFilePaths(paths);

    Console.WriteLine("CloudDownload for {0}", appidParsed);
    var remoteCacheFile = new RemoteCache(this.session.SteamUser.SteamID.AccountID, appidParsed);
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
    if (this.session?.steamCloud == null)
    {
      Console.WriteLine("Steam Cloud not initialized");
      return;
    }
    uint appidParsed = uint.Parse(appid);
    var localFiles = Steam.Cloud.SteamCloud.MapFilePaths(paths);
    Console.WriteLine("CloudUpload for {0}", appidParsed);
    var remoteCacheFile = new RemoteCache(this.session.SteamUser.SteamID.AccountID, appidParsed);
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
