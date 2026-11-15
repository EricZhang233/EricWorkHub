using System.Diagnostics;

namespace EricWorkHub;

internal sealed class OperationService
{
    private readonly HashSet<string> _opening = new(StringComparer.Ordinal);
    private readonly BoundedCache<string, long> _lastOpened = new(64);
    private readonly object _sync = new();
    private readonly Action<ProcessStartInfo> _start;
    public OperationService(Action<ProcessStartInfo>? start = null) => _start = start ?? LaunchService.Start;

    public Task<string?> OpenAppAsync(DetectedApp app) => OpenAsync("app:" + app.Definition.Key, () => LaunchService.ForApp(app), "OpenFailed");
    public Task<string?> OpenDocumentAsync(RecentItem item) => OpenAsync(item.Identity, () => LaunchService.ForDocument(item.Location), "OpenFailed");
    public Task<string?> OpenContainingLocationAsync(RecentItem item) => OpenAsync("parent:" + item.Identity, () => DocumentActions.ContainingLocation(item), "OpenFailed");
    public Task<string?> OpenProjectAsync(bool releases) => OpenAsync(releases ? "releases" : "project",
        () => new(releases ? "https://github.com/EricZhang233/EricWorkHub/releases" : "https://github.com/EricZhang233/EricWorkHub") { UseShellExecute = true }, "OpenWebFailed");
    public Task<string?> OpenLogsAsync() => OpenAsync("logs", () =>
    {
        string directory = Path.Combine(ConfigService.SystemCachePath, "log");
        Directory.CreateDirectory(directory);
        return new(directory) { UseShellExecute = true };
    }, "OpenLogsFailed");

    private async Task<string?> OpenAsync(string key, Func<ProcessStartInfo> create, string error)
    {
        lock (_sync)
        {
            if (_opening.Contains(key) || (_lastOpened.TryGet(key, out var last) && Environment.TickCount64 - last < 1000)) return null;
            _opening.Add(key);
        }
        try
        {
            using var operation = LogService.StartOperation("Launch", "Open");
            await Task.Run(() => _start(create()));
            lock (_sync) _lastOpened.Set(key, Environment.TickCount64);
            LogService.Write("Launch", "Open request handed to Windows");
            return null;
        }
        catch (Exception ex)
        {
            LogService.Write("Launch", "Open failed", ex, level: LogService.LogLevel.Error);
            return ex is FileNotFoundException ? ex.Message : Text.T(error);
        }
        finally { lock (_sync) _opening.Remove(key); }
    }
}
