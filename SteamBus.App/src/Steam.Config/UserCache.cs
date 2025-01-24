using System.Security.Cryptography;
using System.Text;
using SteamKit2;
using Xdg.Directories;


namespace Steam.Config;

public class UserCache
{
    public const string AVATAR_KEY = "avatar";
    public const string PERSONA_NAME = "personaname";

    private KeyValue? data;
    public string path;
    public const string filename = "usercache.vdf";

    // Load the local config from the given custom path
    public UserCache(string path)
    {
        this.path = path;
        this.Reload();
    }

    // Returns the default path to local.vdf: "~/.local/share/steambus/usercache.vdf"
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
            this.data = new KeyValue("UserCache");
            Save();
            return;
        }

        var stream = File.OpenText(this.path);
        var content = stream.ReadToEnd();

        var data = KeyValue.LoadFromString(content);
        this.data = data ?? new KeyValue("UserCache");
        stream.Close();
    }

    // Save the configuration
    public void Save()
    {
        Disk.EnsureParentFolderExists(path);
        this.data?.SaveToFile(this.path, false);
    }

    // Add the value to the cache
    public void SetKey(string key, uint identifier, string avatar)
    {
        var identifierStr = identifier.ToString();

        if (string.IsNullOrEmpty(this.data![identifierStr]?.Name))
            this.data[identifierStr] = new KeyValue(identifierStr);

        this.data[identifierStr][key] = new KeyValue(key, avatar);
    }

    // Gets the value from the cache
    public string? GetKey(string key, uint identifier)
    {
        return this.data![identifier.ToString()]?[key]?.Value;
    }
}

