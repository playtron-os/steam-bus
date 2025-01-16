using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Playtron.Plugin;
using Steam.Config;
using Xdg.Directories;

public class SteamClientApp
{
    public const uint STEAM_CLIENT_APP_ID = 769;

    private static readonly TimeSpan STEAM_FORCEFULLY_QUIT_TIMEOUT = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan STEAM_START_TIMEOUT = TimeSpan.FromSeconds(15);

    private const string COMMAND = "steam";
    private static string[] ARGUMENTS = [
        "-srt-logger-opened",
        "-silent",
        "-noreactlogin",
    ];

    public event Action<InstallStartedDescription>? OnDependencyInstallStarted;
    public event Action<InstallProgressedDescription>? OnDependencyInstallProgressed;
    public event Action<string>? OnDependencyInstallCompleted;
    public event Action<(string appId, string error)>? OnDependencyInstallFailed;
    public event Action<(string appId, string version)>? OnDependencyAppNewVersionFound;

    private bool running;
    private bool updating;
    private string updatingToVersion = "0";
    private Process? process;

    private TaskCompletionSource? updateStartedTask;
    private TaskCompletionSource? startingTask;
    private TaskCompletionSource? endingTask;

    private DisplayManager displayManager;
    private DepotConfigStore depotConfigStore;

    public SteamClientApp(DisplayManager displayManager, DepotConfigStore depotConfigStore)
    {
        this.displayManager = displayManager;
        this.depotConfigStore = depotConfigStore;
    }

    private static string GetManifestDirectory()
    {
        return $"{BaseDirectory.DataHome}/steambus/tools/steam";
    }

