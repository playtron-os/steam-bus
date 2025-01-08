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

namespace Steam.Session;

class SteamSession
{
  public bool IsLoggedOn { get; private set; }
  public string PersonaName { get; private set; } = "";
  public string AvatarUrl { get; private set; } = "";

  public ReadOnlyCollection<SteamApps.LicenseListCallback.License> Licenses
  {
    get;
    private set;
  }
  public Dictionary<uint, ulong> AppTokens { get; } = [];
  public Dictionary<uint, ulong> PackageTokens { get; } = [];
  public Dictionary<uint, byte[]> DepotKeys { get; } = [];
  public ConcurrentDictionary<(uint, string), TaskCompletionSource<SteamContent.CDNAuthToken>> CDNAuthTokens { get; } = [];
  public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> AppInfo { get; } = [];
  public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> PackageInfo { get; } = [];
  public Dictionary<string, byte[]> AppBetaPasswords { get; } = [];

  public SteamClient SteamClient;
  public SteamUser? SteamUser;
  public SteamContent? SteamContentRef;
  readonly SteamApps? steamApps;
  public Steam.Cloud.SteamCloud steamCloud;
  readonly SteamKit2.SteamCloud? steamCloudKit;
  //readonly PublishedFile steamPublishedFile;

  public CallbackManager Callbacks;

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
  readonly CancellationTokenSource abortedToken = new();

  // input
  readonly SteamUser.LogOnDetails logonDetails;
  readonly IAuthenticator? authenticator;
  string? SteamGuardData;
  public bool RememberPassword = true;


  public SteamSession(SteamUser.LogOnDetails details, string? steamGuardData = null, IAuthenticator? authenticator = null)
  {
    this.logonDetails = details;
    this.authenticator = authenticator;
    this.SteamGuardData = steamGuardData;

    var clientConfiguration = SteamConfiguration.Create(config =>
        config.WithConnectionTimeout(TimeSpan.FromSeconds(10))
    );

    this.SteamClient = new SteamClient(clientConfiguration);

    this.SteamUser = this.SteamClient.GetHandler<SteamUser>();
    this.steamApps = this.SteamClient.GetHandler<SteamApps>();
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
    if (IsLoggedOn || bAborted)
      return IsLoggedOn;

    await WaitUntilCallback(() => { }, () => IsLoggedOn);

    return IsLoggedOn;
  }

  public async Task WaitForLibrary()
  {
    if (!isLoadingLibrary || Licenses.Count == 0)
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

    foreach (var appInfo in appInfoMultiple.Results)
    {
      foreach (var app_value in appInfo.Apps)
      {
        var app = app_value.Value;

        Console.WriteLine("Got AppInfo for {0}", app.ID);
        AppInfo[app.ID] = app;
      }

      foreach (var app in appInfo.UnknownApps)
      {
        AppInfo[app] = null;
      }
    }
  }


  public async Task RequestPackageInfo(IEnumerable<uint> packageIds)
  {
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
    {
      Abort();
      return;
    }

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
    bExpectingDisconnectRemote = false;
    bDidDisconnect = false;
    bIsConnectionRecovery = false;
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
    Task.Run(this.TickCallbacks);
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
    bAborted = false;
    bConnecting = true;
    connectionBackoff = 0;
    authSession = null;

    ResetConnectionFlags();
    this.SteamClient.Connect();
  }


