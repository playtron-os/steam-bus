using Tmds.DBus;

namespace Playtron.Plugin;

/// Interface definition for plugin dependencies that are required by the plugin
[DBusInterface("one.playtron.plugin.PluginDependencies")]
public interface IPluginDependencies : IDBusObject
{
    // Methods

    // Gets a list of all installed dependencies
    Task<InstalledAppDescription[]> GetInstalledDependenciesAsync();
    // Gets a list of the dependencies required to run this plugin which need to be installed
    Task<ProviderItem[]> GetRequiredDependenciesAsync();
    // Gets info about a single dependency item
    Task<ProviderItem> GetDependencyItemAsync(string appId);
    // Starts installation of all the required dependencies
    Task InstallAllRequiredDependenciesAsync();

    // Signals
    Task<IDisposable> WatchInstallStartedAsync(Action<InstallStartedDescription> reply);
    Task<IDisposable> WatchInstallProgressedAsync(Action<InstallProgressedDescription> reply);
    Task<IDisposable> WatchInstallCompletedAsync(Action<string> reply);
    Task<IDisposable> WatchInstallFailedAsync(Action<(string appId, string error)> reply);
    Task<IDisposable> WatchDependencyNewVersionFoundAsync(Action<(string appId, string version)> reply);
}
