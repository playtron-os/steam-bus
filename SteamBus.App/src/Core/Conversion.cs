public static class ConversionUtils
{
    public static ulong ConvertToBytes(ulong value, string unit)
    {
        switch (unit.ToUpper())
        {
            case "KB": return value * 1024;
            case "MB": return value * 1024 * 1024;
            case "GB": return value * 1024 * 1024 * 1024;
            case "TB": return value * 1024L * 1024 * 1024 * 1024; // Use `L` for large numbers
            default: return value; // Assume already in bytes if no unit is matched
        }
    }
}