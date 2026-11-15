namespace EricWorkHub;

internal static class DocumentPreview
{
    public static string Key(RecentItem item) => item.Identity + ":" + item.Timestamp.UtcTicks + ":" + item.Revision;

    public static RecentItem Refresh(RecentItem item)
    {
        if (item.IsCloud || item.IsNetwork) return item;
        try
        {
            var file = new FileInfo(item.Location);
            return file.Exists ? item with { Revision = $"{file.LastWriteTimeUtc.Ticks}:{file.Length}" } : item;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Write("Icon", "Document preview metadata unavailable", ex, level: LogService.LogLevel.Debug);
            return item;
        }
    }
}

internal sealed class PreviewRequestState
{
    private string _version = "", _loaded = "";
    private int _generation;
    private bool _pending;

    public void Update(string version)
    {
        if (_version == version) return;
        _version = version;
        _generation++;
        _pending = false;
    }

    public bool TryBegin(out int request)
    {
        request = _generation;
        if (_pending || _loaded == _version) return false;
        _pending = true;
        return true;
    }

    public bool Complete(int request, bool hasImage)
    {
        if (request != _generation) return false;
        _pending = false;
        if (hasImage) _loaded = _version;
        return hasImage;
    }
}
