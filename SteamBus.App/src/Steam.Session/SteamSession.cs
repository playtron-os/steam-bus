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

public class SteamSession : IDisposable
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

  private CallbackManager Callbacks;

  // Keeps tracking whether we are waiting to reconnect, and if not, the reconnection won't happen after the delay
  bool waitingToRetry;

  TaskCompletionSource? loggingInTask;

  public bool IsReconnecting => !bAborted && !IsLoggedOn && loggingInTask != null;

  bool bConnecting;
  bool bAborted;
  bool bExpectingDisconnectRemote;
  bool bDidDisconnect;
  bool bIsConnectionRecovery;
  bool bSuppressReconnect; // Suppresses OnDisconnected's delayed reconnect during controlled recovery
  bool bReconnectScheduled; // A delayed reconnect task is waiting to run
  // True once this session completed a logon, or was constructed from a saved
  // refresh token; reconnection then needs no user interaction and retries
  // indefinitely. Unlike checking logonDetails.AccessToken (which is assigned
  // mid-login before the logon completes), this stays false while a user is
  // still waiting at a login screen, so interactive logins keep their bounded
  // give-up behavior.
  bool bSessionEstablished;
  TaskCompletionSource? disconnectedTcs; // Signaled by OnDisconnected so callers can await disconnect
  readonly ReconnectPolicy reconnectPolicy = new();
  // Serializes reconnect initiation so two initiators (delayed backoff task,
  // OnLogIn retry, watchdog) can never stomp each other's in-flight handshake
  readonly object reconnectLock = new();
  // Cancelled only in Dispose(), unlike abortedToken which is cancelled by the
  // first Disconnect() and never renewed even when the session is revived
  readonly CancellationTokenSource disposedToken = new();
  // Interval between reconnect watchdog checks
  static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(60);
  // Wait before retrying a logon rejected with AlreadyLoggedInElsewhere
  static readonly TimeSpan AlreadyLoggedInRetryDelay = TimeSpan.FromSeconds(10);
  // Bound on how long OnOnline waits for a re-login to complete; reconnection
  // keeps retrying in the background after it elapses
  static readonly TimeSpan ReloginWaitTimeout = TimeSpan.FromSeconds(60);
  int seq; // more hack fixes
  bool isLoadingLibrary = true;
  bool loggingInWithQrCode = false;
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

  public uint cellId { get => steamConnectionConfig.cellId; }

  public Action? OnAvatarUpdated;

  public uint playingAppID { get; private set; }
  public bool playingBlocked { get; private set; }

  public bool isOnline;

  private Task? loginTask;
  private List<IDisposable> subscriptions = [];

  public SteamSession(SteamUser.LogOnDetails details, DepotConfigStore depotConfigStore, string? steamGuardData = null, IAuthenticator? authenticator = null)
  {
    details.ShouldRememberPassword = true;
    this.logonDetails = details;
    this.bSessionEstablished = details.AccessToken != null;
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

    subscriptions.AddRange([
      this.Callbacks.Subscribe<SteamClient.ConnectedCallback>(OnConnected),
      this.Callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected),
      this.Callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLogIn),
      this.Callbacks.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff),
    this.Callbacks.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList),
      this.Callbacks.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo),
      this.Callbacks.Subscribe<SteamUser.PlayingSessionStateCallback>(OnPlayingSessionStateCallback),
      this.Callbacks.Subscribe<SteamFriends.PersonaStateCallback>(OnPersonaState),
    ]);

    _ = Task.Run(RunReconnectWatchdog);
  }

  public void Dispose()
  {
    Abort(false);
    disposedToken.Cancel();

    foreach (var sub in subscriptions)
      sub.Dispose();
  }


  public delegate bool WaitCondition();

  private readonly object steamLock = new();

  private bool AuthenticatedUser()
  {
    return this.logonDetails.Username != null;
  }

  // With a timeout, the wait gives up once the deadline passes even if the
  // condition never became true; without one it only exits on the condition
  // or bAborted, which for an established session retrying indefinitely can
  // mean blocking for an entire Steam outage.
  public async Task<bool> WaitUntilCallback(Action submitter, WaitCondition waiter, TimeSpan? timeout = null)
  {
    var deadline = timeout == null ? DateTime.MaxValue : DateTime.UtcNow + timeout.Value;

    while (!bAborted && !waiter() && DateTime.UtcNow < deadline)
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
      } while (!bAborted && this.seq == seq && !waiter() && DateTime.UtcNow < deadline);

      await Task.Delay(1);
    }

    return bAborted;
  }

  public void SubscribeCallback<T>(Action<T> callback) where T : CallbackMsg
  {
    subscriptions.Add(Callbacks.Subscribe(callback));
  }

  public async Task<bool> WaitForReconnect(TimeSpan? timeout = null)
  {
    await WaitUntilCallback(() => { }, () => (IsLoggedOn && !IsPendingLogin) || bAborted, timeout);
    return IsLoggedOn;
  }

  public async Task<bool> WaitForCredentials(TimeSpan? timeout = null)
  {
    if ((IsLoggedOn && !IsPendingLogin) || bAborted)
      return IsLoggedOn;

    await WaitUntilCallback(() => { }, () => IsLoggedOn && !IsPendingLogin, timeout);

    return IsLoggedOn;
  }

  public async Task WaitForLibrary()
  {
    Console.WriteLine($"WaitForLibrary: IsPendingLogin={IsPendingLogin}, isLoadingLibrary={isLoadingLibrary}, PackageIDs count={PackageIDs?.Count}, bAborted={bAborted}");
    if (IsPendingLogin)
    {
      Console.WriteLine("WaitForLibrary: Returning early - IsPendingLogin=true");
      return;
    }

    if (!isLoadingLibrary && PackageIDs?.Count == 0)
    {
      Console.WriteLine("WaitForLibrary: Returning early - not loading and no packages");
      return;
    }

    Console.WriteLine("WaitForLibrary: Waiting for library to finish loading...");
    while (!bAborted)
    {
      if (!isLoadingLibrary) break;
      await Task.Delay(1);
    }
    Console.WriteLine($"WaitForLibrary: Done. bAborted={bAborted}, PackageIDs count={PackageIDs?.Count}, PackageInfo count={PackageInfo.Count}");
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
    else
    {
      // Try to find a package token for this app
      foreach (var package in PackageInfo.Values)
      {
        if (package == null) continue;
        var appids = package.KeyValues["appids"].Children;
        foreach (var appidKv in appids)
        {
          if (appidKv.AsUnsignedInteger() == appId)
          {
            ulong packageToken = PackageTokens.GetValueOrDefault(package.ID);
            if (packageToken > 0)
            {
              Console.WriteLine("Using package token {0} from package {1} for app {2}", packageToken, package.ID, appId);
              request.AccessToken = packageToken;
              break;
            }
          }
        }
        if (request.AccessToken > 0) break;
      }
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
    if (bAborted)
    {
      Console.WriteLine($"RequestPackageInfo: Early return - bAborted=true, PackageInfo count={PackageInfo.Count}");
      return;
    }

    if (!force && !packageIds.Any((x) => !PackageInfo.ContainsKey(x)))
    {
      Console.WriteLine($"RequestPackageInfo: Early return - all packages already known (force={force}, PackageInfo count={PackageInfo.Count})");
      return;
    }

    var packages = packageIds.ToList();
    var beforeRemove = packages.Count;
    packages.RemoveAll(PackageInfo.ContainsKey);

    if (packages.Count == 0)
    {
      Console.WriteLine($"RequestPackageInfo: Early return - 0 packages after filtering (started with {beforeRemove}, PackageInfo count={PackageInfo.Count})");
      return;
    }

    Console.WriteLine($"RequestPackageInfo: Requesting {packages.Count} packages (had {beforeRemove}, filtered {beforeRemove - packages.Count}, force={force}, bAborted={bAborted})");

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

    Console.WriteLine($"RequestPackageInfo: PICSGetProductInfo returned. Results count={packageInfoMultiple.Results?.Count ?? -1}, Complete={packageInfoMultiple.Complete}");

    int addedCount = 0;
    int unknownCount = 0;
    foreach (var packageInfo in packageInfoMultiple.Results)
    {
      foreach (var package_value in packageInfo.Packages)
      {
        var package = package_value.Value;
        PackageInfo[package.ID] = package;
        addedCount++;
      }

      foreach (var package in packageInfo.UnknownPackages)
      {
        PackageInfo[package] = null;
        unknownCount++;
      }
    }

    Console.WriteLine($"RequestPackageInfo: Done. Added={addedCount}, Unknown={unknownCount}, Total PackageInfo={PackageInfo.Count}");
  }


  public async Task<bool> RequestFreeAppLicense(uint appId)
  {
    Console.WriteLine($"RequestFreeAppLicense({appId}): Sending request. PackageIDs count={PackageIDs?.Count}, PackageInfo count={PackageInfo.Count}");
    var resultInfo = await steamApps.RequestFreeLicense(appId);
    Console.WriteLine($"RequestFreeAppLicense({appId}): Result: GrantedApps=[{string.Join(", ", resultInfo.GrantedApps)}], GrantedPackages=[{string.Join(", ", resultInfo.GrantedPackages)}]");

    if (resultInfo.GrantedApps.Contains(appId))
    {
      Console.WriteLine("Granted free license for app {0}, granted packages: {1}", appId, string.Join(", ", resultInfo.GrantedPackages));

      // Fetch package info for the newly granted packages to get the package token
      if (resultInfo.GrantedPackages.Count > 0)
      {
        await RequestPackageInfo(resultInfo.GrantedPackages, true);

        // Now request app info with the package token
        await RequestAppInfo(appId, true);

        // Update ProviderItemMap so IsAppOwned returns true
        if (AppInfo.TryGetValue(appId, out var appInfo))
        {
          ProviderItemMap[appId] = GetProviderItem(appId.ToString(), appInfo);
        }
      }

      return true;
    }

    return false;
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
    Console.WriteLine($"ResetConnectionFlags: bExpectingDisconnectRemote {bExpectingDisconnectRemote}->false, bDidDisconnect {bDidDisconnect}->false");

    bExpectingDisconnectRemote = false;
    bDidDisconnect = false;
  }


  // Parameterless overload kept so method-group callers (Task.Run(session.Login))
  // still compile
  public Task Login() => Login(null);

  // A bounded credentialsTimeout stops the wait, not the login itself: the
  // reconnect machinery keeps retrying in the background, which for an
  // established session can outlast any caller's patience during an outage.
  public async Task Login(TimeSpan? credentialsTimeout)
  {
    Console.WriteLine("Connecting to Steam...");
    this.Connect();
    OnAuthUpdated?.Invoke();

    if (!await this.WaitForCredentials(credentialsTimeout))
    {
      Console.WriteLine(credentialsTimeout == null
        ? "Unable to get Steam credentials"
        : $"Login: Not logged on after {credentialsTimeout.Value.TotalSeconds:F0}s, reconnection continues in the background");
      return;
    }

    Console.WriteLine("Got credentials...");

    if (loginTask == null)
      loginTask = Task.Run(this.TickCallbacks);
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

  // Waits for any in-progress login to finish. Since an established session
  // retries indefinitely while Steam is unreachable, callers can pass a
  // timeout to fail fast instead of blocking for the whole outage. Returns
  // false when the timeout elapsed with the login still in progress, so
  // callers can distinguish "login pending" from "login finished but failed".
  public async Task<bool> WaitLoggingInTask(TimeSpan? timeout = null)
  {
    var task = loggingInTask?.Task;
    if (task == null)
      return true;

    if (timeout == null)
    {
      await task;
      return true;
    }

    try
    {
      await task.WaitAsync(timeout.Value);
      return true;
    }
    catch (TimeoutException)
    {
      Console.WriteLine($"WaitLoggingInTask: Login still in progress after {timeout.Value.TotalSeconds:F0}s");
      return false;
    }
  }

  void FinishLoggingInTask()
  {
    loggingInTask?.SetResult();
    loggingInTask = null;
  }


  void Connect()
  {
    lock (reconnectLock)
    {
      Console.WriteLine($"Connect: called. bIsConnectionRecovery={bIsConnectionRecovery}, bAborted={bAborted}, bConnecting={bConnecting}, IsConnected={SteamClient.IsConnected}");

      waitingToRetry = false;
      bAborted = false;
      bConnecting = true;
      authSession = null;
      loggingInTask ??= new TaskCompletionSource();

      if (!bIsConnectionRecovery)
        reconnectPolicy.Reset();

      bIsConnectionRecovery = false;

      ResetConnectionFlags();
      this.SteamClient.Connect();
      Console.WriteLine("Connect: SteamClient.Connect() called");
    }
  }


  // Single entry point for background-initiated reconnect attempts (the
  // delayed backoff task, the OnLogIn retry and the watchdog). Atomically
  // claims the in-flight slot via bConnecting and performs the same flag
  // bookkeeping as Connect(), so two initiators can never stomp each other's
  // handshake — SteamKit surfaces a Connect() over a pending connection as a
  // user-initiated disconnect, which would permanently abort the session.
  private bool TryStartReconnect(string source)
  {
    lock (reconnectLock)
    {
      if (bAborted || bSuppressReconnect || bConnecting || SteamClient.IsConnected)
      {
        Console.WriteLine($"{source}: Skipping reconnect. bAborted={bAborted}, bSuppressReconnect={bSuppressReconnect}, bConnecting={bConnecting}, IsConnected={SteamClient.IsConnected}");
        return false;
      }

      bConnecting = true;
      waitingToRetry = false;
      // This attempt is an organic retry: if it fails, OnDisconnected must
      // escalate the backoff rather than loop on the short recovery delay
      bIsConnectionRecovery = false;
      loggingInTask ??= new TaskCompletionSource();
      ResetConnectionFlags();

      Console.WriteLine($"{source}: Starting reconnect attempt");
      try
      {
        SteamClient.Connect();
        return true;
      }
      catch (Exception exception)
      {
        Console.Error.WriteLine($"{source}: SteamClient.Connect() failed, err:{exception}");
        bConnecting = false;
        return false;
      }
    }
  }


  private void Abort(bool sendLogOff = true)
  {
    IsLoggedOn = false;
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
    FinishLoggingInTask();

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


  // Fetches a fresh CM server list from the Steam Directory Web API and
  // replaces the client's cached list. After Valve restarts its CM fleet
  // (weekly maintenance), every cached endpoint may be stale or marked bad,
  // so repeated connection failures trigger a re-fetch here. Failures are
  // non-fatal; the reconnect loop continues with the cached list.
  private async Task RefreshServerList()
  {
    try
    {
      Console.WriteLine("RefreshServerList: Fetching fresh CM server list from Steam Directory");
      var servers = await SteamDirectory.LoadAsync(SteamClient.Configuration);
      SteamClient.Configuration.ServerList.ReplaceList(servers);
      Console.WriteLine($"RefreshServerList: Loaded {servers.Count} CM servers");
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine($"RefreshServerList: Failed to refresh CM server list, continuing with cached list, err:{exception}");
    }
  }


  // Last-resort recovery loop: if the session should be connected but no
  // reconnect is in flight (e.g. a state-flag race dropped the retry chain),
  // kick one off. Runs until the session is disposed — deliberately not tied
  // to abortedToken, which the first Disconnect() cancels permanently even
  // when the session is later revived (e.g. the Steam Guard re-login path).
  private async Task RunReconnectWatchdog()
  {
    while (!disposedToken.IsCancellationRequested)
    {
      try
      {
        await Task.Delay(WatchdogInterval, disposedToken.Token);
      }
      catch (OperationCanceledException)
      {
        return;
      }

      try
      {
        if (bAborted || IsLoggedOn || bConnecting || bReconnectScheduled || bSuppressReconnect || waitingToRetry)
          continue;
        if (!isOnline || !bSessionEstablished || SteamClient.IsConnected)
          continue;

        Console.WriteLine("ReconnectWatchdog: Session is disconnected with no reconnect in flight, forcing a reconnect");
        TryStartReconnect("ReconnectWatchdog");
      }
      catch (Exception exception)
      {
        // The watchdog is the safety net; it must outlive any single failure
        Console.Error.WriteLine($"ReconnectWatchdog: Unexpected error, err:{exception}");
      }
    }
  }


  private void Reconnect()
  {
    Console.WriteLine($"Reconnect: called. waitingToRetry={waitingToRetry}, bIsConnectionRecovery={bIsConnectionRecovery}, IsConnected={SteamClient.IsConnected}");
    waitingToRetry = false;
    bIsConnectionRecovery = true;
    bExpectingDisconnectRemote = true;
    IsPendingLogin = true;
    loggingInTask ??= new TaskCompletionSource();
    SteamClient.Disconnect();
    Console.WriteLine("Reconnect: SteamClient.Disconnect() called");
  }

  private async void OnConnected(SteamClient.ConnectedCallback connected)
  {
    Console.WriteLine("OnConnected: Done!");
    bConnecting = false;
    bDidDisconnect = false;
    bSuppressReconnect = false;

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
        loggingInWithQrCode = true;
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
    Console.WriteLine($"OnOnline: IsPendingLogin={IsPendingLogin}, IsLoggedOn={IsLoggedOn}, bIsConnectionRecovery={bIsConnectionRecovery}, bAborted={bAborted}, bExpectingDisconnectRemote={bExpectingDisconnectRemote}, IsConnected={SteamClient.IsConnected}");

    if (IsPendingLogin)
    {
      loggingInTask ??= new TaskCompletionSource();
      OnAuthUpdated?.Invoke();

      if (bIsConnectionRecovery)
      {
        // If expecting to reconnect, disconnect the stale connection first
        // then establish a fresh one
        Console.WriteLine("OnOnline: Connection recovery path - will disconnect stale session and reconnect");
        bExpectingDisconnectRemote = true;
        bSuppressReconnect = true; // Prevent OnDisconnected from starting a second reconnect
        Console.WriteLine($"OnOnline: Set bExpectingDisconnectRemote=true, bSuppressReconnect=true. IsConnected={SteamClient.IsConnected}");
        if (SteamClient.IsConnected)
        {
          Console.WriteLine("OnOnline: Calling SteamClient.Disconnect() on stale connection");
          disconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          SteamClient.Disconnect();

          // Wait for the disconnect callback with a bounded timeout
          Console.WriteLine("OnOnline: Waiting for disconnect callback...");
          var completed = await Task.WhenAny(disconnectedTcs.Task, Task.Delay(TimeSpan.FromSeconds(1)));
          if (completed == disconnectedTcs.Task)
          {
            Console.WriteLine("OnOnline: Disconnect callback received");
          }
          else
          {
            Console.WriteLine("OnOnline: Timed out waiting for disconnect callback, proceeding anyway");
          }
          disconnectedTcs = null;
        }
        else
        {
          Console.WriteLine("OnOnline: SteamClient already disconnected, skipping Disconnect()");
        }

        // Now reset everything and connect fresh
        bExpectingDisconnectRemote = false;
        Connect();
        // Clear bSuppressReconnect *after* Connect() so that if the new
        // connection attempt itself fails (OnDisconnected fires without
        // OnConnected ever being reached), the normal reconnect-with-backoff
        // logic in OnDisconnected is allowed to run instead of being
        // suppressed forever.
        bSuppressReconnect = false;
        return;
      }

      Console.WriteLine("OnOnline: Previous session exists (not recovery), trying to re-login to steam");

      // Bounded wait so the OnOnline handler (and whatever awaits it in
      // playserve) is not blocked for a whole Steam outage while the session
      // keeps retrying in the background
      await Login(ReloginWaitTimeout);
      if (IsLoggedOn)
        OnAuthUpdated?.Invoke();
    }
    else
    {
      Console.WriteLine($"OnOnline: No pending login, skipping reconnect. IsLoggedOn={IsLoggedOn}, IsConnected={SteamClient.IsConnected}");
    }
  }

  public void OnOffline()
  {
    isOnline = false;
    Console.WriteLine($"OnOffline: IsLoggedOn={IsLoggedOn}, IsPendingLogin={IsPendingLogin}, IsConnected={SteamClient.IsConnected}");

    // If we are currently logged on, mark the session for reconnection
    // so that OnOnline() will re-establish a fresh connection when the
    // network returns. Without this, a stale SteamClient connection can
    // persist across sleep/wake cycles causing CDN server list fetches
    // to silently time out.
    if (IsLoggedOn)
    {
      Console.WriteLine("Network went offline while logged on, marking session for reconnection");
      IsLoggedOn = false;
      IsPendingLogin = true;
      bIsConnectionRecovery = true;
    }
  }

  // Invoked when the steam client is disconnected
  private void OnDisconnected(SteamClient.DisconnectedCallback disconnected)
  {
    bDidDisconnect = true;
    IsLoggedOn = false;

    // Capture flags before TrySetResult, which schedules the TCS continuation on
    // the thread pool. That continuation may call Connect() → OnConnected, which
    // clears bSuppressReconnect. Without capturing here we have a race where the
    // check below sees the already-cleared value and falls through to the abort path.
    var suppressReconnect = bSuppressReconnect;
    var isConnectionRecovery = bIsConnectionRecovery;
    var wasConnecting = bConnecting;

    // The attempt (if any) that produced this disconnect is over. Left
    // latched, every later reconnect initiator would treat the dead
    // connection as an attempt in flight and skip forever.
    bConnecting = false;

    Console.WriteLine($"OnDisconnected: bIsConnectionRecovery={isConnectionRecovery}, UserInitiated={disconnected.UserInitiated}, bExpectingDisconnectRemote={bExpectingDisconnectRemote}, bAborted={bAborted}, bSuppressReconnect={suppressReconnect}, bConnecting={wasConnecting}, isOnline={isOnline}, waitingToRetry={waitingToRetry}, reconnectAttempts={reconnectPolicy.Attempts}");

    // Signal any caller awaiting this disconnect
    disconnectedTcs?.TrySetResult();

    // If OnOnline recovery is driving the reconnect, skip all reconnect logic here
    if (suppressReconnect)
    {
      Console.WriteLine("OnDisconnected: bSuppressReconnect=true, skipping reconnect (OnOnline is handling it)");
      return;
    }

    // When recovering the connection, we want to reconnect even if the remote disconnects us
    if (!isConnectionRecovery && (disconnected.UserInitiated || bExpectingDisconnectRemote))
    {
      Console.WriteLine("OnDisconnected: User-initiated or expected disconnect (not recovery) - aborting operations");
      // Any operations outstanding need to be aborted
      bAborted = true;
    }
    else if (!bSessionEstablished && reconnectPolicy.HasExceededInteractiveAttempts)
    {
      // Only interactive logins give up: a user is waiting at the login
      // screen. Established sessions retry indefinitely below, because Steam
      // outages such as the weekly CM maintenance can last far longer than
      // any bounded retry window.
      Console.WriteLine($"OnDisconnected: Could not connect to Steam after {ReconnectPolicy.InteractiveMaxAttempts} attempts during interactive login");
      Abort(false);
      OnAuthError?.Invoke(DbusErrors.Timeout);
    }
    else if (!bAborted)
    {
      if (isOnline)
      {
        if (waitingToRetry)
        {
          // An OnLogIn retry task owns this cycle — the CM often drops the
          // socket right after rejecting a logon. Recording a second failure
          // here would double-escalate the backoff, and scheduling a second
          // reconnect would race the pending retry.
          Console.WriteLine("OnDisconnected: OnLogIn retry pending, deferring reconnection to it");
          loggingInTask ??= new TaskCompletionSource();
          OnAuthUpdated?.Invoke();
          return;
        }

        // A recovery reconnect keeps the short base delay; only organic
        // failures escalate the backoff
        var delay = isConnectionRecovery
          ? ReconnectPolicy.BaseDelay
          : reconnectPolicy.RecordFailureAndGetDelay(interactive: !bSessionEstablished);

        if (wasConnecting)
        {
          Console.WriteLine($"OnDisconnected: Connection to Steam failed. Trying again (#{reconnectPolicy.Attempts})...");
        }
        else
        {
          Console.WriteLine($"OnDisconnected: Lost connection to Steam. Reconnecting (#{reconnectPolicy.Attempts})");
        }

        loggingInTask ??= new TaskCompletionSource();
        OnAuthUpdated?.Invoke();

        bReconnectScheduled = true;
        _ = Task.Run(async () =>
        {
          try
          {
            Console.WriteLine($"OnDisconnected: Waiting {delay.TotalSeconds:F1}s before reconnect attempt...");
            try
            {
              await Task.Delay(delay, disposedToken.Token);
            }
            catch (OperationCanceledException)
            {
              Console.WriteLine("OnDisconnected: Reconnect cancelled (session disposed)");
              return;
            }

            if (bAborted)
            {
              Console.WriteLine("OnDisconnected: Reconnect cancelled (bAborted=true)");
              return;
            }

            if (reconnectPolicy.ShouldRefreshServerList())
              await RefreshServerList();

            // TryStartReconnect atomically skips if another path (e.g.
            // OnOnline recovery) re-established the connection while we
            // waited; connecting again would tear down the healthy connection
            TryStartReconnect("OnDisconnected");
          }
          catch (Exception exception)
          {
            Console.Error.WriteLine($"OnDisconnected: Delayed reconnect failed, err:{exception}");
          }
          finally
          {
            bReconnectScheduled = false;
          }
        });
      }
      else
      {
        Console.WriteLine("OnDisconnected: Skipping reconnection - no internet connectivity");
        IsPendingLogin = true;
      }
    }
    else
    {
      Console.WriteLine("OnDisconnected: bAborted=true, skipping reconnect logic");
    }

    if (bAborted)
      FinishLoggingInTask();
  }


  // Invoked when the Steam client tries to log in
  private void OnLogIn(SteamUser.LoggedOnCallback loggedOn)
  {
    var loggingInWithQrCode = this.loggingInWithQrCode;
    this.loggingInWithQrCode = false;

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

    // ServiceUnavailable means Steam itself is down (e.g. CM maintenance);
    // for an established session that is retryable like the others rather
    // than a login failure.
    var isRetryableLogOnResult = loggedOn.Result == EResult.TryAnotherCM
      || loggedOn.Result == EResult.AlreadyLoggedInElsewhere
      || loggedOn.Result == EResult.NoConnection
      || (loggedOn.Result == EResult.ServiceUnavailable && bSessionEstablished);

    if (isRetryableLogOnResult)
    {
      Task.Run(async () =>
      {
        if (waitingToRetry) return;

        waitingToRetry = true;

        TimeSpan delay;
        if (loggedOn.Result == EResult.AlreadyLoggedInElsewhere)
        {
          delay = AlreadyLoggedInRetryDelay;
        }
        else
        {
          delay = reconnectPolicy.RecordFailureAndGetDelay(interactive: !bSessionEstablished);
          if (!bSessionEstablished && reconnectPolicy.HasExceededInteractiveAttempts)
          {
            // Same bounded give-up as OnDisconnected: a user is waiting at
            // the login screen and must eventually see an error
            Console.WriteLine($"OnLogIn: Could not log in to Steam after {ReconnectPolicy.InteractiveMaxAttempts} attempts during interactive login ({loggedOn.Result})");
            Abort(false);
            OnAuthError?.Invoke(DbusErrors.Timeout);
            return;
          }
        }

        Console.WriteLine($"OnLogIn: Waiting {delay.TotalSeconds:F1}s before retrying ({loggedOn.Result}, attempt #{reconnectPolicy.Attempts})...");
        try
        {
          await Task.Delay(delay, disposedToken.Token);
        }
        catch (OperationCanceledException)
        {
          return;
        }

        // IsLoggedOn check: another reconnect path may have re-established
        // the session during the delay; tearing it down again would cause
        // periodic churn
        if (!waitingToRetry || bAborted || IsLoggedOn) return;

        Console.WriteLine($"Retrying Steam3 connection ({loggedOn.Result})...");

        // Reconnect() relies on the disconnect callback to drive the retry,
        // which never fires if the CM already dropped the socket while we
        // waited; connect directly in that case
        if (SteamClient.IsConnected)
          Reconnect();
        else
          TryStartReconnect("OnLogIn");
      });

      return;
    }

    if (loggedOn.Result == EResult.RateLimitExceeded)
      OnAuthError?.Invoke(DbusErrors.RateLimitExceeded);

    if (loggingInWithQrCode && loggedOn.Result != EResult.OK)
    {
      Console.WriteLine($"Reconnecting to steam client because of qr code login error: {loggedOn.Result}");
      Reconnect();
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

    bIsConnectionRecovery = false;
    bAborted = false;
    bSessionEstablished = true;
    reconnectPolicy.Reset();
    SaveToken();
    steamConnectionConfig.SaveCellId(loggedOn.CellID);

    Console.WriteLine($"OnLogIn: Done! Setting IsLoggedOn=true, IsPendingLogin=false. PackageIDs count={PackageIDs?.Count}, PackageInfo count={PackageInfo.Count}");

    this.seq++;
    IsLoggedOn = true;
    IsPendingLogin = false;
    FinishLoggingInTask();
  }

  private void OnLoggedOff(SteamUser.LoggedOffCallback loggedOff)
  {
    Console.WriteLine($"Steam log off received: {loggedOff.Result}");
    if (loggedOff.Result == EResult.Revoked) Abort(true);
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
              appInfoCache.Save(entry.Key, entry.Value.KeyValues);

              // Validate if we can install the game
              var validOsList = entry.Value.KeyValues["extended"]?["validoslist"]?.AsString() ?? entry.Value.KeyValues["common"]?["oslist"]?.AsString();
              if (!string.IsNullOrEmpty(validOsList) && !validOsList.Split(',').Any(os => os == "windows" || os == ContentDownloader.GetSteamOS())) continue;

              ProviderItemMap[entry.Key] = GetProviderItem(entry.Key.ToString(), entry.Value.KeyValues);

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
        Console.Error.WriteLine(exception.StackTrace);
      }
      Console.WriteLine("Obtained app info for {0} apps", AppInfo.Count);
      Console.WriteLine($"OnLicenseList: Library loading complete. PackageIDs={PackageIDs?.Count}, PackageInfo={PackageInfo.Count}, ProviderItemMap={ProviderItemMap.Count}");
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
      name = appKeyValues["common"]["name"]?.AsString() ?? "",
      provider = "Steam",
      app_type = (uint)app_type,
      release_date = GetSafeReleaseDate(appKeyValues["common"]["steam_release_date"]?.AsUnsignedLong()),
      release_state = appKeyValues["common"]["releasestate"]?.AsEnum<ReleaseState>() ?? ReleaseState.Released,
    };
  }

  /// <summary>
  /// Safely converts a Steam release date in seconds to milliseconds, avoiding overflow.
  /// </summary>
  private static ulong GetSafeReleaseDate(ulong? seconds)
  {
    if (seconds == null)
      return 0;
    // Max value for ulong before multiplying by 1000 would overflow
    const ulong maxSeconds = ulong.MaxValue / 1000;
    if (seconds > maxSeconds)
      return 0; // or ulong.MaxValue, or log a warning
    try
    {
      return checked(seconds.Value * 1000);
    }
    catch (OverflowException)
    {
      // Optionally log the overflow
      return 0;
    }
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