    public async Task Start(string username)
    {
        if (startingTask != null) await startingTask.Task;
        if (endingTask != null) await endingTask.Task;
        if (running) return;

        running = true;
        startingTask = new();
        updateStartedTask = new();

        // Kill current steam processes if any exist
        var processes = Process.GetProcessesByName("steam");
        await ProcessUtils.TerminateProcessesGracefullyAsync(processes, STEAM_FORCEFULLY_QUIT_TIMEOUT);

        var arguments = new List<string>(ARGUMENTS)
        {
          "-login",
          username
        };

        var display = displayManager.GetHeadlessDisplayId();

        Console.WriteLine($"Launching steam client: DISPLAY={display} LANG=C {COMMAND} {string.Join(" ", arguments)}");

        var startInfo = new ProcessStartInfo
        {
            FileName = COMMAND,
            Arguments = string.Join(" ", arguments),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.EnvironmentVariables["DISPLAY"] = display;
        startInfo.EnvironmentVariables["LANG"] = "C";
        process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += OnOutputDataReceived;
        process.ErrorDataReceived += OnErrorDataReceived;
        process.Exited += OnExited;

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        AppDomain.CurrentDomain.ProcessExit += OnMainProcessExit;
        Console.CancelKeyPress += OnMainProcessExit;

        var processEndTask = process.WaitForExitAsync();

        using var cts = new CancellationTokenSource(STEAM_START_TIMEOUT);
        var timeoutTask = Task.Delay(Timeout.Infinite, cts.Token);

        var completedTask = await Task.WhenAny(startingTask.Task, processEndTask, timeoutTask);

        if (completedTask == updateStartedTask?.Task)
        {
            Console.WriteLine("Steam client started during pre launch hook");
            throw DbusExceptionHelper.ThrowDependencyUpdateRequired();
        }

        if (completedTask == timeoutTask)
        {
            Console.WriteLine("Steam client start has timed out");
            var steamProcesses = Process.GetProcessesByName("steam");
            await ProcessUtils.TerminateProcessesGracefullyAsync(steamProcesses, TimeSpan.FromSeconds(1));
            throw DbusExceptionHelper.ThrowTimeout();
        }

        if (completedTask == processEndTask)
        {
            Console.WriteLine("Steam client failed to start");
            throw DbusExceptionHelper.ThrowDependencyError();
        }

        cts.Cancel();
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;
        Console.WriteLine($"[Steam Client: stdout] {e.Data}");
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;
        Console.WriteLine($"[Steam Client: stderr] {e.Data}");

        // Mark steam client as started when it outputs this string
        if (startingTask != null &&
            (e.Data.Contains("BuildCompleteAppOverviewChange") || e.Data.Contains("steam-runtime-launcher-service is running")))
        {
            // If an update was happening, mark it as complete
            if (updating)
            {
                depotConfigStore.SetDownloadStage(STEAM_CLIENT_APP_ID, null);
                depotConfigStore.Save(STEAM_CLIENT_APP_ID);

                Console.WriteLine("Steam client update has completed");
                updating = false;
                OnDependencyInstallCompleted?.Invoke(STEAM_CLIENT_APP_ID.ToString());
            }

            Console.WriteLine("Steam client has started and is ready");

            startingTask.TrySetResult();
            startingTask = null;

            // Create new update task in case steam client starts updating in the background
            updateStartedTask?.TrySetCanceled();
            updateStartedTask = new();
            return;
        }

        // Get version of new steam client update
        if (e.Data.Contains("Downloaded new manifest"))
        {
            var match = Regex.Match(e.Data, @"version\s(\d+),");
            if (match.Success)
            {
                updatingToVersion = match.Groups[1].Value;
                Console.WriteLine($"New steam client version: {updatingToVersion}");
            }
            else
                Console.Error.WriteLine("Failed to parse steam client version");

            return;
        }

        // Process steam client update
        if (e.Data.Contains("Downloading update"))
        {
            var match = Regex.Match(e.Data, @"\(([\d,]+) of ([\d,]+) (\w+)\)");
            string currentBytes = match.Success ? match.Groups[1].Value.Replace(",", "") : "0";
            string totalBytes = match.Success ? match.Groups[2].Value.Replace(",", "") : "0";
            string unit = match.Success ? match.Groups[3].Value : "KB";

            ulong currentBytesValue = ulong.Parse(currentBytes);
            ulong totalBytesValue = ulong.Parse(totalBytes);

            currentBytesValue = ConversionUtils.ConvertToBytes(currentBytesValue, unit);
            totalBytesValue = ConversionUtils.ConvertToBytes(totalBytesValue, unit);

            var progress = totalBytesValue == 0 ? 100 : (double)currentBytesValue * 100 / totalBytesValue;

            // If not updating yet, send update start event
            if (!updating)
            {
                var manifestDir = GetManifestDirectory();
                Directory.CreateDirectory(manifestDir);
                depotConfigStore.EnsureEntryExists(manifestDir, STEAM_CLIENT_APP_ID);
                depotConfigStore.SetNewVersion(STEAM_CLIENT_APP_ID, uint.Parse(updatingToVersion), "public", "linux");
                depotConfigStore.SetDownloadStage(STEAM_CLIENT_APP_ID, null);
                depotConfigStore.Save(STEAM_CLIENT_APP_ID);

                Console.WriteLine("Steam client update has started");
                updating = true;
                OnDependencyInstallStarted?.Invoke(new InstallStartedDescription
                {
                    AppId = STEAM_CLIENT_APP_ID.ToString(),
                    Version = updatingToVersion,
                    InstallDirectory = SteamConfig.GetConfigDirectory(),
                    TotalDownloadSize = totalBytesValue,
                    RequiresInternetConnection = true,
                    Os = "linux",
                });
            }
            else
            {
                depotConfigStore.SetDownloadStage(STEAM_CLIENT_APP_ID, DownloadStage.Downloading);
                depotConfigStore.SetCurrentSize(STEAM_CLIENT_APP_ID, currentBytesValue);
                depotConfigStore.SetTotalSize(STEAM_CLIENT_APP_ID, totalBytesValue);
                depotConfigStore.Save(STEAM_CLIENT_APP_ID);

                Console.WriteLine($"Current steam download at {progress:F2}% ({currentBytesValue} / {totalBytesValue} bytes)");

                // If already updating, send progress
                OnDependencyInstallProgressed?.Invoke(new InstallProgressedDescription
                {
                    AppId = STEAM_CLIENT_APP_ID.ToString(),
                    Stage = (uint)DownloadStage.Downloading,
                    DownloadedBytes = currentBytesValue,
                    TotalDownloadSize = totalBytesValue,
                    Progress = progress,
                });
            }

            // Complete update start task
            if (updateStartedTask != null)
            {
                updateStartedTask.TrySetResult();
                updateStartedTask = null;
            }

        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        Console.WriteLine($"Steam client exited with code {process?.ExitCode}");
        AppDomain.CurrentDomain.ProcessExit -= OnMainProcessExit;
        Console.CancelKeyPress -= OnMainProcessExit;
        process = null;

        if (updating)
        {
            Console.WriteLine("Steam client update has failed");
            updating = false;
            OnDependencyInstallFailed?.Invoke((STEAM_CLIENT_APP_ID.ToString(), DbusErrors.DownloadFailed));
        }

        running = false;
        endingTask?.TrySetResult();
        endingTask = null;
        startingTask?.TrySetCanceled();
        startingTask = null;
        updateStartedTask?.TrySetCanceled();
        updateStartedTask = null;
    }

    private void OnMainProcessExit(object? sender, EventArgs e)
    {
        var r = running;
        running = false;
        if (r && process != null) ShutdownSteamWithTimeout(TimeSpan.FromSeconds(20));
    }

    bool ShutdownSteamWithTimeout(TimeSpan timeout)
    {
        if (RunSteamShutdown() & !WaitForSteamProcessesToExit(timeout))
        {
            var processes = Process.GetProcessesByName("steam");
            ProcessUtils.TerminateProcessesGracefully(processes, TimeSpan.FromSeconds(5));
            return true;
        }

        return false;
    }

    bool WaitForSteamProcessesToExit(TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            // Check if any Steam processes are still running
            bool steamRunning = Process.GetProcessesByName("steam").Any();

            if (!steamRunning)
            {
                return true; // Steam has shut down
            }

            Thread.Sleep(300);
        }

        Console.WriteLine("Timed out waiting for steam client to exit");

        return false; // Timeout reached
    }

    public async Task<bool> ShutdownSteamWithTimeoutAsync(TimeSpan timeout)
    {
        if (startingTask != null) await startingTask.Task;
        if (endingTask != null) await endingTask.Task;
        endingTask = new();

        if (RunSteamShutdown() && !await WaitForSteamProcessesToExitAsync(timeout))
        {
            var processes = Process.GetProcessesByName("steam");
            await ProcessUtils.TerminateProcessesGracefullyAsync(processes, TimeSpan.FromSeconds(5));
            return true;
        }

        return false;
    }

    async Task<bool> WaitForSteamProcessesToExitAsync(TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            // Check if any Steam processes are still running
            bool steamRunning = Process.GetProcessesByName("steam").Any();

            if (!steamRunning)
            {
                return true; // Steam has shut down
            }

            await Task.Delay(300);
        }

        Console.WriteLine("Timed out waiting for steam client to exit");

        return false; // Timeout reached
    }

    public bool RunSteamShutdown()
    {
        if (!running || !Process.GetProcessesByName("steam").Any()) return false;

        Console.WriteLine("Shutting down steam client");

        try
        {
            var display = displayManager.GetHeadlessDisplayId();

            // Start the steam -shutdown command
            var startInfo = new ProcessStartInfo
            {
                FileName = "steam",
                Arguments = "-shutdown",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.EnvironmentVariables["DISPLAY"] = display;
            startInfo.EnvironmentVariables["LANG"] = "C";

            using (var shutdownProcess = Process.Start(startInfo))
            {
                if (shutdownProcess == null)
                {
                    Console.WriteLine("Failed to start the steam -shutdown command.");
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while shutting down Steam: {ex.Message}");
            return false;
        }
    }
}