using System.Diagnostics;

public static class ProcessUtils
{
    public static void KillProcessById(List<int> processIds)
    {
        foreach (var processId in processIds)
            KillProcessById(processId);
    }

    public static void KillProcessById(int processId)
    {
        try
        {
            // Get the process by ID
            Process process = Process.GetProcessById(processId);

            // Kill the process
            process.Kill(true);

            Console.WriteLine($"Successfully killed process with ID: {processId}");
        }
        catch (ArgumentException)
        {
            Console.WriteLine($"No process with ID {processId} found.");
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine($"The process with ID {processId} has already exited.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.WriteLine($"Access denied when attempting to kill process {processId}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to kill process with ID {processId}: {ex.Message}");
        }
    }

    public static async Task TerminateProcessesGracefully(Process[] processes, TimeSpan timeout)
    {
        // Try to close gracefully
        Console.WriteLine($"Attempting to gracefully terminate {processes.Length} processes");

        foreach (var process in processes)
            ProcessCloseMainWindow(process);

        // Wait for the process to exit within the timeout
        foreach (var process in processes)
        {
            using (var cts = new CancellationTokenSource(timeout))
            {
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                    Console.WriteLine($"Process with ID {process.Id} terminated gracefully.");
                }
                catch (OperationCanceledException)
                {
                    process.Kill();
                    await process.WaitForExitAsync();
                    Console.WriteLine($"Process with ID {process.Id} killed.");
                }
            }
        }

    }

    public static void ProcessCloseMainWindow(Process process)
    {
        try
        {
            process.CloseMainWindow();
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine($"The process with ID {process.Id} has closed.");
        }
    }
}