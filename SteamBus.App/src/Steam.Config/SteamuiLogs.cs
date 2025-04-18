using System.Security.Cryptography;
using System.Text;
using Playtron.Plugin;
using SteamKit2;
using Xdg.Directories;


namespace Steam.Config;

public class SteamuiLogs
{
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

    public async Task<bool> IsLoginFailed()
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                using (var stream = File.OpenText(path))
                {
                    while (true)
                    {
                        cts.Token.ThrowIfCancellationRequested();

                        var line = await stream.ReadLineAsync();
                        if (line == null)
                        {
                            await Task.Delay(50, cts.Token);
                            continue;
                        }

                        if (line.Contains("WaitingForCredentials - Password is not set")
                            || line.Contains("WaitingForCredentials - Already Logged In Elsewhere")
                            || line.Contains("WaitingForCredentials - Access Denied")
                            || line.Contains("WaitingForCredentials - Invalid Password")
                            || line.Contains("WaitingForCredentials - No Connection")
                            || line.Contains("WaitingForCredentials - Logged In Elsewhere"))
                            return true;

                        if (line.Contains("SetLoginState")) return false;
                    }
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

