using System.Diagnostics;

namespace EricWorkHub;

internal static class LaunchService
{
    public static ProcessStartInfo ForDocument(string location)
    {
        if (!DocumentLocation.TryParse(location, out var target, out var cloud)) throw new ArgumentException(Text.T("UnsupportedDocument"));
        if (!cloud && !DocumentLocation.IsNetwork(target) && DocumentLocation.Availability(target) == FileAvailability.Missing)
            throw new FileNotFoundException(Text.T("MissingDocument"));
        return new(target) { UseShellExecute = true, ErrorDialog = false };
    }

    public static ProcessStartInfo ForApp(DetectedApp app, Func<string, bool>? packageEntryExists = null)
    {
        if (!app.Definition.PreferPackagedVersion && AppAvailability.ExecutableExists(app.Executable))
            return new(app.Executable!) { UseShellExecute = true };
        if (app.AppUserModelId != null && (packageEntryExists ?? AppAvailability.PackageEntryExists)(app.AppUserModelId))
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
            info.ArgumentList.Add("shell:AppsFolder\\" + app.AppUserModelId);
            return info;
        }
        if (AppAvailability.ExecutableExists(app.Executable)) return new(app.Executable!) { UseShellExecute = true };
        throw new FileNotFoundException(Text.T("MissingApp"));
    }

    public static void Start(ProcessStartInfo info) { using var process = Process.Start(info); }
}
