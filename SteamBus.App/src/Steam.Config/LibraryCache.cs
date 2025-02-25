using System.Security.Cryptography;
using System.Text;
using Playtron.Plugin;
using SteamKit2;
using Xdg.Directories;


namespace Steam.Config;

public class LibraryCache
{
    public const string PACKAGE_IDS = "packageids";
    public const string APPS = "apps";

    public const string PROVIDER_ITEM_NAME = "name";
    public const string PROVIDER_ITEM_APP_TYPE = "apptype";

    private KeyValue? data;
    public string path;
    public const string filename = "librarycache.vdf";

    // Load the local config from the given custom path
    public LibraryCache(string path)
    {
        this.path = path;
        this.Reload();
    }

    // Returns the default path to local.vdf: "~/.local/share/steambus/librarycache.vdf"
    public static string DefaultPath()
    {
        string baseDir = $"{BaseDirectory.DataHome}/steambus";
        var path = $"{baseDir}/{filename}";

        return path;
    }

    // Load the configuration file from the filesystem
    public void Reload()
    {
        if (!File.Exists(path))
        {
            this.data = new KeyValue("LibraryCache");
            Save();
            return;
        }

        var stream = File.OpenText(this.path);
        var content = stream.ReadToEnd();

        var data = KeyValue.LoadFromString(content);
        this.data = data ?? new KeyValue("LibraryCache");
        stream.Close();
    }

    // Save the configuration
    public void Save()
    {
        Disk.EnsureParentFolderExists(path);
        this.data?.SaveToFileWithAtomicRename(this.path);
    }

    // Adds the list of package ids to the cache
    public void SetPackageIDs(uint identifier, IEnumerable<uint> packageIDs)
    {
        var identifierStr = identifier.ToString();

        if (string.IsNullOrEmpty(this.data![identifierStr]?.Name))
            this.data[identifierStr] = new KeyValue(identifierStr);

        this.data[identifierStr][PACKAGE_IDS] = new KeyValue(PACKAGE_IDS);

        int i = 0;
        foreach (var packageId in packageIDs)
        {
            var key = i.ToString();
            this.data[identifierStr][PACKAGE_IDS][key] = new KeyValue(key, packageId.ToString());
            i++;
        }
    }

    // Gets the list of package ids from the cache
    public List<uint> GetPackageIDs(uint identifier)
    {
        var children = this.data![identifier.ToString()]?[PACKAGE_IDS]?.Children;
        if (children == null) return [];
        return children.Select((child) => uint.Parse(child.Value!)).ToList();
    }

    // Adds the list of apps to the cache
    public void SetApps(uint identifier, List<ProviderItem> providerItems)
    {
        var identifierStr = identifier.ToString();

        if (string.IsNullOrEmpty(this.data![identifierStr]?.Name))
            this.data[identifierStr] = new KeyValue(identifierStr);

        this.data[identifierStr][APPS] = new KeyValue(APPS);

        foreach (var item in providerItems)
        {
            var key = item.id.ToString();
            this.data[identifierStr][APPS][key] = new KeyValue(key);
            this.data[identifierStr][APPS][key][PROVIDER_ITEM_NAME] = new KeyValue(PROVIDER_ITEM_NAME, item.name);
            this.data[identifierStr][APPS][key][PROVIDER_ITEM_APP_TYPE] = new KeyValue(PROVIDER_ITEM_APP_TYPE, item.app_type.ToString());
        }
    }

    // Gets the list of apps from the cache
    public List<ProviderItem> GetApps(uint identifier)
    {
        var result = new List<ProviderItem>();
        var children = this.data![identifier.ToString()]?[APPS]?.Children;
        if (children == null) return result;

        foreach (var child in children)
            result.Add(new ProviderItem
            {
                id = child.Name!,
                app_type = uint.Parse(child[PROVIDER_ITEM_APP_TYPE].Value!),
                name = child[PROVIDER_ITEM_NAME].Value!,
                provider = "Steam"
            });

        return result;
    }
}

