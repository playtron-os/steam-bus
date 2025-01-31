using System.Reflection.PortableExecutable;
using Playtron.Plugin;
using SteamKit2;
using SteamKit2.Internal;

namespace Steam.Cloud;

// For full download complience it may be a good idea to implement transfer reports.
// These are handled by CCloud_ExternalStorageTransferReport_Notification
// and don't provide any data back to the client.

// Common interface betwen different file types that eventually move to RemoteCacheFile
public interface IRemoteFile
{
  public string GetRemotePath();
  public string Sha1();
  public ulong UpdateTime { get; }
}

public class SteamCloud(SteamUnifiedMessages steamUnifiedMessages)
{
  // Handles requests to actual cloud
  private SteamKit2.Internal.Cloud unifiedCloud = steamUnifiedMessages.CreateService<SteamKit2.Internal.Cloud>();
  // Handles notifications to and from the api
  private SteamKit2.Internal.CloudClient unifiedClient = steamUnifiedMessages.CreateService<SteamKit2.Internal.CloudClient>();

  /// <summary>
  /// Gets changelist based on the synced save state
  /// </summary>
  /// <param name="appid">Application id</param>
  /// <param name="syncedChangeNumber">Synchronized change number</param>
  /// <returns>A <see cref="CCloud_GetAppFileChangelist_Response"/> payload or null if failed</returns>
  public async Task<CCloud_GetAppFileChangelist_Response?> GetFilesChangelistAsync(uint appid, ulong? syncedChangeNumber)
  {
    CCloud_GetAppFileChangelist_Request request = new() { appid = appid };
    if (syncedChangeNumber.HasValue)
    {
      request.synced_change_number = syncedChangeNumber.Value;
    }
    var response = await unifiedCloud.GetAppFileChangelist(request);
    if (response.Result != EResult.OK)
    {
      Console.WriteLine("Failed to get changelist for {0}, {1}", request.appid, response.Result);
      return null;
    }
    return response.Body;
  }

  /// <summary>
  /// Gets information needed to download a file
  /// </summary>
  /// <param name="appid">Application id</param>
  /// <param name="filename">Path known to the api</param>
  /// <returns>A <see cref="CCloud_ClientFileDownload_Response"/> payload or null if failed</returns>
  public async Task<CCloud_ClientFileDownload_Response?> DownloadFileAsync(uint appid, string filename)
  {
    // It is possible to force proxification and particular realm, not useful for us I guess.
    var request = new CCloud_ClientFileDownload_Request { appid = appid, filename = filename };
    var response = await unifiedCloud.ClientFileDownload(request);
    if (response.Result != EResult.OK)
    {
      Console.WriteLine("Failed to get data for file download of {0} in {1}, {2}", filename, appid, response.Result);
      return null;
    }
    return response.Body;
  }

  public async Task<CCloud_BeginAppUploadBatch_Response?> BeginAppUploadBatch(uint appid, string[] filesToUpload, string[] filesToDelete)
  {
    // Set all fields here
    CCloud_BeginAppUploadBatch_Request request = new() { appid = appid };
    request.files_to_upload.AddRange(filesToUpload);
    request.files_to_delete.AddRange(filesToDelete);

    var response = await unifiedCloud.BeginAppUploadBatch(request);
    if (response.Result != EResult.OK)
    {
      Console.WriteLine("Failed to begin upload batch for {0}: {1}", appid, response.Result);
      return null;
    }
    return response.Body;
  }


  public async Task<CCloud_ClientBeginFileUpload_Response?> ClientBeginFileUpload(uint appid, string filename, byte[] sha, uint file_size, uint raw_file_size, ulong time_stamp, ERemoteStoragePlatform platformsToSync, ulong upload_batch_id)
  {
    // Unsure what's the difference between raw_file_size and file_size, maybe related to encryption? We don't use encryption so it's fine, right???
    CCloud_ClientBeginFileUpload_Request request = new()
    {
      appid = appid,
      filename = filename,
      file_sha = sha,
      file_size = file_size,
      raw_file_size = raw_file_size,
      platforms_to_sync = (uint)platformsToSync,
      upload_batch_id = upload_batch_id,
      time_stamp = time_stamp
    };
    var response = await unifiedCloud.ClientBeginFileUpload(request);
    if (response.Result != EResult.OK)
    {
      Console.WriteLine("Failed to begin upload for {0} of {1}: {2}", filename, appid, response.Result);
      return null;
    }
    return response.Body;
  }

