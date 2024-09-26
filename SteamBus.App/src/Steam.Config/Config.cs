using System.IO.Hashing;
using SteamKit2;
using System.Security.Cryptography;
using System.Text;

namespace Steam.Config;

public class SteamConfig
{
  // Returns the path to the Steam client configuration directory. Throws an
  // exception if the user's home directory cannot be discovered.
  public static string GetConfigDirectory()
  {
    // Get the user's home directory
    string? homeDir = Environment.GetEnvironmentVariable("HOME");
    if (homeDir == null)
    {
      throw new Exception("Unable to determine HOME directory to find config");
    }

    string path = $"{homeDir}/.local/share/Steam";

    return path;
  }

  // Certain Steam client configuration files like 'local.vdf' reference the
  // Steam user by CRC32 hash with a "1" added to the end. Returns the CRC32
  // hash with "1" appended for the given username.
  public static string GetUsernameCrcString(string username)
  {
    // Get the lowercased username as bytes
    byte[] usernameBytes = Encoding.UTF8.GetBytes(username.ToLower());

    // The key in several configs is the hex-encoded CRC32 hash of the username with "1" added to the end.
    uint crcHash = Crc32.HashToUInt32(usernameBytes);
    string crcHashString = $"{crcHash:X}".ToLower();
    string key = $"{crcHashString}1";

    return key;
  }

  // Encrypt the given token for the given user. Will return a hex-encoded AES
  // encrypted string of the data.
  public static string EncryptTokenForUser(string username, string token)
  {
    // The ConnectCache value is a hex-encoded AES CBC encrypted value of the refreshToken
    // using the SHA256 hash of the username as the key.
    byte[] usernameBytes = Encoding.UTF8.GetBytes(username.ToLower());
    byte[] aesKey = SHA256.HashData(usernameBytes);

    var iv = RandomNumberGenerator.GetBytes(16);
    var encryptedData = Encoding.UTF8.GetBytes(token);
    encryptedData = SymmetricEncryptWithIV(encryptedData, aesKey, iv);

    // Convert the encrypted data to a hex-encoded string
    string value = Convert.ToHexString(encryptedData).ToLower();

    return value;
  }

  // Decrypt the given hex-encoded AES encrypted token for the given user.
  public static string DecryptTokenForUser(string username, string encToken)
  {
    // The ConnectCache value is a hex-encoded AES CBC encrypted value of the refreshToken
    // using the SHA256 hash of the username as the key.
    byte[] usernameBytes = Encoding.UTF8.GetBytes(username.ToLower());
    byte[] aesKey = SHA256.HashData(usernameBytes);

    // Decode the hex string into bytes
    var encryptedData = Convert.FromHexString(encToken);

    // Decrypt the token
    var decryptedData = CryptoHelper.SymmetricDecrypt(encryptedData, aesKey);
    var decryptedToken = Encoding.UTF8.GetString(decryptedData);

    return decryptedToken;
  }

  // Encrypt the given input with the given 32-byte AES key and IV.
  // Reference: https://github.com/SteamRE/SteamKit/blob/0931a597133f4850f0d466709a9605f115c27117/SteamKit2/Tests/CryptoHelperFacts.cs#L32
  static byte[] SymmetricEncryptWithIV(byte[] input, byte[] key, byte[] iv)
  {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(key);
    ArgumentNullException.ThrowIfNull(iv);

    using var aes = Aes.Create();
    aes.BlockSize = 128;
    aes.KeySize = 256;
    aes.Key = key;

    var cryptedIv = aes.EncryptEcb(iv, PaddingMode.None);
    var cipherText = aes.EncryptCbc(input, iv, PaddingMode.PKCS7);

    // final output is 16 byte ecb crypted IV + cbc crypted plaintext
    var output = new byte[cryptedIv.Length + cipherText.Length];

    Array.Copy(cryptedIv, 0, output, 0, cryptedIv.Length);
    Array.Copy(cipherText, 0, output, cryptedIv.Length, cipherText.Length);

    return output;
  }
}
