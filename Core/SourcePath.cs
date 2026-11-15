namespace EricWorkHub;

internal static class SourcePath
{
    public static bool Exists(string path, bool directory)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return ((File.GetAttributes(path) & FileAttributes.Directory) != 0) == directory; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
