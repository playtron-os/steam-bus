using System.Text.Json;
using SteamBusClientBridge.App.Models;
using Steamworks;

namespace SteamBusClientBridge.App.Core;

public class SteamAchievements : IDisposable
{
    private Callback<UserStatsReceived_t> userStatsReceivedCallback;
    private Callback<UserStatsStored_t> userStatsStoredCallback;
    private Callback<UserAchievementStored_t> userAchievementStoredCallback;
    private readonly HashSet<string> achieved = [];

    private readonly uint appId;
    private uint achievementCount = 0;

    public Action<string, AchievementData>? AchivementUnlocked;

    bool firstRun = true;

    public SteamAchievements(uint appId)
    {
        this.appId = appId;
        userStatsReceivedCallback = Callback<UserStatsReceived_t>.Create(OnUserStatsReceived);
        userStatsStoredCallback = Callback<UserStatsStored_t>.Create(OnUserStatsStored);
        userAchievementStoredCallback = Callback<UserAchievementStored_t>.Create(OnAchievementStored);

        var res = SteamUserStats.RequestCurrentStats();
        Console.WriteLine($"Result requesting current stats: {res}");
    }

    public void Dispose()
    {
        userStatsReceivedCallback?.Dispose();
        userStatsStoredCallback?.Dispose();
        userAchievementStoredCallback?.Dispose();
        GC.SuppressFinalize(this);
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

        ProcessAchievements();
    }

    private void OnUserStatsStored(UserStatsStored_t callback)
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

        // Set first run since if this is triggered, achievements were reset
        firstRun = true;

        ProcessAchievements();
    }

    private void OnAchievementStored(UserAchievementStored_t callback)
    {
        var apiName = callback.m_rgchAchievementName;
        Console.WriteLine($"Achievement unlocked: {apiName}");

        if (callback.m_nGameID != appId)
        {
            Console.WriteLine($"Got achievement data for the wrong appId:{callback.m_nGameID}, expecteD:{appId}");
            return;
        }

        var achievement = GetAchievement(apiName);
        if (achievement?.Unlocked ?? false)
        {
            AchivementUnlocked?.Invoke(apiName, achievement.Value);
            achieved.Add(apiName);
        }
        else
            achieved.Remove(apiName);
    }

    private void ProcessAchievements()
    {
        uint count = SteamUserStats.GetNumAchievements();

        if (count != achievementCount)
        {
            achievementCount = count;
            Console.WriteLine($"Total number of achievements: {count}");
        }

        for (uint i = 0; i < count; i++)
        {
            string apiName = SteamUserStats.GetAchievementName(i);
            var achievement = GetAchievement(apiName);

            if (achievement?.Unlocked ?? false)
            {
                if (!achieved.Contains(apiName) && !firstRun)
                {
                    Console.WriteLine($"Achievement unlocked: {apiName}");
                    AchivementUnlocked?.Invoke(apiName, achievement.Value);
                }

                achieved.Add(apiName);
            }
            else
                achieved.Remove(apiName);
        }

        firstRun = false;
    }

    public AchievementData? GetAchievement(string apiName)
    {
        if (SteamUserStats.GetAchievementAndUnlockTime(apiName, out bool unlocked, out uint unlockTime))
        {
            string name = SteamUserStats.GetAchievementDisplayAttribute(apiName, "name");
            string desc = SteamUserStats.GetAchievementDisplayAttribute(apiName, "desc");
            int icon = SteamUserStats.GetAchievementIcon(apiName);
            string hidden = SteamUserStats.GetAchievementDisplayAttribute(apiName, "hidden");
            SteamUserStats.GetAchievementProgressLimits(apiName, out float minProgress, out float maxProgress);
            SteamUserStats.GetAchievementAchievedPercent(apiName, out float percent);

            var iconData = new AchievementIcon(icon);

            if (SteamUtils.GetImageSize(icon, out uint width, out uint height))
            {
                var buffer = new byte[width * height * 4]; // RGBA

                if (SteamUtils.GetImageRGBA(icon, buffer, buffer.Length))
                {
                    iconData.Data = buffer;
                    iconData.Width = width;
                    iconData.Height = height;
                }
            }

            return new AchievementData
            {
                Name = name,
                Desc = desc,
                Icon = iconData,
                Hidden = hidden == "1",
                MinProgress = minProgress,
                MaxProgress = maxProgress,
                Percent = percent,
                Unlocked = unlocked,
                UnlockTime = unlockTime,
            };
        }

        return null;
    }

    public List<AchievementData> GetAchievements()
    {
        var achievements = new List<AchievementData>();
        uint count = SteamUserStats.GetNumAchievements();

        for (uint i = 0; i < count; i++)
        {
            string apiName = SteamUserStats.GetAchievementName(i);
            var achievement = GetAchievement(apiName);
            if (achievement.HasValue) achievements.Add(achievement.Value);
        }

        return achievements;
    }
}
