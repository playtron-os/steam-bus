using Tmds.DBus;

namespace Playtron.Plugin;

[DBusInterface("one.playtron.auth.QrFlow")]
public interface IAuthQrFlow : IDBusObject
{
  Task BeginAsync();
  Task CancelAsync();
  Task<IDisposable> WatchCodeUpdatedAsync(Action<string> handler);
}

