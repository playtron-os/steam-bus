namespace SteamBusClientBridge.App.Models;

public struct AchievementIcon(int id)
{
    public int Id { get; set; } = id;
    public byte[] Data { get; set; } = [];
    public uint Width { get; set; } = 0;
    public uint Height { get; set; } = 0;
}

public struct AchievementData
{
    public string Name { get; set; }
    public string Desc { get; set; }
    public AchievementIcon Icon { get; set; }
    public bool Hidden { get; set; }
    public float MinProgress { get; set; }
    public float MaxProgress { get; set; }
    public float Percent { get; set; }
    public bool Unlocked { get; set; }
    public uint UnlockTime { get; set; }
}