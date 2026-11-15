using EricWorkHub;
using System.Diagnostics;
using System.Text.Json;

internal static class OfficeManagerProbe
{
    public static async Task RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        ConfigService.CachePathOverride = Path.Combine(directory, "cache");
        try
        {
            string? downloaded = null;
            var service = new OfficeManagerService(findProcess: () => null,
                start: info => downloaded = info.FileName, directory: Path.Combine(directory, "downloads"));
            var result = await service.RunAsync(OfficeManagerAction.Install, default);
            if (result.IsError || downloaded == null) throw new InvalidOperationException("Office installer probe failed: " + result.Message);
            var version = FileVersionInfo.GetVersionInfo(downloaded);
            var maintenance = OfficeMaintenance.FindCommand();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Downloaded = downloaded, FileBytes = new FileInfo(downloaded).Length, version.FileVersion,
                version.OriginalFilename, MicrosoftSigned = MicrosoftExecutableTrust.IsMicrosoftSigned(downloaded),
                OfficeExecutable = MicrosoftExecutableTrust.IsOfficeExecutable(downloaded),
                MaintenanceExecutable = maintenance.FileName, MaintenanceArguments = maintenance.ArgumentList.ToArray(),
                MaintenanceVerb = maintenance.Verb, InstallerEndpoint = OfficeManagerService.InstallerUri.AbsoluteUri,
                ExecutedInstaller = false, ExecutedMaintenance = false
            }));
        }
        finally { LogService.FlushAndStop(); }
    }
}
