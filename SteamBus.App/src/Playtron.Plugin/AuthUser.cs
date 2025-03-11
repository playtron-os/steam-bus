using Tmds.DBus;

namespace Playtron.Plugin;

[Dictionary]
public class UserProperties : IEnumerable<KeyValuePair<string, object>>
{
  public string Username = "";
  public string Avatar = "";
  public string Identifier = "";
  public int Status;
  public (string, string) Tokens = ("", "");

  System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
  {
    return this.GetEnumerator();
  }

  public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
  {
    yield return new KeyValuePair<string, object>(nameof(Username), Username);
    yield return new KeyValuePair<string, object>(nameof(Avatar), Avatar);
    yield return new KeyValuePair<string, object>(nameof(Identifier), Identifier);
    yield return new KeyValuePair<string, object>(nameof(Status), Status);
    yield return new KeyValuePair<string, object>(nameof(Tokens), Tokens);
  }
}


[DBusInterface("one.playtron.auth.User")]
public interface IUser : IDBusObject
{
  Task<bool> ChangeUserFromTokensAsync(string userId, string accessToken, string refreshToken);
  Task<bool> ChangeUserAsync(string user_id);
  Task LogoutAsync(string user_id);

  // Get given property
  Task<object> GetAsync(string prop);
  // Set the given property
  Task SetAsync(string prop, object val);

  Task<UserProperties> GetAllAsync();
  Task<IDisposable> WatchPropertiesAsync(Action<PropertyChanges> handler);
  Task<IDisposable> WatchAuthErrorAsync(Action<string> handler);
}
