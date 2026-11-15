using System.Globalization;
using System.Text.RegularExpressions;

namespace EricWorkHub;

internal enum FileAvailability { Available, Network, Missing }

internal static class DocumentLocation
{
    public static bool TryParse(string location, out string target, out bool isCloud)
    {
        target = string.Empty;
        isCloud = false;
        if (string.IsNullOrWhiteSpace(location)) return false;
        location = location.Trim();
        if (location.StartsWith("onenote:", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseResource(location[8..], out var note, out _)) return false;
            target = "onenote:" + note;
            isCloud = true;
            return true;
        }
        int colon = location.IndexOf(':');
        if (colon > 0 && OfficeSchemes.Contains(location[..colon], StringComparer.OrdinalIgnoreCase))
        {
            string[] parts = location[(colon + 1)..].Split('|');
            if (parts.Length != 3 || (parts[0] != "ofv" && parts[0] != "ofe") || parts[1] != "u"
                || !TryParseResource(parts[2], out var resource, out var remote) || !remote) return false;
            target = location[..colon].ToLowerInvariant() + ":" + parts[0] + "|u|" + resource;
            isCloud = true;
            return true;
        }
        return TryParseResource(location, out target, out isCloud);
    }

    private static readonly string[] OfficeSchemes = ["ms-word", "ms-excel", "ms-powerpoint", "ms-visio", "ms-project", "ms-access", "ms-publisher"];
    private static bool TryParseResource(string location, out string target, out bool isCloud)
    {
        target = string.Empty;
        isCloud = false;
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri)) return false;
        if (uri.IsFile)
        {
            target = uri.LocalPath;
            return Path.IsPathFullyQualified(target);
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        target = uri.AbsoluteUri;
        isCloud = true;
        return true;
    }

    public static string Identity(string location)
    {
        if (!TryParse(location, out var target, out var cloud)) throw new ArgumentException("Unsupported document location");
        if (!cloud) return "file:" + Path.GetFullPath(target).Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
        if (target.StartsWith("onenote:", StringComparison.Ordinal)) return "onenote:" + Identity(target[8..]);
        var uri = ResourceUri(target);
        return uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
    }

    public static bool IsNetwork(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try { return new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network; }
        catch (ArgumentException) { return false; }
        catch (IOException) { return true; }
    }

    public static FileAvailability Availability(string path)
    {
        if (IsNetwork(path)) return FileAvailability.Network;
        try { return (File.GetAttributes(path) & FileAttributes.Directory) == 0 ? FileAvailability.Available : FileAvailability.Missing; }
        catch (FileNotFoundException) { return FileAvailability.Missing; }
        catch (DirectoryNotFoundException) { return FileAvailability.Missing; }
        catch (UnauthorizedAccessException) { return FileAvailability.Network; }
        catch (IOException) { return FileAvailability.Network; }
    }

    public static string Extension(string location) => Path.GetExtension(ResourceUri(location).LocalPath).ToLowerInvariant();

    public static string Title(string location)
    {
        if (!TryParse(location, out var target, out var cloud)) return location;
        var uri = ResourceUri(target);
        string path = cloud ? Uri.UnescapeDataString(uri.AbsolutePath) : target;
        return string.IsNullOrWhiteSpace(Path.GetFileName(path)) ? uri.Host : Path.GetFileName(path);
    }

    public static string Display(string location)
    {
        var uri = ResourceUri(location);
        return uri.IsFile ? uri.LocalPath : uri.GetLeftPart(UriPartial.Path);
    }

    private static Uri ResourceUri(string location)
    {
        if (location.StartsWith("onenote:", StringComparison.OrdinalIgnoreCase)) location = location[8..];
        else if (location.Contains("|u|", StringComparison.Ordinal)) location = location[(location.IndexOf("|u|", StringComparison.Ordinal) + 3)..];
        return new Uri(location, UriKind.Absolute);
    }

    public static DateTimeOffset? ParseMruTimestamp(string raw)
    {
        var match = Regex.Match(raw, @"\[T([0-9A-Fa-f]{16})\]|^T([0-9A-Fa-f]{16})(?:\*|$)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        string hex = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        if (!long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) || value <= 0) return null;
        try { return DateTimeOffset.FromFileTime(value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
