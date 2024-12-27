using Tmds.DBus;
using SteamBus.DBus;
using System.Reflection;

namespace SteamBus;

class SteamBus
{
  static async Task Main(string[] args)
  {
    Console.WriteLine("Starting SteamBus v{0}", Assembly.GetExecutingAssembly().GetName().Version);

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
    await connection.RegisterObjectAsync(new Manager(connection));

    // Create a default DBusSteamClient instance
    string path = "/one/playtron/SteamBus/SteamClient0";
    DBusSteamClient client = new DBusSteamClient(new ObjectPath(path));
    await connection.RegisterObjectAsync(client);

    // Register with Playserve
    var pluginManager = connection.CreateProxy<IPluginManager>(
      "one.playtron.Playserve",
      "/one/playtron/plugins/Manager"
    );
    await pluginManager.RegisterPluginAsync("one.playtron.SteamBus", path);

    // Run forever
    await Task.Delay(-1);

    return;
  }
}


