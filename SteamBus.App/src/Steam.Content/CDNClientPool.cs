// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.
// Source: https://github.com/SteamRE/DepotDownloader/blob/master/DepotDownloader/CDNClientPool.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.CDN;

namespace Steam.Content;

/// <summary>
/// CDNClientPool provides a pool of connections to CDN endpoints, requesting CDN tokens as needed
/// </summary>
class CDNClientPool
{
  private const int ServerEndpointMinimumSize = 8;

  private readonly SteamClient steamClient;
  private readonly uint appId;
  public Client CDNClient { get; }
  public Server? ProxyServer { get; private set; }

  private readonly ConcurrentStack<Server> activeConnectionPool = [];
  private readonly BlockingCollection<Server> availableServerEndpoints = [];

  private readonly AutoResetEvent populatePoolEvent = new(true);
  private readonly Task monitorTask;
  private readonly CancellationTokenSource shutdownToken = new();
  public CancellationTokenSource? ExhaustedToken { get; set; }

  private Action<(string appId, string error)>? onInstallFailed;

  public CDNClientPool(SteamClient steamClient, uint appId, Action<(string appId, string error)>? onInstallFailed = null)
  {
    this.steamClient = steamClient;
    this.appId = appId;
    this.onInstallFailed = onInstallFailed;
    CDNClient = new Client(steamClient);

    monitorTask = Task.Factory.StartNew(ConnectionPoolMonitorAsync).Unwrap();
  }

  public void Shutdown()
  {
    shutdownToken.Cancel();
    monitorTask.Wait();
  }

  private async Task<IReadOnlyCollection<Server>?> FetchBootstrapServerListAsync()
  {
    try
    {
      var steamContent = this.steamClient.GetHandler<SteamContent>();
      if (steamContent == null)
      {
        Console.WriteLine("Failed to get content handler from steam client");
        return null;
      }
      var cdnServers = await steamContent!.GetServersForSteamPipe();
      if (cdnServers != null)
      {
        return cdnServers;
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine("Failed to retrieve content server list: {0}", ex.Message);
    }

    return null;
  }

  private async Task ConnectionPoolMonitorAsync()
  {
    var didPopulate = false;

    while (!shutdownToken.IsCancellationRequested)
    {
      populatePoolEvent.WaitOne(TimeSpan.FromSeconds(1));

      // We want the Steam session so we can take the CellID from the session and pass it through to the ContentServer Directory Service
      if (availableServerEndpoints.Count < ServerEndpointMinimumSize && this.steamClient.IsConnected)
      {
        var servers = await FetchBootstrapServerListAsync().ConfigureAwait(false);

        if (servers == null || servers.Count == 0)
        {
          onInstallFailed?.Invoke((appId.ToString(), DbusErrors.ContentNotFound));
          ExhaustedToken?.Cancel();
          return;
        }

        ProxyServer = servers!.Where(x => x.UseAsProxy).FirstOrDefault();

        var weightedCdnServers = servers
            .Where(server =>
            {
              var isEligibleForApp = server.AllowedAppIds.Length == 0 || server.AllowedAppIds.Contains(appId);
              return isEligibleForApp && (server.Type == "SteamCache" || server.Type == "CDN");
            })
            .OrderBy(server => server.WeightedLoad);

        foreach (var server in weightedCdnServers)
        {
          for (var i = 0; i < server.NumEntries; i++)
          {
            availableServerEndpoints.Add(server);
          }
        }

        didPopulate = true;
      }
      else if (availableServerEndpoints.Count == 0 && !this.steamClient.IsConnected && didPopulate)
      {
        onInstallFailed?.Invoke((appId.ToString(), DbusErrors.ContentNotFound));
        ExhaustedToken?.Cancel();
        return;
      }
    }
  }

  private Server BuildConnection(CancellationToken token)
  {
    if (availableServerEndpoints.Count < ServerEndpointMinimumSize)
    {
      populatePoolEvent.Set();
    }

    return availableServerEndpoints.Take(token);
  }

  public Server GetConnection(CancellationToken token)
  {
    if (!activeConnectionPool.TryPop(out var connection))
    {
      connection = BuildConnection(token);
    }

    return connection;
  }

  public void ReturnConnection(Server server)
  {
    if (server == null) return;

    activeConnectionPool.Push(server);
  }

  public void ReturnBrokenConnection(Server server)
  {
    if (server == null) return;

    // Broken connections are not returned to the pool
  }
}
