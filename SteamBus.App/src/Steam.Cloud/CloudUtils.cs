using System.IO.Compression;
using Playtron.Plugin;
using Steam.Config;
using SteamKit2;
using SteamKit2.Internal;

namespace Steam.Cloud;

public class CloudUtils
{
    public struct AnalisisResult
    {
        public List<RemoteCacheFile> missingLocal;
        public List<LocalFile> changedLocal;
        public ConflictDetails? conflictDetails;
    }

    public struct ConflictDetails
    {
        public ulong local;
        public ulong remote;
    }

    public static AnalisisResult AnalyzeSaves(CCloud_GetAppFileChangelist_Response changelist, Dictionary<string, RemoteCacheFile> remoteFiles, Dictionary<string, LocalFile> localFiles)
    {
        AnalisisResult res = new() { missingLocal = [], changedLocal = [] };
        ConflictDetails conflictDetails = new();
        foreach (var file in remoteFiles)
        {
            if (!localFiles.TryGetValue(file.Key, out var localFile))
                res.missingLocal.Add(file.Value);

            if (conflictDetails.remote < file.Value.Time)
                conflictDetails.remote = file.Value.Time;
        }
        foreach (var file in localFiles)
        {
            if (!remoteFiles.TryGetValue(file.Key, out var remoteFile))
                res.changedLocal.Add(file.Value);
            else if (remoteFile.Time < file.Value.UpdateTime)
            {
                if (remoteFile.Sha1() != file.Value.Sha1())
                    res.changedLocal.Add(file.Value);
            }

            if (conflictDetails.local < file.Value.UpdateTime)
                conflictDetails.local = file.Value.UpdateTime;
        }

        bool wasCloudUpdated = changelist.files.Count > 0;
        bool wasLocalUpdated = res.changedLocal.Count > 0;

        if (wasCloudUpdated && wasLocalUpdated)
        {
            res.conflictDetails = conflictDetails;
        }

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
                if (File.Exists(fspath))
                {
                    File.Delete(fspath);
                }
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
                Directory.CreateDirectory(dirname);
                if (file_size != raw_file_size)
                {
                    Console.WriteLine("Got compressed file");
                    var zip = new ZipArchive(fileData);
                    zip.Entries[0].ExtractToFile(fspath, true);
                }
                else if (fileData != null)
                {
                    Console.WriteLine("Got uncompressed file");
                    using FileStream fileH = File.Open(fspath, FileMode.Create);
                    await fileData.CopyToAsync(fileH);
                }
            }
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