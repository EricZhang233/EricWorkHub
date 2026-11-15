namespace EricWorkHub;

internal sealed record AppSettings
{
    public bool IncludeRegistry { get; init; } = true;
    public bool IncludeCache { get; init; } = true;
    public bool IncludeWindowsRecent { get; init; } = true;
    public bool IncludeJumpLists { get; init; } = true;
    public bool IncludeEdgeHistory { get; init; } = true;
    public string[] EdgeProfiles { get; init; } = [];
    public bool UseInstalledAppsOnly { get; init; } = true;
    public int MaxItems { get; init; } = 50;
    public int MaxCacheItems { get; init; } = 200;
    public int CacheMaxAgeDays { get; init; } = 90;
}

internal static class ConfigService
{
    internal static string? CachePathOverride { get; set; }
    public static string SystemCachePath => CachePathOverride ?? Path.Combine(Path.GetTempPath(), "eric", "ericworkhub");
    public static AppSettings Settings { get; } = new();
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        LogService.Write("Config", "Automatic document sources initialized; installed applications only");
    }
}
