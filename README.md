# Steam Bus

DBus interface for interacting with Steam API

## Usage

Login

```bash
busctl --user call one.playtron.SteamBus \
  /one/playtron/SteamBus/SteamClient0 \
  one.playtron.SteamBus.SteamClient \
  Login "ss" "myuser" 'mypassword'
```

2FA

```bash
busctl --user call one.playtron.SteamBus \
  /one/playtron/SteamBus/SteamClient0 \
  one.playtron.auth.TwoFactorFlow \
  SendCode s XXXXX
```

## Attributions

- [SteamKit2](https://github.com/SteamRE/SteamKit) is licensed under the [LGPL v2.1](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.en.html)
- [Tmds.DBus](https://github.com/tmds/Tmds.DBus) is licensed under the [MIT](https://github.com/tmds/Tmds.DBus/blob/main/COPYING)
- [DepotDownloader](https://github.com/SteamRE/DepotDownloader) is licensed under the [GPL v2.0](https://github.com/SteamRE/DepotDownloader/blob/master/LICENSE)

## License

Steam Bus is licensed under THE GNU GPLv2. See LICENSE for details.
