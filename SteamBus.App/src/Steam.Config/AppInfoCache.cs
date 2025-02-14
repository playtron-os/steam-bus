using System.Security.Cryptography;
using System.Text;
using Playtron.Plugin;
using SteamKit2;
using Xdg.Directories;


namespace Steam.Config;

public class AppInfoCache
{
    public string path;
    public const string foldername = "app_info_cache";

    // Load the local config from the given custom path
    public AppInfoCache(string path)
    {
        this.path = path;
    }

    // Returns the default path to local.vdf: "~/.local/share/steambus/app_info_cache"
    public static string DefaultPath()
    {
        string baseDir = $"{BaseDirectory.DataHome}/steambus";
        var path = $"{baseDir}/{foldername}";

        return path;
    }

    // Save the app info value
    public void Save(uint appId, KeyValue? data)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var finalPath = GetFinalPath(appId);

                if (data == null)
                {
                    try
                    {
                        File.Delete(finalPath);
                    }
                    catch (Exception) { }
                }
                else
                {
                    Directory.CreateDirectory(path);
                    data.SaveToFile(finalPath, false);
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Error saving appinfo cache for appid:{appId}, err:{exception}");
            }
        });
    }

    // Get the cached app info value
    public KeyValue? GetCached(uint appId)
    {
        try
        {
            var content = Disk.ReadFileWithRetry(GetFinalPath(appId));
            return KeyValue.LoadFromString(content);
        }
        catch (Exception err)
        {
            Console.WriteLine($"Failed to read cache for {appId}, err:{err}");
            return null;
        }
    }

    private string GetFinalPath(uint appId) => Path.Join(path, $"{appId}.vdf");
}

