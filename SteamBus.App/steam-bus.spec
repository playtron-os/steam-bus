Name: SteamBus
Version: 1.0.0
Release: 1%{?dist}
Summary: SteamBus app used to interface with Steam Services
License: GPLv2
URL: https://github.com/playtron-os/steam-bus
BuildArch: x86_64

Requires: dotnet-sdk-8.0

%description
SteamBus app. Provides integration and functionality interfacing with Steam Services.

%install
# Create target directories
mkdir -p %{buildroot}/usr/share/playtron/plugins/SteamBus
mkdir -p %{buildroot}/usr/share/licenses/SteamBus
mkdir -p %{buildroot}/usr/share/doc/SteamBus
mkdir -p %{buildroot}/usr/bin

# Copy files to the buildroot
cp -r %{_sourcedir}/SteamBus/* %{buildroot}/usr/share/playtron/plugins/SteamBus/
cp %{_sourcedir}/SteamBus/LICENSE %{buildroot}/usr/share/licenses/SteamBus/LICENSE
cp %{_sourcedir}/SteamBus/README.md %{buildroot}/usr/share/doc/SteamBus/README.md

# Create a symlink for the executable
ln -s ../share/playtron/plugins/SteamBus/SteamBus %{buildroot}/usr/bin/steam-bus

%files
%license /usr/share/licenses/SteamBus/LICENSE
%doc /usr/share/doc/SteamBus/README.md
/usr/share/playtron/plugins/SteamBus/
/usr/bin/steam-bus
