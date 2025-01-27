using Steam.Cloud;
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
			result.Add(cacheFile.GetRemotePath().ToLower(), cacheFile);
		}
		return result;
	}

	public void UpdateLocalCache(ulong changeNumber, string osType, RemoteCacheFile[] files)
	{
		KeyValue newPair = new(this.appid.ToString());
		newPair["ChangeNumber"] = new KeyValue("ChangeNumber", changeNumber.ToString());
		newPair["ostype"] = new KeyValue("ostype", osType);
		foreach (var file in files)
		{
			newPair.Children.Add(file.GetKeyValue());
		}
		data = newPair;
	}

	public void Save()
	{
		Console.WriteLine("Saving data to {0}", this.path);
		data.SaveToFile(this.path, false);
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


public class RemoteCacheFile : IRemoteFile
{
	public string Path { get; }
	public ERemoteStorageFileRoot Root { get; }
	public string Sha;
	public uint Size;
	public ulong LocalTime;
	public ulong RemoteTime;
	public ulong Time;
	public ERemoteStorageSyncState SyncState;
	public ERemoteStoragePlatform PlatformsToSync { get; }
	public ECloudStoragePersistState PersistState;

	public RemoteCacheFile(KeyValue data)
	{
		Path = data.Name ?? "";
		Root = data["root"].AsEnum<ERemoteStorageFileRoot>();
		Sha = data["sha"].AsString() ?? "";
		Size = data["size"].AsUnsignedInteger();
		LocalTime = data["localtime"].AsUnsignedLong();
		RemoteTime = data["remotetime"].AsUnsignedLong();
		Time = data["time"].AsUnsignedLong();
		SyncState = data["syncstate"].AsEnum<ERemoteStorageSyncState>();
		PlatformsToSync = data["platformstosync2"].AsEnum<ERemoteStoragePlatform>();
		PersistState = data["persiststate"].AsEnum<ECloudStoragePersistState>();
	}

	public RemoteCacheFile(CCloud_AppFileInfo fileInfo, string path)
	{
		var (root, relpath) = CloudUtils.SplitRootPath(path);
		Path = relpath;
		Root = GetRemoteRootEnum(root);
		Sha = BitConverter.ToString(fileInfo.sha_file).Replace("-", "").ToLower();
		Size = fileInfo.raw_file_size;
		LocalTime = fileInfo.time_stamp;
		RemoteTime = fileInfo.time_stamp;
		Time = fileInfo.time_stamp;
		SyncState = ERemoteStorageSyncState.unknown;
		PlatformsToSync = (ERemoteStoragePlatform)fileInfo.platforms_to_sync;
		PersistState = fileInfo.persist_state;
	}

	public RemoteCacheFile(LocalFile localFile)
	{
		var (root, relpath) = CloudUtils.SplitRootPath(localFile.GetRemotePath());
		Path = relpath;
		Root = GetRemoteRootEnum(root);
		Sha = localFile.Sha1();
		LocalTime = localFile.UpdateTime;
		Time = localFile.UpdateTime;
		Size = localFile.Size;
		RemoteTime = 0;
		SyncState = ERemoteStorageSyncState.inprogress;
		PlatformsToSync = ERemoteStoragePlatform.All;
		PersistState = ECloudStoragePersistState.k_ECloudStoragePersistStatePersisted;
	}

	public KeyValue GetKeyValue()
	{
		KeyValue keyValue = new(this.Path);
		keyValue.Children.Add(new KeyValue("root", this.Root.ToString("D")));
		keyValue.Children.Add(new KeyValue("size", this.Size.ToString()));
		keyValue.Children.Add(new KeyValue("localtime", this.LocalTime.ToString()));
		keyValue.Children.Add(new KeyValue("time", this.Time.ToString()));
		keyValue.Children.Add(new KeyValue("remotetime", this.RemoteTime.ToString()));
		keyValue.Children.Add(new KeyValue("sha", this.Sha));
		keyValue.Children.Add(new KeyValue("syncstate", this.SyncState.ToString("D")));
		keyValue.Children.Add(new KeyValue("persiststate", this.PersistState.ToString("D")));
		keyValue.Children.Add(new KeyValue("platformstosync2", this.PlatformsToSync.ToString("D")));
		return keyValue;
	}

	public string GetRemotePath()
	{
		if (Root <= 0)
		{
			return Path;
		}

		string enumName = Enum.GetName(typeof(ERemoteStorageFileRoot), Root) ?? throw new Exception("Enum.GetName unexpectedly returned null");
		string rootStr = enumName[24..];

		return $"%{rootStr}%{Path}";
	}

	public static ERemoteStorageFileRoot GetRemoteRootEnum(string root)
	{
		return root.ToLower() switch
		{
			"" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootDefault,
			"gameinstall" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootGameInstall,
			"winmydocuments" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootWinMyDocuments,
			"winappdatalocal" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootWinAppDataLocal,
			"winappdataroaming" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootWinAppDataRoaming,
			"steamuserbasestorage" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootSteamUserBaseStorage,
			"machome" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootMacHome,
			"macappsupport" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootMacAppSupport,
			"macdocuments" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootMacDocuments,
			"winsavedgames" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootWinSavedGames,
			"winprogramdata" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootWinProgramData,
			"steamclouddocuments" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootSteamCloudDocuments,
			"winappdatalocallow" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootWinAppDataLocalLow,
			"maccaches" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootMacCaches,
			"linuxhome" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootLinuxHome,
			"linuxxdgdatahome" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootLinuxXdgDataHome,
			"linuxxdgconfighome" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootLinuxXdgConfigHome,
			"androidsteampackageroot" => ERemoteStorageFileRoot.k_ERemoteStorageFileRootAndroidSteamPackageRoot,
			_ => ERemoteStorageFileRoot.k_ERemoteStorageFileRootInvalid,
		};
	}

	public string Sha1()
	{
		return Sha;
	}

	public ulong UpdateTime
	{
		get
		{
			return Time;
		}
	}
}
