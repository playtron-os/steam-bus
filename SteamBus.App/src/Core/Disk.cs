using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Steam.Config;
using SteamKit2;

static partial class Disk
{
  [GeneratedRegex(@"^(\/dev\/[\w\d]+p?\d*) on (\/[^\(]+) type")]
  private static partial Regex MountDriveRegex();

  public static string _homeDrive = "";

  public static bool IsMountPointMainDisk(string mountPoint)
  {
    return mountPoint == "/" || mountPoint.StartsWith("/home") || mountPoint.StartsWith("/var/home");
  }

  public static async Task<string> GetMountPath(string? driveName = null)
  {
    try
    {
      ProcessStartInfo psi = new ProcessStartInfo
      {
        FileName = "/bin/bash",
        Arguments = "-c \"mount\"",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };

      using Process? process = Process.Start(psi);
      if (process == null) return string.Empty;

      using StreamReader reader = process.StandardOutput;
      string output = await reader.ReadToEndAsync();
      await process.WaitForExitAsync();

      string[] lines = output.Split('\n');

      string bestMatch = string.Empty;
      int bestScore = -1;

      foreach (string line in lines)
      {
        Match match = MountDriveRegex().Match(line);
        if (!match.Success) continue;

        string device = match.Groups[1].Value;
        string mountPoint = match.Groups[2].Value.Trim(); // Remove trailing spaces

        // Ignore paths starting with /etc
        if (mountPoint.StartsWith("/etc")) continue;

        if (driveName == null)
        {
          // If no specific drive is requested, return the most relevant home-related mount
          if (IsMountPointMainDisk(mountPoint))
          {
            return mountPoint;
          }
        }
        else if (device == driveName)
        {
          // Score the mount point based on priority
          int score = GetMountPointScore(mountPoint);

          // Pick the best match with the highest score
          if (score > bestScore)
          {
            bestMatch = mountPoint;
            bestScore = score;
          }
        }
      }

      return bestMatch;
    }
    catch (Exception ex)
    {
      Console.WriteLine($"Error running mount: {ex.Message}");
    }

    return string.Empty;
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
