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
  Task LoginAsync(string username, string password);
  Task LogoutAsync(string username);

  Task<PasswordFlowProperties> GetAllAsync();
}
