using System.Diagnostics;
using System.Threading.Tasks;
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
        string selectedMountPoint = "";

        foreach (var line in lines ?? [])
        {
            var parts = line.Split(' ');

            if (parts.Length < 2)
                continue;

            if (parts[0] == device)
            {
                var mountPoint = parts[1];

                if (!mountPoint.StartsWith("/sysroot") && !mountPoint.EndsWith(".btrfs") && mountPoint.Length > selectedMountPoint.Length)
                    selectedMountPoint = mountPoint;
            }
        }

        return selectedMountPoint;
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

    public static async Task<long> GetFolderSizeWithDu(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("Folder path cannot be null or empty.", nameof(folderPath));
        }

        if (!System.IO.Directory.Exists(folderPath))
        {
            throw new System.IO.DirectoryNotFoundException($"The folder '{folderPath}' does not exist.");
        }

        try
        {
            // Set up the process to run `du -sb` for the folder
            ProcessStartInfo processStartInfo = new ProcessStartInfo
            {
                FileName = "du",
                Arguments = $"-sb \"{folderPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process process = Process.Start(processStartInfo)!)
            {
                if (process == null)
                {
                    throw new InvalidOperationException("Failed to start the 'du' process.");
                }

                string output = await process.StandardOutput.ReadLineAsync() ?? "";
                string error = await process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    throw new Exception($"Error while executing 'du': {error}");
                }

                // Parse the output (format: "<size_in_bytes>\t<path>")
                string[] parts = output.Split('\t');
                if (long.TryParse(parts[0], out long size))
                {
                    return size;
                }

                throw new FormatException("Unexpected output format from 'du'.");
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"An error occurred while calculating folder size: {ex.Message}", ex);
        }
    }
}