using Steam.Config;
using SteamKit2;

namespace SteamBus.Tests;

public class Tests
{
  [SetUp]
  public void Setup()
  {
  }

  string GetTestDataDirectory()
  {
    // Tests are executed from the following directory:
    // SteamBus/SteamBus.Tests/bin/Debug/net8.0
    return "../../../Data";
  }

  [Test]
  public void TestUsernameCrc()
  {
    var username = "TestUser";
    var expectedCrc = "b98513741";

    var usernameCrc = SteamConfig.GetUsernameCrcString(username.ToLower());
    Console.WriteLine($"Got crc32 of username '{username}': {usernameCrc}");
    Assert.True(usernameCrc == expectedCrc, $"Username CRC32 should be '{expectedCrc}', but got: {usernameCrc}");

    // Test uppercased username
    var upperUsernameCrc = SteamConfig.GetUsernameCrcString(username);
    Assert.True(upperUsernameCrc == expectedCrc, $"Uppercased username CRC32 should be '{expectedCrc}', but got: {usernameCrc}");
  }

  [Test]
  public void TestEncryptToken()
  {
    var username = "TestUser";
    var token = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

    var encToken = SteamConfig.EncryptTokenForUser(username, token);
    Console.WriteLine($"Got encrypted token: {encToken}");

    var decToken = SteamConfig.DecryptTokenForUser(username, encToken);
    Assert.True(decToken == token, $"Encrypted token should decrypt to the original value '{token}', but got: {decToken}");
  }

  [Test]
  public void TestDecryptToken()
  {
    var username = "TestUser";
    var encToken = "e7b40a1ee908c0014b8d0106f0d2fda7250d25c6887b0e1416cca145b22a94a2ff5ab81f8844cd6f83bfa68e83802b55a14b522cef077c0e864a0bca407332bbfcbbf6f22e9cfadd1d7a09a5242bdfa007f1ad674e7d41b2cb655cab092e87d81a2feb34ddcff2c02ddf91af7f5a2dad15410945a05aa078fe8db0d3dd1214179b1153300ebaf1487e07cd763b0ef34f4e0307ca948eb34c65a759089ef0dbb2d093d093b1361d89e43c2c9cfe634ec3";
    var expectedToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

    var token = SteamConfig.DecryptTokenForUser(username, encToken);
    Console.WriteLine($"Got decrypted token: {token}");

    Assert.True(token == expectedToken, $"Decrypted token should be '{expectedToken}', but got: {token}");
  }

  [Test]
  public void TestLocalConfig()
  {
    var username = "TestUser";
    var filePath = $"{GetTestDataDirectory()}/local.vdf";
    var expectedToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

    // Test reading the token from the config
    var config = new LocalConfig(filePath);
    var token = config.GetRefreshToken(username);

    Console.WriteLine($"Got token from local config: {token}");
    Assert.True(token == expectedToken, $"Decrypted token should be '{expectedToken}', but got: {token}");

    // Test writing a token to a new config
    config = new LocalConfig("/tmp/local.vdf");
    config.SetRefreshToken(username, token!);
    config.Save();
  }

  [Test]
  public void TestParsePackageInfo()
  {
    var filePath = $"{GetTestDataDirectory()}/packages/103387.vdf";
    var data = File.ReadAllText(filePath);

    var kv = KeyValue.LoadFromString(data);
    Console.WriteLine("Got kv: '{0}'", kv["packageid"].Value);
    Console.WriteLine("Got kv: '{0}'", kv["appids"]["0"]);
    Console.WriteLine("Got kv: '{0}'", kv["IDONTEXIST"]);
    Console.WriteLine("Got kv: '{0}'", kv["IDONTEXIST"].Children);
    Console.WriteLine("Got kv: '{0}'", kv["depotids"].Value);

    // Loop
    foreach (var value in kv["depotids"].Children)
    {
      Console.WriteLine("Value: {0}", value.AsUnsignedInteger());
    }
  }
}
