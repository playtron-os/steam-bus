using Tmds.DBus;

namespace Playtron.Plugin;

[DBusInterface("one.playtron.auth.QrFlow")]
public interface IAuthQrFlow : IDBusObject
{
  Task Begin();
  Task Cancel();

  Task<IDisposable> WatchCodeUpdatedAsync(Action<string> handler);
}

