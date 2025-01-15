using Tmds.DBus;
using SteamBus.DBus;
using System.Reflection;
using Xdg.Directories;

namespace SteamBus;

class SteamBus
{
  static async Task Main(string[] args)
  {
    Console.WriteLine("Starting SteamBus v{0}", Assembly.GetExecutingAssembly().GetName().Version);

    var depotConfigStore = await DepotConfigStore.CreateAsync();
    var dependenciesStore = await DepotConfigStore.CreateAsync([$"{BaseDirectory.DataHome}/steambus/tools"]);
    var displayManager = new DisplayManager();

    string? busAddress = Address.Session;
    if (busAddress is null)
    {
      Console.Write("Can not determine system bus address");
      return;
    }

    // Connect to the bus
    using Connection connection = new Connection(busAddress);
    await connection.ConnectAsync();
    Console.WriteLine("Connected to user session bus.");

    await connection.RegisterServiceAsync("one.playtron.SteamBus");
    Console.WriteLine("Registered address: one.playtron.SteamBus");

    // Register the Steam Manager object
    await connection.RegisterObjectAsync(new Manager(connection, depotConfigStore, dependenciesStore, displayManager));

    // Create a default DBusSteamClient instance
    string path = "/one/playtron/SteamBus/SteamClient0";

    DBusSteamClient client = new DBusSteamClient(new ObjectPath(path), depotConfigStore, dependenciesStore, displayManager);
    await connection.RegisterObjectAsync(client);

    // Register with Playserve
    try
    {
      var pluginManager = connection.CreateProxy<IPluginManager>(
        "one.playtron.Playserve",
        "/one/playtron/plugins/Manager"
      );
      await pluginManager.RegisterPluginAsync("one.playtron.SteamBus", path);

      await pluginManager.WatchOnDriveAddedAsync(async (driveInfo) =>
      {
        Console.WriteLine($"Drive:{driveInfo.Name} added");
        await depotConfigStore.Reload();
        client.EmitInstalledAppsUpdated();
      });

      await pluginManager.WatchOnDriveRemovedAsync(async (driveName) =>
      {
        Console.WriteLine($"Drive:{driveName} removed");
        await depotConfigStore.Reload();
        client.EmitInstalledAppsUpdated();
      });
    }
    catch (Exception ex)
    {
      Console.WriteLine($"Error registering plugin to playserve, ex:{ex}");
    }

    // Run forever
    await Task.Delay(-1);

    return;
  }
}


