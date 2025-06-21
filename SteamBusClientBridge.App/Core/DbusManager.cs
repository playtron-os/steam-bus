using System.Text.Json;
using SteamBusClientBridge.App.Models;
using Tmds.DBus;

namespace SteamBusClientBridge.App.Core;

[DBusInterface("one.playtron.SteamBusClientBridge.Manager")]
public interface IDbusManager : IDBusObject
{
    Task<string> GetAchievementsAsync();
    Task<IDisposable> WatchAchievementUnlockedAsync(Action<(string, string)> reply);
}

class DbusManager : IDbusManager
{
    public static readonly ObjectPath Path = new ObjectPath("/one/playtron/SteamBusClientBridge");
    public Connection connection;

    private SteamAchievements achievements;

    public event Action<(string, string)>? OnAchievementUnlocked;

    private readonly JsonSerializerOptions serializerOptions;

    // Creates a new manager instance with the given DBus connection
    public DbusManager(Connection connection, SteamAchievements achievements)
    {
        serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        this.connection = connection;
        this.achievements = achievements;

        achievements.AchivementUnlocked = (apiName, data) =>
        {
            var json = JsonSerializer.Serialize(data, serializerOptions);
            OnAchievementUnlocked?.Invoke((apiName, json));
        };
    }

    Task<string> IDbusManager.GetAchievementsAsync()
    {
        var achievements = this.achievements.GetAchievements();
        var json = JsonSerializer.Serialize(achievements, serializerOptions);

        return Task.FromResult(json);
    }

    Task<IDisposable> IDbusManager.WatchAchievementUnlockedAsync(Action<(string, string)> reply)
    {
        return SignalWatcher.AddAsync(this, nameof(OnAchievementUnlocked), reply);
    }

    public ObjectPath ObjectPath { get { return Path; } }
}


