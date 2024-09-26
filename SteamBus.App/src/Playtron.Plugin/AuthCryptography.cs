using Tmds.DBus;

namespace Playtron.Plugin;

[DBusInterface("one.playtron.auth.Cryptography")]
public interface IAuthCryptography : IDBusObject
{
  // Returns the public key used to send encrypted secrets. The data must be
  // returned as a PEM encoded string. The key type must be one of:
  // ["RSA-SHA256"]
  Task<(string keyType, string data)> GetPublicKeyAsync();
}


