using Microsoft.Win32;

namespace EricWorkHub;

internal static class WindowsRecentReader
{
    public static IReadOnlyList<RecentItem> Read(AppSettings settings, IReadOnlySet<string> installed, CancellationToken token,
        string? recentDirectory = null, IReadOnlyList<string>? oneDriveRoots = null)
    {
        string directory = recentDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!SourcePath.Exists(directory, true)) return [];
        var roots = oneDriveRoots ?? (installed.Contains("OneDrive") ? OneDriveRoots() : []);
        var result = new List<RecentItem>();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-settings.CacheMaxAgeDays);
        var options = new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
        var links = Directory.EnumerateFiles(directory, "*", options).Where(path => path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Path: path, Time: new DateTimeOffset(File.GetLastWriteTimeUtc(path))))
            .Where(link => link.Time >= cutoff).OrderByDescending(link => link.Time).Take(settings.MaxCacheItems);
        foreach (var link in links)
        {
            token.ThrowIfCancellationRequested();
            string? target = ShortcutReader.Read(link.Path);
            if (target == null || !AppCatalog.IsDocument(target)) continue;
            string app = target.StartsWith("onenote:", StringComparison.OrdinalIgnoreCase) ? "OneNote" : AppCatalog.FromExtension(DocumentLocation.Extension(target))!;
            bool inOneDrive = roots.Any(root => IsWithin(target, root));
            bool appInstalled = app == "PDF" || installed.Contains(app);
            if (settings.UseInstalledAppsOnly && app == "Notepad" && !appInstalled) continue;
            if (settings.UseInstalledAppsOnly && !appInstalled && !inOneDrive) continue;
            if (Recent.TryCreate(target, app, Text.T("SourceWindows"), link.Time, out var item))
                result.Add(Recent.NormalizeApplication(item! with
                {
                    App = settings.UseInstalledAppsOnly && !appInstalled && inOneDrive ? "OneDrive" : app,
                    TimeKind = DocumentTimeKind.Recorded,
                    RelatedApps = inOneDrive ? settings.UseInstalledAppsOnly && !appInstalled ? "OneDrive"
                        : string.Join(';', new[] { app, "OneDrive" }.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) : app
                }));
        }
        return result;
    }

    internal static bool IsWithin(string location, string root)
    {
        if (!DocumentLocation.TryParse(location, out var path, out var cloud) || cloud) return false;
        return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> OneDriveRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(variable) is string path && Path.IsPathFullyQualified(path)) roots.Add(path);
        try
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            foreach (string name in accounts?.GetSubKeyNames() ?? [])
            {
                using var account = accounts!.OpenSubKey(name);
                if (account?.GetValue("UserFolder") is string folder && Path.IsPathFullyQualified(folder)) roots.Add(folder);
            }
        }
        catch (Exception ex) { LogService.Write("Recent", "OneDrive account locations unavailable", ex, level: LogService.LogLevel.Warn); }
        return roots.ToArray();
    }
}
