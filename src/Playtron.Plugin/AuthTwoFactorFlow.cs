using Tmds.DBus;

namespace Playtron.Plugin;

[Dictionary]
public class TwoFactorFlowProperties : IEnumerable<KeyValuePair<string, object>>
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

[DBusInterface("one.playtron.auth.TwoFactorFlow")]
public interface IAuthTwoFactorFlow : IDBusObject
{
  // Send the given two-factor code.
  Task SendCodeAsync(string code);

  // Emitted when a two-factor challenge is required.
  Task<IDisposable> WatchTwoFactorRequiredAsync(Action<(bool previousCodeWasIncorrect, string message)> reply);
  // Emitted when an email two-factor challenge is required.
  Task<IDisposable> WatchEmailTwoFactorRequiredAsync(Action<(string email, bool previousCodeWasIncorrect, string message)> reply);
}

