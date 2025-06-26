using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using Playtron.Plugin;
using Steam.Config;
using SteamKit2;
using SteamKit2.Internal;

namespace Steam.Cloud;

public enum EHTTPMethod
{
    Invalid,
    GET,
    HEAD,
    POST,
    PUT,
    DELETE,
    OPTIONS,
    PATCH
}

public class CloudUtils
{
    public struct AnalisisResult
    {
        public List<RemoteCacheFile> missingLocal;
        public List<LocalFile> changedLocal;
        public ConflictDetails conflictDetails;
    }

    public struct ConflictDetails
    {
        public ulong local;
        public ulong remote;
    }

    public static AnalisisResult AnalyzeSaves(CCloud_GetAppFileChangelist_Response changelist, Dictionary<string, RemoteCacheFile> remoteFiles, Dictionary<string, LocalFile> localFiles, ERemoteStoragePlatform platformToSync)
    {
        AnalisisResult res = new() { missingLocal = [], changedLocal = [] };
        ConflictDetails conflictDetails = new() { local = 0, remote = 0 };
        Dictionary<string, string> filePathToSha = [];

        foreach (var file in changelist.files)
        {
            if ((file.platforms_to_sync & (uint)platformToSync) != (uint)platformToSync) continue;

            if (conflictDetails.remote < file.time_stamp)
                conflictDetails.remote = file.time_stamp;

            string shaHex = BitConverter.ToString(file.sha_file).Replace("-", "").ToLowerInvariant();

            var path = "";
            if (changelist.path_prefixes.Count > 0 && file.ShouldSerializepath_prefix_index())
            {
                path = changelist.path_prefixes[(int)file.path_prefix_index];
            }
            var cloudpath = $"{path}{file.file_name}";

            filePathToSha.Add(cloudpath, shaHex);
        }
        foreach (var file in remoteFiles)
        {
            if (!localFiles.TryGetValue(file.Key, out var localFile))
                res.missingLocal.Add(file.Value);
        }
        foreach (var file in localFiles)
        {
            filePathToSha.TryGetValue(file.Key, out var asd);
            if (!remoteFiles.TryGetValue(file.Key, out var remoteFile))
            {
                res.changedLocal.Add(file.Value);
            }
            else if (remoteFile.Time < file.Value.UpdateTime && remoteFile.Sha1() != file.Value.Sha1())
            {
                res.changedLocal.Add(file.Value);
            }
            else if (filePathToSha.TryGetValue(file.Key, out var sha) && sha != file.Value.Sha1())
            {
                res.changedLocal.Add(file.Value);
            }

            if (conflictDetails.local < file.Value.UpdateTime)
                conflictDetails.local = file.Value.UpdateTime;
        }

        res.conflictDetails = conflictDetails;

        return res;
    }


    public static async Task<(RemoteCacheFile, Exception?)> DownloadFileAsync(uint appid, RemoteCacheFile file, string fspath, SemaphoreSlim semaphore, HttpClient httpClient, SteamCloud steamCloud)
    {
        await semaphore.WaitAsync();
        try
        {
            var cloudpath = file.GetRemotePath();
            var response = await steamCloud.DownloadFileAsync(appid, cloudpath);
            if (response == null)
            {
                Console.WriteLine("Unable to get file {0}", file.Path);
                throw new Exception("connection error");
            }
            if (response.encrypted)
            {
                Console.WriteLine("Encypted files aren't supported");
                throw new NotImplementedException();
            }
            // Prepare get request for actual file
            var http = response.use_https ? "https" : "http";
            var url = $"{http}://{response.url_host}{response.url_path}";
            var raw_file_size = response.raw_file_size;
            var file_size = response.file_size;
            if (file.PersistState == ECloudStoragePersistState.k_ECloudStoragePersistStateDeleted)
            {
                await Disk.ExecuteFileOpWithRetry(() =>
                {
                    if (File.Exists(fspath))
                        File.Delete(fspath);
                    return Task.CompletedTask;
                }, fspath, maxRetries: 8, delayMilliseconds: 50);

                semaphore.Release();
                return (file, null);
            }
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var header in response.request_headers)
            {
                request.Headers.Add(header.name, header.value); // Set headers that steam expects us to send to the CDN
            }
            // We can also make it return after headers were read, unsure how big files can get.


            var fileRes = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead);
            Console.WriteLine("Response for {0} received", cloudpath);
            fileRes.EnsureSuccessStatusCode();
            var fileData = await fileRes.Content.ReadAsStreamAsync() ?? throw new Exception("file stream error");
            var dirname = Path.GetDirectoryName(fspath);

            if (fileData != null && dirname != null)
            {
                if (dirname.Length > 0)
                    Directory.CreateDirectory(dirname);
                if (file_size != raw_file_size)
                {
                    Console.WriteLine("Got compressed file");
                    var zip = new ZipArchive(fileData);

                    await Disk.ExecuteFileOpWithRetry(() =>
                    {
                        zip.Entries[0].ExtractToFile(fspath, true);
                        return Task.CompletedTask;
                    }, fspath, maxRetries: 8, delayMilliseconds: 50);
                }
                else if (fileData != null)
                {
                    Console.WriteLine("Got uncompressed file");
                    await Disk.ExecuteFileOpWithRetry(async () =>
                    {
                        using FileStream fileH = File.Open(fspath, FileMode.Create);
                        await fileData.CopyToAsync(fileH);
                    }, fspath, maxRetries: 8, delayMilliseconds: 50);
                }
            }

