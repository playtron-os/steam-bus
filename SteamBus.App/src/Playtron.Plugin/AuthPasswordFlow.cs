using Tmds.DBus;

namespace Playtron.Plugin;

[Dictionary]
public class PasswordFlowProperties : IEnumerable<KeyValuePair<string, object>>
{
  public string AuthenticatedUser = "";
  public int Status;

  System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
  {
    return this.GetEnumerator();
  }

  public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
  {
    yield return new KeyValuePair<string, object>(nameof(AuthenticatedUser), AuthenticatedUser);
    yield return new KeyValuePair<string, object>(nameof(Status), Status);
  }
}

[DBusInterface("one.playtron.auth.PasswordFlow")]
public interface IAuthPasswordFlow : IDBusObject
{
  // Login to the service with the given username and password
  Task LoginAsync(string username, string password);
  // Logout of the service for the given user
  Task LogoutAsync(string username);

  // Emitted when logged in
  Task<IDisposable> WatchLoggedInAsync(Action<string> reply);
  // Emitted when logged out
  Task<IDisposable> WatchLoggedOutAsync(Action<string> reply);

  // Get the given property
  Task<object> GetAsync(string prop);
  // Set the given property
  Task SetAsync(string prop, object val);

  // Emitted when a client requests all properties
  Task<PasswordFlowProperties> GetAllAsync();
  // Emitted when properties have changed
  Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
}
