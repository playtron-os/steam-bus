using Playtron.Plugin;
using Steam.Config;
using SteamKit2;

public class DepotConfigStore
{
    private const string STORE_FILENAME = ".steambus.manifest";

    private List<string>? folders;

    private Dictionary<uint, string> manifestPathMap = [];
    private Dictionary<uint, KeyValue> manifestMap = [];

    /// <summary>
    /// Initializes the DepotConfigStore
    /// </summary>
    static public async Task<DepotConfigStore> CreateAsync(List<string>? folders = null)
    {
        var store = new DepotConfigStore();
        store.folders = folders;
        await store.Reload();
        return store;
    }

    /// <summary>
    /// Reloads the depot config store data from all the install directories
    /// </summary>
    /// <returns></returns>
    public async Task Reload()
    {
        manifestMap.Clear();
        manifestPathMap.Clear();

        if (folders != null)
        {
            foreach (var dir in folders)
                await ReloadApps(dir);
            return;
        }

        var libraryFoldersConfig = await LibraryFoldersConfig.CreateAsync();
        var directories = libraryFoldersConfig.GetInstallDirectories();

        manifestMap.Clear();
        manifestPathMap.Clear();

        foreach (var dir in directories)
            await ReloadApps(dir);
    }

    private async Task ReloadApps(string dir)
    {
        if (!Directory.Exists(dir))
            return;

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

        Console.WriteLine($"Depot config store loaded {manifestPathMap.Count} installed apps");
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
    /// Returns the install directory for an app id
    /// </summary>
    /// <param name="appId"></param>
    /// <returns></returns>
    public string? GetInstallDirectory(uint appId)
    {
        if (!manifestPathMap.TryGetValue(appId, out var manifestPath)) return null;

        return Directory.GetParent(manifestPath)?.FullName;
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
    /// Is app downloaded
    /// </summary>
    /// <param name="appId"></param>
    /// <returns></returns>
    public bool IsAppDownloaded(uint appId)
    {
        if (!manifestMap.TryGetValue(appId, out var manifest)) return false;

        var stage = manifest["downloadstage"];
        var sizeDownloaded = manifest["downloaded"]?.AsUnsignedInteger();
        var totalSize = manifest["totaldownload"]?.AsUnsignedInteger();

        return stage == KeyValue.Invalid && sizeDownloaded == totalSize;
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
    /// Sets the version/branch and removes the updatepending
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="version"></param>
    /// <param name="branch"></param>
    public void SetNewVersion(uint appId, uint version, string branch, string os)
    {
        if (!manifestMap.ContainsKey(appId)) return;

        manifestMap[appId]["version"] = new KeyValue("version", version.ToString());
        manifestMap[appId]["branch"] = new KeyValue("branch", branch);
        manifestMap[appId]["os"] = new KeyValue("os", os);

        var child = manifestMap[appId].Children.Find((child) => child.Name == "updatepending");
        if (child != null)
            manifestMap[appId].Children.Remove(child);
    }

    /// <summary>
    /// Sets the updatepending
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="latestVersion"></param>
    public void SetUpdatePending(uint appId, string latestVersion)
    {
        if (!manifestMap.ContainsKey(appId)) return;

        manifestMap[appId]["updatepending"] = new KeyValue("updatepending", "1");
        manifestMap[appId]["latestversion"] = new KeyValue("latestversion", latestVersion);
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
                Version = entry.Value["version"].AsString() ?? "",
                LatestVersion = entry.Value["latestversion"].AsString() ?? "",
                UpdatePending = entry.Value["updatepending"].AsString() == "1",
                Os = entry.Value["os"].AsString() ?? ""
            })
            .ToArray();
    }

    /// <summary>
    /// Installed apps with its install information
    /// </summary>
    /// <returns></returns>
    public (InstalledAppDescription Info, string Branch)? GetInstalledAppInfo(uint appId)
    {
        manifestMap.TryGetValue(appId, out var manifest);
        manifestPathMap.TryGetValue(appId, out var manifestPath);
        if (manifest == null || manifestPath == null) return null;

        return (new InstalledAppDescription
        {
            AppId = manifest["appid"].AsString()!,
            InstalledPath = Directory.GetParent(manifestPath)!.FullName,
            DownloadedBytes = manifest["downloaded"].AsUnsignedLong(),
            TotalDownloadSize = manifest["totaldownload"].AsUnsignedLong(),
            Version = manifest["version"].AsString() ?? "",
            LatestVersion = manifest["latestversion"].AsString() ?? "",
            UpdatePending = manifest["updatepending"].AsString() == "1",
            Os = manifest["os"].AsString() ?? ""
        }, manifest["branch"].AsString() ?? "");
    }

    /// <summary>
    /// Get map of app id to version/branch
    /// </summary>
    /// <returns></returns>
    public Dictionary<string, (string version, string branch)> GetAppIdToVersionBranchMap(bool ignoreUpdatePending = false)
    {
        var map = new Dictionary<string, (string version, string branch)>();

        foreach (var entry in manifestMap)
        {
            var version = entry.Value["version"]?.AsString();
            var branch = entry.Value["branch"]?.AsString();

            if (version != null && branch != null && (!ignoreUpdatePending || entry.Value["updatepending"] == KeyValue.Invalid))
                map.Add(entry.Key.ToString(), (version, branch));
        }

        return map;
    }

    /// <summary>
    /// Removes the information related to the installed app id
    /// </summary>
    /// <param name="appId"></param>
    public void RemoveInstalledApp(uint appId)
    {
        manifestPathMap.TryGetValue(appId, out var path);

        if (path != null)
        {
            Directory.Delete(Directory.GetParent(path)!.FullName, true);
            manifestPathMap.Remove(appId);
            manifestMap.Remove(appId);
        }
    }

    /// <summary>
    /// Moves the installed app to a new directory
    /// </summary>
    /// <param name="appId"></param>
    /// <param name="newInstallDirectory"></param>
    public async Task<string> MoveInstalledApp(uint appId, string newInstallDirectory, Action<(string appId, double progress)>? OnMoveItemProgressed)
    {
        manifestPathMap.TryGetValue(appId, out var path);
        if (path == null) throw DbusExceptionHelper.ThrowAppNotInstalled();

        var currentInstallDirectory = Directory.GetParent(path)!.FullName;
        if (!Directory.Exists(currentInstallDirectory)) throw DbusExceptionHelper.ThrowMissingDirectory();

        // Ensure the destination directory exists
        Directory.CreateDirectory(newInstallDirectory);

        // Get all files and subdirectories
        var files = Directory.GetFiles(currentInstallDirectory, "*", SearchOption.AllDirectories);
        var directories = Directory.GetDirectories(currentInstallDirectory, "*", SearchOption.AllDirectories);
        var totalItems = files.Length + directories.Length;
        int processedItems = 0;

        // Move all files
        foreach (var file in files)
        {
            // Calculate relative path
            string relativePath = Path.GetRelativePath(currentInstallDirectory, file);
            string destinationFile = Path.Combine(newInstallDirectory, relativePath);

            // Ensure the destination directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            // Move the file
            await Task.Run(() => File.Move(file, destinationFile));

            // Update progress
            processedItems++;
            int progress = (int)((double)processedItems / totalItems * 100);
            OnMoveItemProgressed?.Invoke((appId.ToString(), progress));
        }

        // Move all directories
        foreach (var directory in directories)
        {
            // Calculate relative path
            string relativePath = Path.GetRelativePath(currentInstallDirectory, directory);
            string destinationDir = Path.Combine(newInstallDirectory, relativePath);

            // Create the directory in the destination
            Directory.CreateDirectory(destinationDir);

            // Update progress
            processedItems++;
            int progress = (int)((double)processedItems / totalItems * 100);
            OnMoveItemProgressed?.Invoke((appId.ToString(), progress));
        }

        // Delete the source directory after moving
        Directory.Delete(currentInstallDirectory, true);

        // Update state
        manifestPathMap[appId] = newInstallDirectory;

        // Final progress update to 100%
        OnMoveItemProgressed?.Invoke((appId.ToString(), 100));

        return newInstallDirectory;
    }
}
