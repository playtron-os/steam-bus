using System.Security.Cryptography;

namespace Steam.Cloud;


class LocalFile
{
  // File system path to the file
  private readonly string filePath;
  // Relative path to the SearchRoot
  public string RelativePath { get; private set; }
  // Dir where search happened, like %GameInstall%b1/Save/UserID
  public string SearchRoot { get; private set; }
  public ulong UpdateTime { get; private set; }
  public uint Size { get; private set; }

  public LocalFile(string path, string relpath, string root)
  {
    this.filePath = path;
    this.RelativePath = relpath;
    this.SearchRoot = root;
    // Calculate timestamp
    FileInfo stat = new(path);
    TimeSpan time = stat.LastWriteTimeUtc - DateTime.UnixEpoch;
    this.UpdateTime = (ulong)time.TotalSeconds;
    this.Size = (uint)stat.Length;
  }

  public async Task<string> Sha1()
  {
    // Calculate sha1 hash
    FileStream handle = File.OpenRead(this.filePath);
    BufferedStream bs = new(handle);
    var sha1 = await SHA1.HashDataAsync(bs);
    return BitConverter.ToString(sha1).Replace("-", "").ToLower();
  }

  // Splits path after the variable
  public (string, string) SplitRootPath()
  {
    var path = GetRemotePath();
    var percentageSign = path.IndexOf('%', 1);
    if (percentageSign == -1)
    {
      return ("", path);
    }

    return (path[1..percentageSign++], path[percentageSign..]);
  }

  public string GetRemotePath()
  {
    return Path.Join(SearchRoot, RelativePath);
  }
}