using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Playtron.Plugin;
using SteamKit2;
using Xdg.Directories;


namespace Steam.Config;

public class DownloadFileConfigData
{
    required public string Version;
    required public int ChunkCount;
    required public HashSet<string> DownloadedChunks;
}

public class DownloadFileConfig
{
    public string configDir;

    private const string ROOT_NAME = "DownloadFileConfig";
    private const string KEY_VERSION = "version";
    private const string KEY_CHUNK_COUNT = "chunk_count";
    private const string KEY_CHUNKS_DOWNLOADED_COUNT = "chunk_downloaded_count";
    private const string KEY_DOWNLOADED_CHUNKS = "downloaded_chunks";

    private ConcurrentDictionary<string, KeyValue> dataMap = new();
    private static readonly ConcurrentDictionary<string, object> FileLocks = new ConcurrentDictionary<string, object>();

    public DownloadFileConfig(string configDir)
    {
        this.configDir = configDir;
        Directory.CreateDirectory(configDir);
    }

    public DownloadFileConfigData? Get(string filePath)
    {
        object fileLock = FileLocks.GetOrAdd(filePath, new object());
        lock (fileLock)
        {
            if (dataMap.TryGetValue(filePath, out var data))
                return KeyValueToData(data);

            var keyValue = KeyValue.LoadAsText(GetSavePath(filePath));
            if (keyValue != null)
            {
                dataMap[filePath] = keyValue;
                return KeyValueToData(keyValue);
            }

            return null;
        }
    }

    public void SetAllocated(string filePath, string version, int chunkCount)
    {
        object fileLock = FileLocks.GetOrAdd(filePath, new object());
        lock (fileLock)
        {
            var data = GetKeyValuesOrCreate(filePath);
            data[KEY_VERSION] = new KeyValue(KEY_VERSION, version);
            data[KEY_CHUNK_COUNT] = new KeyValue(KEY_CHUNK_COUNT, chunkCount.ToString());
            data[KEY_CHUNKS_DOWNLOADED_COUNT] = new KeyValue(KEY_CHUNKS_DOWNLOADED_COUNT, "0");
            data[KEY_DOWNLOADED_CHUNKS] = new KeyValue(KEY_DOWNLOADED_CHUNKS);
            Save(filePath, data);
        }
    }

    public void SetChunkDownloaded(string filePath, string chunkID)
    {
        if (string.IsNullOrEmpty(chunkID)) return;

        object fileLock = FileLocks.GetOrAdd(filePath, new object());
        lock (fileLock)
        {
            var data = GetKeyValuesOrCreate(filePath);
            data[KEY_DOWNLOADED_CHUNKS][chunkID] = new KeyValue(chunkID, "1");
            data[KEY_CHUNKS_DOWNLOADED_COUNT] = new KeyValue(KEY_CHUNKS_DOWNLOADED_COUNT, data[KEY_DOWNLOADED_CHUNKS].Children.Count.ToString());
            Save(filePath, data);
        }
    }

    public void SetChunksDownloaded(string filePath, string version, int chunkCount, IEnumerable<string> chunkIDs)
    {
        object fileLock = FileLocks.GetOrAdd(filePath, new object());
        lock (fileLock)
        {
            var data = GetKeyValuesOrCreate(filePath);
            data[KEY_VERSION] = new KeyValue(KEY_VERSION, version);
            data[KEY_CHUNK_COUNT] = new KeyValue(KEY_CHUNK_COUNT, chunkCount.ToString());
            data[KEY_DOWNLOADED_CHUNKS] = new KeyValue(KEY_DOWNLOADED_CHUNKS);
            foreach (var chunkID in chunkIDs)
                if (!string.IsNullOrEmpty(chunkID))
                    data[KEY_DOWNLOADED_CHUNKS][chunkID] = new KeyValue(chunkID, "1");
            data[KEY_CHUNKS_DOWNLOADED_COUNT] = new KeyValue(KEY_CHUNKS_DOWNLOADED_COUNT, data[KEY_DOWNLOADED_CHUNKS].Children.Count.ToString());
            Save(filePath, data);
        }
    }

    public void Remove()
    {
        if (Directory.Exists(configDir))
            Directory.Delete(configDir, true);
    }

    public void Remove(string filePath)
    {
        var finalPath = GetSavePath(filePath);
        if (File.Exists(finalPath))
            File.Delete(finalPath);
        dataMap.TryRemove(filePath, out var _);
    }

    private KeyValue GetKeyValuesOrCreate(string filePath)
    {
        if (dataMap.TryGetValue(filePath, out var data))
            return data;

        var keyValue = KeyValue.LoadAsText(GetSavePath(filePath));
        if (keyValue != null)
        {
            dataMap[filePath] = keyValue;
            return keyValue;
        }

        var newData = new KeyValue(ROOT_NAME);
        newData[KEY_DOWNLOADED_CHUNKS] = new KeyValue(KEY_DOWNLOADED_CHUNKS);
        dataMap[filePath] = newData;
        return newData;
    }

    private void Save(string filePath, KeyValue data)
    {
        var finalPath = GetSavePath(filePath);
        Disk.EnsureParentFolderExists(finalPath);
        data.SaveToFileWithAtomicRename(finalPath);
    }

    private string GetSavePath(string filePath) => Path.Join(configDir, filePath);

    private DownloadFileConfigData KeyValueToData(KeyValue keyValue)
    {
        var downloadedChunks = keyValue[KEY_DOWNLOADED_CHUNKS].Children.Select((child) => child.Name!).ToHashSet();

        return new DownloadFileConfigData
        {
            Version = keyValue[KEY_VERSION].AsString()!,
            ChunkCount = keyValue[KEY_CHUNK_COUNT].AsInteger(),
            DownloadedChunks = downloadedChunks,
        };
    }
}

