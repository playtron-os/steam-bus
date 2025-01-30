using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Steam.Config;
using SteamKit2;

static class Disk
{
    public static string _homeDrive = "";

    public static bool IsMountPointMainDisk(string mountPoint)
    {
        return mountPoint == "/" || mountPoint == "/home" || mountPoint == "/var/home";
    }

    static async Task<string> RunDf(string arg)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"df {arg}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process? process = Process.Start(psi);
            if (process == null) return string.Empty;

            using StreamReader reader = process.StandardOutput;
            string output = await reader.ReadToEndAsync();
            await process.WaitForExitAsync();

            return output;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error running df: {ex.Message}");
        }

        return string.Empty;
    }

    static async Task<string> GetHomeDrive()
    {
        if (string.IsNullOrEmpty(_homeDrive))
        {
            var output = await RunDf(Environment.GetEnvironmentVariable("HOME") ?? string.Empty);
            string[] lines = output.Split('\n').Skip(1).ToArray();

            foreach (string line in lines)
            {
                string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 6)
                    continue;

                _homeDrive = parts[0];
                break;
            }
        }

        return _homeDrive;
    }

    static async Task<string> GetMountPath(string? driveName = null)
    {
        var homePath = Regex.Unescape(Environment.GetEnvironmentVariable("HOME") ?? string.Empty);
        if (driveName == null) return homePath;

        var homeDrive = await GetHomeDrive();
        if (homeDrive == driveName) return homePath;

        try
        {
            var output = await RunDf(driveName);
            string[] lines = output.Split('\n').Skip(1).ToArray();

            foreach (string line in lines)
            {
                string[] parts = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 6)
                    continue;
                var path = parts[5];

                var condition = driveName == null ? IsMountPointMainDisk(path) : line.StartsWith(driveName);

                if (condition)
                {
                    if (IsMountPointMainDisk(path)) return homePath;
                    return Regex.Unescape(path);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }

        return string.Empty;
    }

    public static async Task<string> GetInstallRootFromDevice(string device, string folderName)
    {
        // Get mount point
        var mountPoint = await GetMountPath(device);
        if (string.IsNullOrEmpty(mountPoint))
            throw DbusExceptionHelper.ThrowDiskNotFound();

        return await GetInstallRoot(mountPoint, folderName);
    }

    public static async Task<string> GetInstallRoot(string folderName)
    {
        // Get mount point
        var mountPoint = await GetMountPath();
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

    public static void EnsureParentFolderExists(string filePath)
    {
        var parent = Directory.GetParent(filePath)?.FullName;
        if (parent == null) return;
        Directory.CreateDirectory(parent);
    }
}