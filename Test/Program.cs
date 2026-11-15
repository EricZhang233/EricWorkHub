using Microsoft.Data.Sqlite;
using EricWorkHub;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

if (args is ["--instance-client", var instanceIdentity, var instanceCache])
{
    ConfigService.CachePathOverride = instanceCache;
    using var instance = new SingleInstance(instanceIdentity);
    bool notified = !instance.IsPrimary && await instance.NotifyAsync();
    Console.WriteLine((notified ? "secondary-notified" : "instance-fixture-failed") + " primary=" + instance.IsPrimary);
    Environment.ExitCode = notified ? 0 : 1;
    LogService.FlushAndStop();
    if (!notified)
        foreach (string log in Directory.EnumerateFiles(Path.Combine(instanceCache, "log"), "*" + Environment.ProcessId + "*.log"))
            foreach (string line in File.ReadLines(log).Where(line => line.Contains("Single instance notification"))) Console.Error.WriteLine(line);
    return;
}
if (args is ["--diagnose-icons", var iconOutputDirectory])
{
    await IconDiagnostics.RunAsync(iconOutputDirectory);
    return;
}
if (args is ["--probe-office-manager", var managerOutputDirectory])
{
    await OfficeManagerProbe.RunAsync(managerOutputDirectory);
    return;
}
if (args is ["--validate-release", var releaseArchive, var releaseRepository])
{
    ReleasePackageValidation.Validate(releaseArchive, releaseRepository);
    return;
}

string testCache = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "eric", "ericworkhub", "tests"));
string root = Path.Combine(testCache, "EricWorkHub.Tests." + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
ConfigService.CachePathOverride = root;
string logDirectory = Path.Combine(root, "log");
Directory.CreateDirectory(logDirectory);
string expired = Path.Combine(logDirectory, "app-expired.log");
string recent = Path.Combine(logDirectory, "app-recent.log");
string oversized = Path.Combine(logDirectory, "app-oversized.log");
File.WriteAllText(expired, "expired"); File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-31));
File.WriteAllText(recent, "recent");
using (var file = File.Create(oversized)) file.SetLength(101L * 1024 * 1024);
File.SetLastWriteTimeUtc(oversized, DateTime.UtcNow.AddDays(-1));
int checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL " + description);
    checks++;
    Console.WriteLine("PASS " + description);
}

