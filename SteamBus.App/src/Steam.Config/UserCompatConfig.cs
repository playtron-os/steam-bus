using System.Security.Cryptography;
using System.Text;
using Steam.Content;
using SteamKit2;


namespace Steam.Config;

/*
"platform_overrides"
{
	"990080"
	{
		"dest"		"linux"
		"src"		"windows"
	}
	"1240440"
	{
		"dest"		"linux"
		"src"		"windows"
	}
	"281200"
	{
		"dest"		"linux"
		"src"		"windows"
	}
}
*/

public class UserCompatConfig
{
    private KeyValue? data;
    public string path;
    public const string filename = "compat.vdf";

    // Load the local config from the given custom path
    public UserCompatConfig(string path)
    {
        this.path = path;
        this.Reload();
    }

    // Returns the default path to local.vdf: "~/.local/share/Steam/userdata/{accountId}/config/compat.vdf"
    public static string DefaultPath(uint accountId)
    {
        string baseDir = SteamConfig.GetConfigDirectory();
        var path = $"{baseDir}/userdata/{accountId}/config/{filename}";

        return path;
    }

    // Load the configuration file from the filesystem
    public void Reload()
    {
        if (!File.Exists(path))
        {
            this.data = new KeyValue("platform_overrides");
            Save();
            return;
        }

        var stream = File.OpenText(this.path);
        var content = stream.ReadToEnd();

        var data = KeyValue.LoadFromString(content);
        this.data = data;
        stream.Close();
    }

    // Save the configuration
    public void Save()
    {
        Disk.EnsureParentFolderExists(path);
        Disk.ExecuteFileOpWithRetry(() =>
        {
            this.data?.SaveToFileWithAtomicRename(this.path);
            return "";
        }, this.path);
    }

    // Sets a platform override for an app
    public void SetPlatformOverride(uint appId, string destPlatform, string srcPlatform)
    {
        if (data == null)
            Reload();

        if (destPlatform == srcPlatform)
        {
            data!.Children.Remove(data[appId.ToString()]);
            return;
        }

        var config = new KeyValue(appId.ToString());
        config["dest"] = new KeyValue("dest", destPlatform);
        config["src"] = new KeyValue("src", srcPlatform);
        data![appId.ToString()] = config;
    }

    // Gets platform for an app
    public string? GetAppPlatform(uint appId)
    {
        if (data == null)
            Reload();

        return data![appId.ToString()]["src"]?.AsString();
    }
}

