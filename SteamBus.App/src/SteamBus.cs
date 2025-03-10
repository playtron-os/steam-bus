using Tmds.DBus;
using SteamBus.DBus;
using System.Reflection;
using Xdg.Directories;
using Steam.Config;
using System.Text.RegularExpressions;

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
      Console.Write("Can not determine session bus address");
      return;
    }

    string? systemBusAddress = Address.System;
    if (systemBusAddress is null)
    {
      Console.Write("Can not determine system bus address");
      return;
    }

    // Connect to the bus
    using Connection connection = new Connection(busAddress);
    await connection.ConnectAsync();
    Console.WriteLine("Connected to user session bus.");

    // Connect to the system bus
    using Connection systemConnection = new Connection(systemBusAddress);
    await systemConnection.ConnectAsync();
    Console.WriteLine("Connected to system bus.");

    await connection.RegisterServiceAsync("one.playtron.SteamBus");
    Console.WriteLine("Registered address: one.playtron.SteamBus");

    var networkManager = systemConnection.CreateProxy<INetworkManager>(
      "org.freedesktop.NetworkManager",
      "/org/freedesktop/NetworkManager"
    );

    // Register the Steam Manager object
    await connection.RegisterObjectAsync(new Manager(connection, depotConfigStore, dependenciesStore, displayManager, networkManager));

    // Create a default DBusSteamClient instance
    string path = "/one/playtron/SteamBus/SteamClient0";

    DBusSteamClient client = new DBusSteamClient(new ObjectPath(path), depotConfigStore, dependenciesStore, displayManager, networkManager);
    await connection.RegisterObjectAsync(client);

    // Register with Playserve
    try
    {
      var pluginManager = connection.CreateProxy<IPluginManager>(
        "one.playtron.Playserve",
        "/one/playtron/plugins/Manager"
      );

      await pluginManager.WatchOnDriveAddedAsync(async (driveInfo) =>
      {
        if (driveInfo.NeedsFormatting)
        {
          Console.WriteLine($"Ignoring Drive:{driveInfo.Name} because it has incorrect format");
          return;
        }

        Console.WriteLine($"Drive:{driveInfo.Name} at Path:{driveInfo.Path} added");

        var libraryConfig = await LibraryFoldersConfig.CreateAsync();
        libraryConfig.AddDiskEntry(Regex.Unescape(driveInfo.Path));
        libraryConfig.Save();

        await depotConfigStore.Reload();
        client.EmitInstalledAppsUpdated();
      });

      await pluginManager.WatchOnDriveRemovedAsync(async (driveName) =>
      {
        Console.WriteLine($"Drive:{driveName} removed");
        await depotConfigStore.Reload();
        client.EmitInstalledAppsUpdated();
      });

      var drives = await pluginManager.GetDrivesAsync();
      var libraryConfig = await LibraryFoldersConfig.CreateAsync();
      foreach (var drive in drives)
      {
        if (drive.NeedsFormatting) continue;

        try
        {
          Console.WriteLine($"Verifying drive {drive.Name} is in library config");
          libraryConfig.AddDiskEntry(Regex.Unescape(drive.Path));
        }
        catch (Exception err)
        {
          Console.Error.WriteLine($"Error adding drive to library, err:{err}");
        }
      }
      libraryConfig.Save();

      var directories = libraryConfig.GetInstallDirectories();
      foreach (var dir in directories)
        await depotConfigStore.ReloadApps(dir);

      await pluginManager.RegisterPluginAsync("one.playtron.SteamBus", path);
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


