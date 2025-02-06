using System.Security.Cryptography;
using SteamKit2;

namespace Steam.Cloud;


public class LocalFile : IRemoteFile
{
  // File system path to the file
  private readonly string filePath;
  // Relative path to the SearchRoot
  public string RelativePath { get; private set; }
  // Dir where search happened, like %GameInstall%b1/Save/UserID
  public string SearchRoot { get; private set; }
  public ulong UpdateTime { get; private set; }
  public uint Size { get; private set; }
  public ERemoteStoragePlatform PlatformsToSync { get; private set; }

  public LocalFile(string path, string relpath, string root, ERemoteStoragePlatform platform)
  {
    this.filePath = path;
    this.RelativePath = relpath.Trim('/', '\\');
    this.SearchRoot = root;
    // Calculate timestamp
    FileInfo stat = new(path);
    TimeSpan time = stat.LastWriteTimeUtc - DateTime.UnixEpoch;
    this.UpdateTime = (ulong)time.TotalSeconds;
    this.Size = (uint)stat.Length;
    this.PlatformsToSync = platform;
  }

  public string Sha1()
  {
    // Calculate sha1 hash
    FileStream handle = File.OpenRead(this.filePath);
    BufferedStream bs = new(handle);
    var data = SHA1.HashData(bs);
    return BitConverter.ToString(data).Replace("-", "").ToLower();
  }

  public string GetRemotePath()
  {
    return Path.Join(SearchRoot, RelativePath);
  }
}