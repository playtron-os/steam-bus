using System.Security.Cryptography;
using System.Text;
using SteamKit2;


namespace Steam.Config;

/*
"InstallConfigStore"
{
	"Software"
	{
		"Valve"
		{
			"Steam"
			{
				"Accounts"
				{
					"username"
                    {
                        "SteamID"       "..."
                    }
				}
			}
		}
	}
}
*/

public class GlobalConfig
{
    public const string KEY_ROOT = "InstallConfigStore";
    public const string KEY_SOFTWARE = "Software";
    public const string KEY_VALVE = "Valve";
    public const string KEY_STEAM = "Steam";
    public const string KEY_ACCOUNTS = "Accounts";
    public const string KEY_STEAM_ID = "SteamID";

    private KeyValue? data;
    public string path;
    public const string filename = "config.vdf";

    // Load the local config from the given custom path
    public GlobalConfig(string path)
    {
        this.path = path;
        if (File.Exists(path))
        {
            this.Reload();
        }
        else
        {
            Console.WriteLine($"WARN: no file exists at path: {path}");
            Console.WriteLine(Directory.GetCurrentDirectory());
        }
    }

    // Returns the default path to local.vdf: "~/.local/share/Steam/config/config.vdf"
    public static string DefaultPath()
    {
        string baseDir = SteamConfig.GetConfigDirectory();
        var path = $"{baseDir}/config/{filename}";

        return path;
    }

    // Load the configuration file from the filesystem
    public void Reload()
    {
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
        this.data?.SaveToFile(this.path, false);
    }

    // Add the given steam username and ID to the config file
    public void SetSteamUser(string username, string steamID)
    {
        // Ensure all keys exist
        if (this.data == null)
        {
            var data = new KeyValue(KEY_ROOT);
            this.data = data;
        }
        if (String.IsNullOrEmpty(this.data[KEY_SOFTWARE].Name))
        {
            this.data[KEY_SOFTWARE] = new KeyValue(KEY_SOFTWARE);
        }
        if (String.IsNullOrEmpty(this.data[KEY_SOFTWARE][KEY_VALVE].Name))
        {
            this.data[KEY_SOFTWARE][KEY_VALVE] = new KeyValue(KEY_VALVE);
        }
        if (String.IsNullOrEmpty(this.data[KEY_SOFTWARE][KEY_VALVE][KEY_STEAM].Name))
        {
            this.data[KEY_SOFTWARE][KEY_VALVE][KEY_STEAM] = new KeyValue(KEY_STEAM);
        }
        if (String.IsNullOrEmpty(this.data[KEY_SOFTWARE][KEY_VALVE][KEY_STEAM][KEY_ACCOUNTS].Name))
        {
            this.data[KEY_SOFTWARE][KEY_VALVE][KEY_STEAM][KEY_ACCOUNTS] = new KeyValue(KEY_ACCOUNTS);
        }
        if (String.IsNullOrEmpty(this.data[KEY_SOFTWARE][KEY_VALVE][KEY_STEAM][KEY_ACCOUNTS][username].Name))
        {
            this.data[KEY_SOFTWARE][KEY_VALVE][KEY_STEAM][KEY_ACCOUNTS][username] = new KeyValue(username);
        }

        // Save the key/value pair
        if (this.data != null)
        {
            this.data[KEY_SOFTWARE][KEY_VALVE][KEY_STEAM][KEY_ACCOUNTS][username][KEY_STEAM_ID] = new KeyValue(KEY_STEAM_ID, steamID);
        }
    }
}

