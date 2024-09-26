using Tmds.DBus;

namespace Playtron.Plugin;

[Dictionary]
public class QrFlowProperties : IEnumerable<KeyValuePair<string, object>>
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

[DBusInterface("one.playtron.auth.QrFlow")]
public interface IAuthQrFlow : IDBusObject
{
  Task<int> LoginAsync(string username, string password);
  Task<int> LogoutAsync(string username);

  Task<QrFlowProperties> GetAllAsync();
}

