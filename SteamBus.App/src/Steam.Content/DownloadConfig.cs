// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Playtron.Plugin;

namespace Steam.Content;

class AppDownloadOptions
{
  public const string DEFAULT_BRANCH = "public";

  public List<(uint depotId, ulong manifestId)> DepotManifestIds;
  public string Branch = DEFAULT_BRANCH;
  public string Os;
  public string Arch;
  public string Language;
  public bool LowViolence = false;
  public bool IsUgc = false;

  public int CellID { get; set; }
  public bool DownloadAllPlatforms { get; set; }
  public bool DownloadAllArchs { get; set; }
  public bool DownloadAllLanguages { get; set; }
  public bool DownloadManifestOnly = false;
  public string? InstallDirectory { get; set; }

  public bool UsingFileList { get; set; }
  public HashSet<string>? FilesToDownload { get; set; }
  public List<Regex>? FilesToDownloadRegex { get; set; }

  public string? BetaPassword { get; set; }

  public bool VerifyAll = false;

  // maximum number of content servers to use. (default: 20).
  public int MaxServers = 20;
  // maximum number of chunks to download concurrently. (default: 8).
  public int MaxDownloads = 8;

  public AppDownloadOptions()
  {
    this.DepotManifestIds = new List<(uint depotId, ulong manifestId)>();
    this.Branch = DEFAULT_BRANCH;
    this.Os = GetSteamOS();
    this.Arch = GetSteamArch();
    this.Language = "english";
    this.LowViolence = false;
    this.IsUgc = false;

    this.DownloadAllPlatforms = false;
    this.DownloadAllArchs = false;
    this.DownloadAllLanguages = false;

  }

  public AppDownloadOptions(InstallOptions options)
  {
    this.DepotManifestIds = new List<(uint depotId, ulong manifestId)>();
    this.Branch = String.IsNullOrEmpty(options.branch) ? DEFAULT_BRANCH : options.branch;
    this.Os = String.IsNullOrEmpty(options.os) ? GetSteamOS() : options.os;
    this.Arch = String.IsNullOrEmpty(options.architecture) ? GetSteamArch() : options.architecture;
    this.Language = String.IsNullOrEmpty(options.language) ? "english" : options.language;
    this.LowViolence = false;
    this.IsUgc = false;

    this.DownloadAllPlatforms = false;
    this.DownloadAllArchs = false;
    this.DownloadAllLanguages = false;
  }

  public static string GetSteamOS()
  {
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
      return "windows";
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
      return "macos";
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
      return "linux";
    }

    if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
    {
      // Return linux as freebsd steam client doesn't exist yet
      return "linux";
    }

    return "unknown";
  }

  public static string GetSteamArch()
  {
    return Environment.Is64BitOperatingSystem ? "64" : "32";
  }
}
