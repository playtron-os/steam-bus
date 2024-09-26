using System.Security.Cryptography;
using System.Text;
using SteamKit2;


namespace Steam.Config;

/*
"MachineUserConfigStore"
{
	"Software"
	{
		"Valve"
		{
			"Steam"
			{
				"ConnectCache"
				{
					"111111111"		"abc123..."
				}
			}
		}
	}
}
*/

public class LocalConfig
{
  private KeyValue? data;
  public string path;
  public const string filename = "local.vdf";

  // Load the local config from the given custom path
  public LocalConfig(string path)
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

  // Returns the default path to local.vdf: "~/.local/share/Steam/local.vdf"
  public static string DefaultPath()
  {
    string baseDir = SteamConfig.GetConfigDirectory();
    var path = $"{baseDir}/{filename}";

    return path;
  }

  // Load the configuration file from the filesystem
  public void Reload()
  {
    var stream = File.OpenText(this.path);
    var content = stream.ReadToEnd();

    var data = KeyValue.LoadFromString(content);
    this.data = data;
  }

  // Save the configuration
  public void Save()
  {
    this.data?.SaveToFile(this.path, false);
  }

  // Add the given refresh token to the 'ConnectCache' section of the 'MachineUserConfigStore'
  public void SetRefreshToken(string username, string refreshToken)
  {
    // The ConnectCache key is the hex-encoded CRC32 hash of the username with "1" added to the end.
    string key = SteamConfig.GetUsernameCrcString(username);

    // Encrypt the token into a hex-encoded string
    string value = SteamConfig.EncryptTokenForUser(username, refreshToken);

    // Ensure all keys exist
    if (this.data == null)
    {
      var data = new KeyValue("MachineUserConfigStore");
      this.data = data;
    }
    if (String.IsNullOrEmpty(this.data["Software"].Name))
    {
      this.data["Software"] = new KeyValue("Software");
    }
    if (String.IsNullOrEmpty(this.data["Software"]["Valve"].Name))
    {
      this.data["Software"]["Valve"] = new KeyValue("Valve");
    }
    if (String.IsNullOrEmpty(this.data["Software"]["Valve"]["Steam"].Name))
    {
      this.data["Software"]["Valve"]["Steam"] = new KeyValue("Steam");
    }
    if (String.IsNullOrEmpty(this.data["Software"]["Valve"]["Steam"]["ConnectCache"].Name))
    {
      this.data["Software"]["Valve"]["Steam"]["ConnectCache"] = new KeyValue("ConnectCache");
    }

    // Save the key/value pair
    if (this.data != null)
    {
      this.data["Software"]["Valve"]["Steam"]["ConnectCache"][key] = new KeyValue(key, value);
    }
  }

  // Get the refresh token for the given user. Returns null if user is not found.
  public string? GetRefreshToken(string username)
  {
    if (this.data == null)
    {
      Console.WriteLine("Data is null!");
      return null;
    }

    // The ConnectCache key is the hex-encoded CRC32 hash of the username with "1" added to the end.
    string key = SteamConfig.GetUsernameCrcString(username);

    // Get the entry from the KV map
    var entry = this.data["Software"]["Valve"]["Steam"]["ConnectCache"][key];
    if (entry == null || String.IsNullOrEmpty(entry.Name) || entry.Value == null)
    {
      Console.WriteLine("Entry is null!");
      return null;
    }

    // Decrypt the token value
    var token = SteamConfig.DecryptTokenForUser(username, entry.Value);

    return token;
  }
}

