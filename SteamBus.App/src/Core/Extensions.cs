using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using SteamKit2;

public static class EnumExtensions
{
    public static string GetDescription(this Enum value)
    {
        FieldInfo? field = value.GetType().GetField(value.ToString());
        DescriptionAttribute? attribute = field?.GetCustomAttribute<DescriptionAttribute>();
        return attribute?.Description ?? value.ToString();
    }
}

public static class KeyValueExtensions
{
    // This is needed because since KeyValue.SaveToFile writes data recursively, if the program is killed the file will be left in an invalid state
    public static void SaveToFileWithAtomicRename(this KeyValue keyValue, string filePath)
    {
        string tempFilePath = filePath + ".temp";

        try
        {
            Disk.ExecuteFileOpWithRetry(() =>
            {
                keyValue.SaveToFile(tempFilePath, false);
                return "";
            }, tempFilePath, maxRetries: 3, delayMilliseconds: 1, OnError: () => File.Delete(tempFilePath));

            Disk.ExecuteFileOpWithRetry(() =>
            {
                File.Move(tempFilePath, filePath, true);
                return "";
            }, filePath, maxRetries: 3, delayMilliseconds: 5);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error occurred saving KeyValue file {filePath}: {ex.Message}");
            throw;
        }
    }
}
