using System.Text.Json;
using Tmds.DBus;
using SteamKit2;
using SteamKit2.Authentication;
using Playtron.Plugin;
using SteamBus.Auth;
using System.Security.Cryptography;
using System.Text;
using Xdg.Directories;

namespace SteamBus.DBus;

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

class DBusSteamClient : IDBusSteamClient, IAuthPasswordFlow, IAuthCryptography, IAuthTwoFactorFlow, IPluginLibraryProvider, IAuthenticator
{
  // Path to the object on DBus (e.g. "/one/playtron/SteamBus/SteamClient0")
  public ObjectPath Path;
  // Instance of the SteamKit2 steam client
  private SteamClient steamClient;
  // SteakKit2 callback manager for handling callbacks
  private CallbackManager manager;
  // Logged in status
  private bool loggedIn = false;
  // Unique login ID used to allow multiple active login sessions from the same account
  private uint? loginId;
  // Username used to login to Steam
  private string? user;
  // Password used to login to Steam
  private string? pass;
  // Two-factor code used to login to Steam
  private string? tfaCode;
  private bool shouldRememberPassword = true; // TODO: Make this configurable via dbus property
  // TODO: Just use SteamAuthSession for these properties
  private string? previouslyStoredGuardData = null; // For the sake of this sample, we do not persist guard data
  private string? accessToken = null;
  private string? refreshToken = null;
  private string? accountName = null;

  private string authFile = "auth.json";
  private List<SteamApps.LicenseListCallback.License> licenses = new List<SteamApps.LicenseListCallback.License>();

  // Create an RSA keypair for secure secret sending
  private bool useEncryption = false;
  private RSA rsa = RSA.Create(2048);

