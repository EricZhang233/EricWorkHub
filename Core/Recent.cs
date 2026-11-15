using Microsoft.Win32;
using System.Globalization;

namespace EricWorkHub;

internal enum DocumentTimeKind { Accessed, Recorded, Modified, Unknown }

internal sealed record RecentItem(string Title, string Location, DateTimeOffset Timestamp, string App,
    string Source, bool IsCloud, DocumentTimeKind TimeKind = DocumentTimeKind.Accessed, bool IsNetwork = false, string RelatedApps = "", string Revision = "")
{
    public string Identity => DocumentLocation.Identity(Location);
    public string DisplayLocation => DocumentLocation.Display(Location);
    public string TimestampText => TimeKind == DocumentTimeKind.Unknown ? Text.T("TimeUnknown")
        : Text.T(TimeKind == DocumentTimeKind.Modified ? "TimeModified" : TimeKind == DocumentTimeKind.Recorded ? "TimeRecorded" : "TimeAccessed") + Timestamp.ToLocalTime().ToString(Text.T("TimeFormat"), CultureInfo.CurrentCulture);
    public IEnumerable<string> AppKeys => (string.IsNullOrEmpty(RelatedApps) ? App : RelatedApps).Split(';');
    public string StatusText => string.Join(Text.T("ListSeparator"), AppKeys.Select(key => AppCatalog.Definitions.FirstOrDefault(app => app.Key == key)?.Name ?? key))
        + Text.T("StatusSeparator") + Source + Text.T("StatusSeparator") + Text.T(IsNetwork ? "NetworkDocument" : Location.StartsWith("onenote:", StringComparison.Ordinal) ? "Notebook" : IsCloud ? "CloudDocument" : "LocalDocument");
}

internal sealed record RecentResult(IReadOnlyList<RecentItem> Items, IReadOnlyList<string> Warnings, bool AllSourcesFailed, bool HasSourceSnapshots = false);

