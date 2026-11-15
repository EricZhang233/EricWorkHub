namespace EricWorkHub;

internal sealed class RecentSourceCache(Func<DateTimeOffset>? clock = null)
{
    private sealed record Snapshot(DateTimeOffset SavedAt, IReadOnlyList<RecentItem> Items);
    private readonly Dictionary<string, Snapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public IReadOnlyList<RecentItem> Update(string key, IReadOnlyList<RecentItem> items, bool succeeded, int? ageDays = null)
    {
        if (succeeded) { _snapshots[key] = new(_clock(), items.ToArray()); return items; }
        if (!_snapshots.TryGetValue(key, out var snapshot)) return [];
        if (snapshot.SavedAt >= _clock().AddDays(-(ageDays ?? ConfigService.Settings.CacheMaxAgeDays))) return snapshot.Items;
        _snapshots.Remove(key);
        return [];
    }

    public void Retain(IReadOnlySet<string> active)
    {
        foreach (string key in _snapshots.Keys.Where(key => !active.Contains(key)).ToArray()) _snapshots.Remove(key);
    }

    internal static IReadOnlyList<RecentItem> Filter(IEnumerable<RecentItem> items, AppSettings settings,
        IReadOnlySet<string> installed, CancellationToken token, DateTimeOffset? now = null)
    {
        var result = new List<RecentItem>();
        var cutoff = (now ?? DateTimeOffset.UtcNow).AddDays(-settings.CacheMaxAgeDays);
        foreach (var candidate in items)
        {
            token.ThrowIfCancellationRequested();
            var item = Recent.NormalizeApplication(candidate);
            if (!DocumentLocation.TryParse(item.Location, out var target, out var cloud)) continue;
            if (item.TimeKind != DocumentTimeKind.Unknown && item.Timestamp < cutoff) continue;
            if (!cloud && DocumentLocation.Availability(target) == FileAvailability.Missing) continue;
            var original = item.AppKeys.ToArray();
            var apps = original.Where(app => !settings.UseInstalledAppsOnly || app == "PDF" || installed.Contains(app)).Distinct(StringComparer.Ordinal).ToArray();
            if (apps.Length == 0) continue;
            result.Add(apps.SequenceEqual(original, StringComparer.Ordinal) ? item
                : item with { App = apps.Contains(item.App, StringComparer.Ordinal) ? item.App : apps[0], RelatedApps = string.Join(';', apps) });
        }
        return result;
    }
}