try
{
    await OfficeManagerTests.RunAsync(root, Check);
    await TodoTests.RunAsync(root, Check);
    Check(typeof(RecentItem).Assembly == typeof(Text).Assembly, "Core logic and text resources compile into the same assembly");
    Check(Text.T("Recent") == "最近文档" && Text.F("VersionDisplay", "2.3.4") == "Ver.2.3.4", "embedded YAML supplies static and formatted user text");
    var systemImage = await Task.Run(() => SystemIconReader.ForApplication(new(AppCatalog.Definitions[0],
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), null)));
    Check(systemImage is { Length: > 8 } && systemImage.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "background Shell image extraction returns a real PNG without launching the executable");
    using (var image = System.Drawing.Image.FromStream(new MemoryStream(systemImage!)))
        Check(image.Width > 0 && image.Height > 0, "system application icon has valid image dimensions");
    Check(await Task.Run(() => SystemIconReader.ForExtension(".txt")) is { Length: > 0 }, "system extension icon lookup requires no real document");
    Check(await Task.Run(() => SystemIconReader.ForApplication(new(AppCatalog.Definitions[0], Path.Combine(root, "missing.exe"), null))) == null,
        "missing application icons return the placeholder state");
    Check(AppCatalog.Definitions.Take(4).Select(app => app.Name).SequenceEqual(new[] { "Notepad", "Paint", "WordPad", "Word" }),
        "Windows editors use English names and appear before Word in the requested order");
    string executableFixture = Path.Combine(root, "fixture.exe");
    File.WriteAllText(executableFixture, "non-executable existence fixture");
    var wordDefinition = AppCatalog.Definitions.Single(app => app.Key == "Word");
    var existingApp = new DetectedApp(wordDefinition, executableFixture, null);
    Check(existingApp.IsInstalled && !new DetectedApp(wordDefinition, Path.Combine(root, "missing.exe"), null).IsInstalled
        && !new DetectedApp(wordDefinition, null, "").IsInstalled, "all classic applications require an existing executable and empty package IDs are rejected");
    Check(!AppAvailability.ExecutableExists(root) && !AppAvailability.PackageEntryExists("invalid-package-id"), "directories and malformed package launch entries cannot become installed apps");
    string emptyAlias = Path.Combine(root, "olk.exe");
    File.WriteAllBytes(emptyAlias, []);
    Check(!AppAvailability.ExecutableExists(emptyAlias) && !new DetectedApp(wordDefinition, emptyAlias, null).IsInstalled,
        "zero-byte execution aliases are not treated as real executable images");
    var aliasFirst = wordDefinition with { ExeNames = [], ExtraPaths = [emptyAlias, executableFixture] };
    Check(new OfficeDetector().Detect(aliasFirst, default) == executableFixture,
        "an empty alias cannot shadow a valid application executable");
    Check(await Task.Run(() => SystemIconReader.ForApplication(new(wordDefinition, emptyAlias, null))) == null,
        "icon extraction does not accept a generic image from an empty execution alias");
    Check(LaunchService.ForApp(existingApp).FileName == executableFixture, "application launch rechecks a valid executable without starting the fixture");
    var classicWithPackage = existingApp with { AppUserModelId = "Fixture.Package!App" };
    Check(LaunchService.ForApp(classicWithPackage, _ => true).FileName == executableFixture,
        "Office apps keep their existing classic executable preference when a store version is also installed");
    var packagedNotepad = classicWithPackage with { Definition = AppCatalog.Definitions.Single(app => app.Key == "Notepad") };
    var packagedLaunch = LaunchService.ForApp(packagedNotepad, _ => true);
    Check(packagedLaunch.ArgumentList.Single() == "shell:AppsFolder\\Fixture.Package!App"
        && LaunchService.ForApp(packagedNotepad, _ => false).FileName == executableFixture,
        "Notepad prefers a verified store launch entry and falls back to the existing classic executable");
    var removedApp = new DetectedApp(wordDefinition, Path.Combine(root, "uninstalled.exe"), null);
    try { LaunchService.ForApp(removedApp); throw new Exception("uninstalled application was accepted"); }
    catch (FileNotFoundException) { Check(true, "applications removed since detection cannot be launched"); }
    var missingWordPad = AppCatalog.Definitions.Single(app => app.Key == "WordPad") with { ExeNames = [], ExtraPaths = [Path.Combine(root, "missing-wordpad.exe")] };
    Check(new OfficeDetector().Detect(missingWordPad, default) == null, "WordPad is optional and no executable is fabricated when it is missing");
    var coreApps = new[] { existingApp, new DetectedApp(AppCatalog.Definitions.Single(app => app.Key == "Excel"), null, null) };
    var coreDocument = new RecentItem("fixture", "https://example.com/core.docx", DateTimeOffset.UtcNow, "Word", "Office", true);
    int phase = 0;
    var hub = new HubService((_, cancellation) => { cancellation.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<DetectedApp>>(coreApps); },
        (_, _) => phase switch
        {
            0 => Task.FromResult(new RecentResult([coreDocument], [], false)),
            1 => Task.FromResult(new RecentResult([], ["fixture-source-failure"], true)),
            2 => throw new IOException("fixture collection failure"),
            _ => Task.FromResult(new RecentResult([], [], false))
        });
    var initialSnapshot = await hub.RefreshAsync(true, default);
    Check(initialSnapshot is { Apps.Count: 1, Documents.Count: 1 } && initialSnapshot.Apps[0].Definition.Key == "Word",
        "Core application lists contain installed applications without a synthetic manager entry");
    phase = 1;
    var failedSnapshot = await hub.RefreshAsync(true, default);
    Check(initialSnapshot!.Documents.SequenceEqual(failedSnapshot!.Documents) && failedSnapshot.Warnings.Single() == "fixture-source-failure", "all-source failures preserve the last document snapshot");
    phase = 2;
    var crashedSnapshot = await hub.RefreshAsync(true, default);
    Check(initialSnapshot.Documents.SequenceEqual(crashedSnapshot!.Documents) && crashedSnapshot.Warnings.Single() == Text.T("RefreshFailed"), "Core reports unexpected failures while preserving prior apps and documents");
    phase = 3;
    Check((await hub.RefreshAsync(true, default)) is { Documents.Count: 0, Warnings.Count: 0 }, "successful empty refresh clears stale documents and previous warnings");
    Check(await hub.RefreshAsync(false, default) == null, "Core coalesces background refreshes");
    using (var hubCancellation = new CancellationTokenSource())
    {
        hubCancellation.Cancel();
        try { await hub.RefreshAsync(true, hubCancellation.Token); throw new Exception("cancelled Core refresh ran"); }
        catch (OperationCanceledException) { Check(true, "Core cancellation remains cancellation"); }
    }
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var launchRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int launches = 0;
    var opener = new OperationService(_ => { Interlocked.Increment(ref launches); started.SetResult(); launchRelease.Task.GetAwaiter().GetResult(); });
    var firstOpen = opener.OpenProjectAsync(false);
    try
    {
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(await opener.OpenProjectAsync(false) == null && launches == 1, "Core prevents concurrent duplicate launches");
    }
    finally { launchRelease.TrySetResult(); }
    Check(await firstOpen == null && await opener.OpenProjectAsync(false) == null && launches == 1, "successful launch is debounced for one second");
    var brokenOpener = new OperationService(_ => throw new InvalidOperationException("fixture shell failure"));
    Check(await brokenOpener.OpenProjectAsync(false) == Text.T("OpenWebFailed"), "Core returns resource-backed operation failures");
    Check(await brokenOpener.OpenDocumentAsync(coreDocument with { Location = Path.Combine(root, "deleted.docx") }) == Text.T("MissingDocument"), "Core reports missing documents without invoking the shell");
    var expected = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    string hex = expected.ToFileTime().ToString("X16");
    Check(DocumentLocation.ParseMruTimestamp($"[F00000000][T{hex}][O00000000]*C:\\Example.docx") == expected, "Office bracketed access time is decoded");
    Check(DocumentLocation.ParseMruTimestamp($"T{hex}*C:\\Example.docx") == expected, "legacy access time remains supported");
    Check(DocumentLocation.ParseMruTimestamp("[TFFFFFFFFFFFFFFFF]*x") == null && DocumentLocation.ParseMruTimestamp("[T0000000000000000]*x") == null, "invalid timestamps do not become real access dates");
    Check(DocumentLocation.Identity(@"C:\Docs\A.docx") == DocumentLocation.Identity("file:///C:/docs/a.docx"), "file URI and Windows path share an identity");
    Check(DocumentLocation.Identity("https://EXAMPLE.com/Report.pdf?token=AbC") == DocumentLocation.Identity("https://example.com/Report.pdf?token=AbC"), "web host case is normalized");
    Check(DocumentLocation.Identity("https://example.com/Report.pdf?token=AbC") != DocumentLocation.Identity("https://example.com/report.pdf?token=abc"), "web path and token case remain distinct");
    Check(DocumentLocation.Identity("https://example.com//a.pdf") != DocumentLocation.Identity("https://example.com/a.pdf"), "significant double slashes remain distinct");
    Check(!DocumentLocation.TryParse("custom:run.pdf", out _, out _) && !DocumentLocation.TryParse("relative.docx", out _, out _), "unsupported schemes and relative paths are rejected");
    Check(!DocumentLocation.Display("https://example.com/a.pdf?token=secret").Contains("secret"), "display location omits URL query tokens");
    var first = new RecentItem("old", "https://example.com/a.pdf", expected, "Edge", "Office", true);
    var newer = first with { Title = "new", Timestamp = expected.AddHours(1), Source = "Edge" };
    var merged = Recent.Merge([first, newer, first], 50);
    Check(merged.Count == 1 && merged[0].Timestamp == newer.Timestamp && merged[0].Title == newer.Title && merged[0].Source.Contains("Office"), "cross-source merge retains the newest visit and all sources");
    Check(Recent.Merge([first, newer with { Location = "https://example.com/A.pdf" }], 50).Count == 2, "distinct web documents survive merging");
    Check(Recent.Merge([first, newer with { Location = "https://example.com/b.pdf" }], 1)[0].Title == "new", "limit is applied after global sorting");
    Check(!Recent.TryCreate(Path.Combine(root, "missing.docx"), "Word", "Cache", expected, out _), "deleted cached documents are filtered");
    Check(Recent.TryCreate(@"\\offline-server\share\a.docx", "Word", "Cache", null, out var network) && network!.IsNetwork && network.TimeKind == DocumentTimeKind.Unknown, "offline network paths survive without filesystem probing");
    string document = Path.Combine(root, "document.docx"); File.WriteAllText(document, "fixture");
    Check(Recent.TryCreate(document, "Word", "Office", null, out var modified) && modified!.TimeKind == DocumentTimeKind.Modified, "modification fallback is labelled explicitly");
    Check(AppCatalog.FromExtension(".docx.exe") == null && AppCatalog.FromExtension(".ppsx") == "PowerPoint", "document types use exact extensions");
    Check(LaunchService.ForDocument(new Uri(document).AbsoluteUri).FileName == document, "file URI opens through the normalized local path");
    try { LaunchService.ForDocument("custom:run.pdf"); Check(false, "unsafe launch rejected"); } catch (ArgumentException) { Check(true, "unsafe launch rejected"); }

    var policy = ConfigService.Settings;
    Check(policy.IncludeRegistry && policy.IncludeCache && policy.IncludeWindowsRecent && policy.IncludeJumpLists && policy.IncludeEdgeHistory && policy.UseInstalledAppsOnly && policy.EdgeProfiles.Length == 0, "all document sources and all Edge profiles are enabled without a settings UI");
    Check(!EdgeHistoryReader.IsProfileName("../escape"), "browser profile traversal is rejected");
    Check(DocumentLocation.TryParse("onenote:https://example.com/Notes.one#Page", out var noteTarget, out _) && noteTarget.StartsWith("onenote:"), "OneNote notebook and page links are retained");
    Check(!DocumentLocation.TryParse("onenote:custom:run", out _, out _), "nested unsupported notebook protocols are rejected");
    Check(DocumentLocation.TryParse("ms-excel:ofv|u|https://example.com/report.xlsx", out _, out _) && DocumentLocation.Identity("ms-excel:ofv|u|https://example.com/report.xlsx") == DocumentLocation.Identity("https://example.com/report.xlsx"), "documented Office open URI shares its underlying document identity");
    Check(!DocumentLocation.TryParse("ms-excel:nft|u|https://example.com/template.xlsx|s|https://elsewhere/", out _, out _), "Office create and multi-argument commands are excluded from history");
    Check(Recent.ExtractMruLocation("[F00000000][T" + hex + "]*https://example.com/a.docx?filter=*") == "https://example.com/a.docx?filter=*", "MRU extraction preserves stars inside document URLs");
    var related = Recent.Merge([first with { App = "Word" }, first with { App = "OneDrive", Source = "Windows" }], 50)[0];
    Check(related.AppKeys.Contains("Word") && related.AppKeys.Contains("OneDrive"), "cross-app deduplication preserves every associated app");
    Check(Recent.Merge([first, first with { Timestamp = expected.AddYears(1), TimeKind = DocumentTimeKind.Modified }], 50)[0].TimeKind == DocumentTimeKind.Accessed, "a modification fallback cannot replace a known visit");
    Check(AppCatalog.FromExtension(".TXT") == "Notepad" && AppCatalog.FromExtension(".rtf") == "Word", "TXT belongs to Notepad while existing rich text classification is preserved");
    var textFromWord = first with { Location = "https://example.com/notes.TXT", App = "Word", RelatedApps = "Word;WordPad" };
    var textFromNotepad = textFromWord with { App = "Notepad", RelatedApps = "Notepad", Source = "Windows", Timestamp = expected.AddHours(1) };
    var mergedText = Recent.Merge([textFromWord, textFromNotepad], 50).Single();
    Check(mergedText.App == "Notepad" && mergedText.AppKeys.SequenceEqual(new[] { "Notepad" }) && mergedText.Source.Contains("Office") && mergedText.Source.Contains("Windows"),
        "cross-source TXT merging keeps both sources but removes all Word and WordPad app tags");
    Check(Recent.TryCreate("https://example.com/notes.txt", "Word", "Office", expected, out var officeText) && officeText!.App == "Notepad",
        "TXT from an Office MRU is normalized before reaching the list");
    Check(AppCatalog.IsDocument("https://example.com/drawing.png", "Paint") && !AppCatalog.IsDocument("https://example.com/drawing.png"),
        "Paint image history requires a Paint source rather than attributing every picture to Paint");
    foreach (var pair in new[] { (".msg", "Outlook"), (".onepkg", "OneNote"), (".accdt", "Access"), (".vssx", "Visio"), (".mpt", "Project"), (".pub", "Publisher"), (".clipchamp", "Clipchamp") })
        Check(AppCatalog.FromExtension(pair.Item1) == pair.Item2, "application document coverage " + pair.Item2);
    Check(AppCatalog.IsDocument("https://example.com/video.mp4", "Clipchamp") && !AppCatalog.IsDocument("https://example.com/video.mp4", "Word"), "video destinations require an actual Clipchamp source");
    Check(WindowsRecentReader.IsWithin(document, root) && !WindowsRecentReader.IsWithin(root + "-other\\document.docx", root), "OneDrive attribution respects folder boundaries");
    var legacy = new List<RecentItem>();
    string accessFile = Path.Combine(root, "database.accdb"); File.WriteAllText(accessFile, "fixture");
    Recent.AddApplicationRecord(accessFile, "Access", legacy);
    Recent.AddApplicationRecord("https://example.com/Notebook", "OneNote", legacy);
    Check(legacy.Count == 2 && legacy[0].App == "Access" && legacy[1].Location.StartsWith("onenote:"), "application-specific stores support Access databases and OneNote notebook destinations");
    var arguments = JumpListReader.DocumentArguments("/n \"C:\\Docs\\A B.docx\" \"https://example.com/a.pdf?token=AbC\"");
    Check(arguments.Count == 2 && arguments[0] == @"C:\Docs\A B.docx", "jump-list arguments preserve quoted document paths without executing commands");
    Check(JumpListReader.ReadTargets("EricWorkHub.IsolatedFixture." + Guid.NewGuid().ToString("N"), 5, default).Count == 0, "documented jump-list COM ABI reads an isolated empty application without touching Office history");
    string shellRecent = Path.Combine(root, "Recent"); Directory.CreateDirectory(shellRecent);
    File.WriteAllText(Path.Combine(shellRecent, "word.url"), "[InternetShortcut]\r\nURL=" + new Uri(document).AbsoluteUri);
    File.WriteAllText(Path.Combine(shellRecent, "outlook.url"), "[InternetShortcut]\r\nURL=https://example.com/message.msg");
    var windowsDocuments = WindowsRecentReader.Read(policy, new HashSet<string> { "Word", "OneDrive" }, default, shellRecent, [root]);
    Check(windowsDocuments.Count == 1 && windowsDocuments[0].AppKeys.Contains("OneDrive") && windowsDocuments[0].TimeKind == DocumentTimeKind.Recorded, "Windows recent items filter uninstalled apps and retain OneDrive attribution");
    var oneDriveOnly = WindowsRecentReader.Read(policy, new HashSet<string> { "OneDrive" }, default, shellRecent, [root]);
    Check(oneDriveOnly.Count == 1 && oneDriveOnly[0].App == "OneDrive" && !oneDriveOnly[0].AppKeys.Contains("Word"), "OneDrive fallback does not reintroduce an uninstalled editor");
    File.WriteAllText(Path.Combine(shellRecent, "notepad.url"), "[InternetShortcut]\r\nURL=https://example.com/notes.txt");
    var withNotepad = WindowsRecentReader.Read(policy, new HashSet<string> { "Word", "Notepad" }, default, shellRecent, []);
    Check(withNotepad.Count == 2 && withNotepad.Single(item => item.Location.EndsWith(".txt")).App == "Notepad", "Windows recent TXT appears as Notepad when it is installed");
    Check(WindowsRecentReader.Read(policy, new HashSet<string> { "Word" }, default, shellRecent, []).All(item => !item.Location.EndsWith(".txt")),
        "Windows recent TXT is filtered if Notepad is unavailable");

    var restored = WindowPlacement.Fit(new(-1700, -900, 900, 600), [new(0, 0, 1920, 1080), new(-1920, -1080, 1920, 1080)]);
    Check(restored.X == -1700 && restored.Y == -900, "negative monitor coordinates restore correctly");
    var detached = WindowPlacement.Fit(new(-5000, 5000, 9000, 5000), [new(0, 0, 1280, 720)]);
    Check(detached == new WindowBounds(0, 0, 1280, 720), "disconnected monitor and oversized bounds are clamped");
    var lru = new BoundedCache<string, int>(2); lru.Set("a", 1); lru.Set("b", 2); lru.TryGet("a", out _); lru.Set("c", 3);
    Check(!lru.TryGet("b", out _) && lru.TryGet("a", out _), "bitmap cache evicts the least recently used item");

    var rows = new ObservableCollection<ListRow> { new("a", "A"), new("b", "B"), new("c", "C") };
    var a = rows[0]; var b = rows[1]; var c = rows[2];
    var changes = new List<NotifyCollectionChangedAction>();
    rows.CollectionChanged += (_, args) => changes.Add(args.Action);
    void ApplyRows(params RowData[] models) => StableList.Apply(rows, models, row => row.Key, model => model.Key,
        model => new ListRow(model.Key, model.Text), (row, model) => row.Text = model.Text);
    ApplyRows(new("a", "A", ["new metadata allocation"]), new("b", "B", []), new("c", "C", []));
    Check(changes.Count == 0 && ReferenceEquals(rows[0], a) && ReferenceEquals(rows[1], b) && ReferenceEquals(rows[2], c), "unchanged snapshots cause zero list events despite newly allocated metadata");
    var icon = a.Icon;
    ApplyRows(new("a", "updated", []), new("b", "B", []), new("c", "C", []));
    Check(changes.Count == 0 && rows[0].Text == "updated" && ReferenceEquals(rows[0].Icon, icon), "text updates reuse the row and retain its decoded icon");
    ApplyRows(new("c", "C", []), new("b", "B", []), new("a", "updated", []));
    Check(rows.Select(row => row.Key).SequenceEqual(new[] { "c", "b", "a" }) && changes.All(action => action == NotifyCollectionChangedAction.Move)
        && ReferenceEquals(rows[2], a), "genuine reordering uses Move without Reset or replacement");
    changes.Clear();
    ApplyRows(new("b", "B", []), new("a", "updated", []), new("d", "D", []));
    Check(changes.SequenceEqual(new[] { NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add }) && ReferenceEquals(rows[0], b), "membership changes touch only the removed and added rows");
    changes.Clear();
    try { ApplyRows(new("b", "B", []), new("b", "duplicate", [])); Check(false, "duplicate snapshot rejected"); }
    catch (ArgumentException) { Check(changes.Count == 0 && rows.Count == 3, "invalid duplicate snapshot is rejected before mutating the visible list"); }

    var gate = new RefreshGate();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int running = 0, concurrent = 0, scans = 0;
    var scan = gate.RunAsync(false, TimeSpan.Zero, async _ => { concurrent = Math.Max(concurrent, ++running); scans++; entered.SetResult(); await release.Task; running--; }, default);
    await entered.Task;
    Check(!await gate.RunAsync(false, TimeSpan.Zero, _ => throw new Exception("overlap"), default), "background refresh does not overlap an active scan");
    var forced = gate.RunAsync(true, TimeSpan.Zero, _ => { concurrent = Math.Max(concurrent, ++running); scans++; running--; return Task.CompletedTask; }, default);
    Check(!forced.IsCompleted, "manual refresh waits for the active scan");
    using var cancelled = new CancellationTokenSource();
    var waiting = gate.RunAsync(true, TimeSpan.Zero, _ => throw new Exception("cancelled scan ran"), cancelled.Token);
    cancelled.Cancel();
    try { await waiting; Check(false, "queued refresh cancellation"); } catch (OperationCanceledException) { Check(true, "queued refresh cancellation"); }
    release.SetResult(); await scan; await forced;
    Check(concurrent == 1 && scans == 2, "refresh concurrency is bounded to one");
    Check(!await gate.RunAsync(false, TimeSpan.FromSeconds(10), _ => throw new Exception("throttled scan ran"), default), "successful refresh is throttled");
    try { await gate.RunAsync(true, TimeSpan.Zero, _ => throw new IOException("fixture failure"), default); } catch (IOException) { }
    Check(await gate.RunAsync(true, TimeSpan.Zero, _ => Task.CompletedTask, default), "refresh gate is released after failure");

    string browser = Path.Combine(root, "Edge");
    Directory.CreateDirectory(Path.Combine(browser, "Default"));
    Directory.CreateDirectory(Path.Combine(browser, "Profile 1"));
    Directory.CreateDirectory(Path.Combine(browser, "System Profile"));
    string dbPath = Path.Combine(browser, "Default", "History");
    using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
    {
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE urls(url TEXT, title TEXT, last_visit_time INTEGER);";
        command.ExecuteNonQuery();
        void Add(string url, string title, DateTimeOffset when)
        {
            using var insert = database.CreateCommand();
            insert.CommandText = "INSERT INTO urls VALUES ($url, $title, $time)";
            insert.Parameters.AddWithValue("$url", url); insert.Parameters.AddWithValue("$title", title); insert.Parameters.AddWithValue("$time", when.ToFileTime() / 10); insert.ExecuteNonQuery();
        }
        Add("https://example.com/Live.pdf#page=2", "live WAL PDF", DateTimeOffset.UtcNow);
        Add("https://example.com/old.pdf", "too old", DateTimeOffset.UtcNow.AddDays(-100));
        Add("https://example.com/not.pdf.exe?x=.pdf", "not PDF", DateTimeOffset.UtcNow);
        Add("custom:unsafe.pdf", "invalid scheme", DateTimeOffset.UtcNow);
        Add(new Uri(document).AbsoluteUri.Replace(".docx", ".pdf"), "deleted local PDF", DateTimeOffset.UtcNow);
        byte[] DatabaseHash()
        {
            using var file = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return SHA256.HashData(file);
        }
        byte[] before = DatabaseHash();
        var reader = new EdgeHistoryReader(browser);
        var warnings = new List<string>();
        var edgeSettings = new AppSettings { IncludeEdgeHistory = true, EdgeProfiles = ["Default"], CacheMaxAgeDays = 90 };
        var pdfs = reader.Read(edgeSettings, warnings, default);
        Check(pdfs.Count == 1 && pdfs[0].Title == "live WAL PDF", "read-only Edge query sees committed WAL rows and filters old, invalid and deleted entries");
        Check(before.SequenceEqual(DatabaseHash()), "Edge query does not modify the source database");
        Check(warnings.Count == 0 && reader.GetProfiles().SequenceEqual(new[] { "Default", "Profile 1" }), "Edge profile discovery excludes system profiles");
        Check(reader.Read(edgeSettings, warnings, default).Count == 1, "unchanged history reuses cached results");
        Add("https://example.com/new.pdf?token=AbC", "new row", DateTimeOffset.UtcNow.AddSeconds(1));
        Check(reader.Read(edgeSettings, warnings, default).Count == 2, "WAL changes invalidate the history cache");
        Check(reader.Read(edgeSettings with { EdgeProfiles = ["Profile 1"] }, warnings, default).Count == 0, "profile selection does not leak other profiles' records");
        reader.Clear();
    }
    Check(Directory.GetFiles(root, "EdgeHistory_*.db", SearchOption.AllDirectories).Length == 0, "no complete browser database copies are created");
    var empty = Recent.GetItems(new AppSettings { IncludeRegistry = false, IncludeCache = false, IncludeEdgeHistory = false, IncludeWindowsRecent = false, IncludeJumpLists = false }, new HashSet<string>(), new EdgeHistoryReader(browser), default);
    Check(empty.Items.Count == 0 && !empty.AllSourcesFailed, "disabled sources produce a clean empty state");
    string badProfile = Path.Combine(browser, "Profile 1", "History");
    var profileOnePolicy = new AppSettings { EdgeProfiles = ["Profile 1"] };
    var profileOneReader = new EdgeHistoryReader(browser);
    var emptyWarnings = new List<string>();
    Check(profileOneReader.Read(profileOnePolicy, emptyWarnings, default).Count == 0 && emptyWarnings.Count == 0 && !profileOneReader.LastReadFailed,
        "a profile without a History file is a normal empty result");
    using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = badProfile, Pooling = false }.ToString()))
    {
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA user_version=1"; command.ExecuteNonQuery();
        Check(profileOneReader.Read(profileOnePolicy, emptyWarnings, default).Count == 0 && emptyWarnings.Count == 0 && !profileOneReader.LastReadFailed,
            "an initialized SQLite profile without a urls table is not an error");
        command.CommandText = "CREATE TABLE urls(url TEXT, title TEXT, last_visit_time INTEGER); INSERT INTO urls VALUES ('https://example.com/index.html', 'not PDF', 0)";
        command.ExecuteNonQuery();
        profileOneReader.Clear();
        Check(profileOneReader.Read(profileOnePolicy, emptyWarnings, default).Count == 0 && emptyWarnings.Count == 0 && !profileOneReader.LastReadFailed,
            "a valid history with no PDF visits produces no warning");
    }
    profileOneReader.Clear();
    using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = badProfile, Pooling = false }.ToString()))
    {
        database.Open();
        using var command = database.CreateCommand();
        long now = DateTimeOffset.UtcNow.ToFileTime() / 10;
        command.CommandText = $"INSERT INTO urls VALUES ('https://example.com/cached.pdf', 'cached', {now})";
        command.ExecuteNonQuery();
        long clock = 0;
        var busyReader = new EdgeHistoryReader(browser, () => clock);
        var warnings = new List<string>();
        Check(busyReader.Read(profileOnePolicy, warnings, default).Count == 1, "busy-history fixture first establishes a successful cached result");
        command.CommandText = $"INSERT INTO urls VALUES ('https://example.com/later.pdf', 'later', {now + 1}); BEGIN EXCLUSIVE";
        command.ExecuteNonQuery();
        var retryPolicy = profileOnePolicy with { CacheMaxAgeDays = 89 };
        Check(busyReader.Read(retryPolicy, warnings, default).Count == 1 && warnings.Count == 0 && busyReader.LastReadFailed,
            "an exclusively locked history preserves cached PDFs and schedules a quiet retry");
        command.CommandText = "COMMIT"; command.ExecuteNonQuery();
        Check(busyReader.Read(retryPolicy, warnings, default).Count == 1 && warnings.Count == 0, "backoff prevents immediate repeated history access after a lock");
        clock += 15_001;
        Check(busyReader.Read(retryPolicy, warnings, default).Count == 2 && warnings.Count == 0 && !busyReader.LastReadFailed,
            "background retry recovers after the history lock is released");
    }
    File.WriteAllText(badProfile, "not a sqlite database");
    var failedSource = Recent.GetItems(new AppSettings { IncludeRegistry = false, IncludeCache = false, IncludeWindowsRecent = false, IncludeJumpLists = false, IncludeEdgeHistory = true, EdgeProfiles = ["Profile 1"] }, new HashSet<string>(), new EdgeHistoryReader(browser), default);
    Check(failedSource.AllSourcesFailed && failedSource.Warnings.Count > 0, "complete source failure is distinguished from a legitimate empty list");

    string? repository = AppContext.BaseDirectory;
    while (repository != null && !File.Exists(Path.Combine(repository, "EricWorkHub.csproj"))) repository = Path.GetDirectoryName(repository);
    if (repository != null)
    {
        WorkflowValidation.Run(repository, Check);
        string logSource = File.ReadAllText(Path.Combine(repository, "Core", "LogService.cs")).Replace("\r\n", "\n").Trim();
        Check(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(logSource))) == "33EEC098572DC775B083E422B6E0FFF2FF49F570573704F1AC8214B0AF4690DA", "logger matches the pinned EGL source contract in standalone CI");
        string egl = Path.Combine(repository, "..", "EricGameLauncher", "Core");
        if (File.Exists(Path.Combine(egl, "LogService.cs")))
        {
            string Normalize(string value) => value.Replace("EricGameLauncher", "EricWorkHub").Replace("ericgamelauncher-log-error", "ericworkhub-log-error").Replace("\r\n", "\n").Trim();
            Check(Normalize(File.ReadAllText(Path.Combine(egl, "LogService.cs"))) == Normalize(File.ReadAllText(Path.Combine(repository, "Core", "LogService.cs"))), "logger matches EricGameLauncher exactly except application identity");
            Check(Normalize(File.ReadAllText(Path.Combine(egl, "SensitiveText.cs"))) == Normalize(File.ReadAllText(Path.Combine(repository, "Core", "SensitiveText.cs"))), "redaction matches EricGameLauncher exactly");
        }
    }
    string fakeToken = "ghp_" + new string('a', 36);
    using (LogService.StartOperation("Test", "TimedOperation", "test-operation"))
        LogService.Write("Test", "githubToken=" + fakeToken);
    LogService.Write("Test", "debug-marker", level: LogService.LogLevel.Debug);
    LogService.Write("Test", "error-marker", new InvalidOperationException("fixture exception"), level: LogService.LogLevel.Error);
    LogService.Write("Stress", "rotation-probe|" + new string('r', 6 * 1024 * 1024) + "|end", level: LogService.LogLevel.Warn);
    bool rotated = SpinWait.SpinUntil(() => Directory.GetFiles(logDirectory, "app-*-1.log").Length > 0, TimeSpan.FromSeconds(15));
    const int count = 12000;
    var stopwatch = Stopwatch.StartNew();
    Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, index =>
        LogService.Write("Stress", $"critical-{index:D5}|" + new string('x', 1024) + "|end", level: LogService.LogLevel.Warn));
    LogService.Write("Stress", "flush-tail-marker", level: LogService.LogLevel.Error);
    LogService.FlushAndStop();
    string contents = string.Join('\n', Directory.GetFiles(logDirectory, "app-*.log").Select(File.ReadAllText));
    var observed = Regex.Matches(contents, @"critical-(\d{5})\|").Select(match => int.Parse(match.Groups[1].Value)).ToHashSet();
    Check(observed.Count == count, "all 12000 concurrent warnings survive bounded queue overflow");
    Check(contents.Contains("flush-tail-marker"), "shutdown drains the final error entry");
    Check(!File.Exists(expired) && !File.Exists(oversized) && File.Exists(recent), "logger enforces 30-day and 100-MB retention");
    Check(rotated, "logger rotates at the 5-MB threshold");
    Check(contents.Split('\n').Where(line => line.Contains("critical-")).All(line => line.TrimEnd('\r').EndsWith("|end")), "concurrent critical fallback preserves complete lines");
    Check(!contents.Contains(fakeToken) && contents.Contains("[redacted]"), "logger redacts secrets");
    Check(contents.Contains("OperationStart TimedOperation") && contents.Contains("OperationEnd TimedOperation duration=") && contents.Contains("op=test-operation"), "operation IDs and timings survive shutdown");
    Check(Regex.IsMatch(contents, @"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}Z Test/ERROR\] Program.cs:\d+\.") && contents.Contains("Stack="), "UTC, level, caller and exception layout matches the EGL format");
    Check(contents.Contains("Test/DEBUG"), "debug entries use the shared level format");
    Check(contents.Contains("History busy; background retry scheduled") && !Regex.IsMatch(contents, @"Edge/WARN[^\r\n]*database is locked"), "expected database locks are logged without warning-level errors");
    Console.WriteLine($"{checks} checks passed; logger stress={stopwatch.ElapsedMilliseconds}ms.");
}

finally
{
    LogService.FlushAndStop();
    string resolved = Path.GetFullPath(root);
    if (resolved.StartsWith(testCache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(resolved).StartsWith("EricWorkHub.Tests.", StringComparison.Ordinal)) Directory.Delete(resolved, true);
}

sealed record RowData(string Key, string Text, string[] Metadata);
sealed class ListRow(string key, string text)
{
    public string Key { get; } = key;
    public string Text { get; set; } = text;
    public object Icon { get; } = new();
}
