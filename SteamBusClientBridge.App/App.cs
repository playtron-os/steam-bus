using System.Reflection;
using System.Runtime.InteropServices;
using SteamBusClientBridge.App.Core;
using Steamworks;

namespace SteamBusClientBridge.App;

class App
{
    [DllImport("libc")]
    private static extern int prctl(int option, int arg2, int arg3, int arg4, int arg5);
    const int PR_SET_PDEATHSIG = 1;
    const int SIGTERM = 15;

    public static void EnsureParentDeathSignal()
    {
        _ = prctl(PR_SET_PDEATHSIG, SIGTERM, 0, 0, 0);
    }

    static async Task Main(string[] args)
    {
        if (OperatingSystem.IsLinux())
            EnsureParentDeathSignal();

        if (args.Length == 0 || !uint.TryParse(args[0], out uint appId))
        {
            Console.WriteLine("Usage: SteamBusClientBridge <AppId>");
            return;
        }

        Console.WriteLine("Starting SteamBusClientBridge v{0} for appId:{1}", Assembly.GetExecutingAssembly().GetName().Version, appId);

        try
        {
            if (!await WaitForSteamAsync()) return;

            var result = SteamAPI.InitEx(out string errorMsg);
            if (result != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
            {
                Console.WriteLine($"Error initializing Steam API, result:{result}, err:{errorMsg}");
                Environment.Exit(1);
            }

            var achievements = new SteamAchievements(appId);
            var dbus = new Dbus(achievements);

            _ = Task.Run(async () =>
            {
                try
                {
                    await dbus.Connect();
                    await Task.Delay(Timeout.Infinite);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"D-Bus failed: {ex}");
                }
            });

            while (true)
            {
                SteamAPI.RunCallbacks();
                await Task.Delay(1000);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex}");
            Environment.Exit(1);
        }
    }

    public static async Task<bool> WaitForSteamAsync(int timeoutSeconds = 10)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var startTime = DateTime.UtcNow;

        while (!SteamAPI.IsSteamRunning())
        {
            if (DateTime.UtcNow - startTime > timeout)
            {
                Console.WriteLine("Steam did not start in time.");
                return false;
            }

            await Task.Delay(500);
        }

        Console.WriteLine("Steam is running.");
        return true;
    }
}