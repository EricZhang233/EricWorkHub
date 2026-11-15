using EricWorkHub;
using System.Diagnostics;
using System.Globalization;
using System.Net;

internal static class OfficeManagerTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var englishInstaller = OfficeManagerService.CreateInstallerUri(CultureInfo.GetCultureInfo("en-US"));
        var chineseInstaller = OfficeManagerService.CreateInstallerUri(CultureInfo.GetCultureInfo("zh-CN"));
        check(englishInstaller.Host == "c2rsetup.officeapps.live.com" && englishInstaller.AbsolutePath == "/c2r/download.aspx"
            && englishInstaller.Query.Contains("productReleaseID=O365ProPlusRetail&platform=x64")
            && englishInstaller.Query.Contains("language=en-US") && chineseInstaller.Query.Contains("language=zh-CN"),
            "latest download is the product-stamped enterprise x64 GUI installer and language follows the UI culture");
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            check(OfficeManagerService.InstallerUri.Query.Contains("language=fr-FR"),
                "installer language is evaluated from the current Windows UI culture rather than fixed to Chinese");
        }
        finally { CultureInfo.CurrentUICulture = previousCulture; }
        var emptyHub = new HubService((_, _) => Task.FromResult<IReadOnlyList<DetectedApp>>([]),
            (_, _) => Task.FromResult(new RecentResult([], [], false)));
        check(emptyHub.Snapshot.Apps.Count == 0, "application snapshots start without a synthetic manager row");
        check((await emptyHub.RefreshAsync(true, default))!.Apps.Count == 0,
            "empty application detection does not insert the manager into the installed application list");

        var normal = new OfficeSetupProcess(123, new IntPtr(456));
        using var unusedClient = new HttpClient(new Handler(_ => throw new Exception("Unexpected download")));
        string runningCache = Path.Combine(root, "running-manager");
        var runningService = new OfficeManagerService(unusedClient, () => normal, _ => throw new Exception("Unexpected verification"),
            _ => throw new Exception("Unexpected launch"), directory: runningCache);
        check((await runningService.RunAsync(OfficeManagerAction.Install, default)).Process == normal && !Directory.Exists(runningCache),
            "a running Office installer is returned before any download or cache access");
        check((await runningService.RunAsync(OfficeManagerAction.Repair, default)).Process == normal,
            "repair requests also reuse an existing Office setup window");
        check(!MicrosoftExecutableTrust.IsOfficeExecutable(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")),
            "a Microsoft Windows executable does not satisfy the Office product identity check");

        int requests = 0, verified = 0;
        var launched = new List<ProcessStartInfo>();
        using var client = new HttpClient(new Handler(request =>
        {
            requests++;
            check(request.RequestUri == OfficeManagerService.InstallerUri && request.Headers.CacheControl is { NoCache: true, NoStore: true },
                "installer requests use the current Microsoft endpoint without cache reuse");
            return Task.FromResult(Response((byte)requests));
        }));
        string cache = Path.Combine(root, "download-manager");
        var service = new OfficeManagerService(client, () => null, path => { verified++; return File.ReadAllBytes(path).Length == 128; },
            launched.Add, directory: cache);
        check(!(await service.RunAsync(OfficeManagerAction.Install, default)).IsError, "verified download is handed to the injected launcher");
        check(!(await service.RunAsync(OfficeManagerAction.Install, default)).IsError && requests == 2 && verified == 2 && launched.Count == 2
            && launched[0].FileName == Path.Combine(cache, "OfficeSetup.exe") && launched[0].FileName == launched[1].FileName
            && File.ReadAllBytes(launched[0].FileName)[0] == 2 && !Directory.EnumerateDirectories(cache).Any(),
            "separate installer uses fetch fresh content and overwrite the same private cache file without version directories");
        check(!Directory.EnumerateFiles(cache, "*.part", SearchOption.AllDirectories).Any(), "successful downloads leave no partial installer files");
        check(launched.All(info => info.UseShellExecute && info.ArgumentList.Count == 0 && string.IsNullOrEmpty(info.Arguments))
            && Directory.EnumerateFiles(cache).All(path => Path.GetExtension(path) != ".xml"),
            "product-stamped graphical installer launches like a double click without CLI arguments or deployment XML");

        using var rejectedClient = new HttpClient(new Handler(_ => Task.FromResult(Response())));
        string rejectedCache = Path.Combine(root, "rejected-manager");
        Directory.CreateDirectory(rejectedCache);
        string previousInstaller = Path.Combine(rejectedCache, "OfficeSetup.exe");
        File.WriteAllBytes(previousInstaller, [7, 8, 9]);
        var rejected = new OfficeManagerService(rejectedClient, () => null, _ => false,
            _ => throw new Exception("Untrusted installer launched"), directory: rejectedCache);
        check((await rejected.RunAsync(OfficeManagerAction.Install, default)).IsError
            && File.ReadAllBytes(previousInstaller).SequenceEqual(new byte[] { 7, 8, 9 })
            && !File.Exists(Path.Combine(rejectedCache, "OfficeSetup.exe.part")),
            "failed verification preserves the prior private cache without launching it and removes the partial download");

        int maintenanceCalls = 0;
        var repair = new OfficeManagerService(unusedClient, () => null, start: info =>
        {
            maintenanceCalls++;
            check(info.FileName == "official-maintenance.exe", "repair uses the registered maintenance command");
        }, maintenance: () => new("official-maintenance.exe"), directory: Path.Combine(root, "repair-manager"));
        check(!(await repair.RunAsync(OfficeManagerAction.Repair, default)).IsError && maintenanceCalls == 1,
            "repair opens the official interface without downloading an installer");
        var cancelledElevation = new OfficeManagerService(unusedClient, () => null,
            start: _ => throw new System.ComponentModel.Win32Exception(1223), maintenance: () => new("official-maintenance.exe"),
            directory: Path.Combine(root, "cancelled-elevation-manager"));
        check(!(await cancelledElevation.RunAsync(OfficeManagerAction.Repair, default)).IsError,
            "cancelling the Microsoft maintenance elevation prompt is a normal cancellation");

        int lookups = 0;
        var race = new OfficeManagerService(rejectedClient, () => ++lookups >= 3 ? normal : null, _ => true,
            _ => throw new Exception("Duplicate installer launched"), directory: Path.Combine(root, "raced-manager"));
        check((await race.RunAsync(OfficeManagerAction.Install, default)).Process == normal,
            "an installer appearing during download is reused before a duplicate can launch");

        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int concurrentDownloads = 0;
        using var blockedClient = new HttpClient(new Handler(async _ =>
        {
            concurrentDownloads++;
            began.TrySetResult();
            await finish.Task;
            return Response();
        }));
        string sharedCache = Path.Combine(root, "shared-manager");
        var first = new OfficeManagerService(blockedClient, () => null, _ => true, _ => { }, directory: sharedCache);
        var second = new OfficeManagerService(unusedClient, () => null, _ => true, _ => { }, directory: sharedCache);
        var pending = first.RunAsync(OfficeManagerAction.Install, default);
        try
        {
            await began.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check((await first.RunAsync(OfficeManagerAction.Install, default)).Message == Text.T("OfficeManagerBusy")
                && (await second.RunAsync(OfficeManagerAction.Install, default)).Message == Text.T("OfficeManagerBusy") && concurrentDownloads == 1,
                "concurrent clicks and separate manager instances share a single download lease");
        }
        finally { finish.TrySetResult(); }
        check(!(await pending).IsError, "download lease is released after the active request completes");

        var cancelBegan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelClient = new HttpClient(new Handler(async request =>
        {
            cancelBegan.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, request.Options.TryGetValue(Handler.TokenKey, out var token) ? token : default);
            return Response();
        }));
        string cancelCache = Path.Combine(root, "cancel-manager");
        var cancellable = new OfficeManagerService(cancelClient, () => null, _ => true,
            _ => throw new Exception("Cancelled installer launched"), directory: cancelCache);
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = cancellable.RunAsync(OfficeManagerAction.Install, cancellation.Token);
            await cancelBegan.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try { await cancelled; throw new Exception("Cancellation was ignored"); }
            catch (OperationCanceledException) { check(!Directory.EnumerateFiles(cancelCache, "*.exe*", SearchOption.AllDirectories).Any(), "cancelled download removes its staging files"); }
        }

        using var redirectClient = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new("https://unrelated.example/OfficeSetup.exe");
            return Task.FromResult(response);
        }));
        var redirected = new OfficeManagerService(redirectClient, () => null, _ => true,
            _ => throw new Exception("Redirected installer launched"), directory: Path.Combine(root, "redirect-manager"));
        check((await redirected.RunAsync(OfficeManagerAction.Install, default)).IsError, "download rejects redirects outside Microsoft download hosts");
        using var oversizedClient = new HttpClient(new Handler(_ =>
        {
            var response = Response();
            response.Content.Headers.ContentLength = 65L * 1024 * 1024;
            return Task.FromResult(response);
        }));
        var oversized = new OfficeManagerService(oversizedClient, () => null, _ => true,
            _ => throw new Exception("Oversized installer launched"), directory: Path.Combine(root, "oversized-manager"));
        check((await oversized.RunAsync(OfficeManagerAction.Install, default)).IsError, "oversized download responses are rejected before saving a payload");
        check(!OfficeManagerService.IsDownloadUri(new("http://officecdn.microsoft.com/setup.exe"))
            && !OfficeManagerService.IsDownloadUri(new("https://officecdn.microsoft.com.attacker.example/setup.exe"))
            && !OfficeManagerService.IsDownloadUri(new("https://user@officecdn.microsoft.com/setup.exe")),
            "download host validation requires HTTPS and exact Microsoft identities");

        const string maintenance = "\"C:\\Program Files\\Common Files\\Microsoft Shared\\ClickToRun\\OfficeClickToRun.exe\" scenario=repair platform=x64 culture=zh-cn";
        check(OfficeMaintenance.TryCreateCommand(maintenance, out var command, _ => true)
            && command!.ArgumentList.SequenceEqual(new[] { "scenario=repair", "platform=x64", "culture=zh-cn" })
            && command.UseShellExecute && command.Verb == "runas" && command.WorkingDirectory == Path.GetDirectoryName(command.FileName),
            "registered Office maintenance requests elevation before opening its interactive selection UI without forced repair flags");
        check(!OfficeMaintenance.TryCreateCommand(maintenance + " forceappshutdown=true", out _, _ => true)
            && !OfficeMaintenance.TryCreateCommand(maintenance + " RepairType=FullRepair", out _, _ => true)
            && !OfficeMaintenance.TryCreateCommand(maintenance.Replace("OfficeClickToRun.exe", "other.exe"), out _, _ => true)
            && !OfficeMaintenance.TryCreateCommand(maintenance, out _, _ => false),
            "maintenance rejects forced shutdown, unattended repair and untrusted commands");
        check(OfficeMaintenance.TryCreateCommand("\"C:\\Windows\\System32\\msiexec.exe\" /I{12345678-1234-1234-1234-123456789ABC}", out _, _ => true)
            && !OfficeMaintenance.TryCreateCommand("\"C:\\Windows\\System32\\msiexec.exe\" /X{12345678-1234-1234-1234-123456789ABC}", out _, _ => true),
            "legacy MSI maintenance allows the interactive modify action and rejects uninstall");
        string unsigned = Path.Combine(root, "unsigned-installer.exe");
        File.WriteAllBytes(unsigned, new byte[128]);
        check(!MicrosoftExecutableTrust.IsMicrosoftSigned(unsigned) && !MicrosoftExecutableTrust.IsOfficeExecutable(unsigned),
            "native signature validation rejects an unsigned executable without displaying UI");
    }

    private static HttpResponseMessage Response(byte marker = 0)
    {
        byte[] bytes = new byte[128];
        bytes[0] = marker;
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public static readonly HttpRequestOptionsKey<CancellationToken> TokenKey = new("TestCancellationToken");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            request.Options.Set(TokenKey, token);
            return respond(request);
        }
    }
}
