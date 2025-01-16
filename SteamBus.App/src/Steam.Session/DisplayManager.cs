using System.Diagnostics;

public class DisplayManager
{
    private static readonly string[] HeadlessArguments = { "-noreset", "-ac", HeadlessDisplayId, "-screen", "0", "800x600x24" };
    private static readonly TimeSpan RetryTime = TimeSpan.FromSeconds(1);

    private bool IsRunning = false;

    private const string HeadlessDisplayId = ":97";
    private const string HeadlessCommand = "Xvfb";
    private const int MaxRetry = 3;

    public DisplayManager()
    {
        _ = Task.Run(async () =>
        {
            int retryCount = 0;

            while (true)
            {
                IsRunning = true;

                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = HeadlessCommand,
                        Arguments = string.Join(" ", HeadlessArguments),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(startInfo);
                    if (process != null)
                    {
                        Console.WriteLine($"Running headless display manager with ID: {HeadlessDisplayId}");

                        AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
                        {
                            if (!process.HasExited)
                            {
                                Console.WriteLine($"Terminating process with PID: {process.Id}");
                                process.Kill();
                            }
                        };

                        await process.WaitForExitAsync();
                        if (process.ExitCode != 0)
                        {
                            throw new Exception($"Process exited with code {process.ExitCode}");
                        }
                    }

                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error running display manager: {ex.Message}");
                    retryCount++;

                    var processes = Process.GetProcessesByName(HeadlessCommand);
                    await ProcessUtils.TerminateProcessesGracefullyAsync(processes, TimeSpan.FromMilliseconds(100));

                    await Task.Delay(RetryTime);
                }

                IsRunning = false;

                if (retryCount > MaxRetry)
                {
                    Console.WriteLine("Exceeded number of retries for display manager. Please restart if required.");
                    break;
                }
            }
        });
    }

    public string GetHeadlessDisplayId()
    {
        return IsRunning ? HeadlessDisplayId : Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
    }

    public bool IsRunningStatus()
    {
        return IsRunning;
    }
}
