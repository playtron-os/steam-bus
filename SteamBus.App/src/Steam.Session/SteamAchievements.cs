using System.Diagnostics;
using System.Text.Json;
using SteamBusClientBridge.App.Core;
using SteamBusClientBridge.App.Models;
using Tmds.DBus;

public class SteamAchievements : IDisposable
{
    private uint appId = 0;
    private Process? childProcess;

    private Connection connection;
    private IDbusManager? dbusManager;

    public Action<string>? AchievementUnlocked;

    public SteamAchievements()
    {
        string? busAddress = Address.Session;
        if (busAddress is null)
        {
            Console.Error.Write("Can not determine session bus address");
            throw new Exception("No bus address");
        }

        connection = new Connection(busAddress);
    }

    public async Task StartTracking(uint appId)
    {
        try
        {
            this.appId = appId;

            Console.WriteLine($"Launching SteamBusClientBridge for appId:{appId}");

            string exeName = OperatingSystem.IsWindows() ? "SteamBusClientBridge.App.exe" : "SteamBusClientBridge.App";
            string exePath = Path.Combine(AppContext.BaseDirectory, exeName);

            if (!File.Exists(exePath))
            {
                Console.Error.WriteLine($"Steam Client Bridge App executable not found: {exePath}");
                return;
            }

            childProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"{appId}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    Environment =
                    {
                        ["SteamAppId"] = appId.ToString()
                    },
                }
            };

            childProcess.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine($"[Bridge] {e.Data}"); };
            childProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.Error.WriteLine($"[Bridge ERROR] {e.Data}"); };

            childProcess.Start();
            childProcess.BeginOutputReadLine();
            childProcess.BeginErrorReadLine();

            await connection.ConnectAsync();
            dbusManager = connection.CreateProxy<IDbusManager>(
                "one.playtron.SteamBusClientBridge",
                "/one/playtron/SteamBusClientBridge"
            );

            await dbusManager.WatchAchievementUnlockedAsync(OnSteamAchievementUnlocked);

            Console.WriteLine("SteamBusClientBridge started.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error launching SteamBusClientBridge for appId:{appId}, ex:{ex}");
        }
    }

    public void StopTracking()
    {
        if (childProcess != null && !childProcess.HasExited)
        {
            Console.WriteLine($"Stopping SteamBusClientBridge for appId:{appId}");
            try
            {
                childProcess.Kill(entireProcessTree: true);
                childProcess.WaitForExit();
                Console.WriteLine("Bridge process terminated.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to kill child process: {ex}");
            }
        }

        childProcess?.Dispose();
        childProcess = null;
        appId = 0;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        StopTracking();
    }

    public async Task<string> GetAchievements() => dbusManager == null ? "" : await dbusManager.GetAchievementsAsync();

    private void OnSteamAchievementUnlocked((string apiName, string json) res)
    {
        Console.WriteLine($"New achievement unlocked: {res.apiName}");
        AchievementUnlocked?.Invoke(res.json);
    }
}
