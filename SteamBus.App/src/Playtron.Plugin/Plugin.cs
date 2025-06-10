using Tmds.DBus;
using System.Reflection;

namespace Playtron.Plugin;


[Dictionary]
public class PlaytronPluginProperties
{
  public readonly string Id = "steam";
  public readonly string Name = "Steam";
  public readonly string Version = Assembly.GetExecutingAssembly().GetName().Version!.ToString();
  public readonly string MinimumApiVersion = "0.1.1";
}

[DBusInterface("one.playtron.plugin.Plugin")]
public interface IPlaytronPlugin : IDBusObject
{
  Task<object> GetAsync(string prop);
  Task<PlaytronPluginProperties> GetAllAsync();

}
