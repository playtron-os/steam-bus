using System.Runtime.InteropServices;
using Tmds.DBus;

[StructLayout(LayoutKind.Sequential)]
public struct DriveInfo
{
    public string Vendor { get; set; }
    public string Model { get; set; }
    public string HintName { get; set; }
    public string Name { get; set; }
    public ulong AvailableSpace { get; set; }
    public ulong MaxSize { get; set; }
    public string Path { get; set; }
    public string FileSystem { get; set; }
    public bool IsRoot { get; set; }
    public bool NeedsFormatting { get; set; }
}

[DBusInterface("one.playtron.plugin.Manager")]
public interface IPluginManager : IDBusObject
{
    // Methods
    Task<bool> IsPluginRegisteredAsync(string pluginName);
    Task RegisterPluginAsync(string pluginName, ObjectPath pluginPath);
    Task<DriveInfo[]> GetDrivesAsync();

    // Properties
    Task<string> GetVersionAsync();
    Task<string> GetRunningAppIdAsync();
    Task<string> GetRunningProviderAsync();

    // Signals
    Task<IDisposable> WatchPluginRegisteredAsync(Action<(string pluginName, uint pluginId)> handler);
    Task<IDisposable> WatchOnDriveAddedAsync(Action<DriveInfo> handler);
    Task<IDisposable> WatchOnDriveRemovedAsync(Action<string> handler);
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler, Action<Exception>? exception = null);
}
