using System;
using System.Text.Json;
using Tmds.DBus;
using SteamKit2;
using SteamKit2.Authentication;

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

[DBusInterface("com.playtron.SteamBus.DBusSteamClient")]
public interface IDBusSteamClient : IDBusObject
{
  Task<int> LoginAsync(string username, string password);
  Task<SteamClientProperties> GetAllAsync();

  Task<object> GetAsync(string prop);
  Task SetAsync(string prop, object val);
  //Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}

class DBusSteamClient : IDBusSteamClient
{
  public ObjectPath Path;
  private SteamClient steamClient;
  private CallbackManager manager;
  // TODO: Can we pass these to the callback without setting them here?
  private string? user;
  private string? pass;
  private bool isRunning = true;
  private bool shouldRememberPassword = false;
  private string? previouslyStoredGuardData = null; // For the sake of this sample, we do not persist guard data

  // Creates a new DBusSteamClient instance with the given DBus path
  public DBusSteamClient(ObjectPath path)
  {
    // DBus path to this Steam Client instance
    this.Path = path;
    // Create the Steam Client instance
    this.steamClient = new SteamClient();
    // Create the callback manager which will route callbacks to function calls
    this.manager = new CallbackManager(steamClient);

    // register a few callbacks we're interested in
    // these are registered upon creation to a callback manager, which will then route the callbacks
    // to the functions specified
    manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
    manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

    manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
    manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

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

  // Login using the given credentials
  public Task<int> LoginAsync(string username, string password)
  {
    Console.WriteLine($"Logging in for user: {username}");

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
      Authenticator = new UserConsoleAuthenticator(),
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

  public ObjectPath ObjectPath { get { return Path; } }
}

