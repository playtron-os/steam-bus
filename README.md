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
