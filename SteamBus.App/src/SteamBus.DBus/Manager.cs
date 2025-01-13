using Tmds.DBus;

namespace SteamBus.DBus;

[DBusInterface("one.playtron.SteamBus.Manager")]
public interface IManager : IDBusObject
{
  Task<string> GreetAsync(string message);
  Task<string> CreateClientAsync();
}

class Manager : IManager
{
  public static readonly ObjectPath Path = new ObjectPath("/one/playtron/SteamBus");
  public Connection connection;
  public DepotConfigStore depotConfigStore;

  // Creates a new manager instance with the given DBus connection
  public Manager(Connection connection, DepotConfigStore depotConfigStore)
  {
    this.connection = connection;
    this.depotConfigStore = depotConfigStore;
  }

  // Create a new Steam Client instance. Returns the DBus path to the created
  // client.
  // TODO: Keep track of steam client instances
  public async Task<string> CreateClientAsync()
  {
    string path = "/one/playtron/SteamBus/SteamClient1";
    DBusSteamClient client = new DBusSteamClient(new ObjectPath(path), depotConfigStore);

    // Register the object with DBus
    await this.connection.RegisterObjectAsync(client);

    return path;
  }

  public Task<string> GreetAsync(string name)
  {
    return Task.FromResult($"Hello {name}!");
  }

  public ObjectPath ObjectPath { get { return Path; } }
}


