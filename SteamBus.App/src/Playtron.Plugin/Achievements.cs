using Tmds.DBus;

namespace Playtron.Plugin;

/// Interface definition for achievements
[DBusInterface("one.playtron.plugin.Achievements")]
public interface IPluginAchievements : IDBusObject
{
    // Methods
    Task<string> GetAchievementsAsync();

    // Signals
    Task<IDisposable> WatchAchievementUnlockedAsync(Action<string> reply);
}
