using System.Diagnostics;

namespace EricWorkHub;

internal sealed record DocumentMenu(string CopyText, bool IsLink, bool IsWeb);

internal static class DocumentActions
{
    public static DocumentMenu Menu(RecentItem item)
    {
        if (!DocumentLocation.TryParse(item.Location, out var target, out var cloud)) throw new ArgumentException(Text.T("UnsupportedDocument"));
        return new(target, cloud, !Resource(target).IsFile);
    }

    public static ProcessStartInfo ContainingLocation(RecentItem item)
    {
        var target = Menu(item);
        var uri = Resource(target.CopyText);
        if (!uri.IsFile) return new(new Uri(uri, ".").AbsoluteUri) { UseShellExecute = true };
        string path = uri.LocalPath;
        if (path.Contains('"')) throw new ArgumentException(Text.T("UnsupportedDocument"));
        return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = true,
            Arguments = "/select,\"" + path + "\""
        };
    }

    private static Uri Resource(string resource)
    {
        if (resource.StartsWith("onenote:", StringComparison.OrdinalIgnoreCase)) resource = resource[8..];
        else if (resource.Contains("|u|", StringComparison.Ordinal)) resource = resource[(resource.IndexOf("|u|", StringComparison.Ordinal) + 3)..];
        return new(resource, UriKind.Absolute);
    }
}
