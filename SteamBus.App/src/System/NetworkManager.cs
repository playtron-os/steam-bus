using Tmds.DBus;

public enum NmConnectivityStatus
{
    Unknown = 0,
    None = 1,
    Portal = 2,
    Limited = 3,
    Full = 4
}

[DBusInterface("org.freedesktop.NetworkManager")]
public interface INetworkManager : IDBusObject
{
    // Getter for properties
    Task<T> GetAsync<T>(string propertyName);

    // Connectivity signal
    Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler, Action<Exception>? exception = null);
}
