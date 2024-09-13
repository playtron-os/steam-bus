using System.Text.Json;

namespace SteamBus.Auth;


// {"username": {"accountId": 1234, "steamguard": "..."}}

public class SteamAuthSession
{
  public uint accountId { get; set; }
  public string? steamGuard { get; set; }
}

//[Dictionary]
//class SteamAuth
//{
//  //
//}
