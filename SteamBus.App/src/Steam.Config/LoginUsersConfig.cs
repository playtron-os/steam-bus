using SteamKit2;


namespace Steam.Config;

/*
"users"
{
    "43561239554266212"
    {
        "AccountName"           "..."
        "PersonaName"           "..."
        "RememberPassword"              "1"
        "WantsOfflineMode"              "0"
        "SkipOfflineModeWarning"                "0"
        "AllowAutoLogin"                "1"
        "MostRecent"            "1"
        "Timestamp"             "1736955628"
    }
}
*/

public class LoginUsersConfig
{
    private KeyValue? data;
    public string path;
    public const string filename = "loginusers.vdf";

    // Load the local config from the given custom path
    public LoginUsersConfig(string path)
    {
        this.path = path;
        this.Reload();
    }

    // Returns the default path to local.vdf: "~/.local/share/Steam/config/loginusers.vdf"
    public static string DefaultPath()
    {
        string baseDir = SteamConfig.GetConfigDirectory();
        return Path.Join(baseDir, "config", filename);
    }

    // Load the configuration file from the filesystem
    public void Reload()
    {
        if (!File.Exists(path))
        {
            this.data = new KeyValue("users");
            Save();
            return;
        }

        var stream = File.OpenText(path);
        var content = stream.ReadToEnd();

        var data = KeyValue.LoadFromString(content);
        this.data = data;
        stream.Close();
    }

    // Save the configuration
    public void Save()
    {
        this.data?.SaveToFile(this.path, false);
    }

    // Add the given user to the loginusers config
    public void SetUser(string sub, string accountName, string personaName)
    {
        if (data == null)
        {
            data = new KeyValue("users");
        }

        if (data[sub] == KeyValue.Invalid)
        {
            data[sub] = new KeyValue(sub);
        }

        data[sub]["AccountName"] = new KeyValue("AccountName", accountName);
        data[sub]["PersonaName"] = new KeyValue("PersonaName", personaName);
        data[sub]["RememberPassword"] = new KeyValue("RememberPassword", "1");
        data[sub]["WantsOfflineMode"] = new KeyValue("WantsOfflineMode", "0");
        data[sub]["SkipOfflineModeWarning"] = new KeyValue("SkipOfflineModeWarning", "0");
        data[sub]["AllowAutoLogin"] = new KeyValue("AllowAutoLogin", "1");
        data[sub]["MostRecent"] = new KeyValue("MostRecent", "1");
        data[sub]["Timestamp"] = new KeyValue("Timestamp", DateTimeOffset.Now.ToUnixTimeSeconds().ToString());
    }
}

