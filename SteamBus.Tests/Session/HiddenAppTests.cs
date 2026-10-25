using Playtron.Plugin;
using Steam.Config;
using Steam.Session;
using SteamKit2;

namespace SteamBus.Tests.Session;

// Apps in an account's packages that Steam shows it only as "public_only" stay out of the library,
// so Playserve never asks for their metadata, which SteamBus can only refuse with InvalidAppId.
public class HiddenAppTests
{
  // As Steam returned 375350 to the test laptop's account, and as app_info_cache keeps it.
  const string HiddenAppInfo = """
    "appinfo"
    {
      "appid"   "375350"
      "public_only"   "1"
    }
    """;

  const string GameAppInfo = """
    "appinfo"
    {
      "appid"   "260790"
      "common"
      {
        "name"   "1001 Spikes"
        "type"   "Game"
      }
    }
    """;

  static KeyValue Parse(string text) => KeyValue.LoadFromString(text)!;

  [Test]
  public void AnAppWithOnlyPublicInfoIsNotListed()
  {
    Assert.That(SteamSession.IsListed(SteamSession.GetProviderItem("375350", Parse(HiddenAppInfo))), Is.False);
    Assert.That(SteamSession.IsListed(SteamSession.GetProviderItem("260790", Parse(GameAppInfo))), Is.True);
  }

  [Test]
  public async Task AppInfoWithoutANameLeavesTheAppOutOfTheLibrary()
  {
    await using var harness = await SessionHarness.Create();
    var session = harness.Session;

    session.SetProviderItem(260790, Parse(GameAppInfo));
    session.SetProviderItem(375350, Parse(HiddenAppInfo));

    Assert.That(session.IsAppOwned(260790), Is.True);
    Assert.That(session.IsAppOwned(375350), Is.False);
    Assert.That(session.GetProviderItems().Select(item => item.id), Is.EqualTo(new[] { "260790" }));
  }

  [Test]
  public async Task AppInfoThatLostItsNameRemovesTheApp()
  {
    await using var harness = await SessionHarness.Create();
    var session = harness.Session;

    session.SetProviderItem(375350, Parse(GameAppInfo));
    Assert.That(session.IsAppOwned(375350), Is.True);

    session.SetProviderItem(375350, Parse(HiddenAppInfo));
    Assert.That(session.IsAppOwned(375350), Is.False);
  }

  [Test]
  public async Task NamelessAppsFromAnOlderLibraryCacheAreDropped()
  {
    const uint account = 1594000507;
    var cache = new LibraryCache(LibraryCache.DefaultPath());
    cache.SetApps(account, [
      new ProviderItem { id = "260790", name = "1001 Spikes", provider = "Steam", app_type = (uint)AppType.Game },
      // What an older SteamBus cached for a public_only app: a game with no name.
      new ProviderItem { id = "375350", name = "", provider = "Steam", app_type = (uint)AppType.Game },
    ]);
    cache.Save();

    await using var harness = await SessionHarness.Create(accountId: account);

    Assert.That(harness.Session.GetProviderItems().Select(item => item.id), Is.EqualTo(new[] { "260790" }));
    Assert.That(harness.Session.IsAppOwned(375350), Is.False);
  }
}
