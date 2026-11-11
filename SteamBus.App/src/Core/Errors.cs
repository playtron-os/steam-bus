using Tmds.DBus;

public static class DbusErrors
{
    public const string DiskNotFound = "inc.kora.Error.DiskNotFound";
    public const string DownloadInProgress = "inc.kora.Error.DownloadInProgress";
    public const string DownloadFailed = "inc.kora.Error.DownloadFailed";
    public const string ContentNotFound = "inc.kora.Error.ContentNotFound";
    public const string AppNotInstalled = "inc.kora.Error.AppNotInstalled";
    public const string InvalidAppId = "inc.kora.Error.InvalidAppId";
    public const string NotLoggedIn = "inc.kora.Error.NotLoggedIn";
    public const string MissingDirectory = "inc.kora.Error.MissingDirectory";
    public const string InvalidPassword = "inc.kora.Error.InvalidPassword";
    public const string AuthenticationError = "inc.kora.Error.AuthenticationError";
    public const string TfaTimedOut = "inc.kora.Error.TfaTimedOut";
    public const string RateLimitExceeded = "inc.kora.Error.RateLimitExceeded";
    public const string AppUpdateRequired = "inc.kora.Error.AppUpdateRequired";
    public const string DependencyUpdateRequired = "inc.kora.Error.DependencyUpdateRequired";
    public const string Timeout = "inc.kora.Error.Timeout";
    public const string DependencyError = "inc.kora.Error.DependencyError";
    public const string PreLaunchError = "inc.kora.Error.PreLaunchError";
    public const string CloudConflict = "inc.kora.Error.CloudConflict";
    public const string CloudQuota = "inc.kora.Error.CloudQuota";
    public const string CloudFileDownload = "inc.kora.Error.CloudFileDownload";
    public const string CloudFileUpload = "inc.kora.Error.CloudFileUpload";
    public const string CloudBusy = "inc.kora.Error.CloudBusy";
    public const string AppNotOwned = "inc.kora.Error.AppNotOwned";
    public const string PlayingBlocked = "inc.kora.Error.PlayingBlocked";
    public const string NotEnoughSpace = "inc.kora.Error.NotEnoughSpace";
    public const string Permission = "inc.kora.Error.Permission";
    public const string NetworkRequired = "inc.kora.Error.NetworkRequired";
    public const string Generic = "inc.kora.Error.Generic";
    public const string MoveItemCancelled = "inc.kora.Error.MoveItemCancelled";
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
