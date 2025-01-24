using System.Text;
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
            data = new KeyValue("users");

        if (data[sub] == KeyValue.Invalid)
            data[sub] = new KeyValue(sub);

        data[sub]["AccountName"] = new KeyValue("AccountName", accountName);
        data[sub]["PersonaName"] = new KeyValue("PersonaName", personaName);
        data[sub]["RememberPassword"] = new KeyValue("RememberPassword", "1");
        data[sub]["WantsOfflineMode"] = new KeyValue("WantsOfflineMode", "0");
        data[sub]["SkipOfflineModeWarning"] = new KeyValue("SkipOfflineModeWarning", "0");
        data[sub]["AllowAutoLogin"] = new KeyValue("AllowAutoLogin", "1");
        data[sub]["Timestamp"] = new KeyValue("Timestamp", DateTimeOffset.Now.ToUnixTimeSeconds().ToString());
        SetUserMostRecent(sub);
    }

    private void SetUserMostRecent(string sub)
    {
        foreach (var child in data?.Children ?? [])
        {
            if (child.Name == sub)
            {
                child["MostRecent"] = new KeyValue("MostRecent", "1");
                child["AllowAutoLogin"] = new KeyValue("AllowAutoLogin", "1");
                child["Timestamp"] = new KeyValue("Timestamp", DateTimeOffset.Now.ToUnixTimeSeconds().ToString());
            }
            else
            {
                child["MostRecent"] = new KeyValue("MostRecent", "0");
                child["AllowAutoLogin"] = new KeyValue("AllowAutoLogin", "0");
            }
        }
    }

    public void UpdateConfigFiles(string sub, string accountId, bool wantsOfflineMode)
    {
        // Updates offline mode config
        if (data != null && data[sub] != KeyValue.Invalid)
        {
            data[sub]["WantsOfflineMode"] = new KeyValue("WantsOfflineMode", wantsOfflineMode ? "1" : "0");
            data[sub]["SkipOfflineModeWarning"] = new KeyValue("SkipOfflineModeWarning", "1");
            SetUserMostRecent(sub);
            Save();
        }

        UpdateUserConfig(accountId);
        UpdateUserSharedConfig(accountId);
    }

    public (KeyValue, string) GetUserConfig(string accountId)
    {
        var steamConfigDir = SteamConfig.GetConfigDirectory();
        var userConfigPath = Path.Join(steamConfigDir, "userdata", accountId, "config", "localconfig.vdf");
        var parent = Directory.GetParent(userConfigPath)!.FullName;

        if (!File.Exists(userConfigPath))
        {
            Directory.CreateDirectory(parent);
            return (new KeyValue("users"), userConfigPath);
        }

        // Use this method to read the file because using ReadToEnd isn't reading the entire file
        string content = "";
        using (var stream = File.OpenText(userConfigPath))
        {
            string? line;
            while ((line = stream.ReadLine()) != null)
            {
                content += line;
            }
        }

        return (KeyValue.LoadFromString(content) ?? new KeyValue("users"), userConfigPath);
    }

    private void UpdateUserConfig(string accountId)
    {
        var (userData, path) = GetUserConfig(accountId);
        userData = UpdateUserConfig(userData);
        userData.SaveToFile(path, false);
    }

    private KeyValue UpdateUserConfig(KeyValue data)
    {
        if (data["system"] == KeyValue.Invalid)
            data["system"] = new KeyValue("system");
        data["system"]["EnableGameOverlay"] = new KeyValue("EnableGameOverlay", "0");

        if (data["friends"] == KeyValue.Invalid)
            data["friends"] = new KeyValue("friends");
        data["friends"]["Sounds_EventsAndAnnouncements"] = new KeyValue("Sounds_EventsAndAnnouncements", "0");
        data["friends"]["Sounds_PlayMessage"] = new KeyValue("Sounds_PlayMessage", "0");
        data["friends"]["Sounds_PlayOnline"] = new KeyValue("Sounds_PlayOnline", "0");
        data["friends"]["Sounds_PlayIngame"] = new KeyValue("Sounds_PlayIngame", "0");
        data["friends"]["Notifications_ShowMessage"] = new KeyValue("Notifications_ShowMessage", "0");
        data["friends"]["Notifications_ShowOnline"] = new KeyValue("Notifications_ShowOnline", "0");
        data["friends"]["Notifications_ShowIngame"] = new KeyValue("Notifications_ShowIngame", "0");
        data["friends"]["Notifications_EventsAndAnnouncements"] = new KeyValue("Notifications_EventsAndAnnouncements", "0");
        data["friends"]["ChatFlashMode"] = new KeyValue("ChatFlashMode", "0");

        return data;
    }

    private void UpdateUserSharedConfig(string accountId)
    {
        // Updates other user related config
        var steamConfigDir = SteamConfig.GetConfigDirectory();
        var userSharedConfigPath = Path.Join(steamConfigDir, "userdata", accountId, "7", "remote", "sharedconfig.vdf");
        var parent = Directory.GetParent(userSharedConfigPath)!.FullName;
        Directory.CreateDirectory(parent);

        if (!File.Exists(userSharedConfigPath))
        {
            var newSharedConfigData = UpdateUserSharedConfig(new KeyValue("UserRoamingConfigStore"));
            newSharedConfigData.SaveToFile(userSharedConfigPath, false);
            return;
        }

        var stream = File.OpenText(path);
        var content = stream.ReadToEnd();
        var userSharedConfigData = KeyValue.LoadFromString(content)!;
        stream.Close();
        userSharedConfigData = UpdateUserSharedConfig(userSharedConfigData);
        userSharedConfigData.SaveToFile(userSharedConfigPath, false);
    }

    private KeyValue UpdateUserSharedConfig(KeyValue data)
    {
        data["DisableAllToasts"] = new KeyValue("DisableAllToasts", "1");
        data["DisableToastsInGame"] = new KeyValue("DisableToastsInGame", "1");

        return data;
    }
}

