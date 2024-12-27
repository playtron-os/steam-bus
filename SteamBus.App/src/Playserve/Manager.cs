using Tmds.DBus;

[DBusInterface("one.playtron.plugin.Manager")]
public interface IPluginManager : IDBusObject
{
    // Methods
    Task<bool> IsPluginRegisteredAsync(string pluginName);
    Task RegisterPluginAsync(string pluginName, ObjectPath pluginPath);

    // Properties
    Task<string> GetVersionAsync();

    // Signals
    Task<IDisposable> WatchPluginRegisteredAsync(Action<(string pluginName, uint pluginId)> handler);
}