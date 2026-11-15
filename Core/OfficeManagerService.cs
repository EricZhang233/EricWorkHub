using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;

namespace EricWorkHub;

internal enum OfficeManagerAction { Install, Repair }
internal sealed record OfficeManagerResult(OfficeSetupProcess? Process = null, string? Message = null, bool IsError = false);

internal sealed class OfficeManagerService
{
    internal static Uri InstallerUri => CreateInstallerUri(CultureInfo.CurrentUICulture);

    internal static Uri CreateInstallerUri(CultureInfo culture)
    {
        string language = (culture.IsNeutralCulture ? CultureInfo.CreateSpecificCulture(culture.Name) : culture).Name;
        string query = "productReleaseID=O365ProPlusRetail&platform=x64";
        if (language.Length != 0) query += "&language=" + Uri.EscapeDataString(language);
        return new("https://c2rsetup.officeapps.live.com/c2r/download.aspx?" + query);
    }
    private const long MaximumBytes = 64L * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http;
    private readonly Func<OfficeSetupProcess?> _findProcess;
    private readonly Func<string, bool> _verify;
    private readonly Action<ProcessStartInfo> _start;
    private readonly Func<ProcessStartInfo> _maintenance;
    private readonly string _directory;
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };

    public OfficeManagerService(HttpClient? http = null, Func<OfficeSetupProcess?>? findProcess = null,
        Func<string, bool>? verify = null, Action<ProcessStartInfo>? start = null,
        Func<ProcessStartInfo>? maintenance = null, string? directory = null)
    {
        _http = http ?? Client;
        _findProcess = findProcess ?? OfficeSetupProcesses.Find;
        _verify = verify ?? MicrosoftExecutableTrust.IsOfficeExecutable;
        _start = start ?? LaunchService.Start;
        _maintenance = maintenance ?? OfficeMaintenance.FindCommand;
        _directory = directory ?? Path.Combine(ConfigService.SystemCachePath, "office-setup");
    }

    public Task<OfficeSetupProcess?> FindRunningAsync(CancellationToken token) => Task.Run(_findProcess, token);

    public async Task<OfficeManagerResult> RunAsync(OfficeManagerAction action, CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) return new(Message: Text.T("OfficeManagerBusy"));
        try
        {
            using var operation = LogService.StartOperation("OfficeManager", action.ToString());
            if (await FindRunningAsync(token) is { } existing) return new(existing);
            Directory.CreateDirectory(_directory);
            FileStream lease;
            try { lease = new(Path.Combine(_directory, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return new(Message: Text.T("OfficeManagerBusy")); }
            using (lease)
            {
                return await RunLockedAsync(action, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            LogService.Write("OfficeManager", "User cancelled the Office setup or maintenance elevation prompt");
            return new();
        }
        catch (Exception ex)
        {
            LogService.Write("OfficeManager", "Office management operation failed", ex, level: LogService.LogLevel.Error);
            return new(Message: Text.T("OfficeManagerFailed"), IsError: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<OfficeManagerResult> RunLockedAsync(OfficeManagerAction action, CancellationToken token)
    {
        if (await FindRunningAsync(token) is { } raced) return new(raced);
        if (action == OfficeManagerAction.Repair)
        {
            await Task.Run(() => _start(_maintenance()), token);
            LogService.Write("OfficeManager", "Official maintenance interface requested");
            return new();
        }
        string partial = Path.Combine(_directory, "OfficeSetup.exe.part");
        string executable = Path.Combine(_directory, "OfficeSetup.exe");
        try
        {
            File.Delete(partial);
            await DownloadAsync(partial, token);
            if (!await Task.Run(() => _verify(partial), token)) throw new InvalidDataException("Installer is not a trusted Microsoft Office executable");
            token.ThrowIfCancellationRequested();
            if (await FindRunningAsync(token) is { } running) return new(running);
            try { File.Move(partial, executable, true); }
            catch (IOException)
            {
                if (await FindRunningAsync(token) is { } locked) return new(locked);
                throw;
            }
            if (await FindRunningAsync(token) is { } appeared) return new(appeared);
            await Task.Run(() => _start(new(executable) { UseShellExecute = true, WorkingDirectory = _directory }), token);
            LogService.Write("OfficeManager", "Private installer cache replaced with latest Microsoft download; handed to Windows");
            return new();
        }
        finally
        {
            try { File.Delete(partial); }
            catch (Exception ex) { LogService.Write("OfficeManager", "Partial installer cleanup deferred", ex, level: LogService.LogLevel.Debug); }
        }
    }

    private async Task DownloadAsync(string path, CancellationToken token)
    {
        Uri uri = InstallerUri;
        LogService.Write("OfficeManager", $"Requesting latest enterprise x64 graphical installer language={CultureInfo.CurrentUICulture.Name}");
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            if (!IsDownloadUri(uri)) throw new InvalidDataException("Installer redirect is outside Microsoft download hosts");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            request.Headers.Pragma.ParseAdd("no-cache");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location == null) throw new InvalidDataException("Installer redirect has no target");
                uri = new Uri(uri, response.Headers.Location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumBytes) throw new InvalidDataException("Installer is too large");
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            byte[] buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token)) != 0)
            {
                total += count;
                if (total > MaximumBytes) throw new InvalidDataException("Installer is too large");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
            }
            if (total < 64) throw new InvalidDataException("Installer is empty or truncated");
            LogService.Write("OfficeManager", $"Latest installer downloaded bytes={total}");
            return;
        }
        throw new HttpRequestException("Too many installer redirects");
    }

    internal static bool IsDownloadUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && (uri.Host.Equals("c2rsetup.officeapps.live.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("officecdn.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("download.microsoft.com", StringComparison.OrdinalIgnoreCase));

}
