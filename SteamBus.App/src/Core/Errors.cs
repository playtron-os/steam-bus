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
    public const string MissingDirectory = "one.playtron.Error.MissingDirectory";
    public const string InvalidPassword = "one.playtron.Error.InvalidPassword";
    public const string AuthenticationError = "one.playtron.Error.AuthenticationError";
    public const string TfaTimedOut = "one.playtron.Error.TfaTimedOut";
    public const string RateLimitExceeded = "one.playtron.Error.RateLimitExceeded";
    public const string AppUpdateRequired = "one.playtron.Error.AppUpdateRequired";
    public const string DependencyUpdateRequired = "one.playtron.Error.DependencyUpdateRequired";
    public const string Timeout = "one.playtron.Error.Timeout";
    public const string DependencyError = "one.playtron.Error.DependencyError";
    public const string PreLaunchError = "one.playtron.Error.PreLaunchError";
    public const string CloudConflict = "one.playtron.Error.CloudConflict";
    public const string CloudQuota = "one.playtron.Error.CloudQuota";
    public const string CloudFileDownload = "one.playtron.Error.CloudFileDownload";
    public const string CloudFileUpload = "one.playtron.Error.CloudFileUpload";
    public const string AppNotOwned = "one.playtron.Error.AppNotOwned";
    public const string PlayingBlocked = "one.playtron.Error.PlayingBlocked";
    public const string NotEnoughSpace = "one.playtron.Error.NotEnoughSpace";
    public const string Permission = "one.playtron.Error.Permission";
    public const string NetworkRequired = "one.playtron.Error.NetworkRequired";
    public const string Generic = "one.playtron.Error.Generic";
    public const string MoveItemCancelled = "one.playtron.Error.MoveItemCancelled";
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

    public static DBusException ThrowMissingDirectory(string message = "Directory not found")
    {
        return new DBusException(DbusErrors.MissingDirectory, message);
    }

    public static DBusException ThrowAppUpdateRequired(string message = "The app needs to be updated before it is launched")
    {
        return new DBusException(DbusErrors.AppUpdateRequired, message);
    }

    public static DBusException ThrowDependencyUpdateRequired(string message = "A dependency needs to be updated")
    {
        return new DBusException(DbusErrors.DependencyUpdateRequired, message);
    }

    public static DBusException ThrowTimeout(string message = "Timeout")
    {
        return new DBusException(DbusErrors.Timeout, message);
    }

    public static DBusException ThrowDependencyError(string message = "An error happened when executing a dependency")
    {
        return new DBusException(DbusErrors.DependencyError, message);
    }

    public static DBusException ThrowPlayingBlocked(string message = "Library is currently in use")
    {
        return new DBusException(DbusErrors.PlayingBlocked, message);
    }

    public static DBusException ThrowAppNotOwned(string message = "App not owned by current user")
    {
        return new DBusException(DbusErrors.AppNotOwned, message);
    }

    public static DBusException ThrowNotEnoughSpace(string message = "Not enough space to install this app")
    {
        return new DBusException(DbusErrors.NotEnoughSpace, message);
    }

    public static DBusException ThrowPermission(string message = "No permission")
    {
        return new DBusException(DbusErrors.Permission, message);
    }

    public static DBusException ThrowCloudConflict(string message = "Cloud save conflict")
    {
        return new DBusException(DbusErrors.CloudConflict, message);
    }

    public static DBusException ThrowNotOnline(string message = "Internet connection is required")
    {
        return new DBusException(DbusErrors.NetworkRequired, message);
    }
}
