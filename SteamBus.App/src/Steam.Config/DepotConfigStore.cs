using Playtron.Plugin;
using Steam.Config;
using SteamKit2;

public class DepotConfigStore
{
    private const string STORE_FILENAME = ".steambus.manifest";

    private Dictionary<uint, string> manifestPathMap = [];
    private Dictionary<uint, KeyValue> manifestMap = [];

    /// <summary>
    /// Initializes the DepotConfigStore
    /// </summary>
    static public async Task<DepotConfigStore> CreateAsync()
    {
        var store = new DepotConfigStore();
        await store.Reload();
        return store;
    }

    /// <summary>
    /// Reloads the depot config store data from all the install directories
    /// </summary>
    /// <returns></returns>
    public async Task Reload()
    {
        var libraryFoldersConfig = await LibraryFoldersConfig.CreateAsync();
        var directories = libraryFoldersConfig.GetInstallDirectories();

        foreach (var dir in directories)
        {
            var appPaths = Directory.EnumerateDirectories(dir);

            foreach (var appPath in appPaths ?? [])
            {
                var manifestPath = Path.Join(appPath, STORE_FILENAME);

                if (File.Exists(manifestPath))
                {
                    var manifestData = await File.ReadAllTextAsync(manifestPath);
                    if (string.IsNullOrEmpty(manifestData))
                        continue;

                    var data = KeyValue.LoadFromString(manifestData);
                    if (data == null)
                        continue;

                    var appId = data["appid"].AsUnsignedInteger();
                    manifestMap.TryAdd(appId, data);
                    manifestPathMap.TryAdd(appId, manifestPath);
                }
            }
        }
    }

    /// <summary>
    /// Save the changes to the depot config for an app id
    /// </summary>
    /// <returns></returns>
    public void Save(uint appId)
    {
        manifestMap.TryGetValue(appId, out var manifest);
        manifestPathMap.TryGetValue(appId, out var path);

        if (manifest == null && path != null && File.Exists(path))
        {
            File.Delete(path);
        }
        else if (manifest != null && path != null)
        {
            manifest.SaveToFile(path, false);
        }
    }

    /// <summary>
    /// Returns the manifest ID installed related to the app id and depot id
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="depotId"></param>
    /// <returns></returns>
    public ulong? GetManifestID(uint appId, uint depotId)
    {
        if (!manifestMap.TryGetValue(appId, out var manifest)) return null;

        return manifest["depots"]?[depotId.ToString()]?.AsUnsignedLong();
    }

    /// <summary>
    /// Gets the size in bytes downloaded by an app id
    /// </summary>
    /// <param name="appId"></param>
    /// <returns></returns>
    public uint? GetSizeDownloaded(uint appId)
    {
        if (!manifestMap.TryGetValue(appId, out var manifest)) return null;

        return manifest["downloaded"]?.AsUnsignedInteger();
    }

    /// <summary>
    /// Gets the download stage by an app id
    /// </summary>
    /// <param name="appId"></param>
    /// <returns></returns>
    public DownloadStage? GetDownloadStage(uint appId)
    {
        if (!manifestMap.TryGetValue(appId, out var manifest)) return null;

        return manifest["downloadstage"]?.AsEnum<DownloadStage>();
    }

    /// <summary>
    /// Removes a manifest ID installed for an app id and depot id
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="depotId"></param>
    public void RemoveManifestID(uint appId, uint depotId)
    {
        if (!manifestMap.TryGetValue(appId, out var manifest)) return;

        var depots = manifest["depots"];
        var child = manifest["depots"]?.Children.Find((child) => child.Name == depotId.ToString());
        if (child != null)
            depots.Children.Remove(child);
    }

    /// <summary>
    /// Sets the manifest ID for an app id and depot id
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="depotId"></param>
    /// <param name="manifestId"></param>
    public void SetManifestID(uint appId, uint depotId, ulong manifestId)
    {
        if (!manifestMap.ContainsKey(appId)) return;
        manifestMap[appId]["depots"][depotId.ToString()] = new KeyValue(depotId.ToString(), manifestId.ToString());
    }

    /// <summary>
    /// Sets the total download property of the manifest
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="size"></param>
    public void SetTotalSize(uint appId, ulong size)
    {
        if (!manifestMap.ContainsKey(appId)) return;
        manifestMap[appId]["totaldownload"] = new KeyValue("totaldownload", size.ToString());
    }

    /// <summary>
    /// Sets the downloaded property of the manifest
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="size"></param>
    public void SetCurrentSize(uint appId, ulong size)
    {
        if (!manifestMap.ContainsKey(appId)) return;
        manifestMap[appId]["downloaded"] = new KeyValue("downloaded", size.ToString());
    }

    /// <summary>
    /// Sets the download stage
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="stage"></param>
    public void SetDownloadStage(uint appId, DownloadStage? stage)
    {
        if (!manifestMap.ContainsKey(appId)) return;

        if (stage == null)
        {
            var child = manifestMap[appId].Children.Find((child) => child.Name == "downloadstage");
            if (child != null)
                manifestMap[appId].Children.Remove(child);
        }
        else
            manifestMap[appId]["downloadstage"] = new KeyValue("downloadstage", stage.ToString());
    }

    /// <summary>
    /// Ensures the necessary entry exists in the manifest map
    /// </summary>
    /// <param name="installDirectory"></param>
    /// <param name="appId"></param>
    public void EnsureEntryExists(string installDirectory, uint appId)
    {
        var manifestPath = Path.Join(installDirectory, STORE_FILENAME);

        if (!manifestPathMap.ContainsKey(appId))
            manifestPathMap.Add(appId, manifestPath);
        else
            manifestPathMap[appId] = manifestPath;

        if (!manifestMap.ContainsKey(appId))
        {
            manifestMap.Add(appId, new KeyValue("manifest"));
            manifestMap[appId]["appid"] = new KeyValue("appid", appId.ToString());
        }

        if (manifestMap[appId]["depots"] == KeyValue.Invalid)
        {
            manifestMap[appId]["depots"] = new KeyValue("depots");
        }
    }

    /// <summary>
    /// List of installed apps with their install information
    /// </summary>
    /// <returns></returns>
    public InstalledAppDescription[] GetInstalledAppInfo()
    {
        return manifestMap.Where((entry) => manifestPathMap.ContainsKey(entry.Key)).Select((entry) =>
            new InstalledAppDescription
            {
                AppId = entry.Value["appid"].AsString()!,
                InstalledPath = Directory.GetParent(manifestPathMap[entry.Key])!.FullName,
                DownloadedBytes = entry.Value["downloaded"].AsUnsignedLong(),
                TotalDownloadSize = entry.Value["totaldownload"].AsUnsignedLong(),
            })
            .ToArray();
    }
}
