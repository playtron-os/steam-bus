// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.
// Source: https://github.com/SteamRE/DepotDownloader/blob/master/DepotDownloader/Steam3Session.cs

using SteamBus.Auth;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Steam.Cloud;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;
using SteamKit2.Internal;
using Playtron.Plugin;
using Steam.Config;
using Steam.Content;
using SteamBus.DBus;

namespace Steam.Session;

public class SteamSession
{
  public const uint INVALID_APP_ID = uint.MaxValue;
  public bool IsLoggedOn { get; private set; }
  public bool IsPendingLogin { get; set; }
  public string PersonaName { get; private set; } = "";
  public string AvatarUrl { get; private set; } = "";

  public ReadOnlyCollection<uint> PackageIDs
  {
    get;
    private set;
  }
  public ConcurrentDictionary<uint, ulong> AppTokens { get; } = [];
  public ConcurrentDictionary<uint, ulong> PackageTokens { get; } = [];
  public ConcurrentDictionary<uint, byte[]> DepotKeys { get; } = [];
  public ConcurrentDictionary<(uint, string), TaskCompletionSource<SteamContent.CDNAuthToken>> CDNAuthTokens { get; } = [];
  public ConcurrentDictionary<uint, KeyValue> AppInfo { get; } = [];
  public ConcurrentDictionary<uint, ProviderItem> ProviderItemMap { get; } = [];
  public ConcurrentDictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> PackageInfo { get; } = [];
  public ConcurrentDictionary<string, byte[]> AppBetaPasswords { get; } = [];

  public SteamClient SteamClient;
  public SteamUser? SteamUser;
  public SteamContent? SteamContentRef;
  readonly SteamApps? steamApps;
  readonly SteamFriends? steamFriends;
  public Steam.Cloud.SteamCloud steamCloud;
  readonly SteamKit2.SteamCloud? steamCloudKit;
  //readonly PublishedFile steamPublishedFile;

  public CallbackManager Callbacks;

  // Keeps tracking whether we are waiting to reconnect, and if not, the reconnection won't happen after the delay
  bool waitingToRetry;

  bool bConnecting;
  bool bAborted;
  bool bExpectingDisconnectRemote;
  bool bDidDisconnect;
  bool bIsConnectionRecovery;
  int connectionBackoff;
  int seq; // more hack fixes
  bool isLoadingLibrary = true;
  AuthSession? authSession;
  QrAuthSession? qrAuthSession;
  public Action<string>? OnNewQrCode;
  public Action<ProviderItem[]>? OnLibraryUpdated;
  readonly CancellationTokenSource abortedToken = new();

  // input
  readonly SteamUser.LogOnDetails logonDetails;
  readonly IAuthenticator? authenticator;
  string? SteamGuardData;
  public bool RememberPassword = true;

  private DepotConfigStore depotConfigStore;
  public Action<(string appId, string version)>? OnAppNewVersionFound;
  public Action<string>? OnAuthError;
  public Action? OnAuthUpdated;
  public Action? InstalledAppsUpdated;

  private LoginUsersConfig loginUsersConfig;
  private UserCache userCache;
  private LibraryCache libraryCache;
  private AppInfoCache appInfoCache;
  private SteamConnectionConfig steamConnectionConfig;

  public Action? OnAvatarUpdated;

  public uint playingAppID { get; private set; }
  public bool playingBlocked { get; private set; }

  private bool isOnline;

