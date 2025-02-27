using System.Text.RegularExpressions;
using Playtron.Plugin;
using Steam.Config;
using Steam.Content;
using SteamKit2;

public class InstallScript
{
    private const string COMMON_REDIST_FOLDER_NAME = "_CommonRedist";

    /// <summary>
    /// The drive letter to where the application is installed, like C
    /// </summary>
    private const string ROOT_DRIVE = "Z";

    private static readonly Regex SIGNATURE_REGEX = new Regex(@"""kvsignatures"".*", RegexOptions.Singleline);

    private DepotConfigStore _depotConfigStore;
    private uint _appId;
    private string _installDirectory;
    private string _appDataFolder;
    private string _myDocsFolder;
    private string _commonMyDocsFolder;
    private string _localAppDataFolder;
    private string _windowsFolder;
    private string _steamFolder;

    private List<string> loadedScripts = [];
    public List<PostInstall> scripts = [];

    private InstallScript(DepotConfigStore depotConfigStore, uint appId, string installDirectory)
    {
        this._depotConfigStore = depotConfigStore;
        this._appId = appId;
        this._installDirectory = installDirectory;

        var steamapps = Path.Join(SteamConfig.GetConfigDirectory(), "steamapps");
        _appDataFolder = Path.Join(steamapps, "compatdata", appId.ToString(), "pfx", "drive_c", "users", "steamuser", "AppData");
        _myDocsFolder = Path.Join(steamapps, "compatdata", appId.ToString(), "pfx", "drive_c", "users", "steamuser", "Documents");
        _commonMyDocsFolder = Path.Join(steamapps, "compatdata", appId.ToString(), "pfx", "drive_c", "users", "Public", "Documents");
        _localAppDataFolder = Path.Join(steamapps, "compatdata", appId.ToString(), "pfx", "drive_c", "users", "steamuser", "Local Settings", "Application Data");
        _windowsFolder = Path.Join(steamapps, "compatdata", appId.ToString(), "pfx", "drive_c", "windows");
        _steamFolder = Path.Join(steamapps, "compatdata", appId.ToString(), "pfx", "drive_c", "Program Files (x86)", "Steam");
    }

    /// <summary>
    /// Initializes the InstallScript
    /// </summary>
    static public async Task<InstallScript> CreateAsync(DepotConfigStore depotConfigStore, uint appId, string installDirectory)
    {
        var store = new InstallScript(depotConfigStore, appId, installDirectory);
        await store.Reload();
        return store;
    }

    /// <summary>
    /// Checks if the file at the path is an install script
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    static public bool IsInstallScript(string path)
    {
        try
        {
            if (path.EndsWith(".vdf") && File.Exists(path))
            {
                var content = File.ReadAllText(path);
                var cleanedContent = SIGNATURE_REGEX.Replace(content ?? "", "");
                var installScript = KeyValue.LoadFromString(cleanedContent);

                if (installScript != null && installScript.Name?.ToLower() == "installscript")
                    return true;
            }
        }
        catch (Exception err)
        {
            Console.Error.WriteLine($"Error reading install script from path:{path}, err:{err}");
        }

        return false;
    }

    /// <summary>
    /// Reloads all of the install scripts associated with the install directory
    /// </summary>
    /// <returns></returns>
    public async Task Reload()
    {
        Console.WriteLine($"Reading install scripts at appId:{_appId}");

        try
        {
            loadedScripts.Clear();

            foreach (var installScriptInfo in _depotConfigStore.GetInstallScripts(_appId))
            {
                var installScript = await GetScriptForPathAsync(_installDirectory, installScriptInfo.Path.Replace("\\", "/"));
                if (installScript != null)
                    scripts.Add((PostInstall)installScript);
            }

            foreach (var depot in _depotConfigStore.GetSharedDepots(_appId))
            {
                var installDir = _depotConfigStore.GetInstallDirectory(depot.DepotAppId);
                if (installDir == null) continue;

                var installScriptPath = _depotConfigStore.GetInstallScripts(depot.DepotAppId)
                    .FirstOrDefault(x => x.DepotId == depot.DepotId)
                    .Path
                    .Replace("\\", "/");
                var installScript = await GetScriptForPathAsync(installDir, Path.Join(installDir, installScriptPath));
                if (installScript != null)
                    scripts.Add((PostInstall)installScript);
            }

            // Leave the code below for backward compatibility in case the appmanifest doesn't contain "IntallScripts" field
            await LoadInstallScriptsForDir(_installDirectory);

            Console.WriteLine($"Found {scripts.Count} scripts for appId:{_appId}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Error when reading install scripts, {exception}");
            throw;
        }
    }

    private async Task LoadInstallScriptsForDir(string dir)
    {
        // Look at the common redistributable folder
        var commonRedistDir = Path.Combine(dir, COMMON_REDIST_FOLDER_NAME);
        if (Directory.Exists(commonRedistDir))
        {
            foreach (var folder in Directory.EnumerateDirectories(commonRedistDir))
            {
                foreach (var nestedFolder in Directory.EnumerateDirectories(folder))
                {
                    foreach (var file in Directory.EnumerateFiles(nestedFolder))
                    {
                        if (file?.EndsWith(".vdf") == true)
                        {
                            var installScript = await GetScriptForPathAsync(dir, file);
                            if (installScript != null)
                                scripts.Add((PostInstall)installScript);
                        }
                    }
                }
            }
        }

        // Look at the root install directory
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (file?.EndsWith(".vdf") == true)
            {
                var rootInstallScript = await GetScriptForPathAsync(dir, file);
                if (rootInstallScript != null)
                    scripts.Add((PostInstall)rootInstallScript);
            }
        }
    }

    private async Task<PostInstall?> GetScriptForPathAsync(string installDir, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var content = await File.ReadAllTextAsync(path);
                if (loadedScripts.Contains(content))
                    return null;
                loadedScripts.Add(content);

                var cleanedContent = SIGNATURE_REGEX.Replace(content ?? "", "");
                var installScript = KeyValue.LoadFromString(cleanedContent);

                if (installScript != null)
                {
                    if (installScript.Name?.ToLower() != "installscript")
                        return null;

                    return new PostInstall
                    {
                        Path = path,
                        Registry = GetRegistry(installDir, installScript),
                        RunProcess = GetRunProcessList(installDir, installScript).ToArray(),
                    };
                }
            }
            else
            {
                Console.WriteLine($"No install script file found at {path}");
            }
        }
        catch (Exception err)
        {
            Console.Error.WriteLine($"Error reading install script from path:{path}, err:{err}");
        }

        return null;
    }

    private PostInstallRegistry GetRegistry(string installDir, KeyValue installScript)
    {
        var registry = installScript.Children.Find((child) => child.Name?.ToLowerInvariant() == "registry");
        if (registry == null) return new PostInstallRegistry { };

        var strings = new List<PostInstallRegistryValue>();
        var dwords = new List<PostInstallRegistryValue>();

        foreach (var group in registry.Children)
        {
            if (string.IsNullOrEmpty(group.Name))
                continue;

            foreach (var type in group.Children)
            {
                var isDword = type.Name?.ToLowerInvariant() == "dword";

                foreach (var keyOrLanguage in type.Children)
                {
                    var value = keyOrLanguage.AsString();

                    if (value == null)
                    {
                        // Is language, look into nested object
                        foreach (var key in keyOrLanguage.Children)
                        {
                            if (isDword)
                            {
                                dwords.Add(new PostInstallRegistryValue
                                {
                                    Language = keyOrLanguage.Name,
                                    Group = group.Name,
                                    Key = key.Name ?? "",
                                    Value = ReplaceDynamicVariables(installDir, key.AsString() ?? "", true)!,
                                });
                            }
                            else
                            {
                                strings.Add(new PostInstallRegistryValue
                                {
                                    Language = keyOrLanguage.Name,
                                    Group = group.Name,
                                    Key = key.Name ?? "",
                                    Value = ReplaceDynamicVariables(installDir, key.AsString() ?? "", true)!,
                                });
                            }
                        }
                    }
                    else if (isDword)
                    {
                        dwords.Add(new PostInstallRegistryValue
                        {
                            Language = null,
                            Group = group.Name,
                            Key = keyOrLanguage.Name ?? "",
                            Value = ReplaceDynamicVariables(installDir, value, true)!,
                        });
                    }
                    else
                    {
                        strings.Add(new PostInstallRegistryValue
                        {
                            Language = keyOrLanguage.Name,
                            Group = group.Name,
                            Key = keyOrLanguage.Name ?? "",
                            Value = ReplaceDynamicVariables(installDir, value, true)!,
                        });
                    }
                }
            }
        }

        return new PostInstallRegistry
        {
            Dword = dwords.ToArray(),
            String = strings.ToArray(),
        };
    }

    private List<PostInstallRunProcess> GetRunProcessList(string installDir, KeyValue installScript)
    {
        var runProcessParams = new List<PostInstallRunProcess>();
        var runProcess = installScript.Children.Find((child) => child.Name?.ToLowerInvariant() == "run process");

        if (runProcess != null)
        {
            foreach (var processEntry in runProcess.Children)
            {
                if (processEntry == null)
                    continue;

                var process = processEntry.Children.Find((child) => child.Name?.ToLowerInvariant() == "process 1")?.AsString();
                if (process == null)
                    continue;

                var requirementOs = processEntry.Children.Find((child) => child.Name?.ToLowerInvariant() == "requirement_os");
                var is64BitWindows = requirementOs?.Children.Find((child) => child.Name?.ToLowerInvariant() == "is64bitwindows")?.AsString();

                var runProcessEntry = new PostInstallRunProcess
                {
                    Name = processEntry.Name ?? "script",
                    HasRunKey = processEntry.Children.Find((child) => child.Name?.ToLowerInvariant() == "hasrunkey")?.AsString(),
                    Process = ReplaceDynamicVariables(installDir, process, false)!,
                    Command = ReplaceDynamicVariables(installDir, processEntry.Children.Find((child) => child.Name?.ToLowerInvariant() == "command 1")?.AsString(), false),
                    NoCleanUp = processEntry.Children.Find((child) => child.Name?.ToLowerInvariant() == "nocleanup")?.AsString() == "1",
                    MinimumHasRunValue = processEntry.Children.Find((child) => child.Name?.ToLowerInvariant() == "minimumhasrunvalue")?.AsString(),
                    RequirementOs = new PostInstallRunProcessRequirementOS
                    {
                        Is64BitWindows = is64BitWindows == null ? null : is64BitWindows == "1",
                        OsType = requirementOs?.Children.Find((child) => child.Name?.ToLowerInvariant() == "ostype")?.AsString(),
                    },
                };

                runProcessParams.Add(runProcessEntry);
            }
        }

        return runProcessParams;
    }

    private string? ReplaceDynamicVariables(string installDir, string? value, bool useWindowsPath)
    {
        if (value == null) return null;

        if (useWindowsPath)
        {
            return value
                .Replace("%INSTALLDIR%", GetWindowsPath(installDir))
                .Replace("%ROOTDRIVE%", GetWindowsPath(ROOT_DRIVE))
                .Replace("%APPDATA%", GetWindowsPath(_appDataFolder))
                .Replace("%USER_MYDOCS%", GetWindowsPath(_myDocsFolder))
                .Replace(
                    "COMMON_MYDOCS",
                    GetWindowsPath(_commonMyDocsFolder)
                )
                .Replace(
                    "%LOCALAPPDATA%",
                    GetWindowsPath(_localAppDataFolder)
                )
                .Replace("%WinDir%", GetWindowsPath(_windowsFolder))
                .Replace("%STEAMPATH%", GetWindowsPath(_steamFolder));
        }

        return value
                .Replace("%INSTALLDIR%", installDir)
                .Replace("%ROOTDRIVE%", ROOT_DRIVE)
                .Replace("%APPDATA%", _appDataFolder)
                .Replace("%USER_MYDOCS%", _myDocsFolder)
                .Replace(
                    "COMMON_MYDOCS",
                    _commonMyDocsFolder
                )
                .Replace(
                    "%LOCALAPPDATA%",
                    _localAppDataFolder
                )
                .Replace("%WinDir%", _windowsFolder)
                .Replace("%STEAMPATH%", _steamFolder);
    }

    private string GetWindowsPath(string value)
    {
        return $"{ROOT_DRIVE}:{value.Replace("/", "\\")}";
    }
}

public struct PostInstallRegistryValue
{
    public string? Language { get; set; }
    public string Group { get; set; }
    public string Key { get; set; }
    public string Value { get; set; }
}

public struct PostInstallRegistry
{
    public PostInstallRegistryValue[] Dword { get; set; }
    public PostInstallRegistryValue[] String { get; set; }
}

public struct PostInstallRunProcessRequirementOS
{
    public bool? Is64BitWindows { get; set; }
    public string? OsType { get; set; }
}

public struct PostInstallRunProcess
{
    public string Name { get; set; }
    public string? HasRunKey { get; set; }
    public string Process { get; set; }
    public string? Command { get; set; }
    public bool NoCleanUp { get; set; }
    public string? MinimumHasRunValue { get; set; }
    public PostInstallRunProcessRequirementOS RequirementOs { get; set; }
}

public struct PostInstall
{
    public string Path { get; set; }
    public PostInstallRegistry Registry { get; set; }
    public PostInstallRunProcess[] RunProcess { get; set; }
}