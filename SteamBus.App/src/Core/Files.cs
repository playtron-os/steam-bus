public static class FileUtils
{
    public static string CaseInsensitiveLookup(string filePath, string root)
    {
        if (Path.Exists(filePath))
        {
            return filePath;
        }
        string? enumerator = filePath;
        List<string> pathElements = [];
        while (true)
        {
            if (enumerator == null || String.Equals(enumerator, root))
            {
                return filePath;
            }
            if (Path.Exists(enumerator)) break;

            string fileName = Path.GetFileName(enumerator);
            pathElements.Add(fileName);
            enumerator = Path.GetDirectoryName(enumerator);
        }

        pathElements.Reverse();

        for (int i = 0; i < pathElements.Count; i++)
        {
            var matchedEntry = Directory.EnumerateFileSystemEntries(enumerator).FirstOrDefault(entry => String.Equals(Path.GetFileName(entry), pathElements[i], StringComparison.OrdinalIgnoreCase));
            if (matchedEntry != null)
            {
                enumerator = matchedEntry;
            }
            else
            {
                enumerator = Path.Combine([enumerator, .. pathElements[i..]]);
                return enumerator;
            }
        }
        return enumerator;
    }
}