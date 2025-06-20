using Steamworks;
using Steam.Session;

public class SteamAchievements : IDisposable
{
    private readonly SteamSession session;
    private Callback<UserStatsReceived_t> userStatsReceivedCallback;
    private readonly HashSet<string> achieved = new HashSet<string>();

    private Thread? callbackThread;
    private bool running = false;

    private uint appId = 0;

    public SteamAchievements(SteamSession session)
    {
        this.session = session;
        userStatsReceivedCallback = Callback<UserStatsReceived_t>.Create(OnUserStatsReceived);
    }

    public void Dispose()
    {
        running = false;
        if (callbackThread != null && callbackThread.IsAlive)
            callbackThread.Join();

        userStatsReceivedCallback?.Dispose();
    }

    public void StartTracking(uint appId)
    {
        Console.WriteLine($"Start tracking achievements for appId:{appId}");
        this.appId = appId;
        var result = SteamAPI.InitEx(out string errorMsg);
        if (result != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
        {
            Console.WriteLine($"Error initializing Steam API, result:{result}, err:{errorMsg}");
            return;
        }

        // Request stats initially
        SteamUserStats.RequestCurrentStats();

        // Run callback loop
        running = true;
        callbackThread = new Thread(() =>
        {
            while (running)
            {
                SteamAPI.RunCallbacks();
                Thread.Sleep(2000);
            }
        })
        {
            IsBackground = true
        };
        callbackThread.Start();
    }

    public void StopTracking()
    {
        if (appId == 0) return;

        Console.WriteLine($"Stop tracking achievements for appId:{appId}");
        appId = 0;
        running = false;
    }

    private void OnUserStatsReceived(UserStatsReceived_t callback)
    {
        if (callback.m_eResult != EResult.k_EResultOK)
        {
            Console.WriteLine($"Error getting user stats, result:{callback.m_eResult}");
            return;
        }
        if (callback.m_nGameID != appId)
        {
            Console.WriteLine($"Got achievement data for the wrong appId:{callback.m_nGameID}, expecteD:{appId}");
            return;
        }

        uint count = SteamUserStats.GetNumAchievements();
        Console.WriteLine($"Number of achievements: {count}");

        for (uint i = 0; i < count; i++)
        {
            string apiName = SteamUserStats.GetAchievementName(i);

            if (SteamUserStats.GetAchievementAndUnlockTime(apiName, out bool unlocked, out uint unlockTime) && unlocked)
            {
                if (!achieved.Contains(apiName))
                {
                    string name = SteamUserStats.GetAchievementDisplayAttribute(apiName, "name");
                    string desc = SteamUserStats.GetAchievementDisplayAttribute(apiName, "desc");
                    int icon = SteamUserStats.GetAchievementIcon(apiName);
                    string hidden = SteamUserStats.GetAchievementDisplayAttribute(apiName, "hidden");
                    SteamUserStats.GetAchievementProgressLimits(apiName, out float minProgress, out float maxProgress);
                    SteamUserStats.GetAchievementAchievedPercent(apiName, out float percent);

                    Console.WriteLine($"New achievement! {name}");
                    Console.WriteLine($"Description: {desc}");
                    Console.WriteLine($"Icon: {icon}");
                    Console.WriteLine($"Hidden: {hidden == "1"}");
                    Console.WriteLine($"Unlock Time: {unlockTime}");
                    Console.WriteLine($"MinProgress/MaxProgress: {minProgress}/{maxProgress}");
                    Console.WriteLine($"Achieved Percent: {percent}");
                }

                achieved.Add(apiName);
            }
        }
    }
}