  public async Task<CCloud_ClientCommitFileUpload_Response?> CientCommitFileUpload(uint appid, bool transfer_succeeded, string filename, byte[] file_sha)
  {
    CCloud_ClientCommitFileUpload_Request request = new()
    {
      appid = appid,
      filename = filename,
      transfer_succeeded = transfer_succeeded,
      file_sha = file_sha
    };
    var response = await unifiedCloud.ClientCommitFileUpload(request);
    if (response.Result != EResult.OK)
    {
      Console.WriteLine("Failed to commit upload for {0} of {1}: {2}", filename, appid, response.Result);
      return null;
    }
    return response.Body;
  }

  public async Task<CCloud_CompleteAppUploadBatch_Response?> CompleteAppUploadBatch(uint appid, ulong batch_id, uint batch_eresult)
  {
    CCloud_CompleteAppUploadBatch_Request request = new()
    {
      appid = appid,
      batch_id = batch_id,
      batch_eresult = batch_eresult,
    };
    var response = await unifiedCloud.CompleteAppUploadBatchBlocking(request);
    if (response.Result != EResult.OK)
    {
      Console.WriteLine("Failed to complete upload for {0}: {1}", appid, response.Result);
      return null;
    }
    return response.Body;
  }

  public async Task<CCloud_ClientDeleteFile_Response?> DeleteFileAsync(uint appid, string file, ulong batch_id)
  {
    CCloud_ClientDeleteFile_Request request = new()
    {
      appid = appid,
      filename = file,
      upload_batch_id = batch_id
    };
    var response = await unifiedCloud.ClientDeleteFile(request);
    if (response.Result != EResult.OK)
    {
      Console.WriteLine("Failed to delete file for {0}: {1}", appid, file);
      return null;
    }
    return response.Body;
  }

  public async Task<CCloud_AppLaunchIntent_Response?> SendLaunchIntent(uint appid, ulong client_id)
  {
    CCloud_AppLaunchIntent_Request request = new() { appid = appid, client_id = client_id, machine_name = Environment.MachineName };

    var response = await unifiedCloud.SignalAppLaunchIntent(request);
    if (response.Result != EResult.OK)
    {
      Console.WriteLine("Failed to send launch intent for {0}: {1}", appid, response.Result);
      return null;
    }
    return response.Body;
  }

  public static ERemoteStoragePlatform PlatformsToFlag(string[] platforms)
  {
    if (platforms.Length == 0) return ERemoteStoragePlatform.All;
    ERemoteStoragePlatform res = ERemoteStoragePlatform.None;
    foreach (var platform in platforms)
    {
      switch (platform)
      {
        case "windows":
          res |= ERemoteStoragePlatform.Windows;
          break;
        case "macos":
          res |= ERemoteStoragePlatform.OSX;
          break;
        case "ps3":
          res |= ERemoteStoragePlatform.PS3;
          break;
        case "linux":
          res |= ERemoteStoragePlatform.Linux;
          break;
        // These are guesses from now on
        case "switch":
          res |= ERemoteStoragePlatform.Switch;
          break;
        case "android":
          res |= ERemoteStoragePlatform.Android;
          break;
        case "ios":
          res |= ERemoteStoragePlatform.IPhoneOS;
          break;
      }
    }
    return res;
  }

  public static Dictionary<string, LocalFile> MapFilePaths(CloudPathObject[] paths)
  {
    Dictionary<string, LocalFile> results = [];
    foreach (var mapRoot in paths)
    {
      if (!Directory.Exists(mapRoot.path)) continue;
      ERemoteStoragePlatform syncPlatform = PlatformsToFlag(mapRoot.platforms);
      string[] files = Directory.GetFiles(mapRoot.path, mapRoot.pattern, new EnumerationOptions() { RecurseSubdirectories = mapRoot.recursive });
      foreach (var file in files)
      {
        var newFile = new LocalFile(file, file[mapRoot.path.Length..], mapRoot.alias, syncPlatform);
        results.Add(newFile.GetRemotePath().ToLower(), newFile);
      }
    }
    return results;
  }
}