  // Signal events
  public event Action<string>? OnPing;
  public event Action<ObjectPath>? OnClientConnected;
  public event Action<string>? OnLoggedIn;
  public event Action<string>? OnLoggedOut;
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
      builder.WithConnectionTimeout(TimeSpan.FromSeconds(10));
    });

    // Create the Steam Client instance
    this.steamClient = new SteamClient(config);
    // Create the callback manager which will route callbacks to function calls
    this.manager = new CallbackManager(steamClient);

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

    // register a few callbacks we're interested in
    // these are registered upon creation to a callback manager, which will then route the callbacks
    // to the functions specified
    manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
    manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
    manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
    manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
    manager.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);
    manager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);
    //manager.Subscribe<SteamApps.PICSProductInfoCallback>(OnProductInfo);

    // Run the callback manager
    // create our callback handling loop
    _ = Task.Run(() =>
    {
      while (true)
      {
        // in order for the callbacks to get routed, they need to be handled by the manager
        manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
      }

    });

    Console.WriteLine("Callback handler is running");
  }


  // Decrypt the given base64 encoded string using our private key
  private string Decrypt(string base64EncodedString)
  {
    byte[] base64EncodedBytes = Convert.FromBase64String(base64EncodedString);
    byte[] decrypted = this.rsa.Decrypt(base64EncodedBytes, RSAEncryptionPadding.OaepSHA256);
    return Encoding.Unicode.GetString(decrypted);
  }

  // --- Password flow Implementation ---

  // Login using the given credentials. The password string should be encrypted
  // using the provided public key to prevent session bus eavesdropping.
  Task IAuthPasswordFlow.LoginAsync(string username, string password)
  {
    // TODO: prevent logging in if already logged in
    Console.WriteLine($"Logging in for user: {username}");

    // Decrypt the password using our private key
    if (this.useEncryption && password != "")
    {
      password = this.Decrypt(password);
    }

    // Configure the user/pass for the client
    this.user = username;
    this.pass = password != "" ? password : null;

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
        this.accountName = authSession.accountName;
        this.previouslyStoredGuardData = authSession.steamGuard;
        this.refreshToken = authSession.refreshToken;
        this.accessToken = authSession.accessToken;
      }
    }
    catch (Exception e)
    {
      Console.WriteLine("Failed to open auth.json for stored auth sessions: {0}", e);
    }

    // Initiate the connection
    Console.WriteLine("Initializing Steam Client connection");
    this.steamClient.Connect();

    return Task.FromResult(0);
  }


  // Log out of the given account
  Task IAuthPasswordFlow.LogoutAsync(string username)
  {
    // get the steamuser handler, which is used for logging on after successfully connecting
    var steamUser = this.steamClient.GetHandler<SteamUser>();
    steamUser?.LogOff();
    this.loggedIn = false;

    return Task.FromResult(0);
  }


  // Returns all properties of the DBusSteamClient
  Task<PasswordFlowProperties> IAuthPasswordFlow.GetAllAsync()
  {
    var properties = new PasswordFlowProperties();
    properties.AuthenticatedUser = this.user is null ? "" : this.user!;
    if (this.loggedIn)
    {
      properties.Status = 1;
    }
    return Task.FromResult(properties);
  }

  // Return the value of the given property
  Task<object> IAuthPasswordFlow.GetAsync(string prop)
  {
    switch (prop)
    {
      case "AuthenticatedUser":
        object user = this.user is null ? "" : this.user!;
        return Task.FromResult(user);
      case "Status":
        var loggedIn = this.loggedIn;
        object status = this.loggedIn ? 1 : 0;
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
  async void OnConnected(SteamClient.ConnectedCallback callback)
  {
    Console.WriteLine("Connected to Steam! Logging in '{0}'...", this.user);

    // Emit DBus signal to inform interested applications
    OnClientConnected?.Invoke(Path);

    // Create a new authentication session if one does not yet exist for this user.
    if (this.refreshToken == null)
    {
      // Begin authenticating via credentials
      var authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
      {
        Username = this.user,
        Password = this.pass,
        IsPersistentSession = this.shouldRememberPassword,

        // Set the user agent string
        DeviceFriendlyName = $"{Environment.MachineName} (SteamBus)",

        // See NewGuardData comment below
        GuardData = previouslyStoredGuardData,

        /// <see cref="UserConsoleAuthenticator"/> is the default authenticator implemention provided by SteamKit
        /// for ease of use which blocks the thread and asks for user input to enter the code.
        /// However, if you require special handling (e.g. you have the TOTP secret and can generate codes on the fly),
        /// you can implement your own <see cref="SteamKit2.Authentication.IAuthenticator"/>.
        Authenticator = this, // Use this class as the authenticator
      });

      // Starting polling Steam for authentication response
      var pollResponse = await authSession.PollingWaitForResultAsync();

      if (pollResponse.NewGuardData != null)
      {
        // When using certain two factor methods (such as email 2fa), guard data may be provided by Steam
        // for use in future authentication sessions to avoid triggering 2FA again (this works similarly to the old sentry file system).
        // Do note that this guard data is also a JWT token and has an expiration date.
        this.previouslyStoredGuardData = pollResponse.NewGuardData;
      }
      this.accountName = pollResponse.AccountName;
      this.accessToken = pollResponse.AccessToken;
      this.refreshToken = pollResponse.RefreshToken;
    }

    // get the steamuser handler, which is used for logging on after successfully connecting
    var steamUser = this.steamClient.GetHandler<SteamUser>();

    // Logon to Steam with the access token we have received
    // Note that we are using RefreshToken for logging on here
    steamUser?.LogOn(new SteamUser.LogOnDetails
    {
      Username = this.accountName,
      LoginID = this.loginId,
      AccessToken = this.refreshToken,
      ShouldRememberPassword = this.shouldRememberPassword, // If you set IsPersistentSession to true, this also must be set to true for it to work correctly
    });

    // This is not required, but it is possible to parse the JWT access token to see the scope and expiration date.
    if (this.accessToken != null && this.refreshToken != null)
    {
      ParseJsonWebToken(this.accessToken!, nameof(this.accessToken));
      ParseJsonWebToken(this.refreshToken!, nameof(this.refreshToken));
    }
  }


  // Invoked when the Steam client disconnects
  void OnDisconnected(SteamClient.DisconnectedCallback callback)
  {
    Console.WriteLine("Disconnected from Steam");
    this.loggedIn = false;

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

    if (this.user == null)
    {
      Console.WriteLine("Successfully logged in, but username was unset!");
      return;
    }

    Console.WriteLine("Successfully logged on!");

    // Update local state
    this.loggedIn = true;

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
    SteamAuthSession session = new SteamAuthSession();
    session.steamGuard = this.previouslyStoredGuardData;
    session.accountId = 0;
    session.accountName = this.accountName;
    session.accessToken = this.accessToken;
    session.refreshToken = this.refreshToken;
    var steamUser = this.steamClient.GetHandler<SteamUser>();
    if (steamUser != null && steamUser!.SteamID != null)
    {
      session.accountId = steamUser!.SteamID!.AccountID;
    }
    Console.WriteLine($"Found steam id: {steamUser?.SteamID}");

    // Store the updated session for this user
    authSessions[this.user!.ToLower()] = session;

    // Update the auth file with the updated session details
    using StreamWriter writer = new StreamWriter(authFile, false);
    string? authSessionsSerialized = JsonSerializer.Serialize(authSessions);
    writer.Write(authSessionsSerialized);

    Console.WriteLine($"Updated saved logins at '{authFile}'");

    // TODO: We need to update all the appropriate VDF files to allow the Steam
    // client to use this session. The refresh token is stored in 'local.vdf'
    // encrypted using AES, with the key being the sha256 hash of the lowercased username.
    // The SymmetricDecrypt function can help with decrypting these values.
    var key = SHA256.HashData(Encoding.UTF8.GetBytes(this.user!.ToLower()));


    // Emit dbus signal when logged in successfully
    OnLoggedIn?.Invoke(this.user is null ? "" : this.user!);
    object user = this.user!;
    OnPasswordPropsChanged?.Invoke(new PropertyChanges([new KeyValuePair<string, object>("AuthenticatedUser", user)]));

    // Do stuff with SteamApps
    var steamApps = this.steamClient.GetHandler<SteamApps>();

    // at this point, we'd be able to perform actions on Steam

  }

  void OnLoggedOff(SteamUser.LoggedOffCallback callback)
  {
    Console.WriteLine("Logged off of Steam: {0}", callback.Result);
    this.loggedIn = false;
  }


  // Invoked shortly after login to provide account information
  void OnAccountInfo(SteamUser.AccountInfoCallback callback)
  {
    Console.WriteLine($"Account persona name: {callback.PersonaName}");
  }


  // Invoked on login to list the game/app licenses associated with the user.
  async void OnLicenseList(SteamApps.LicenseListCallback callback)
  {
    Console.WriteLine("Licenses listed: {0}: {1}", callback.Result, callback.LicenseList);

    // Clear any old licenses
    this.licenses.Clear();

    // Build a list of requests to get app info for each owned app.
    var requests = new List<SteamApps.PICSRequest>();

    // Loop through each license to build a request and save the info
    foreach (var license in callback.LicenseList)
    {
      this.licenses.Append(license);
      Console.WriteLine("Found license: {0}", license.ToString());
      Console.WriteLine("  PackageID: {0}", license.PackageID);
      Console.WriteLine("  Token: {0}", license.AccessToken);
      Console.WriteLine("  OwnerAccountID: {0}", license.OwnerAccountID);
      Console.WriteLine("  LicenseType: {0}", license.LicenseType);
      Console.WriteLine("  MasterPackageID: {0}", license.MasterPackageID);
      Console.WriteLine("  LicenseFlags: {0}", license.LicenseFlags);
      var id = license.PackageID;
      var token = license.AccessToken;
      var req = new SteamApps.PICSRequest(id, token);
      requests.Add(req);
    }

    Console.WriteLine($"Requesting info for {requests.Count} number of packages");
    Console.WriteLine($"Requests: {requests.ToString()}");


    // TODO: this
    // Request app information for all owned apps
    var steamApps = this.steamClient.GetHandler<SteamApps>();
    if (steamApps == null)
    {
      Console.WriteLine("Failed to get SteamApps handle");
      return;
    }

    // Wait for the job to complete
    Console.WriteLine("Waiting for product info request");
    var result = await steamApps!.PICSGetProductInfo(new List<SteamApps.PICSRequest>(), requests, false);
    if (result == null)
    {
      Console.WriteLine("Failed to get result for fetching package info");
      return;
    }

    if (result.Complete)
    {
      if (result!.Results == null)
      {
        Console.WriteLine("No results were returned for fetching package info");
        return;
      }

      // Loop through each result
      foreach (SteamApps.PICSProductInfoCallback productInfo in result!.Results!)
      {
        // Loop through each package result
        foreach (KeyValuePair<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> entry in productInfo.Packages)
        {
          var pkgId = entry.Key;
          var pkgInfo = entry.Value;

          pkgInfo.KeyValues.SaveToFile($"/tmp/pkgs/{pkgId}.vdf", false);

          // Package KeyValues looks like this:
          /*
            "103387"
            {
                    "packageid"             "103387"
                    "billingtype"           "10"
                    "licensetype"           "1"
                    "status"                "0"
                    "extended"
                    {
                            "allowcrossregiontradingandgifting"             "false"
                    }
                    "appids"
                    {
                            "0"             "377160"
                    }
                    "depotids"
                    {
                            "0"             "377161"
                            "1"             "377162"
                            "2"             "377163"
                            "3"             "377164"
                            "4"             "377165"
                            "5"             "377166"
                            "6"             "377167"
                            "7"             "377168"
                            "8"             "393880"
                            "9"             "393881"
                            "10"            "393882"
                            "11"            "393883"
                            "12"            "393884"
                    }
                    "appitems"
                    {
                    }
            }
           */

          // Get the app ids associated with this package
          foreach (var value in pkgInfo.KeyValues["appids"].Children)
          {
            var appId = value.AsUnsignedInteger();
            //
          }

          // Get the depot ids associated with this package
          foreach (var value in pkgInfo.KeyValues["depotids"].Children)
          {
            var depotId = value.AsUnsignedInteger();
          }
        }
      }

      // ... do something with our product info
    }
    else if (result.Failed)
    {
      // the request partially completed, and then Steam encountered a remote failure. for async jobs with only a single result (such as
      // GetDepotDecryptionKey), this would normally throw an AsyncJobFailedException. but since Steam had given us a partial set of callbacks
      // we get to decide what to do with the data

      // keep in mind that if Steam immediately fails to provide any data, or times out while waiting for the first result, an
      // AsyncJobFailedException or TaskCanceledException will be thrown

      // the result set might not have our data, so we need to test to see if we have results for our request
      //SteamApps.PICSProductInfoCallback productInfo = resultSet.Results.FirstOrDefault(prodCallback => prodCallback.Apps.ContainsKey(appid));
      Console.WriteLine("Some results failed");

      //if (productInfo != null)
      //{
      //  // we were lucky and Steam gave us the info we requested before failing
      //}
      //else
      //{
      //  // bad luck
      //}
    }
    else
    {
      // the request partially completed, but then we timed out. essentially the same as the previous case, but Steam didn't explicitly fail.
      Console.WriteLine("Some other failures happened or timed out");

      // we still need to check our result set to see if we have our data
      //SteamApps.PICSProductInfoCallback productInfo = resultSet.Results.FirstOrDefault(prodCallback => prodCallback.Apps.ContainsKey(appid));

      //if (productInfo != null)
      //{
      //  // we were lucky and Steam gave us the info we requested before timing out
      //}
      //else
      //{
      //  // bad luck
      //}
    }

  }


  // Invoked when SteamApps.PICSGetProductInfo() returns a result
  //void OnProductInfo(SteamApps.PICSProductInfoCallback callback)
  //{
  //  Console.WriteLine($"Got response for product info: {callback.Packages.Count} {callback.Apps.Count}");
  //  foreach (KeyValuePair<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> entry in callback.Packages)
  //  {
  //    var pkgId = entry.Key;
  //    var pkgInfo = entry.Value;

  //    pkgInfo.KeyValues.SaveToFile($"/tmp/pkgs/{pkgId}.vdf", false);
  //    Console.WriteLine($"Info: {pkgInfo.KeyValues.ToString()}");
  //  }

  //  foreach (KeyValuePair<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> entry in callback.Apps)
  //  {
  //    var appId = entry.Key;
  //    var appInfo = entry.Value;

  //    Console.WriteLine($"Info: {appInfo.KeyValues.ToString()}");
  //  }

  //}


  // This is simply showing how to parse JWT, this is not required to login to Steam
  void ParseJsonWebToken(string token, string name)
  {
    // You can use a JWT library to do the parsing for you
    var tokenComponents = token.Split('.');

    // Fix up base64url to normal base64
    var base64 = tokenComponents[1].Replace('-', '+').Replace('_', '/');

    if (base64.Length % 4 != 0)
    {
      base64 += new string('=', 4 - base64.Length % 4);
    }

    var payloadBytes = Convert.FromBase64String(base64);

    // Payload can be parsed as JSON, and then fields such expiration date, scope, etc can be accessed
    var payload = JsonDocument.Parse(payloadBytes);

    // For brevity we will simply output formatted json to console
    var formatted = JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
      WriteIndented = true,
    });
    Console.WriteLine($"{name}: {formatted}");
    Console.WriteLine();
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


  // --- IAuthenticator implementation ---

  /// <summary>
  /// This method is called when the account being logged into requires 2-factor authentication using the authenticator app.
  /// </summary>
  /// <param name="previousCodeWasIncorrect">True when previously provided code was incorrect.</param>
  /// <returns>The 2-factor auth code used to login. This is the code that can be received from the authenticator app.</returns>
  async Task<string> IAuthenticator.GetDeviceCodeAsync(bool previousCodeWasIncorrect)
  {
    // Clear any old 2FA code
    this.tfaCode = "";

    // Emit a signal that a 2-factor code is required using the authenticator app
    OnTwoFactorRequired?.Invoke((previousCodeWasIncorrect, "Enter the two-factor code from the Steam authenticator app."));

    // Wait for a UI to call SendTwoFactor() with the code
    Console.WriteLine("Waiting for application to send two-factor code");
    // TODO: Add timeout and better signaling to determine if 2FA code was sent
    string code = "";
    while (code == "")
    {
      await Task.Delay(500);
      code = this.tfaCode is null ? "" : this.tfaCode!;
    }
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
    // Clear any old 2FA code
    this.tfaCode = "";

    // Emit a signal that a 2-factor code is required using Steam Guard email authentication
    OnEmailTwoFactorRequired?.Invoke((email, previousCodeWasIncorrect, $"Enter the two-factor code sent to {email}"));

    // Wait for a UI to call SendTwoFactor() with the code
    Console.WriteLine("Waiting for application to send two-factor code");
    // TODO: Add timeout and better signaling to determine if 2FA code was sent
    string code = "";
    while (code == "")
    {
      await Task.Delay(500);
      code = this.tfaCode is null ? "" : this.tfaCode!;
    }
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
      Console.WriteLine($"Got 2FA code: {code}");
      this.tfaCode = code;
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
}

