using System.Text.Json;
using Tmds.DBus;
using SteamKit2;
using SteamKit2.Authentication;
using Playtron.Plugin;
using System.Security.Cryptography;
using System.Text;

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
  Task<int> LoginAsync(string username, string password);
  Task<SteamClientProperties> GetAllAsync();

  Task<object> GetAsync(string prop);
  Task SetAsync(string prop, object val);
  //Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);

  // Signals functions must be prefixed with 'Watch'
  // Test signal
  Task<IDisposable> WatchPongAsync(Action<string> reply);
  Task<IDisposable> WatchConnectedAsync(Action<ObjectPath> reply);
  Task<IDisposable> WatchLoggedInAsync(Action<string> reply);

}

class DBusSteamClient : IDBusSteamClient, IAuthCryptography, IAuthenticator, IAuthTwoFactorFlow, IPluginLibraryProvider
{
  // Path to the object on DBus (e.g. "/one/playtron/SteamBus/SteamClient0")
  public ObjectPath Path;
  // Instance of the SteamKit2 steam client
  private SteamClient steamClient;
  // SteakKit2 callback manager for handling callbacks
  private CallbackManager manager;
  // TODO: Can we pass these to the callback without setting them here?
  private string? user;
  private string? pass;
  private string? tfaCode;
  private bool isRunning = true;
  private bool shouldRememberPassword = false;
  private string? previouslyStoredGuardData = null; // For the sake of this sample, we do not persist guard data

  // Create an RSA keypair for secure secret sending
  private bool useEncryption = false;
  private RSA rsa = RSA.Create(2048);
  //private RSAParameters rsaKeyInfo;

  // Signal events
  public event Action<string>? OnPing;
  public event Action<ObjectPath>? OnClientConnected;
  public event Action<string>? OnLoggedIn;
  public event Action<(bool previousCodeWasIncorrect, string message)>? OnTwoFactorRequired;
  public event Action<(string email, bool previousCodeWasIncorrect, string message)>? OnEmailTwoFactorRequired;

  // Creates a new DBusSteamClient instance with the given DBus path
  public DBusSteamClient(ObjectPath path)
  {
    // DBus path to this Steam Client instance
    this.Path = path;
    // Create the Steam Client instance
    this.steamClient = new SteamClient();
    // Create the callback manager which will route callbacks to function calls
    this.manager = new CallbackManager(steamClient);

    // Security
    if (!this.useEncryption)
    {
      Console.WriteLine("WARNING: Encryption not being used for secure communication");
    }
    //this.rsaKeyInfo = rsa.ExportParameters(false);

    // register a few callbacks we're interested in
    // these are registered upon creation to a callback manager, which will then route the callbacks
    // to the functions specified
    manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
    manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

    manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
    manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

    // Run some crazy Task
    _ = Task.Run(async () =>
    {
      // Do stuff
      Console.WriteLine("Wait 5 seconds");
      await Task.Delay(5000);
      Console.WriteLine("Stuff!");
      // Send a signal
      this.OnPing?.Invoke("Hello!");
    });

    // Run the callback manager
    // create our callback handling loop
    _ = Task.Run(() =>
    {
      while (this.isRunning)
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

  // TODO: Remove this test
  public Task<int> GreetAsync()
  {
    return Task.FromResult(1);
  }

  // Login using the given credentials
  // TODO: By default, the session bus allows any process running as the user to
  // monitor session bus traffic. We should consider how to prevent this.
  // Maybe send encrypted strings?
  public Task<int> LoginAsync(string username, string password)
  {
    Console.WriteLine($"Logging in for user: {username}");

    // Decrypt the password using our private key
    if (this.useEncryption)
    {
      password = this.Decrypt(password);
    }

    // Configure the user/pass for the client
    this.user = username;
    this.pass = password;

    // initiate the connection
    this.steamClient.Connect();

    return Task.FromResult(0);
  }

  // Invoked when connected to Steam
  async void OnConnected(SteamClient.ConnectedCallback callback)
  {
    Console.WriteLine("Connected to Steam! Logging in '{0}'...", this.user);
    OnClientConnected?.Invoke(Path);

    // Begin authenticating via credentials
    var authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
    {
      Username = this.user,
      Password = this.pass,
      IsPersistentSession = this.shouldRememberPassword,

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
      previouslyStoredGuardData = pollResponse.NewGuardData;
    }

    // get the steamuser handler, which is used for logging on after successfully connecting
    var steamUser = this.steamClient.GetHandler<SteamUser>();

    // Logon to Steam with the access token we have received
    // Note that we are using RefreshToken for logging on here
    steamUser?.LogOn(new SteamUser.LogOnDetails
    {
      Username = pollResponse.AccountName,
      AccessToken = pollResponse.RefreshToken,
      ShouldRememberPassword = this.shouldRememberPassword, // If you set IsPersistentSession to true, this also must be set to true for it to work correctly
    });

    // This is not required, but it is possible to parse the JWT access token to see the scope and expiration date.
    ParseJsonWebToken(pollResponse.AccessToken, nameof(pollResponse.AccessToken));
    ParseJsonWebToken(pollResponse.RefreshToken, nameof(pollResponse.RefreshToken));
  }

  void OnDisconnected(SteamClient.DisconnectedCallback callback)
  {
    Console.WriteLine("Disconnected from Steam");

    isRunning = false;
  }

  void OnLoggedOn(SteamUser.LoggedOnCallback callback)
  {
    if (callback.Result != EResult.OK)
    {
      Console.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);

      isRunning = false;
      return;
    }

    Console.WriteLine("Successfully logged on!");

    // Emit dbus signal when logged in successfully
    OnLoggedIn?.Invoke(this.user is null ? "" : this.user!);

    // at this point, we'd be able to perform actions on Steam
    // for this sample we'll just log off
    // get the steamuser handler, which is used for logging on after successfully connecting
    var steamUser = this.steamClient.GetHandler<SteamUser>();
    steamUser?.LogOff();
  }

  void OnLoggedOff(SteamUser.LoggedOffCallback callback)
  {
    Console.WriteLine("Logged off of Steam: {0}", callback.Result);
  }

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

  public Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler)
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

  // LoggedIn Signal
  public Task<IDisposable> WatchLoggedInAsync(Action<string> reply)
  {
    return SignalWatcher.AddAsync(this, nameof(OnLoggedIn), reply);
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