internal static class Recent
{
    public static RecentResult GetItems(AppSettings settings, IReadOnlySet<string> installedApps,
        EdgeHistoryReader edge, CancellationToken token, IReadOnlyList<DetectedApp>? detectedApps = null, RecentSourceCache? snapshots = null)
    {
        using var operation = LogService.StartOperation("Recent", "Collect");
        var items = new List<RecentItem>();
        var warnings = new List<string>();
        snapshots ??= new();
        var active = new HashSet<string>(StringComparer.Ordinal);
        int enabled = 0, failed = 0;
        void Read(string key, string name, Func<List<RecentItem>, bool> read, bool preserveOnFailure = true)
        {
            active.Add(key);
            enabled++;
            var fresh = new List<RecentItem>();
            bool succeeded = false;
            bool completed = false;
            try { token.ThrowIfCancellationRequested(); succeeded = read(fresh); completed = true; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                warnings.Add(Text.F("SourceFailed", name));
                LogService.Write("Recent", $"Source failed source={name}", ex, level: LogService.LogLevel.Warn);
            }
            if (!succeeded) failed++;
            var retained = snapshots.Update(key, fresh, succeeded || (!preserveOnFailure && completed), settings.CacheMaxAgeDays);
            items.AddRange(RecentSourceCache.Filter(retained, settings, installedApps, token));
            if (!succeeded) LogService.Write("Recent", $"Source snapshot preserved source={key} count={retained.Count}");
        }
        if (settings.IncludeRegistry)
            foreach (var app in AppCatalog.Definitions.Where(app => app.HasRecentDocuments && (!settings.UseInstalledAppsOnly || installedApps.Contains(app.Key))))
            foreach (string version in AppCatalog.OfficeVersions)
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                Read($"office:{app.Key}:{version}:{view}", Text.T("SourceOffice"), output =>
                {
                    ReadRegistryItems(output, app.Key, version, view, token);
                    return true;
                });
        if (settings.IncludeCache)
            foreach (var folder in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
            {
                string directory = Path.Combine(Environment.GetFolderPath(folder), "Microsoft", "Office", "Recent");
                Read("cache:" + directory, Text.T("SourceCache"), output => AddCacheItems(output, settings, installedApps, warnings, token, directory));
            }
        if (settings.IncludeWindowsRecent) Read("windows", Text.T("SourceWindows"), output => { output.AddRange(WindowsRecentReader.Read(settings, installedApps, token)); return true; });
        if (settings.IncludeJumpLists)
            foreach (var app in (detectedApps ?? []).Where(app => app.IsInstalled || !settings.UseInstalledAppsOnly))
            foreach (string id in (app.RecentAppIds ?? []).Concat(app.AppUserModelId == null ? [] : new[] { app.AppUserModelId }).Distinct(StringComparer.Ordinal))
                Read($"jump:{app.Definition.Key}:{id}", Text.F("AppJumpList", app.Definition.Name), output =>
                {
                    output.AddRange(JumpListReader.ReadItems(app, id, settings, token));
                    return true;
                });
        if (settings.IncludeEdgeHistory) Read("edge", Text.T("SourceEdge"), output => { output.AddRange(edge.Read(settings, warnings, token)); return !edge.LastReadFailed; }, false);
        snapshots.Retain(active);
        var merged = Merge(items, settings.MaxItems).Select(DocumentPreview.Refresh).ToArray();
        LogService.Write("Recent", $"Collect complete candidates={items.Count} items={merged.Length} warnings={warnings.Count}");
        return new(merged, warnings.Distinct().ToArray(), enabled > 0 && failed == enabled, true);
    }

    internal static IReadOnlyList<RecentItem> Merge(IEnumerable<RecentItem> candidates, int maximum)
    {
        var merged = new Dictionary<string, RecentItem>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!DocumentLocation.TryParse(candidate.Location, out _, out _)) continue;
            var item = NormalizeApplication(candidate);
            if (!merged.TryGetValue(item.Identity, out var previous)) { merged[item.Identity] = item; continue; }
            bool itemVisit = item.TimeKind is DocumentTimeKind.Accessed or DocumentTimeKind.Recorded;
            bool previousVisit = previous.TimeKind is DocumentTimeKind.Accessed or DocumentTimeKind.Recorded;
            var winner = itemVisit != previousVisit ? (itemVisit ? item : previous)
                : item.Timestamp > previous.Timestamp || (item.Timestamp == previous.Timestamp && item.TimeKind < previous.TimeKind) ? item : previous;
            merged[item.Identity] = winner with
            {
                RelatedApps = string.Join(';', previous.AppKeys.Concat(item.AppKeys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                Source = string.Join(Text.T("ListSeparator"), previous.Source.Split(Text.T("ListSeparator"), StringSplitOptions.None).Concat(item.Source.Split(Text.T("ListSeparator"), StringSplitOptions.None)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            };
        }
        return merged.Values.OrderByDescending(item => item.Timestamp).ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(0, maximum)).ToArray();
    }

    internal static RecentItem NormalizeApplication(RecentItem item) => DocumentLocation.Extension(item.Location) == ".txt"
        ? item with { App = "Notepad", RelatedApps = "Notepad" } : item;

    private static void ReadRegistryItems(List<RecentItem> items, string app, string version, RegistryView view, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
        using var root = baseKey.OpenSubKey($@"SOFTWARE\Microsoft\Office\{version}\{(app == "Project" ? "MS Project" : app)}");
        using var legacy = root?.OpenSubKey("File MRU");
        AddMruKey(legacy, app, items, token);
        using var accounts = root?.OpenSubKey("User MRU");
        foreach (var account in accounts?.GetSubKeyNames() ?? [])
        {
            token.ThrowIfCancellationRequested();
            using var mru = accounts!.OpenSubKey(account + @"\File MRU");
            AddMruKey(mru, app, items, token);
        }
        AddApplicationKeys(root, app, items, token);
    }

    private static void AddMruKey(RegistryKey? key, string app, List<RecentItem> items, CancellationToken token)
    {
        if (key == null) return;
        foreach (var name in key.GetValueNames())
        {
            token.ThrowIfCancellationRequested();
            string index = name.StartsWith("Item ", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;
            if (!int.TryParse(index, out _) || key.GetValue(name) is not string raw) continue;
            string location = ExtractMruLocation(raw);
            if (location.StartsWith('Z') && DocumentLocation.TryParse(location[1..], out _, out _)) location = location[1..];
            if (!TryCreate(location, app, "Office", DocumentLocation.ParseMruTimestamp(raw), out var item)) continue;
            items.Add(item!);
        }
    }

    internal static string ExtractMruLocation(string raw)
    {
        int delimiter = raw.IndexOf('*');
        return ((raw.StartsWith('[') || DocumentLocation.ParseMruTimestamp(raw).HasValue) && delimiter >= 0 ? raw[(delimiter + 1)..] : raw).Trim();
    }

    private static void AddApplicationKeys(RegistryKey? root, string app, List<RecentItem> items, CancellationToken token)
    {
        if (root == null) return;
        string[] paths = app switch
        {
            "Access" => ["Settings"],
            "Project" => ["Recent File List", @"Settings\Recent File List"],
            "Visio" => ["Application"],
            "OneNote" => ["OpenNotebooks"],
            _ => []
        };
        foreach (string path in paths)
        {
            using var key = root.OpenSubKey(path);
            if (key == null) continue;
            foreach (string name in key.GetValueNames())
            {
                token.ThrowIfCancellationRequested();
                bool match = app == "OneNote" || System.Text.RegularExpressions.Regex.IsMatch(name, app == "Access" ? @"^MRU\d+$" : app == "Visio" ? @"^LastFile\d+$" : @"^File\s*\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (!match || key.GetValue(name) is not string raw) continue;
                AddApplicationRecord(raw, app, items);
            }
        }
    }

    internal static void AddApplicationRecord(string raw, string app, List<RecentItem> items)
    {
        string target = ExtractMruLocation(raw);
        bool notebook = app == "OneNote";
        if (notebook && !target.StartsWith("onenote:", StringComparison.OrdinalIgnoreCase)) target = "onenote:" + target;
        if (!AppCatalog.IsDocument(target, app)) return;
        if (TryCreate(target, app, notebook ? Text.T("Notebooks") : Text.F("AppRecent", app), DocumentLocation.ParseMruTimestamp(raw), out var item)) items.Add(item!);
    }

    internal static bool TryCreate(string location, string app, string source, DateTimeOffset? accessed, out RecentItem? item)
    {
        item = null;
        if (!DocumentLocation.TryParse(location, out var target, out var cloud)) return false;
        var availability = cloud ? FileAvailability.Available : DocumentLocation.Availability(target);
        if (availability == FileAvailability.Missing) return false;
        DateTimeOffset? modified = null;
        if (!accessed.HasValue && !cloud && availability == FileAvailability.Available)
        {
            try { modified = File.GetLastWriteTimeUtc(target); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { LogService.Write("Recent", "Document timestamp unavailable", ex, level: LogService.LogLevel.Debug); }
        }
        var timeKind = accessed.HasValue ? DocumentTimeKind.Accessed : modified.HasValue ? DocumentTimeKind.Modified : DocumentTimeKind.Unknown;
        item = NormalizeApplication(new(DocumentLocation.Title(target), target, accessed ?? modified ?? DateTimeOffset.MinValue, app, source, cloud, timeKind, availability == FileAvailability.Network));
        return true;
    }

    private static bool AddCacheItems(List<RecentItem> items, AppSettings settings, IReadOnlySet<string> installedApps,
        List<string> warnings, CancellationToken token, string directory)
    {
        var candidates = new List<(string Path, DateTimeOffset Timestamp)>();
        int attempted = 0, successful = 0;
        foreach (string root in new[] { directory })
        {
            if (!SourcePath.Exists(root, true)) continue;
            attempted++;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (string path in Directory.EnumerateFiles(root, "*", options))
                {
                    token.ThrowIfCancellationRequested();
                    if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) continue;
                    var time = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
                    if (time >= DateTimeOffset.UtcNow.AddDays(-settings.CacheMaxAgeDays)) candidates.Add((path, time));
                }
                successful++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                warnings.Add(Text.T("CacheFailed"));
                LogService.Write("Recent", "Cache enumeration failed", ex, level: LogService.LogLevel.Warn);
            }
        }
        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Timestamp).Take(settings.MaxCacheItems))
        {
            token.ThrowIfCancellationRequested();
            string? target = ShortcutReader.Read(candidate.Path);
            if (target == null || !DocumentLocation.TryParse(target, out _, out _)) continue;
            string? app = AppCatalog.FromExtension(DocumentLocation.Extension(target));
            if (app == null || (settings.UseInstalledAppsOnly && app != "PDF" && !installedApps.Contains(app))) continue;
            if (TryCreate(target, app, Text.T("Cache"), candidate.Timestamp, out var item)) items.Add(item!);
        }
        return attempted == 0 || successful > 0;
    }
}
