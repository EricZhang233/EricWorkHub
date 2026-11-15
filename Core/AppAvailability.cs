using Windows.Management.Deployment;

namespace EricWorkHub;

internal static class AppAvailability
{
    public static bool ExecutableExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try { return new FileInfo(path).Length > 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LogService.Write("Apps", "Executable metadata unavailable", ex, level: LogService.LogLevel.Debug);
            return false;
        }
    }

    public static bool PackageEntryExists(string appUserModelId)
    {
        int separator = appUserModelId.IndexOf('!');
        if (separator <= 0 || separator == appUserModelId.Length - 1) return false;
        string family = appUserModelId[..separator];
        try
        {
            foreach (var package in new PackageManager().FindPackagesForUser(string.Empty))
            {
                if (!package.Id.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase) || package.IsFramework
                    || package.IsResourcePackage || !package.Status.VerifyIsOK()) continue;
                var entries = package.GetAppListEntriesAsync().AsTask().GetAwaiter().GetResult();
                for (int index = 0; index < entries.Count; index++)
                    if (entries[index].AppUserModelId.Equals(appUserModelId, StringComparison.Ordinal)) return true;
            }
        }
        catch (Exception ex) { LogService.Write("Apps", "Package launch entry validation failed", ex, level: LogService.LogLevel.Warn); }
        return false;
    }
}
