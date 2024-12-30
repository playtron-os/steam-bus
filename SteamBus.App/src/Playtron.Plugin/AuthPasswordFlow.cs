using Tmds.DBus;

namespace Playtron.Plugin;

[DBusInterface("one.playtron.auth.PasswordFlow")]
public interface IAuthPasswordFlow : IDBusObject
{
  // Login to the service with the given username and password
  Task LoginAsync(string username, string password);
}
