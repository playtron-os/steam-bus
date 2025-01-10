using Tmds.DBus;

public static class DbusErrors
{
    public const string DiskNotFound = "one.playtron.Error.DiskNotFound";
    public const string DownloadInProgress = "one.playtron.Error.DownloadInProgress";
    public const string DownloadFailed = "one.playtron.Error.DownloadFailed";
    public const string ContentNotFound = "one.playtron.Error.ContentNotFound";
    public const string AppNotInstalled = "one.playtron.Error.AppNotInstalled";
    public const string InvalidAppId = "one.playtron.Error.InvalidAppId";
    public const string NotLoggedIn = "one.playtron.Error.NotLoggedIn";
}

public static class DbusExceptionHelper
{
    public static DBusException ThrowDiskNotFound(string message = "The specified disk is not mounted")
    {
        return new DBusException(DbusErrors.DiskNotFound, message);
    }

    public static DBusException ThrowContentNotFound(string message = "Content not found for specified app")
    {
        return new DBusException(DbusErrors.ContentNotFound, message);
    }

    public static DBusException ThrowAppNotInstalled(string message = "The requested app is not installed")
    {
        return new DBusException(DbusErrors.AppNotInstalled, message);
    }

    public static DBusException ThrowInvalidAppId(string message = "Invalid app ID")
    {
        return new DBusException(DbusErrors.InvalidAppId, message);
    }

    public static DBusException ThrowNotLoggedIn(string message = "Not logged in to steam")
    {
        return new DBusException(DbusErrors.NotLoggedIn, message);
    }
}