  private void Abort(bool sendLogOff = true)
  {
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
    bIsConnectionRecovery = false;
    abortedToken.Cancel();
    SteamClient.Disconnect();

    // flush callbacks until our disconnected event
    while (!bDidDisconnect)
    {
      Callbacks.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(100));
    }
  }


  private void Reconnect()
  {
    bIsConnectionRecovery = true;
    SteamClient.Disconnect();
  }


  private async void OnConnected(SteamClient.ConnectedCallback connected)
  {
    Console.WriteLine(" Done!");
    bConnecting = false;

    // Update our tracking so that we don't time out, even if we need to reconnect multiple times,
    // e.g. if the authentication phase takes a while and therefore multiple connections.
    connectionBackoff = 0;

    if (!AuthenticatedUser())
    {
      Console.Write("No credentials specified. Initializing QR flow");
      qrAuthSession = await SteamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
      {
        DeviceFriendlyName = $"{Environment.MachineName} (SteamBus)",
      });

      qrAuthSession.ChallengeURLChanged = () =>
      {
        OnNewQrCode?.Invoke(qrAuthSession.ChallengeURL);
      };
      OnNewQrCode?.Invoke(qrAuthSession.ChallengeURL);
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
        catch (Exception ex)
        {
          Console.Error.WriteLine("Failed to authenticate with Steam: " + ex.Message);
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
        Console.WriteLine("Got QR result");
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
        Console.WriteLine("Login failure, task cancelled");
        return;
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine("Failed to authenticate with Steam: " + ex.Message);
        Abort(false);
        return;
      }
      finally
      {
        qrAuthSession = null;
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
        Console.Error.WriteLine("Failed to authenticate with Steam: " + ex.Message);
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
      Console.Error.WriteLine("Failed to authenticate with Steam: " + ex.Message);
      Abort(false);
      return;
    }
  }


  // Invoked when the steam client is disconnected
  private void OnDisconnected(SteamClient.DisconnectedCallback disconnected)
  {
    bDidDisconnect = true;

    DebugLog.WriteLine(nameof(SteamSession), $"Disconnected: bIsConnectionRecovery = {bIsConnectionRecovery}, UserInitiated = {disconnected.UserInitiated}, bExpectingDisconnectRemote = {bExpectingDisconnectRemote}");

    // When recovering the connection, we want to reconnect even if the remote disconnects us
    if (!bIsConnectionRecovery && (disconnected.UserInitiated || bExpectingDisconnectRemote))
    {
      Console.WriteLine("Disconnected from Steam");

      // Any operations outstanding need to be aborted
      bAborted = true;
    }
    else if (connectionBackoff >= 10)
    {
      Console.WriteLine("Could not connect to Steam after 10 tries");
      Abort(false);
    }
    else if (!bAborted)
    {
      connectionBackoff += 1;

      if (bConnecting)
      {
        Console.WriteLine($"Connection to Steam failed. Trying again (#{connectionBackoff})...");
      }
      else
      {
        Console.WriteLine("Lost connection to Steam. Reconnecting");
      }

      Thread.Sleep(1000 * connectionBackoff);

      // Any connection related flags need to be reset here to match the state after Connect
      ResetConnectionFlags();
      SteamClient.Connect();
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

      Console.Write("Retrying Steam3 connection...");
      Connect();

      return;
    }

    if (loggedOn.Result == EResult.TryAnotherCM)
    {
      Console.Write("Retrying Steam3 connection (TryAnotherCM)...");

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

    Console.WriteLine(" Done!");

    this.seq++;
    IsLoggedOn = true;
  }


  // Invoked on login to list the game/app licenses associated with the user.
  private async void OnLicenseList(SteamApps.LicenseListCallback licenseList)
  {
    if (licenseList.Result != EResult.OK)
    {
      Console.WriteLine("Unable to get license list: {0} ", licenseList.Result);
      Abort();

      return;
    }

    isLoadingLibrary = true;
    Console.WriteLine("Got {0} licenses for account!", licenseList.LicenseList.Count);
    this.Licenses = licenseList.LicenseList;
    List<uint> packageIds = [];
    // Parse licenses and associate their access tokens
    foreach (var license in licenseList.LicenseList)
    {
      packageIds.Add(license.PackageID);
      if (license.AccessToken > 0)
      {
        PackageTokens.TryAdd(license.PackageID, license.AccessToken);
      }
    }
    Console.WriteLine("Requesting info for {0} packages", packageIds.Count);
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
          AppInfo[entry.Key] = entry.Value;
        }
      }
    }
    else if (result.Failed)
    {
      Console.WriteLine("Some requests failed");
    }
    Console.WriteLine("Obtained app info for {0} apps", AppInfo.Count);
    isLoadingLibrary = false;
  }

  // Invoked shortly after login to provide account information
  private void OnAccountInfo(SteamUser.AccountInfoCallback callback)
  {
    Console.WriteLine($"Account persona name: {callback.PersonaName}");
    this.PersonaName = callback.PersonaName;
  }
}
