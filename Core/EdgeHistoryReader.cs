using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace EricWorkHub;

internal sealed class EdgeHistoryReader
{
    public static string DefaultUserDataPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data");
    private readonly string _userDataPath;
    private readonly Dictionary<string, CachedHistory> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BusyRetry> _busy = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<long> _milliseconds;
    private sealed record CachedHistory(string Fingerprint, int Days, int Limit, DateTime Date, IReadOnlyList<RecentItem> Items);
    private sealed record BusyRetry(int Attempts, long RetryAt);
    public EdgeHistoryReader(string? userDataPath = null, Func<long>? milliseconds = null)
    {
        _userDataPath = userDataPath ?? DefaultUserDataPath;
        _milliseconds = milliseconds ?? (() => Environment.TickCount64);
    }
    public void Clear() { _cache.Clear(); _busy.Clear(); }
    public bool LastReadFailed { get; private set; }
    public static bool IsProfileName(string? name) => name == "Default" || (name != null && Regex.IsMatch(name, @"^Profile \d+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));

    public string[] GetProfiles() => !SourcePath.Exists(_userDataPath, true) ? [] : Directory.EnumerateDirectories(_userDataPath)
        .Select(Path.GetFileName).Where(IsProfileName).Cast<string>().OrderBy(name => name, StringComparer.Ordinal).ToArray();

    public IReadOnlyList<RecentItem> Read(AppSettings settings, List<string> warnings, CancellationToken token)
    {
        using var operation = LogService.StartOperation("Edge", "ReadPdfHistory");
        var profiles = settings.EdgeProfiles.Length == 0 ? GetProfiles() : settings.EdgeProfiles.Where(IsProfileName).ToArray();
        var output = new List<RecentItem>();
        LastReadFailed = false;
        int attempted = 0, failed = 0;
        var active = profiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string old in _cache.Keys.Where(key => !active.Contains(key)).ToArray()) _cache.Remove(old);
        foreach (string old in _busy.Keys.Where(key => !active.Contains(key)).ToArray()) _busy.Remove(old);
        foreach (string profile in profiles)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.Combine(_userDataPath, profile, "History");
            attempted++;
            void KeepPrevious()
            {
                if (_cache.TryGetValue(profile, out var previous))
                    output.AddRange(previous.Items.Where(item => item.Timestamp >= DateTimeOffset.UtcNow.AddDays(-settings.CacheMaxAgeDays)));
            }
            try
            {
                if (!SourcePath.Exists(path, false)) { _cache.Remove(profile); _busy.Remove(profile); continue; }
                if (_busy.TryGetValue(profile, out var retry) && _milliseconds() < retry.RetryAt)
                { failed++; KeepPrevious(); continue; }
                string fingerprint = Fingerprint(path) + ":" + Fingerprint(path + "-wal");
                if (_cache.TryGetValue(profile, out var cached) && cached.Fingerprint == fingerprint && cached.Days == settings.CacheMaxAgeDays
                    && cached.Limit == settings.MaxItems && cached.Date == DateTime.UtcNow.Date)
                { _busy.Remove(profile); output.AddRange(cached.Items.Where(item => item.Timestamp >= DateTimeOffset.UtcNow.AddDays(-settings.CacheMaxAgeDays))); continue; }
                var read = ReadDatabase(path, settings.CacheMaxAgeDays, settings.MaxItems, token);
                if (_cache.Count >= 32 && !_cache.ContainsKey(profile)) _cache.Remove(_cache.Keys.First());
                _cache[profile] = new(fingerprint, settings.CacheMaxAgeDays, settings.MaxItems, DateTime.UtcNow.Date, read);
                _busy.Remove(profile);
                output.AddRange(read);
            }
            catch (OperationCanceledException) { throw; }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
            {
                token.ThrowIfCancellationRequested();
                failed++;
                int attempts = Math.Min((_busy.GetValueOrDefault(profile)?.Attempts ?? 0) + 1, 6);
                int delaySeconds = Math.Min(15 * (1 << (attempts - 1)), 300);
                _busy[profile] = new(attempts, _milliseconds() + delaySeconds * 1000L);
                LogService.Write("Edge", $"History busy; background retry scheduled profile={profile} code={ex.SqliteErrorCode} retrySeconds={delaySeconds}");
                KeepPrevious();
            }
            catch (Exception ex)
            {
                failed++;
                warnings.Add(Text.F("EdgeFailed", profile));
                LogService.Write("Edge", $"History read failed profile={profile}", ex, level: LogService.LogLevel.Warn);
                KeepPrevious();
            }
        }
        LastReadFailed = attempted > 0 && failed == attempted;
        return output.Where(item => item.IsCloud || DocumentLocation.Availability(item.Location) != FileAvailability.Missing).ToArray();
    }

    internal static IReadOnlyList<RecentItem> ReadDatabase(string path, int ageDays, int maximum, CancellationToken token)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'urls' LIMIT 1";
        using var cancellation = token.Register(command.Cancel);
        token.ThrowIfCancellationRequested();
        if (command.ExecuteScalar() == null) return [];
        command.CommandText = """
            SELECT url, title, last_visit_time FROM urls
            WHERE last_visit_time >= $cutoff
              AND (url LIKE '%.pdf' OR url LIKE '%.pdf?%' OR url LIKE '%.pdf#%')
            ORDER BY last_visit_time DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-Math.Clamp(ageDays, 1, 365)).ToFileTime() / 10);
        command.Parameters.AddWithValue("$limit", Math.Clamp(maximum * 4, 100, 800));
        using var reader = command.ExecuteReader();
        var result = new List<RecentItem>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.IsDBNull(0) || reader.IsDBNull(2)) continue;
            string url = reader.GetString(0);
            if (!DocumentLocation.TryParse(url, out _, out _) || DocumentLocation.Extension(url) != ".pdf") continue;
            long micros = reader.GetInt64(2);
            if (micros <= 0 || micros > DateTimeOffset.MaxValue.ToFileTime() / 10) continue;
            if (!Recent.TryCreate(url, "Edge", "Edge", DateTimeOffset.FromFileTime(micros * 10), out var item)) continue;
            string title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            result.Add(string.IsNullOrWhiteSpace(title) ? item! : item! with { Title = title });
        }
        return result;
    }

    private static string Fingerprint(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "missing";
    }
}
