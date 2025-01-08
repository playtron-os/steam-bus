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
    public void SetManifestID(string installDirectory, uint appId, uint depotId, ulong manifestId)
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

        manifestMap[appId]["depots"][depotId.ToString()] = new KeyValue(depotId.ToString(), manifestId.ToString());
    }
}