            await Disk.ExecuteFileOpWithRetry(() =>
            {
                File.SetLastWriteTimeUtc(fspath, DateTime.UnixEpoch.AddSeconds(file.RemoteTime));
                return Task.CompletedTask;
            }, fspath, maxRetries: 8, delayMilliseconds: 50);
        }
        catch (Exception e)
        {
            return (file, e);
        }
        finally
        {
            semaphore.Release();
        }
        return (file, null);
    }

    public static async Task<(RemoteCacheFile, Exception?)> UploadFileAsync(uint appid, RemoteCacheFile file, string fspath, ulong batch, SemaphoreSlim semaphore, HttpClient httpClient, SteamCloud steamCloud)
    {
        await semaphore.WaitAsync();
        try
        {
            var cloudpath = file.GetRemotePath();
            byte[] sha_hash = new byte[20];
            Stream fileContents;
            if (file.Size > 512 * 1024)
            {
                var zipStream = new MemoryStream(1024);
                using var newArchive = new ZipArchive(zipStream, ZipArchiveMode.Create);
                var entry = newArchive.CreateEntry("z");
                using (var entryStream = entry.Open())
                {
                    using var fileStream = File.OpenRead(fspath);
                    await fileStream.CopyToAsync(entryStream);
                }
                fileContents = zipStream;
            }
            else
            {
                fileContents = File.OpenRead(fspath);
            }

            for (int i = 0; i < sha_hash.Length; i++)
            {
                string hex = file.Sha.Substring(i * 2, 2);
                sha_hash[i] = Convert.ToByte(hex, 16);
            }
            var response = await steamCloud.ClientBeginFileUpload(appid, cloudpath, sha_hash, (uint)fileContents.Length, file.Size, file.LocalTime, file.PlatformsToSync, batch);
            if (response == null)
            {
                Console.WriteLine("Unable to upload file {0}", file.Path);
                throw new Exception("connection error");
            }
            if (response.encrypt_file)
            {
                throw new Exception("encryption is unsupported");
            }
            foreach (var request in response.block_requests)
            {
                string http = request.use_https ? "https" : "http";
                string url = $"{http}://{request.url_host}{request.url_path}";
                Console.WriteLine("Uploading to {0}", url);
                // These methods are a guess, I assume Steam uses same http method codes here as in steamworks 
                HttpMethod httpMethod = (EHTTPMethod)request.http_method switch
                {
                    EHTTPMethod.GET => HttpMethod.Get,
                    EHTTPMethod.HEAD => HttpMethod.Head,
                    EHTTPMethod.POST => HttpMethod.Post,
                    EHTTPMethod.DELETE => HttpMethod.Delete,
                    EHTTPMethod.OPTIONS => HttpMethod.Options,
                    EHTTPMethod.PATCH => HttpMethod.Patch,
                    EHTTPMethod.PUT => HttpMethod.Put,
                    _ => HttpMethod.Put
                };
                var httpRequest = new HttpRequestMessage(httpMethod, url);
                if (request.ShouldSerializeexplicit_body_data())
                {
                    httpRequest.Content = new ByteArrayContent(request.explicit_body_data);
                }
                else
                {
                    var offset = request.ShouldSerializeblock_offset() ? (long)request.block_offset : 0;
                    var length = request.ShouldSerializeblock_length() ? (int)request.block_length : (int)fileContents.Length;

                    if (length != 0)
                    {
                        fileContents.Seek(offset, SeekOrigin.Begin);
                        httpRequest.Content = new StreamContent(fileContents, length);
                    }
                }

                foreach (var header in request.request_headers)
                {
                    try
                    {
                        httpRequest.Headers.Add(header.name, header.value);
                    }
                    catch (Exception)
                    {
                        Console.WriteLine("Unable to insert {0} header to request", header.name);
                        httpRequest.Content.Headers.Add(header.name, header.value);
                    }
                }
                var httpRes = await httpClient.SendAsync(httpRequest);
                // Gracefully handle errors
                httpRes.EnsureSuccessStatusCode();
            }
            var commitRes = await steamCloud.CientCommitFileUpload(appid, true, cloudpath, sha_hash) ?? throw new Exception("Failed to commit file");
            Console.WriteLine("File {0} is commited?: {1}", cloudpath, commitRes.file_committed);
        }
        catch (Exception e)
        {
            return (file, e);
        }
        finally
        {
            semaphore.Release();
        }
        return (file, null);
    }

    // Splits path after the variable
    public static (string, string) SplitRootPath(string path)
    {
        var percentageSign = path.IndexOf('%', 1);
        if (percentageSign == -1)
        {
            return ("", path);
        }

        return (path[1..percentageSign++], path[percentageSign..]);
    }
}