  public SteamSession(SteamUser.LogOnDetails details, DepotConfigStore depotConfigStore, string? steamGuardData = null, IAuthenticator? authenticator = null)
  {
    details.ShouldRememberPassword = true;
    this.logonDetails = details;
    this.authenticator = authenticator;
    this.SteamGuardData = steamGuardData;
    this.depotConfigStore = depotConfigStore;
    depotConfigStore.steamSession = this;
    this.loginUsersConfig = new LoginUsersConfig(LoginUsersConfig.DefaultPath());
    this.userCache = new UserCache(UserCache.DefaultPath());
    this.libraryCache = new LibraryCache(LibraryCache.DefaultPath());
    this.appInfoCache = new AppInfoCache(AppInfoCache.DefaultPath());
    this.steamConnectionConfig = new SteamConnectionConfig(SteamConnectionConfig.CellIdDefaultPath(), SteamConnectionConfig.ServersBinDefaultPath());

    if (details.AccountID != 0)
    {
      AvatarUrl = userCache.GetKey(UserCache.AVATAR_KEY, details.AccountID) ?? "";
      PersonaName = userCache.GetKey(UserCache.PERSONA_NAME, details.AccountID) ?? "";

      PackageIDs = new ReadOnlyCollection<uint>(libraryCache.GetPackageIDs(details.AccountID));
      ProviderItemMap = new ConcurrentDictionary<uint, ProviderItem>(libraryCache.GetApps(details.AccountID).ToDictionary((x) => uint.Parse(x.id)));
    }
    else
    {
      PackageIDs = new ReadOnlyCollection<uint>([]);
    }

    var clientConfiguration = steamConnectionConfig.GetSteamClientConfig();
    this.SteamClient = new SteamClient(clientConfiguration);

    this.SteamUser = this.SteamClient.GetHandler<SteamUser>();
    this.steamApps = this.SteamClient.GetHandler<SteamApps>();
    this.steamFriends = this.SteamClient.GetHandler<SteamFriends>();
    this.steamCloudKit = this.SteamClient.GetHandler<SteamKit2.SteamCloud>();

    var steamUnifiedMessages = this.SteamClient.GetHandler<SteamUnifiedMessages>();
    if (steamUnifiedMessages == null)
    {
      Console.WriteLine("Failed to obtain unified messages handler");
      throw new ArgumentNullException();
    }
    this.steamCloud = new Steam.Cloud.SteamCloud(steamUnifiedMessages);
    //this.steamPublishedFile = steamUnifiedMessages.CreateService<PublishedFile>();
    this.SteamContentRef = this.SteamClient.GetHandler<SteamContent>();

    this.Callbacks = new CallbackManager(this.SteamClient);

    this.Callbacks.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
    this.Callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
    this.Callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLogIn);
    this.Callbacks.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);
    this.Callbacks.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);
    this.Callbacks.Subscribe<SteamUser.PlayingSessionStateCallback>(OnPlayingSessionStateCallback);
    this.Callbacks.Subscribe<SteamFriends.PersonaStateCallback>(OnPersonaState);
  }


  public delegate bool WaitCondition();

  private readonly object steamLock = new();

  private bool AuthenticatedUser()
  {
    return this.logonDetails.Username != null;
  }

  public async Task<bool> WaitUntilCallback(Action submitter, WaitCondition waiter)
  {
    while (!bAborted && !waiter())
    {
      lock (steamLock)
      {
        submitter();
      }

      var seq = this.seq;
      do
      {
        lock (steamLock)
        {
          Callbacks.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }
        await Task.Delay(1);
      } while (!bAborted && this.seq == seq && !waiter());

      await Task.Delay(1);
    }

    return bAborted;
  }


  public async Task<bool> WaitForCredentials()
  {
    if ((IsLoggedOn && !IsPendingLogin) || bAborted)
      return IsLoggedOn;

    await WaitUntilCallback(() => { }, () => IsLoggedOn && !IsPendingLogin);

    return IsLoggedOn;
  }

  public async Task WaitForLibrary()
  {
    if (IsPendingLogin)
      return;

    if (!isLoadingLibrary && PackageIDs?.Count == 0)
      return;

    while (!bAborted)
    {
      if (!isLoadingLibrary) break;
      await Task.Delay(1);
    }
  }


  public async Task TickCallbacks()
  {
    var token = abortedToken.Token;

    try
    {
      while (!token.IsCancellationRequested)
      {
        await this.Callbacks.RunWaitCallbackAsync(token);
      }
    }
    catch (OperationCanceledException)
    {
      //
    }
  }


  public async Task RequestAppInfo(uint appId, bool bForce = false)
  {
    if ((AppInfo.ContainsKey(appId) && !bForce) || bAborted)
      return;

    if (!bForce)
    {
      var cached = appInfoCache.GetCached(appId);
      if (cached != null)
      {
        AppInfo[appId] = cached;
        return;
      }
    }

    var appTokens = await steamApps?.PICSGetAccessTokens([appId], []);

    if (appTokens.AppTokensDenied.Contains(appId))
    {
      Console.WriteLine("Insufficient privileges to get access token for app {0}", appId);
    }

    foreach (var token_dict in appTokens.AppTokens)
    {
      this.AppTokens[token_dict.Key] = token_dict.Value;
    }

    var request = new SteamApps.PICSRequest(appId);

    if (AppTokens.TryGetValue(appId, out var token))
    {
      request.AccessToken = token;
    }

    var appInfoMultiple = await steamApps.PICSGetProductInfo([request], []);

    if (appInfoMultiple.Results != null)
    {
      foreach (var appInfo in appInfoMultiple.Results)
      {
        foreach (var app_value in appInfo.Apps)
        {
          var app = app_value.Value;

          Console.WriteLine("Got AppInfo for {0}", app.ID);
          AppInfo[app.ID] = app.KeyValues;
          ProviderItemMap[app.ID] = GetProviderItem(app.ID.ToString(), app.KeyValues);
          appInfoCache.Save(app.ID, app.KeyValues);
        }

        foreach (var app in appInfo.UnknownApps)
        {
          AppInfo.Remove(app, out _);
          ProviderItemMap.Remove(app, out _);
          appInfoCache.Save(app, null);
        }
      }

      if (SteamUser?.SteamID?.AccountID != null)
      {
        libraryCache.SetApps(SteamUser.SteamID.AccountID, ProviderItemMap.Values.ToList());
        libraryCache.Save();
      }
    }
  }


  public async Task RequestPackageInfo(IEnumerable<uint> packageIds, bool force = true)
  {
    if (!force && packageIds.Any((x) => !PackageInfo.ContainsKey(x)))
      return;

    var packages = packageIds.ToList();
    packages.RemoveAll(PackageInfo.ContainsKey);

    if (packages.Count == 0 || bAborted)
      return;

    var packageRequests = new List<SteamApps.PICSRequest>();

    foreach (var package in packages)
    {
      var request = new SteamApps.PICSRequest(package);

      if (PackageTokens.TryGetValue(package, out var token))
      {
        request.AccessToken = token;
      }

      packageRequests.Add(request);
    }

    var packageInfoMultiple = await steamApps.PICSGetProductInfo([], packageRequests);

    foreach (var packageInfo in packageInfoMultiple.Results)
    {
      foreach (var package_value in packageInfo.Packages)
      {
        var package = package_value.Value;
        PackageInfo[package.ID] = package;
      }

      foreach (var package in packageInfo.UnknownPackages)
      {
        PackageInfo[package] = null;
      }
    }
  }


  public async Task<bool> RequestFreeAppLicense(uint appId)
  {
    var resultInfo = await steamApps.RequestFreeLicense(appId);

    return resultInfo.GrantedApps.Contains(appId);
  }


  public async Task RequestDepotKey(uint depotId, uint appId = 0)
  {
    if (DepotKeys.ContainsKey(depotId) || bAborted)
      return;

    var depotKey = await steamApps.GetDepotDecryptionKey(depotId, appId);

    Console.WriteLine("Got depot key for {0} result: {1}", depotKey.DepotID, depotKey.Result);

    if (depotKey.Result != EResult.OK)
      return;

    DepotKeys[depotKey.DepotID] = depotKey.DepotKey;
  }


  public async Task<ulong> GetDepotManifestRequestCodeAsync(uint depotId, uint appId, ulong manifestId, string branch)
  {
    if (bAborted)
      return 0;

    var requestCode = await SteamContentRef.GetManifestRequestCode(depotId, appId, manifestId, branch);

    Console.WriteLine("Got manifest request code for {0} {1} result: {2}",
        depotId, manifestId,
        requestCode);

    return requestCode;
  }


  public async Task RequestCDNAuthToken(uint appid, uint depotid, Server server)
  {
    var cdnKey = (depotid, server.Host);
    var completion = new TaskCompletionSource<SteamContent.CDNAuthToken>();

    if (bAborted || !CDNAuthTokens.TryAdd(cdnKey, completion))
    {
      return;
    }

    DebugLog.WriteLine("Session", $"Requesting CDN auth token for {server.Host}");

    var cdnAuth = await this.SteamContentRef.GetCDNAuthToken(appid, depotid, server.Host);

    Console.WriteLine($"Got CDN auth token for {server.Host} result: {cdnAuth.Result} (expires {cdnAuth.Expiration})");

    if (cdnAuth.Result != EResult.OK)
    {
      return;
    }

    completion.TrySetResult(cdnAuth);
  }


  public async Task CheckAppBetaPassword(uint appid, string password)
  {
    var appPassword = await steamApps.CheckAppBetaPassword(appid, password);

    Console.WriteLine("Retrieved {0} beta keys with result: {1}", appPassword.BetaPasswords.Count, appPassword.Result);

    foreach (var entry in appPassword.BetaPasswords)
    {
      AppBetaPasswords[entry.Key] = entry.Value;
    }
  }


  /// Get details for the given user generated content
  public async Task<SteamKit2.SteamCloud.UGCDetailsCallback> GetUGCDetails(UGCHandle ugcHandle)
  {
    var callback = await steamCloudKit.RequestUGCDetails(ugcHandle);

    if (callback.Result == EResult.OK)
    {
      return callback;
    }
    else if (callback.Result == EResult.FileNotFound)
    {
      return null;
    }

    throw new Exception($"EResult {(int)callback.Result} ({callback.Result}) while retrieving UGC details for {ugcHandle}.");
  }


  private void ResetConnectionFlags()
  {
    Console.WriteLine("Resetting connection flags");

    bExpectingDisconnectRemote = false;
    bDidDisconnect = false;
  }


  public async Task Login()
  {
    Console.WriteLine("Connecting to Steam...");
    this.Connect();
    if (!await this.WaitForCredentials())
    {
      Console.WriteLine("Unable to get Steam credentials");
      return;
    }

    Console.WriteLine("Got credentials...");
    _ = Task.Run(this.TickCallbacks);
  }


  /// Returns the login details for the this session
  public SteamUser.LogOnDetails GetLogonDetails()
  {
    return this.logonDetails;
  }

  /// Returns the steam guard data for this session
  public string? GetSteamGuardData()
  {
    return this.SteamGuardData;
  }


  void Connect()
  {
    Console.WriteLine("Starting steam connection");

    waitingToRetry = false;
    bAborted = false;
    bConnecting = true;
    authSession = null;

    if (!bIsConnectionRecovery)
      connectionBackoff = 0;

    bIsConnectionRecovery = false;

    ResetConnectionFlags();
    this.SteamClient.Connect();
  }


  private void Abort(bool sendLogOff = true)
  {
    IsPendingLogin = false;
    Disconnect(sendLogOff);
  }


  public void Disconnect(bool sendLogOff = true)
  {
    if (sendLogOff)
    {
      SteamUser?.LogOff();
    }

    bAborted = true;
    bConnecting = false;

    if (!bExpectingDisconnectRemote)
      bIsConnectionRecovery = false;

    abortedToken.Cancel();
    SteamClient.Disconnect();

    // flush callbacks until our disconnected event
    while (!bDidDisconnect)
    {
      lock (steamLock)
      {
        Callbacks.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(100));
      }
    }

    OnAuthUpdated?.Invoke();
  }


  private void Reconnect()
  {
    waitingToRetry = false;
    bIsConnectionRecovery = true;
    bExpectingDisconnectRemote = true;
    SteamClient.Disconnect();
  }

  private async void OnConnected(SteamClient.ConnectedCallback connected)
  {
    Console.WriteLine("OnConnected: Done!");
    bConnecting = false;

    if (!bIsConnectionRecovery)
    {
      // Update our tracking so that we don't time out, even if we need to reconnect multiple times,
      // e.g. if the authentication phase takes a while and therefore multiple connections.
      connectionBackoff = 0;
    }

    bIsConnectionRecovery = false;

    if (!AuthenticatedUser())
    {
      Console.Write("No credentials specified. Initializing QR flow");
      try
      {
        qrAuthSession = await SteamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
        {
          DeviceFriendlyName = $"{Environment.MachineName} (SteamBus)",
        });
      }
      catch (Exception exception)
      {
        Console.Error.WriteLine($"Error starting QR auth session, err:{exception}");
        Abort(false);
        return;
      }

      qrAuthSession.ChallengeURLChanged = () =>
      {
        if (qrAuthSession != null)
        {
          OnNewQrCode?.Invoke(qrAuthSession.ChallengeURL);
        }
        else
        {
          Console.WriteLine("Challenge URL changed but session is null");
        }
      };

      if (qrAuthSession != null)
      {
        OnNewQrCode?.Invoke(qrAuthSession.ChallengeURL);
      }
    }
    else
    {
      Console.WriteLine("Logging '{0}' into Steam...", logonDetails.Username);
    }

    // If no existing session exists with a valid refresh token, create one.
    if (authSession is null)
    {
      if (logonDetails.Username != null && logonDetails.Password != null && logonDetails.AccessToken is null)
      {
        try
        {
          authSession = await SteamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
          {
            Username = logonDetails.Username,
            Password = logonDetails.Password,
            IsPersistentSession = this.RememberPassword,
            GuardData = this.SteamGuardData,
            // Set the user agent string
            DeviceFriendlyName = $"{Environment.MachineName} (SteamBus)",
            /// <see cref="UserConsoleAuthenticator"/> is the default authenticator implemention provided by SteamKit
            /// for ease of use which blocks the thread and asks for user input to enter the code.
            /// However, if you require special handling (e.g. you have the TOTP secret and can generate codes on the fly),
            /// you can implement your own <see cref="SteamKit2.Authentication.IAuthenticator"/>.
            Authenticator = this.authenticator,
          });
        }
        catch (TaskCanceledException)
        {
          return;
        }
        catch (AuthenticationException ex)
        {
          if (ex.Message.Contains("InvalidPassword"))
          {
            Console.Error.WriteLine($"Failed to authenticate with Steam: InvalidPassword", ex);
            OnAuthError?.Invoke(DbusErrors.InvalidPassword);
          }
          else if (ex.Message.Contains("AccountLoginDeniedThrottle"))
          {
            Console.Error.WriteLine($"Rate limit reached", ex);
            OnAuthError?.Invoke(DbusErrors.RateLimitExceeded);
          }
          else
          {
            Console.Error.WriteLine($"Failed to authenticate with Steam, AuthenticationException: {ex.Message}", ex);
            OnAuthError?.Invoke(DbusErrors.AuthenticationError);
          }

          Abort(false);
        }
        catch (Exception ex)
        {
          Console.Error.WriteLine($"Failed to authenticate with Steam when authSession is null: {ex.Message}", ex);
          OnAuthError?.Invoke(DbusErrors.AuthenticationError);
          Abort(false);
          return;
        }
      }
    }

    // If an auth session exists, wait for the result and update the access token
    // in the login details.
    if (qrAuthSession != null)
    {
      try
      {
        Console.WriteLine("Polling for QR result");
        var result = await qrAuthSession.PollingWaitForResultAsync(abortedToken.Token);
        Console.WriteLine($"Got QR result, AccountName:{result.AccountName}");
        logonDetails.Username = result.AccountName;
        logonDetails.AccessToken = result.RefreshToken;
        if (result.NewGuardData != null)
        {
          this.SteamGuardData = result.NewGuardData;
        }
        else
        {
          this.SteamGuardData = null;
        }
      }
      catch (TaskCanceledException)
      {
        // [TaskCanceledException] can be thrown from [PollingWaitForResultAsync] even when our token is not cancelled, probably something internal
        if (abortedToken.IsCancellationRequested)
        {
          Console.WriteLine($"Login failure, task cancelled");
          Abort(false);
        }
        else
        {
          Console.WriteLine("QR Code polling canceled, reconnect");
          Reconnect();
        }

        return;
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine("Failed to authenticate with Steam when qrAuthSession is not null: " + ex.Message, ex);
        OnAuthError?.Invoke(DbusErrors.AuthenticationError);
        Abort(false);
        return;
      }
      finally
      {
        if (qrAuthSession != null)
        {
          qrAuthSession.ChallengeURLChanged = null;
          qrAuthSession = null;
        }
      }
    }
    else if (authSession != null)
    {
      try
      {
        var result = await authSession.PollingWaitForResultAsync(abortedToken.Token);

        logonDetails.Username = result.AccountName;
        logonDetails.Password = null;
        logonDetails.AccessToken = result.RefreshToken;

        if (result.NewGuardData != null)
        {
          this.SteamGuardData = result.NewGuardData;
        }
        else
        {
          this.SteamGuardData = null;
        }
      }
      catch (TaskCanceledException)
      {
        return;
      }
      catch (Exception ex)
      {
        if (ex.Message.Contains("Waiting for 2fa code timed out"))
        {
          Console.Error.WriteLine("Waitiing for 2fa code timed out");
          OnAuthError?.Invoke(DbusErrors.TfaTimedOut);
        }
        else
        {
          Console.Error.WriteLine("Failed to authenticate with Steam when auth session is not null: " + ex.Message, ex);
          OnAuthError?.Invoke(DbusErrors.AuthenticationError);
        }

        Abort(false);
        return;
      }
      finally
      {
        authSession = null;
      }
    }

    try
    {
      SteamUser?.LogOn(logonDetails);
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine("Failed to authenticate with Steam when logging in: " + ex.Message, ex);
      OnAuthError?.Invoke(DbusErrors.AuthenticationError);
      Abort(false);
      return;
    }
  }

  public async Task OnOnline()
  {
    isOnline = true;

    if (IsPendingLogin)
    {
      if (bIsConnectionRecovery)
      {
        // If expecting to reconnect, just connect to steam client
        ResetConnectionFlags();
        SteamClient.Connect();
        return;
      }

      Console.WriteLine("Previous session exists, trying to re-login to steam");

      await Login();
      if (IsLoggedOn)
        OnAuthUpdated?.Invoke();
    }
  }

  public void OnOffline()
  {
    isOnline = false;
  }

  // Invoked when the steam client is disconnected
  private void OnDisconnected(SteamClient.DisconnectedCallback disconnected)
  {
    bDidDisconnect = true;

    Console.WriteLine(nameof(SteamSession), $"Disconnected: bIsConnectionRecovery = {bIsConnectionRecovery}, UserInitiated = {disconnected.UserInitiated}, bExpectingDisconnectRemote = {bExpectingDisconnectRemote}");

    // When recovering the connection, we want to reconnect even if the remote disconnects us
    if (!bIsConnectionRecovery && (disconnected.UserInitiated || bExpectingDisconnectRemote))
    {
      // Any operations outstanding need to be aborted
      bAborted = true;
    }
    else if (connectionBackoff >= 7)
    {
      Console.WriteLine("Could not connect to Steam after 7 tries");
      Abort(false);
    }
    else if (!bAborted)
    {
      connectionBackoff += 1;

      if (isOnline)
      {
        if (bConnecting)
        {
          Console.WriteLine($"Connection to Steam failed. Trying again (#{connectionBackoff})...");
        }
        else
        {
          Console.WriteLine($"Lost connection to Steam. Reconnecting (#{connectionBackoff})");
        }

        Thread.Sleep(3000);

        // Any connection related flags need to be reset here to match the state after Connect
        ResetConnectionFlags();
        SteamClient.Connect();
      }
      else
      {
        Console.WriteLine("Skipping reconnection for now because no internet connectivity found");
        IsPendingLogin = true;
      }
    }
  }


  // Invoked when the Steam client tries to log in
  private void OnLogIn(SteamUser.LoggedOnCallback loggedOn)
  {
    var isSteamGuard = loggedOn.Result == EResult.AccountLogonDenied;
    var is2FA = loggedOn.Result == EResult.AccountLoginDeniedNeedTwoFactor;
    var isAccessToken = this.RememberPassword && logonDetails.AccessToken != null &&
        loggedOn.Result is EResult.InvalidPassword
        or EResult.InvalidSignature
        or EResult.AccessDenied
        or EResult.Expired
        or EResult.Revoked;

    if (isSteamGuard || is2FA || isAccessToken)
    {
      bExpectingDisconnectRemote = true;
      Abort(false);

      if (!isAccessToken)
      {
        Console.WriteLine("This account is protected by Steam Guard.");
      }

      if (is2FA)
      {
        Console.Write("Two-factor code required");
      }
      else if (isAccessToken)
      {
        // TODO: Handle gracefully by falling back to password prompt?
        Console.WriteLine($"Access token was rejected ({loggedOn.Result}).");
        Abort(false);
        return;
      }
      else
      {
        Console.Write("Email two-factor code required");
      }

      Console.WriteLine("Retrying Steam3 connection...");
      Connect();

      return;
    }

    if (loggedOn.Result == EResult.TryAnotherCM || loggedOn.Result == EResult.AlreadyLoggedInElsewhere || loggedOn.Result == EResult.NoConnection)
    {
      Task.Run(async () =>
      {
        if (waitingToRetry) return;

        waitingToRetry = true;
        if (loggedOn.Result == EResult.AlreadyLoggedInElsewhere) await Task.Delay(10000);
        if (!waitingToRetry) return;

        Console.WriteLine($"Retrying Steam3 connection ({loggedOn.Result})...");

        Reconnect();
      });

      return;
    }

    if (loggedOn.Result == EResult.ServiceUnavailable)
    {
      Console.WriteLine("Unable to login to Steam3: {0}", loggedOn.Result);
      Abort(false);

      return;
    }

    if (loggedOn.Result != EResult.OK)
    {
      Console.WriteLine("Unable to login to Steam3: {0}", loggedOn.Result);
      Abort();

      return;
    }

    connectionBackoff = 0;
    SaveToken();
    steamConnectionConfig.SaveCellId(loggedOn.CellID);

    Console.WriteLine("OnLogIn: Done!");

    this.seq++;
    IsLoggedOn = true;
    IsPendingLogin = false;
  }

  public void SaveToken()
  {
    if (logonDetails?.Username != null && logonDetails?.AccessToken != null && SteamUser?.SteamID != null)
    {
      var localConfig = new LocalConfig(LocalConfig.DefaultPath());
      localConfig.SetRefreshToken(logonDetails.Username, logonDetails.AccessToken);
      localConfig.Save();

      var globalConfig = new GlobalConfig(GlobalConfig.DefaultPath());
      globalConfig.SetSteamUser(logonDetails.Username, SteamUser.SteamID.ConvertToUInt64().ToString());
      globalConfig.SetConnectCache(logonDetails.Username, logonDetails.AccessToken);
      globalConfig.Save();
    }
  }

  public bool IsAppOwned(uint appId) => ProviderItemMap.ContainsKey(appId);

  // Invoked on login to list the game/app licenses associated with the user.
  private async void OnLicenseList(SteamApps.LicenseListCallback licenseList)
  {
    try
    {
      bool firstCallback = AppInfo.Count == 0;
      if (licenseList.Result != EResult.OK)
      {
        Console.WriteLine("Unable to get license list: {0} ", licenseList.Result);
        return;
      }
      isLoadingLibrary = true;
      var installedAppIdsToVersion = depotConfigStore.GetAppIdToVersionBranchMap(true);

      Console.WriteLine("Got {0} licenses for account!", licenseList.LicenseList.Count);

      List<uint> packageIds = [];
      // Parse licenses and associate their access tokens
      foreach (var license in licenseList.LicenseList)
      {
        if ((license.LicenseFlags & ELicenseFlags.Expired) != 0)
          continue;

        packageIds.Add(license.PackageID);
        if (license.AccessToken > 0)
          PackageTokens.TryAdd(license.PackageID, license.AccessToken);
      }
      PackageIDs = new ReadOnlyCollection<uint>(packageIds);

      if (SteamUser?.SteamID?.AccountID != null)
      {
        libraryCache.SetPackageIDs(SteamUser.SteamID.AccountID, packageIds);
        libraryCache.Save();
      }

      ProviderItemMap.Clear();

      Console.WriteLine("Requesting info for {0} packages", packageIds.Count);
      PackageInfo.Clear();
      await RequestPackageInfo(packageIds);
      Console.WriteLine("Got packages");

      var requests = new List<SteamApps.PICSRequest>();
      var appids = new List<uint>();
      foreach (var package in PackageInfo.Values)
      {
        ulong token = PackageTokens.GetValueOrDefault(package.ID);
        foreach (var appid in package.KeyValues["appids"].Children)
        {
          var appidI = appid.AsUnsignedInteger();
          if (appids.Contains(appidI)) continue;
          var req = new SteamApps.PICSRequest(appidI, token);
          requests.Add(req);
          appids.Add(appidI);
        }
      }

      Console.WriteLine("Making requests for {0} apps", requests.Count);
      try
      {
        var result = await steamApps!.PICSGetProductInfo(requests, []);
        if (result == null)
        {
          // TODO: Handle error
          Console.WriteLine("Failed to get apps");
          return;
        }

        if (result.Complete)
        {
          if (result.Results == null || result.Results.Count == 0)
          {
            Console.WriteLine("No results retrieved");
            return;
          }

          foreach (var productInfo in result.Results)
          {
            foreach (var entry in productInfo.Apps)
            {
              AppInfo[entry.Key] = entry.Value.KeyValues;
              ProviderItemMap[entry.Key] = GetProviderItem(entry.Key.ToString(), entry.Value.KeyValues);
              appInfoCache.Save(entry.Key, entry.Value.KeyValues);

              if (installedAppIdsToVersion.TryGetValue(entry.Key.ToString(), out var item))
              {
                var (version, branch) = item;
                var newVersion = GetSteam3AppBuildNumber(entry.Key, branch);

                if (newVersion != 0 && version != newVersion.ToString())
                {
                  Console.WriteLine($"Found new version for appid:{entry.Key}, version:{newVersion}, installedVersion:{version}");

                  depotConfigStore.SetUpdatePending(entry.Key, newVersion.ToString());
                  depotConfigStore.Save(entry.Key);

                  OnAppNewVersionFound?.Invoke((entry.Key.ToString(), newVersion.ToString()));
                }
              }
            }
          }

          if (SteamUser?.SteamID?.AccountID != null)
          {
            libraryCache.SetApps(SteamUser.SteamID.AccountID, ProviderItemMap.Values.ToList());
            libraryCache.Save();
          }
        }
        else if (result.Failed)
        {
          Console.WriteLine("Some requests failed");
        }
      }
      catch (Exception exception)
      {
        Console.Error.WriteLine($"Error when getting product list for licenses, ex: {exception.Message}", exception);
      }
      Console.WriteLine("Obtained app info for {0} apps", AppInfo.Count);
      isLoadingLibrary = false;

      if (!firstCallback)
      {
        List<ProviderItem> updatedItems = new(appids.Count);
        foreach (var id in appids)
          if (ProviderItemMap.TryGetValue(id, out var providerItem))
            updatedItems.Add(providerItem);
        OnLibraryUpdated?.Invoke(updatedItems.ToArray());
      }

      await VerifyDownloadedApps();
      await ImportSteamClientApps();

      if (await depotConfigStore.VerifyAppsAreSized())
        InstalledAppsUpdated?.Invoke();
    }
    catch (TaskCanceledException)
    {
      Console.Error.WriteLine("Task cancelled when loading library");
    }
  }

  // Invoked shortly after login to provide account information
  private void OnAccountInfo(SteamUser.AccountInfoCallback callback)
  {
    Console.WriteLine($"Account persona name: {callback.PersonaName}");
    this.PersonaName = callback.PersonaName;

    if (SteamUser?.SteamID?.AccountID != null)
    {
      userCache.SetKey(UserCache.PERSONA_NAME, SteamUser.SteamID.AccountID, PersonaName);
      userCache.Save();
    }

    SaveLoginUsersConfig();

    // We need to explicitly make a request for our user to obtain avatar
    // I didn't find any other way
    if (steamFriends != null && SteamUser?.SteamID != null)
      steamFriends.RequestFriendInfo([SteamUser.SteamID]);
  }

  private void SaveLoginUsersConfig()
  {
    if (logonDetails?.Username != null && logonDetails?.AccessToken != null)
    {
      var sub = Jwt.GetSub(logonDetails.AccessToken);

      if (sub != null)
      {
        loginUsersConfig.SetUser(sub, logonDetails.Username, PersonaName);
        loginUsersConfig.Save();
      }
      else
      {
        Console.Error.WriteLine("Error parsing Sub out of access token");
      }
    }
  }

  private void OnPlayingSessionStateCallback(SteamUser.PlayingSessionStateCallback callback)
  {
    Console.WriteLine($"Updating Playing Session State, AppID:{callback.PlayingAppID}, Blocked:{callback.PlayingBlocked}");

    playingAppID = callback.PlayingAppID;
    playingBlocked = callback.PlayingBlocked;
  }

  private void OnPersonaState(SteamFriends.PersonaStateCallback callback)
  {
    if (callback.FriendID == SteamUser?.SteamID && callback.AvatarHash is not null)
    {
      var avatarStr = BitConverter.ToString(callback.AvatarHash).Replace("-", "").ToLowerInvariant();
      AvatarUrl = $"https://avatars.akamai.steamstatic.com/{avatarStr}_full.jpg";
      userCache.SetKey(UserCache.AVATAR_KEY, SteamUser.SteamID.AccountID, AvatarUrl);
      userCache.Save();
      OnAvatarUpdated?.Invoke();
    }
  }

  public List<ProviderItem> GetProviderItems()
  {
    List<ProviderItem> providerItems = new(ProviderItemMap.Count);
    foreach (var app in ProviderItemMap)
      providerItems.Add(app.Value);

    return providerItems;
  }

  public static ProviderItem GetProviderItem(string appId, KeyValue appKeyValues)
  {
    var app_type = AppType.Game;
    switch (appKeyValues["common"]["type"].Value?.ToLower())
    {
      case "game":
        app_type = AppType.Game;
        break;
      case "dlc":
        app_type = AppType.Dlc;
        break;
      case "tool":
        app_type = AppType.Tool;
        break;
      case "application":
        app_type = AppType.Application;
        break;
      case "music":
        app_type = AppType.Music;
        break;
      case "config":
        app_type = AppType.Config;
        break;
      case "demo":
        app_type = AppType.Demo;
        break;
      case "beta":
        app_type = AppType.Beta;
        break;
    }
    return new ProviderItem
    {
      id = appId,
      name = appKeyValues["common"]["name"].Value?.ToString() ?? "",
      provider = "Steam",
      app_type = (uint)app_type,
    };
  }

  public uint GetSteam3AppBuildNumber(uint appId, string branch)
  {
    if (appId == INVALID_APP_ID)
      return 0;

    var depots = GetSteam3AppSection(appId, EAppInfoSection.Depots);
    if (depots == null)
      return 0;

    var branches = depots["branches"];
    var node = branches[branch];

    if (node == KeyValue.Invalid)
      return 0;

    var buildid = node["buildid"];

    if (buildid == KeyValue.Invalid)
      return 0;

    return uint.Parse(buildid.Value!);
  }

  public bool GetSteam3AppRequiresInternetConnection(uint appId)
  {
    var common = GetSteam3AppSection(appId, EAppInfoSection.Common);
    return common?["steam_deck_compatibility"]?["configuration"]?["requires_internet_for_singleplayer"]?.AsBoolean() ?? false;
  }

  public string GetSteam3AppName(uint appId)
  {
    var common = GetSteam3AppSection(appId, EAppInfoSection.Common);
    return common?["name"].Value?.ToString() ?? "";
  }

  public List<uint> GetExtendedDLCs(uint appId)
  {
    var extended = GetSteam3AppSection(appId, EAppInfoSection.Extended);
    return extended?["listofdlc"]?.AsString()?.Split(",")?.Select(uint.Parse).ToList() ?? [];
  }

  public KeyValue? GetSteam3AppSection(uint appId, EAppInfoSection section)
  {
    KeyValue appinfo;

    if (!AppInfo.TryGetValue(appId, out var app) || app == null)
    {
      var cached = appInfoCache.GetCached(appId);
      if (cached == null) return null;

      appinfo = cached;
    }
    else
      appinfo = app;

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

  public void UpdateConfigFiles(bool wantsOfflineMode)
  {
    Console.WriteLine($"Updating steam config files with offline mode: {wantsOfflineMode}");

    SaveToken();

    if (logonDetails?.Username != null && logonDetails?.AccessToken != null)
    {
      var sub = Jwt.GetSub(logonDetails.AccessToken);

      if (sub != null)
        loginUsersConfig.UpdateConfigFiles(sub, logonDetails.AccountID.ToString(), wantsOfflineMode);
      else
        Console.Error.WriteLine("Error parsing Sub out of access token");
    }
  }

  /// <summary>
  /// Verifies the downloaded apps to make sure they are not missing depots
  /// </summary>
  /// <returns></returns>
  public async Task VerifyDownloadedApps()
  {
    if (DBusSteamClient.fetchingSteamClientData != null) await DBusSteamClient.fetchingSteamClientData.Task;
    Console.WriteLine("Verifying downloaded apps...");

    var downloader = new ContentDownloader(this, depotConfigStore);
    var installedAppOptions = depotConfigStore.GetInstalledAppOptions();
    var hasChange = false;

    foreach (var installedApp in installedAppOptions)
    {
      var appHasChange = await VerifyDownloadedApp(downloader, installedApp);
      hasChange |= appHasChange;
    }

    if (hasChange)
      InstalledAppsUpdated?.Invoke();

    Console.WriteLine("Verifed downloaded apps");
  }

  public async Task<bool> VerifyDownloadedApp(ContentDownloader downloader, InstallOptionsExtended installedApp)
  {
    if (DBusSteamClient.fetchingSteamClientData != null) await DBusSteamClient.fetchingSteamClientData.Task;
    if (isLoadingLibrary) await WaitForLibrary();

    try
    {
      var newestVersion = GetSteam3AppBuildNumber(installedApp.appId, installedApp.branch).ToString();
      if (newestVersion != installedApp.version)
      {
        if (!installedApp.isUpdatePending)
        {
          Console.WriteLine($"AppId:{installedApp.appId} is needs a version update, newestVersion:{newestVersion}, oldVersion:{installedApp.version}");
          depotConfigStore.SetUpdatePending(installedApp.appId, newestVersion);
          depotConfigStore.Save(installedApp.appId);
          return true;
        }

        return false;
      }

      var requiredDepots = (await downloader.GetAppRequiredDepots(installedApp.appId, new AppDownloadOptions(installedApp, installedApp.installDir), false, false))
        .Select((x) => (x.DepotId, x.ManifestId));
      var sharedDepotIds = depotConfigStore.GetSharedDepotIds(installedApp.appId);
      requiredDepots = [.. requiredDepots.ExceptBy(sharedDepotIds, (x) => x.DepotId)];

      // 0 depots required means the user doesn't own this app
      if (requiredDepots.Count() == 0)
        return false;

      var isMissingDepots = requiredDepots.Any((requiredDepot) => !installedApp.depotIds.Contains(requiredDepot));
      if (isMissingDepots)
      {
        if (!installedApp.isUpdatePending)
        {
          var missingDepots = requiredDepots.Where((requiredDepot) => !installedApp.depotIds.Contains(requiredDepot));
          Console.WriteLine($"AppId:{installedApp.appId} is missing depots! Missing Depots:{string.Join(",", missingDepots)}");
          depotConfigStore.SetUpdatePending(installedApp.appId, newestVersion);
          depotConfigStore.Save(installedApp.appId);
          return true;
        }

        return false;
      }

      var hasAdditionalDepots = installedApp.depotIds.Any((installedDepot) => !requiredDepots.Contains(installedDepot));
      if (hasAdditionalDepots)
      {
        if (!installedApp.isUpdatePending)
        {
          var additionalDepots = installedApp.depotIds.Where((installedDepot) => !requiredDepots.Contains(installedDepot));
          Console.WriteLine($"AppId:{installedApp.appId} has additional depots! Additional Depots:{string.Join(",", additionalDepots)}");
          depotConfigStore.SetUpdatePending(installedApp.appId, newestVersion);
          depotConfigStore.Save(installedApp.appId);
          return true;
        }

        return false;
      }

      if (installedApp.isUpdatePending)
      {
        Console.WriteLine($"AppId:{installedApp.appId} has all depots downloaded, no update needed");
        depotConfigStore.SetNotUpdatePending(installedApp.appId);
        depotConfigStore.Save(installedApp.appId);
        return true;
      }
    }
    catch (Exception)
    {
      // Skip verifying this app
    }

    return false;
  }

  public async Task ImportSteamClientApps()
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
      var userCompatConfig = new UserCompatConfig(UserCompatConfig.DefaultPath(GetLogonDetails().AccountID));
      foreach (var appId in importedAppIds)
        depotConfigStore.VerifyAppsOsConfig(globalConfig, userCompatConfig, uint.Parse(appId));
      globalConfig.Save();
      userCompatConfig.Save();

      InstalledAppsUpdated?.Invoke();
    }
  }

  public static async Task<ProviderItem?> GetProviderItemRequest(uint appId, bool force = false)
  {
    if (!force)
    {
      var appInfoCache = new AppInfoCache(AppInfoCache.DefaultPath());
      var cached = appInfoCache.GetCached(appId);

      if (cached != null)
        return GetProviderItem(appId.ToString(), cached);
    }

    var steamConnectionConfig = new SteamConnectionConfig(SteamConnectionConfig.CellIdDefaultPath(), SteamConnectionConfig.ServersBinDefaultPath());
    var steamClient = new SteamClient(steamConnectionConfig.GetSteamClientConfig());
    var steamUser = steamClient.GetHandler<SteamUser>();
    var steamApps = steamClient.GetHandler<SteamApps>();
    if (steamUser == null || steamApps == null) return null;

    var callbacks = new CallbackManager(steamClient);
    var loginTask = new TaskCompletionSource();

    callbacks.Subscribe<SteamClient.ConnectedCallback>((_) => steamUser.LogOnAnonymous());
    callbacks.Subscribe<SteamClient.DisconnectedCallback>((_) => loginTask.TrySetCanceled());
    callbacks.Subscribe<SteamUser.LoggedOnCallback>((_) => loginTask.TrySetResult());
    callbacks.Subscribe<SteamUser.LoggedOffCallback>((_) => loginTask.TrySetCanceled());

    var cts = new CancellationTokenSource();
    _ = Task.Run(async () =>
    {
      var token = cts.Token;

      try
      {
        while (!token.IsCancellationRequested)
          await callbacks.RunWaitCallbackAsync(token);
      }
      catch (OperationCanceledException)
      {
        //
      }
    });

    steamClient.Connect();

    await loginTask.Task;

    try
    {
      var request = new SteamApps.PICSRequest(appId);

      var appInfoMultiple = await steamApps.PICSGetProductInfo([request], []);

      if (appInfoMultiple.Results != null)
      {
        foreach (var appInfo in appInfoMultiple.Results)
        {
          foreach (var app_value in appInfo.Apps)
          {
            var app = app_value.Value;
            Console.WriteLine("Got AppInfo for {0}", app.ID);
            return GetProviderItem(app.ID.ToString(), app.KeyValues);
          }
        }
      }
    }
    catch (Exception)
    {
      throw;
    }
    finally
    {
      cts.Cancel();
      steamClient.Disconnect();
    }

    return null;
  }
}
