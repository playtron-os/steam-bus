using System.Diagnostics;
using Steam.Config;
using SteamKit2;

static class Disk
{
    public static bool IsMountPointMainDisk(string mountPoint)
    {
        return mountPoint == "/" || mountPoint == "/home" || mountPoint == "/var/home";
    }

    public static async Task<string> GetMountPointFromProc(string device)
    {
        var lines = await File.ReadAllLinesAsync("/proc/mounts");

        foreach (var line in lines ?? [])
        {
            var parts = line.Split(' ');
            if (parts[0] == device)
            {
                return parts[1];
            }
        }

        return string.Empty;
    }

    public static async Task<string> GetMountPointFromProc()
    {
        var lines = await File.ReadAllLinesAsync("/proc/mounts");

        foreach (var line in lines ?? [])
        {
            var parts = line.Split(' ');
            if (IsMountPointMainDisk(parts[1]))
            {
                return parts[1];
            }
        }

        return string.Empty;
    }

    public static async Task<string> GetInstallRootFromDevice(string device, string folderName)
    {
        // Get mount point
        var mountPoint = await GetMountPointFromProc(device);
        if (string.IsNullOrEmpty(mountPoint))
            throw DbusExceptionHelper.ThrowDiskNotFound();

        return await GetInstallRoot(mountPoint, folderName);
    }

    public static async Task<string> GetInstallRoot(string folderName)
    {
        // Get mount point
        var mountPoint = await GetMountPointFromProc();
        if (string.IsNullOrEmpty(mountPoint))
            throw DbusExceptionHelper.ThrowDiskNotFound();

        return await GetInstallRoot(mountPoint, folderName);
    }

    public static async Task<string> GetInstallRoot(string mountPoint, string folderName)
    {
        var libraryFoldersConfig = await LibraryFoldersConfig.CreateAsync();
        var installPath = libraryFoldersConfig.GetInstallDirectory(mountPoint);

        if (installPath == null)
        {
            libraryFoldersConfig.AddDiskEntry(mountPoint);
            libraryFoldersConfig.Save();
            installPath = libraryFoldersConfig.GetInstallDirectory(mountPoint);
        }

        return Path.Join(installPath!, folderName);
    }
}