namespace EricWorkHub;

internal sealed record HubSnapshot(IReadOnlyList<DetectedApp> Apps, IReadOnlyList<RecentItem> Documents, IReadOnlyList<string> Warnings);

internal sealed class HubService
{
    private readonly RefreshGate _gate = new();
    private readonly AppDetectionService _apps = new();
    private readonly EdgeHistoryReader _edge = new();
    private readonly RecentSourceCache _recent = new();
    private readonly Func<bool, CancellationToken, Task<IReadOnlyList<DetectedApp>>> _detect;
    private readonly Func<IReadOnlyList<DetectedApp>, CancellationToken, Task<RecentResult>> _collect;
    private HubSnapshot _snapshot = new([AppCatalog.OfficeManager], [], []);
    public HubSnapshot Snapshot => _snapshot;

    public HubService(Func<bool, CancellationToken, Task<IReadOnlyList<DetectedApp>>>? detect = null,
        Func<IReadOnlyList<DetectedApp>, CancellationToken, Task<RecentResult>>? collect = null)
    {
        _detect = detect ?? _apps.DetectAsync;
        _collect = collect ?? ((apps, token) => Task.Run(() => Recent.GetItems(ConfigService.Settings,
            apps.Where(app => app.IsInstalled).Select(app => app.Definition.Key).ToHashSet(StringComparer.OrdinalIgnoreCase), _edge, token, apps, _recent), token));
    }

    public async Task<HubSnapshot?> RefreshAsync(bool force, CancellationToken token, Action<HubSnapshot>? applicationsReady = null)
    {
        HubSnapshot? result = null;
        await _gate.RunAsync(force, TimeSpan.FromSeconds(10), async cancellation =>
        {
            using var operation = LogService.StartOperation("Refresh", "RefreshHub");
            try
            {
                var apps = await _detect(force, cancellation);
                cancellation.ThrowIfCancellationRequested();
                var installed = apps.Where(app => app.IsInstalled).Select(app => app.Definition.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                _snapshot = _snapshot with
                {
                    Apps = apps.Where(app => !app.Definition.IsOfficeManager && app.IsInstalled).Append(AppCatalog.OfficeManager).ToArray(),
                    Documents = RecentSourceCache.Filter(_snapshot.Documents, ConfigService.Settings, installed, cancellation)
                };
                applicationsReady?.Invoke(_snapshot);
                LogService.Write("Refresh", $"Installed applications ready count={_snapshot.Apps.Count}");
                var recent = await _collect(apps, cancellation);
                cancellation.ThrowIfCancellationRequested();
                var warnings = recent.Warnings.ToList();
                if (_apps.Warning != null) warnings.Add(_apps.Warning);
                _snapshot = new(apps.Where(app => !app.Definition.IsOfficeManager && app.IsInstalled).Append(AppCatalog.OfficeManager).ToArray(),
                    recent.AllSourcesFailed && !recent.HasSourceSnapshots ? _snapshot.Documents : recent.Items.ToArray(), warnings.Distinct().ToArray());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Write("Refresh", "Refresh failed; previous list preserved", ex, level: LogService.LogLevel.Error);
                _snapshot = _snapshot with { Warnings = [Text.T("RefreshFailed")] };
            }
            result = _snapshot;
        }, token);
        return result;
    }
}
