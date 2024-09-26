using Tmds.DBus;

namespace Playtron.Plugin;

[DBusInterface("one.playtron.plugin.LibraryProvider")]
public interface IPluginLibraryProvider : IDBusObject
{
  //Task<int> GreetAsync();
}


