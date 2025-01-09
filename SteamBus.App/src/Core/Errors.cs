using Tmds.DBus;

public static class DbusErrors
{
    public const string DiskNotFound = "one.playtron.Error.DiskNotFound";
    public const string DownloadInProgress = "one.playtron.Error.DownloadInProgress";
    public const string DownloadFailed = "one.playtron.Error.DownloadFailed";
    public const string ContentNotFound = "one.playtron.Error.ContentNotFound";
}

public static class DbusExceptionHelper
{
    public static void ThrowDiskNotFound(string message = "The specified disk is not mounted")
    {
        throw new DBusException(DbusErrors.DiskNotFound, message);
    }

    public static void ThrowContentNotFound(string message = "Content not found for specified app")
    {
        throw new DBusException(DbusErrors.ContentNotFound, message);
    }
}
