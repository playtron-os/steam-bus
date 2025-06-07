using System.Security.Cryptography;
using System.Text;
using SteamKit2;


namespace Steam.Config;

/*
"libraryfolders"
{
    "0"
    {
        "path"          ".../.local/share/Steam"
        "label"         ""
        "contentid"             "9188593632319197941"
        "totalsize"             "0"
        "update_clean_bytes_tally"              "649081694"
        "time_last_update_verified"             "1736267851"
        "apps"
        {
                "123"                "0"
        }
    }
}
*/

public class LibraryFoldersConfig
{
    private KeyValue? data;
    public string path;

    public const string FILENAME = "libraryfolders.vdf";
    public const string EXTERNAL_FILENAME = "libraryfolder.vdf";

    // Content of the main libraryfolders config file
    const string DEFAULT_MAIN_LIBRARY_FOLDERS_CONTENT = """
    "libraryfolder"
    {
    }
    """;

    // Content of the library folders config file that goes inside the library folder
    const string DEFAULT_EXTERNAL_LIBRARY_FOLDERS_CONTENT = """
    "libraryfolder"
    {
        "contentid"             ""
        "label"         ""
    }
    """;

    /// <summary>
    /// Load the local config from the given custom path
    /// </summary>
    /// <param name="path"></param>
    private LibraryFoldersConfig(string path)
    {
        this.path = path;
    }

    /// <summary>
    /// Creates a new instance of the LibraryFoldersConfig with the default path
    /// </summary>
    /// <returns></returns>
    public static async Task<LibraryFoldersConfig> CreateAsync()
    {
        var config = new LibraryFoldersConfig(DefaultPath());
        await config.Reload();
        return config;
    }

    /// <summary>
    /// Returns the default path to libraryfolders.vdf: "~/.local/share/Steam/config/libraryfolders.vdf"
    /// </summary>
    /// <returns></returns>
    public static string DefaultPath()
    {
        string baseDir = SteamConfig.GetConfigDirectory();
        return Path.Join(baseDir, "config", FILENAME);
    }

    /// <summary>
    /// Load the configuration file from the filesystem
    /// </summary>
    /// <returns></returns>
    public async Task Reload()
    {
        if (File.Exists(path))
        {
            var stream = File.OpenText(this.path);
            var content = await stream.ReadToEndAsync();
            this.data = KeyValue.LoadFromString(content);
            stream.Close();
        }
        else
            await CreateFile();

        var configDir = SteamConfig.GetConfigDirectory();
        var hasMainMountPath = data?.Children.Any((c) => c["path"]?.AsString() == configDir) ?? false;
        if (!hasMainMountPath)
        {
            AddDiskEntry(await Disk.GetMountPath());
            Save();
        }
    }

    /// <summary>
    /// Creates the file in case it didn't exist
    /// </summary>
    /// <returns></returns>
    async Task CreateFile()
    {
        DirectoryInfo parentDirectory = new DirectoryInfo(path).Parent!;
        Directory.CreateDirectory(parentDirectory.FullName);
        await File.WriteAllTextAsync(path, DEFAULT_MAIN_LIBRARY_FOLDERS_CONTENT);
        this.data = KeyValue.LoadFromString(DEFAULT_MAIN_LIBRARY_FOLDERS_CONTENT);
    }

    /// <summary>
    /// Save the configuration
    /// </summary>
    public void Save()
    {
        Disk.EnsureParentFolderExists(path);
        this.data?.SaveToFileWithAtomicRename(this.path);

        var otherPath = Path.Join(SteamConfig.GetConfigDirectory(), "steamapps", FILENAME);
        this.data?.SaveToFileWithAtomicRename(otherPath);
    }

    /// <summary>
    /// Adds an entry to the libraryfolders config
    /// </summary>
    /// <param name="mountPoint"></param>
    public void AddDiskEntry(string mountPoint)
    {
        if (mountPoint.StartsWith("/etc")) return;

        string installPath;
        bool isMainDisk = Disk.IsMountPointMainDisk(mountPoint);
        if (isMainDisk)
            installPath = SteamConfig.GetConfigDirectory();
        else
            installPath = Path.Join(mountPoint, "SteamLibrary");

        var child = data!.Children.Find((child) => child["path"].AsString() == installPath);
        if (child != null) return;

        var index = data!.Children.Count.ToString();
        var newEntry = new KeyValue(index);
        newEntry["path"] = new KeyValue("path", installPath);
        newEntry["label"] = new KeyValue("label", "");
        newEntry["contentid"] = new KeyValue("contentid", "");
        newEntry["totalsize"] = new KeyValue("totalsize", "0");
        newEntry["update_clean_bytes_tally"] = new KeyValue("update_clean_bytes_tally", "0");
        newEntry["time_last_update_verified"] = new KeyValue("time_last_update_verified", "0");
        newEntry["apps"] = new KeyValue("apps");
        data[index] = newEntry;

        string baseDir = SteamConfig.GetConfigDirectory();
        var steamappsFolder = Path.Join(baseDir, "steamapps");

        if (!Directory.Exists(steamappsFolder))
            Directory.CreateDirectory(steamappsFolder);

        if (!isMainDisk)
        {
            var externalLibraryFoldersConfigFile = Path.Join(installPath, EXTERNAL_FILENAME);

            if (!File.Exists(externalLibraryFoldersConfigFile))
            {
                var singleEntry = KeyValue.LoadFromString(DEFAULT_EXTERNAL_LIBRARY_FOLDERS_CONTENT)!;
                Disk.EnsureParentFolderExists(externalLibraryFoldersConfigFile);
                singleEntry.SaveToFileWithAtomicRename(externalLibraryFoldersConfigFile);
            }
        }

        Console.WriteLine($"Added {mountPoint} to steam library folder");
    }

    /// <summary>
    /// Get install directory
    /// </summary>
    /// <param name="mountPoint"></param>
    /// <returns></returns>
    public string? GetInstallDirectory(string mountPoint)
    {
        foreach (var entry in data!.Children)
        {
            var path = entry["path"]?.AsString();

            if (path?.Contains(mountPoint) == true)
            {
                var finalPath = Path.Join(path, "steamapps", "common");
                Directory.CreateDirectory(finalPath);
                return finalPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets a list of all the install directories
    /// </summary>
    /// <returns></returns>
    public List<string> GetInstallDirectories()
    {
        var dirs = new List<string>();

        foreach (var entry in data!.Children)
        {
            var path = entry["path"]?.AsString();

            if (path != null)
            {
                var finalPath = Path.Join(path, "steamapps", "common");
                dirs.Add(finalPath);
            }
        }

        return dirs;
    }
}

