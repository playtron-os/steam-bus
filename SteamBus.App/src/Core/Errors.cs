using Tmds.DBus;

public static class DbusErrors
{
    public const string DiskNotFound = "one.playtron.SteamBus.Error.DiskNotFound";
    public const string DownloadInProgress = "one.playtron.SteamBus.Error.DownloadInProgress";
}

public static class DbusExceptionHelper
{
    public static void ThrowDiskNotFound(string message = "The specified disk is not mounted")
    {
        throw new DBusException(DbusErrors.DiskNotFound, message);
    }
}
