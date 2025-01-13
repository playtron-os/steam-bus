using SteamKit2;
using SteamKit2.Internal;

namespace Steam.Config;

/*
"1863080"
{
	"ChangeNumber"		"2"
	"ostype"		"-203"
	"Raffa/BeatInvaders/savegames/slot_0/savegame.bin"
	{
		"root"		"4"
		"size"		"1504"
		"localtime"		"1713504607"
		"time"		"1713504606"
		"remotetime"		"1713504606"
		"sha"		"ae0bdd810d72861a7d3b5c5808b85d228bf0f50d"
		"syncstate"		"1"
		"persiststate"		"0"
		"platformstosync2"		"-1"
	}
}

FORMAT:
	ChangeNumber - identifier of the cloud state
	ostype - 0 for windows, -203 for linux
	..other keys - relative path to the file
		root - ERemoteStorageFileRoot
		syncstate - possibly ERemoteStorageSyncState, every entry I see has it set to 1
		persiststate - 0 - persisted, 1 - forgotten, 2 - deleted
		platformstosync2 - a SteamKit2.ERemoteStoragePlatform
*/

public enum ERemoteStorageSyncState : uint
{
	disabled,
	unknown,
	synchronized,
	inprogress,
	changesincloud,
	changeslocally,
	changesincloudandlocally,
	conflictingchanges,
	notinitialized
};

public enum ERemoteStorageFileRoot
{
	k_ERemoteStorageFileRootInvalid = -1, // Invalid
	k_ERemoteStorageFileRootDefault, // Default
	k_ERemoteStorageFileRootGameInstall, // GameInstall
	k_ERemoteStorageFileRootWinMyDocuments, // WinMyDocuments
	k_ERemoteStorageFileRootWinAppDataLocal, // WinAppDataLocal
	k_ERemoteStorageFileRootWinAppDataRoaming, // WinAppDataRoaming
	k_ERemoteStorageFileRootSteamUserBaseStorage, // SteamUserBaseStorage
	k_ERemoteStorageFileRootMacHome, // MacHome
	k_ERemoteStorageFileRootMacAppSupport, // MacAppSupport
	k_ERemoteStorageFileRootMacDocuments, // MacDocuments
	k_ERemoteStorageFileRootWinSavedGames, // WinSavedGames
	k_ERemoteStorageFileRootWinProgramData, // WinProgramData
	k_ERemoteStorageFileRootSteamCloudDocuments, // SteamCloudDocuments
	k_ERemoteStorageFileRootWinAppDataLocalLow, // WinAppDataLocalLow
	k_ERemoteStorageFileRootMacCaches, // MacCaches
	k_ERemoteStorageFileRootLinuxHome, // LinuxHome
	k_ERemoteStorageFileRootLinuxXdgDataHome, // LinuxXdgDataHome
	k_ERemoteStorageFileRootLinuxXdgConfigHome, // LinuxXdgConfigHome
	k_ERemoteStorageFileRootAndroidSteamPackageRoot, // AndroidSteamPackageRoot
}

// TODO: Be able to modify entries
public class RemoteCache
{
	private KeyValue data;
	public string path;
	public uint appid;

	public RemoteCache(uint userid, uint appid) : this(appid, GetRemoteCachePath(userid, appid)) { }

	public RemoteCache(uint appid, string path)
	{
		this.appid = appid;
		this.path = path;
		if (File.Exists(path))
		{
			Reload();
		}
		else
		{
			Console.WriteLine("WARN: File {0} doesn't exist", path);
			Console.WriteLine(Directory.GetCurrentDirectory());
		}
		this.data ??= new KeyValue(appid.ToString());
	}
	public void Reload()
	{
		var stream = File.OpenText(this.path);
		var content = stream.ReadToEnd();

		var data = KeyValue.LoadFromString(content);
		this.data = data ?? new KeyValue(appid.ToString());
		stream.Close();
	}

	public ulong? GetChangeNumber()
	{
		return this.data["ChangeNumber"].AsUnsignedLong();
	}

	public Dictionary<string, RemoteCacheFile> MapRemoteCacheFiles()
	{
		Dictionary<string, RemoteCacheFile> result = [];
		foreach (var entry in this.data.Children)
		{
			if (entry.Name == "ChangeNumber" || entry.Name == "ostype") continue;
			RemoteCacheFile cacheFile = new(entry);
			result.Add(cacheFile.GetRemoteSavePath(), cacheFile);
		}
		return result;
	}

	public static string GetRemoteCachePath(uint userid, uint appid)
	{
		var config = SteamConfig.GetConfigDirectory();
		return $"{config}/userdata/{userid}/{appid}/remotecache.vdf";
	}

	public static string GetRemoteSavePath(uint userid, uint appid)
	{
		var config = SteamConfig.GetConfigDirectory();
		return $"{config}/userdata/{userid}/{appid}/remote";
	}
}


public class RemoteCacheFile(KeyValue data)
{
	public string Path { get; } = data.Name ?? "";
	public ERemoteStorageFileRoot Root { get; } = data["root"].AsEnum<ERemoteStorageFileRoot>();
	public string Sha = data["sha"].AsString() ?? "";
	public uint Size = data["size"].AsUnsignedInteger();
	public ulong LocalTime = data["localtime"].AsUnsignedLong();
	public ulong RemoteTime = data["remotetime"].AsUnsignedLong();
	public ulong Time = data["time"].AsUnsignedLong();
	public ERemoteStorageSyncState SyncState = data["syncstate"].AsEnum<ERemoteStorageSyncState>();
	public ERemoteStoragePlatform PlatformsToSync { get; } = data["platformstosync2"].AsEnum<ERemoteStoragePlatform>();
	public ECloudStoragePersistState PersistState = data["persiststate"].AsEnum<ECloudStoragePersistState>();

	public string GetRemoteSavePath()
	{
		if (Root <= 0)
		{
			return Path;
		}

		string enumName = Enum.GetName(typeof(ERemoteStorageFileRoot), Root) ?? throw new Exception("Enum.GetName unexpectedly returned null");
		string rootStr = enumName[24..];

		return $"%{rootStr}%{Path}";
	}
}
