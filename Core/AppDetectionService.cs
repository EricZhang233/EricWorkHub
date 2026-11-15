using Windows.Management.Deployment;

namespace EricWorkHub;

internal sealed class AppDetectionService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<DetectedApp>? _cached;
    private long _lastScan;
    public string? Warning { get; private set; }

    public async Task<IReadOnlyList<DetectedApp>> DetectAsync(bool force, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!force && _cached != null && Environment.TickCount64 - _lastScan < 120_000) return _cached;
            using var operation = LogService.StartOperation("Apps", "DetectInstalled");
            Warning = null;
            var detector = new OfficeDetector();
            var executables = await Task.Run(() => AppCatalog.Definitions.ToDictionary(app => app.Key, app => detector.Detect(app, token)), token);
            var packaged = await Task.Run(async () =>
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var wanted = AppCatalog.Definitions.SelectMany(app => app.PackageNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var package in new PackageManager().FindPackagesForUser(string.Empty))
                    {
                        token.ThrowIfCancellationRequested();
                        if (!wanted.Contains(package.Id.Name) || package.IsFramework || package.IsResourcePackage) continue;
                        try
                        {
                            if (!package.Status.VerifyIsOK())
                            {
                                LogService.Write("Apps", $"Package is not available package={package.Id.Name}");
                                continue;
                            }
                            var entries = await package.GetAppListEntriesAsync().AsTask(token);
                            for (int index = 0; index < entries.Count; index++)
                            {
                                if (string.IsNullOrWhiteSpace(entries[index].AppUserModelId)) continue;
                                result.TryAdd(package.Id.Name, entries[index].AppUserModelId);
                                break;
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Warning = Text.T("PackageEntryFailed");
                            LogService.Write("Apps", $"Package entry unavailable package={package.Id.Name}", ex, level: LogService.LogLevel.Warn);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Warning = Text.T("PackageDetectionFailed");
                    LogService.Write("Apps", "Package enumeration failed", ex, level: LogService.LogLevel.Warn);
                }
                return result;
            }, token);
            var recentIds = await Task.Run(() => JumpListReader.FindAppIds(token), token);
            _cached = AppCatalog.Definitions.Select(app => new DetectedApp(app, executables[app.Key],
                app.PackageNames.Select(name => packaged.GetValueOrDefault(name)).FirstOrDefault(id => id != null), recentIds.GetValueOrDefault(app.Key))).ToArray();
            _lastScan = Environment.TickCount64;
            LogService.Write("Apps", $"Detection complete installed={_cached.Count(app => app.IsInstalled)} total={_cached.Count}");
            return _cached;
        }
        finally { _gate.Release(); }
    }
}
