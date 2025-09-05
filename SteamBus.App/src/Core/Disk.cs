using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Steam.Config;
using SteamKit2;

static partial class Disk
{
  public static bool IsMountPointMainDisk(string mountPoint)
  {
    return mountPoint == "/" || mountPoint.StartsWith("/home") || mountPoint.StartsWith("/var/home");
  }

  public static async Task<string> GetMountPath(string? deviceName = null)
  {
    // If no drive is specified, use the home directory
    if (deviceName == null)
    {
      return Environment.GetEnvironmentVariable("HOME") ?? "/var/home/playtron";
    }

    // Read the mount points directly from '/proc/mounts'
    string text = string.Empty;
    try
    {
      using StreamReader reader = new("/proc/mounts");
      text = await reader.ReadToEndAsync();
    }
    catch (IOException ex)
    {
      Console.WriteLine($"Error reading mount points: {ex.Message}");
      return string.Empty;
    }
    string[] lines = text.Split('\n');

    string bestMatch = string.Empty;
    int bestScore = -1;

    // Each line in the output is:
    // <device> <mount point> <filesystem> <options>
    foreach (string line in lines)
    {
      string[] parts = line.Split(" ");
      if (parts.Length < 3)
      {
        continue;
      }

      string device = parts[0];
      string mountPoint = parts[1];
      string filesystem = parts[2];
      string options = parts[3];

      if (device != deviceName)
      {
        continue;
      }

      // Ignore paths starting with '/etc/'
      if (mountPoint.StartsWith("/etc")) continue;

      // Score the mount point based on priority
      int score = GetMountPointScore(mountPoint);

      // Pick the best match with the highest score
      if (score > bestScore)
      {
        bestMatch = mountPoint;
        bestScore = score;
      }
    }

    return bestMatch;
  }

  private static int GetMountPointScore(string mountPoint)
  {
    if (mountPoint.StartsWith("/var/home")) return 3;
    if (mountPoint.StartsWith("/home")) return 2;
    if (mountPoint.StartsWith("/var")) return 1;
    return 0;
  }

  public static async Task<string> GetInstallRootFromDevice(string device, string folderName)
  {
    // Get mount point
    var mountPoint = await GetMountPath(device);
    if (string.IsNullOrEmpty(mountPoint))
    {
      Console.Error.WriteLine($"Mount path not found for drive:{device}");
      throw DbusExceptionHelper.ThrowDiskNotFound();
    }

    return await GetInstallRoot(mountPoint, folderName);
  }

  public static async Task<string> GetInstallRoot(string folderName)
  {
    // Get mount point
    var mountPoint = await GetMountPath();
    if (string.IsNullOrEmpty(mountPoint))
    {
      Console.Error.WriteLine($"Mount path not found for home drive");
      throw DbusExceptionHelper.ThrowDiskNotFound();
    }

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

    return Regex.Unescape(Path.Join(installPath!, folderName));
  }

  public static async Task<ulong> GetFolderSizeWithDu(string folderPath)
  {
    if (string.IsNullOrWhiteSpace(folderPath))
    {
      throw new ArgumentException("Folder path cannot be null or empty.", nameof(folderPath));
    }

    if (!System.IO.Directory.Exists(folderPath))
      return 0;

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
        if (ulong.TryParse(parts[0], out ulong size))
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

  public static string ReadFileWithRetry(string filePath, int maxRetries = 10, int delayMilliseconds = 10)
  {
    return ExecuteFileOpWithRetry(() => File.ReadAllText(filePath), filePath, maxRetries, delayMilliseconds);
  }

  public static T ExecuteFileOpWithRetry<T>(Func<T> Callback, string filePath, int maxRetries = 10, int delayMilliseconds = 10, Action? OnError = null)
  {
    int attempt = 0;
    while (attempt < maxRetries)
    {
      try
      {
        return Callback();
      }
      catch (IOException e) when (IsFileLocked(e))
      {
        OnError?.Invoke();
        attempt++;
        Console.WriteLine($"File is locked, retrying {attempt}/{maxRetries}...");
        Thread.Sleep(delayMilliseconds);
      }
    }

    throw new IOException($"File '{filePath}' is still locked after {maxRetries} attempts.");
  }

  public static Task<T> ExecuteFileOpWithRetry<T>(Func<Task<T>> Callback, string filePath, int maxRetries = 10, int delayMilliseconds = 10)
  {
    int attempt = 0;
    while (attempt < maxRetries)
    {
      try
      {
        return Callback();
      }
      catch (IOException e) when (IsFileLocked(e))
      {
        attempt++;
        Console.WriteLine($"File is locked, retrying {attempt}/{maxRetries}...");
        Thread.Sleep(delayMilliseconds);
      }
    }

    throw new IOException($"File '{filePath}' is still locked after {maxRetries} attempts.");
  }

  private static bool IsFileLocked(IOException e)
  {
    return e.Message.Contains("because it is being used by another process");
  }
}
