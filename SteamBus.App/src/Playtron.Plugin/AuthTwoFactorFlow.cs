using Tmds.DBus;

namespace Playtron.Plugin;

[DBusInterface("one.playtron.auth.TwoFactorFlow")]
public interface IAuthTwoFactorFlow : IDBusObject
{
  // Send the given two-factor code.
  Task SendCodeAsync(string code);

  // Emitted when a two-factor challenge is required.
  Task<IDisposable> WatchTwoFactorRequiredAsync(Action<(bool previousCodeWasIncorrect, string message)> reply);
  // Emitted when an email two-factor challenge is required.
  Task<IDisposable> WatchEmailTwoFactorRequiredAsync(Action<(string email, bool previousCodeWasIncorrect, string message)> reply);
  // Emitted when a mobile app approval is required.
  Task<IDisposable> WatchConfirmationRequiredAsync(Action<string> reply);
}

