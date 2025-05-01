using SteamKit2;
using SteamKit2.Discovery;
using Xdg.Directories;

namespace Steam.Config;

public class SteamConnectionConfig
{
    public string cellIdPath;
    public string serversBinPath;

    public uint cellId = 0;

    // Load the steam connection config
    public SteamConnectionConfig(string cellIdPath, string serversBinPath)
    {
        this.cellIdPath = cellIdPath;
        this.serversBinPath = serversBinPath;
        Reload();
    }

    // Returns the cell id default path: "~/.local/share/steambus/cell_id.txt"
    public static string CellIdDefaultPath()
    {
        string baseDir = $"{BaseDirectory.DataHome}/steambus";
        return $"{baseDir}/cell_id.txt";
    }

    // Returns the servers bin default path: "~/.local/share/steambus/servers_list.bin"
    public static string ServersBinDefaultPath()
    {
        string baseDir = $"{BaseDirectory.DataHome}/steambus";
        return $"{baseDir}/servers_list.bin";
    }

    public void Reload()
    {
        if (File.Exists(cellIdPath))
        {
            if (!uint.TryParse(File.ReadAllText(cellIdPath), out cellId))
            {
                Console.WriteLine($"Error parsing cellid from {cellIdPath}. Continuing with cellid 0.");
                cellId = 0;
            }
            else
            {
                Console.WriteLine($"Using persisted cell ID {cellId}");
            }
        }

        if (File.Exists(serversBinPath))
        {
            var content = File.ReadAllText(serversBinPath).Trim();

            if (string.IsNullOrEmpty(content))
            {
                Console.WriteLine($"Error reading servers list from {serversBinPath}. Continuing with empty servers list.");
                File.Delete(serversBinPath);
            }
            else
            {
                Console.WriteLine($"Using persisted servers list");
            }
        }
    }

    // Save the cell id
    public void SaveCellId(uint cellId)
    {
        try
        {
            this.cellId = cellId;
            File.WriteAllText(cellIdPath, cellId.ToString());
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Error saving steam connection config, err:{exception}");
        }
    }

    public SteamConfiguration GetSteamClientConfig()
    {
        return SteamConfiguration.Create(config =>
            config
                .WithCellID(cellId)
                .WithServerListProvider(new FileStorageServerListProvider(serversBinPath))
                .WithConnectionTimeout(TimeSpan.FromSeconds(10))
        );
    }
}

