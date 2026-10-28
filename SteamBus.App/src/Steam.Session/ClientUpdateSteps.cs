using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

// Counts the downloads of one Steam client update. A fresh install takes two: the
// bootstrapper's 32-bit client restarts into the 64-bit one, which downloads again.
public class ClientUpdateSteps
{
    private const string CLIENT_32_BIT_MANIFEST = "steam_client_ubuntu12";

    private readonly Architecture architecture;
    private string manifest = "";
    private bool downloaded;
    private bool installing;

    // 1-based, 0 while no update runs
    public uint Step { get; private set; }
    // How many downloads the update is expected to take, it grows if Steam asks for more
    public uint Steps { get; private set; }
    // Size of the current step's download
    public ulong TotalBytes { get; private set; }

    public ClientUpdateSteps() : this(RuntimeInformation.OSArchitecture) { }

    public ClientUpdateSteps(Architecture architecture)
    {
        this.architecture = architecture;
    }

    // "Downloaded new manifest: /steam_client_ubuntu12 version 1792618285, installed version 0, ..."
    public static string? ParseManifest(string line)
    {
        var match = Regex.Match(line, @"Downloaded new manifest: /(\S+) version");
        return match.Success ? match.Groups[1].Value : null;
    }

    // Printed while a finished download is unpacked and copied into place
    public static bool IsInstallingLine(string line) =>
        line.Contains("Extracting package") || line.Contains("Installing update");

    // Downloads left for an update that starts with this manifest, this one included
    public static uint ExpectedSteps(string manifest, Architecture architecture) =>
        manifest == CLIENT_32_BIT_MANIFEST && architecture == Architecture.X64 ? 2u : 1u;

    // The update started downloading
    public void Start()
    {
        Step = 1;
        Steps = ExpectedSteps(manifest, architecture);
        downloaded = false;
        installing = false;
        TotalBytes = 0;
    }

    // Returns whether the step or the expected count changed
    public bool OnManifest(string name, bool updating)
    {
        manifest = name;
        if (!updating || Step == 0) return false;

        var (step, steps) = (Step, Steps);
        // A manifest after this step's download is a restarted client downloading again
        if (downloaded)
        {
            Step++;
            downloaded = false;
            installing = false;
            TotalBytes = 0;
        }
        Steps = Math.Max(Steps, Step - 1 + ExpectedSteps(name, architecture));
        return (step, steps) != (Step, Steps);
    }

    public void OnDownloadProgress(ulong totalBytes)
    {
        downloaded = true;
        TotalBytes = totalBytes;
    }

    // Returns true for the first install line of a step
    public bool OnInstalling()
    {
        if (Step == 0 || installing) return false;
        installing = true;
        return true;
    }

    public void Reset()
    {
        Step = 0;
        Steps = 0;
        downloaded = false;
        installing = false;
        TotalBytes = 0;
    }
}
