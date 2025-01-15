using System.Diagnostics;
using System.Threading.Tasks;
using Playtron.Plugin;

public class SteamClientApp
{
    private const string BOOTSTRAP_LOG_START_TEXT = "Startup - updater built";
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
    private Process? process;

    private TaskCompletionSource? startingTask;
    private TaskCompletionSource? endingTask;

    private DisplayManager displayManager;

    public SteamClientApp(DisplayManager displayManager)
    {
        this.displayManager = displayManager;
    }

    public async Task Start(string username)
    {
        if (startingTask != null) await startingTask.Task;
        if (endingTask != null) await endingTask.Task;
        if (running) return;

        running = true;
        startingTask = new();

        // Kill current steam processes if any exist
        var processes = Process.GetProcessesByName("steam");
        await ProcessUtils.TerminateProcessesGracefully(processes, TimeSpan.FromSeconds(5));

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
        process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += OnOutputDataReceived;
        process.ErrorDataReceived += OnErrorDataReceived;
        process.Exited += OnExited;

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        AppDomain.CurrentDomain.ProcessExit += OnMainProcessExit;
        Console.CancelKeyPress += OnMainProcessExit;

        startingTask?.TrySetResult();
        startingTask = null;
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
    }

    private void OnExited(object? sender, EventArgs e)
    {
        Console.WriteLine($"Steam client exited with code {process?.ExitCode}");
        AppDomain.CurrentDomain.ProcessExit -= OnMainProcessExit;
        Console.CancelKeyPress -= OnMainProcessExit;
        process = null;
        running = false;
        endingTask?.TrySetResult();
        endingTask = null;
    }

    private void OnMainProcessExit(object? sender, EventArgs e)
    {
        var r = running;
        running = false;
        if (r && process != null) ShutdownSteamWithTimeout(TimeSpan.FromSeconds(20));
    }

    bool ShutdownSteamWithTimeout(TimeSpan timeout)
    {
        if (!Process.GetProcessesByName("steam").Any()) return true;

        Console.WriteLine("Shutting down steam client");

        try
        {
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

            using (var shutdownProcess = Process.Start(startInfo))
            {
                if (shutdownProcess == null)
                {
                    Console.WriteLine("Failed to start the steam -shutdown command.");
                    return false;
                }
            }

            // Wait for Steam processes to terminate
            return WaitForSteamProcessesToExit(timeout);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while shutting down Steam: {ex.Message}");
            return false;
        }
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

        return false; // Timeout reached
    }

    public async Task<bool> ShutdownSteamWithTimeoutAsync(TimeSpan timeout)
    {
        if (!running || !Process.GetProcessesByName("steam").Any()) return true;

        Console.WriteLine("Shutting down steam client");

        try
        {
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

            using (var shutdownProcess = Process.Start(startInfo))
            {
                if (shutdownProcess == null)
                {
                    Console.WriteLine("Failed to start the steam -shutdown command.");
                    return false;
                }
            }

            // Wait for Steam processes to terminate
            return await WaitForSteamProcessesToExitAsync(timeout);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while shutting down Steam: {ex.Message}");
            return false;
        }
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

        return false; // Timeout reached
    }
}