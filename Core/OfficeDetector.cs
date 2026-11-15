using Microsoft.Win32;

namespace EricWorkHub;

internal sealed class OfficeDetector
{
    private readonly List<string> _roots = [];
    private bool _rootsLoaded;

    public string? Detect(OfficeAppDefinition app, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        foreach (string extra in app.ExtraPaths) if (AppAvailability.ExecutableExists(extra)) return extra;
        foreach (string executable in app.ExeNames)
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            token.ThrowIfCancellationRequested();
            string? candidate = ReadRegistry(hive, view, $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executable}", "");
            if (AppAvailability.ExecutableExists(candidate)) return candidate;
        }
        LoadRoots();
        foreach (string root in _roots)
        foreach (string executable in app.ExeNames)
        {
            token.ThrowIfCancellationRequested();
            string candidate = Path.Combine(root, executable);
            if (AppAvailability.ExecutableExists(candidate)) return candidate;
        }
        return null;
    }

    private void LoadRoots()
    {
        if (_rootsLoaded) return;
        _rootsLoaded = true;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            string? installation = ReadRegistry(RegistryHive.LocalMachine, view, @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration", "InstallationPath");
            if (installation != null)
            {
                _roots.Add(Path.Combine(installation, "root", "Office16"));
                _roots.Add(Path.Combine(installation, "Office16"));
            }
            foreach (string version in AppCatalog.OfficeVersions)
            {
                string? root = ReadRegistry(RegistryHive.LocalMachine, view, $@"SOFTWARE\Microsoft\Office\{version}\Common\InstallRoot", "Path");
                if (root != null) _roots.Add(root);
            }
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        foreach (string version in new[] { "Office16", "Office15", "Office14" })
        {
            string programs = Environment.GetFolderPath(folder);
            _roots.Add(Path.Combine(programs, "Microsoft Office", "root", version));
            _roots.Add(Path.Combine(programs, "Microsoft Office", version));
        }
    }

    private static string? ReadRegistry(RegistryHive hive, RegistryView view, string path, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value) ? value.Trim(' ', '"') : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            LogService.Write("Apps", $"App registration unavailable hive={hive} view={view}", ex, level: LogService.LogLevel.Warn);
            return null;
        }
    }
}
