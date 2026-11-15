using EricWorkHub;
using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.Management.Deployment;

internal static class IconDiagnostics
{
    public static async Task RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        ConfigService.CachePathOverride = Path.Combine(directory, "cache");
        try
        {
            var definition = AppCatalog.Definitions.Single(app => app.Key == "Outlook");
            string? detected = new OfficeDetector().Detect(definition, default);
            string? classic = new OfficeDetector().Detect(definition with { ExtraPaths = [] }, default);
            string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "olk.exe");
            string? packageId = null;
            foreach (var package in new PackageManager().FindPackagesForUser(string.Empty))
            {
                if (!definition.PackageNames.Contains(package.Id.Name) || !package.Status.VerifyIsOK()) continue;
                var entries = await package.GetAppListEntriesAsync();
                if (entries.Count > 0) { packageId = entries[0].AppUserModelId; break; }
            }
            foreach (var sample in new[]
            {
                (Name: "detected", App: new DetectedApp(definition, detected, packageId)),
                (Name: "alias", App: new DetectedApp(definition, alias, null)),
                (Name: "alias-with-package", App: new DetectedApp(definition, alias, packageId)),
                (Name: "classic", App: new DetectedApp(definition, classic, null)),
                (Name: "package", App: new DetectedApp(definition, null, packageId))
            })
            {
                byte[]? png = await Task.Run(() => SystemIconReader.ForApplication(sample.App));
                if (png == null)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { sample.Name, sample.App.Executable, sample.App.AppUserModelId, Found = false }));
                    continue;
                }
                string path = Path.Combine(directory, sample.Name + ".png");
                File.WriteAllBytes(path, png);
                using var stream = new MemoryStream(png);
                using var bitmap = new Bitmap(stream);
                int visiblePixels = 0;
                for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                    if (bitmap.GetPixel(x, y).A > 0) visiblePixels++;
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    sample.Name, sample.App.Executable, sample.App.AppUserModelId,
                    FileBytes = AppAvailability.ExecutableExists(sample.App.Executable) ? new FileInfo(sample.App.Executable!).Length : (long?)null,
                    bitmap.Width, bitmap.Height, VisiblePixels = visiblePixels, PngBytes = png.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(png)), Image = path
                }));
            }
        }
        finally { LogService.FlushAndStop(); }
    }
}
