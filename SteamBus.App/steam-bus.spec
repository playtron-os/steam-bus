Name: SteamBus
Version: 1.15.2
Release: 1%{?dist}
Summary: SteamBus app used to interface with Steam Services
License: GPLv2
URL: https://github.com/playtron-os/steam-bus
BuildArch: x86_64

Requires: dotnet-sdk-8.0

%description
SteamBus app. Provides integration and functionality interfacing with Steam Services.

%install
make -C %{_sourcedir}/SteamBus install PREFIX=%{buildroot}%{_prefix} SOURCE=%{_sourcedir}

%files
%license %{_prefix}/share/licenses/SteamBus/LICENSE
%doc %{_prefix}/share/doc/SteamBus/README.md
%{_prefix}/share/playtron/plugins/SteamBus/
%{_prefix}/bin/steam-bus
