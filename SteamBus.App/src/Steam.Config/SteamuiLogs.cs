using System.Security.Cryptography;
using System.Text;
using Playtron.Plugin;
using SteamKit2;
using Xdg.Directories;


namespace Steam.Config;

public class SteamuiLogs
{
    private KeyValue? data;
    public string path;
    public const string filename = "steamui_login.txt";

    // Load the local config from the given custom path
    public SteamuiLogs(string path)
    {
        this.path = path;
    }

    // Returns the default path to local.vdf: "~/.local/share/Steam/logs/steamui_login.vdf"
    public static string DefaultPath()
    {
        string baseDir = SteamConfig.GetConfigDirectory();
        var path = $"{baseDir}/logs/{filename}";

        return path;
    }

    public void Delete()
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception) { }
    }

    public bool Exists() => File.Exists(path);

    public bool IsLoginFailed()
    {
        if (!File.Exists(path)) return false;

        try
        {
            using (var stream = File.OpenText(path))
            {
                string? line;
                while ((line = stream.ReadLine()) != null)
                {
                    if (line.Contains("WaitingForCredentials - Password is not set") || line.Contains("WaitingForCredentials - Access Denied") || line.Contains("WaitingForCredentials - Invalid Password"))
                        return true;

                    if (line.Contains("SetLoginState")) return false;
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Error reading steamui_login.txt file, err:{exception}");
        }

        return false;
    }
}

