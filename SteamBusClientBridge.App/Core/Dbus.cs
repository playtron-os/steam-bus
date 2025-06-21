using Tmds.DBus;

namespace SteamBusClientBridge.App.Core;

public class Dbus
{
    private Connection connection;

    SteamAchievements achievements;

    public Dbus(SteamAchievements achievements)
    {
        this.achievements = achievements;

        string? busAddress = Address.Session;
        if (busAddress is null)
        {
            Console.Error.Write("Can not determine session bus address");
            throw new Exception("No bus address");
        }

        connection = new Connection(busAddress);
    }

    public async Task Connect()
    {
        await connection.ConnectAsync();

        await connection.RegisterServiceAsync("one.playtron.SteamBusClientBridge");
        Console.WriteLine("Registered address: one.playtron.SteamBusClientBridge");

        await connection.RegisterObjectAsync(new DbusManager(connection, achievements));
    }
}