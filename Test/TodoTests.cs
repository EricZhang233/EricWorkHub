using EricWorkHub;
using System.Diagnostics;
using System.Text;

internal static class TodoTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var schedule = new ForegroundRefreshPolicy();
        check(ForegroundRefreshPolicy.Interval == TimeSpan.FromMilliseconds(7500) && !schedule.RunTimer && !schedule.TryBegin(),
            "refresh uses a 7.5-second interval and has no timer or scan while initially in the background");
        check(schedule.SetActive(true) && schedule.RunTimer && schedule.TryBegin() && !schedule.TryBegin(),
            "foreground activation starts an immediate refresh and overlapping timer ticks are coalesced");
        check(!schedule.SetActive(true), "activation notifications without a focus transition do not schedule another immediate scan");
        schedule.SetActive(false);
        check(!schedule.RunTimer && !schedule.Complete(), "losing foreground stops timing and does not queue a background refresh");
        schedule.SetActive(true);
        schedule.TryBegin();
        schedule.SetActive(false);
        schedule.SetActive(true);
        schedule.SetActive(false);
        schedule.SetActive(true);
        check(schedule.Complete() && schedule.TryBegin() && !schedule.Complete(),
            "focus gained during a slow scan queues exactly one immediate follow-up refresh");
        schedule.Close();
        check(!schedule.RunTimer && !schedule.SetActive(true) && !schedule.TryBegin(), "closing the window permanently stops refresh scheduling");
        var now = DateTimeOffset.UtcNow;
        string file = Path.Combine(root, "preview.docx");
        File.WriteAllText(file, "isolated document metadata fixture");
        check(SourcePath.Exists(root, true) && SourcePath.Exists(file, false) && !SourcePath.Exists(Path.Combine(root, "absent"), true),
            "source existence probes distinguish missing data from exceptions without suppressing permission failures");
        var document = new RecentItem("preview", file, now, "Word", "Office", false);
        var expiryTime = now;
        var expiring = new RecentSourceCache(() => expiryTime);
        expiring.Update("offline", [document with { TimeKind = DocumentTimeKind.Unknown }], true, 10);
        expiryTime = now.AddDays(11);
        check(expiring.Update("offline", [], false, 10).Count == 0,
            "failed snapshots with unknown document times also expire by their last successful read");
        var pdf = document with { Location = "https://example.com/Folder/Preview.pdf?token=isolated#page=2", App = "PDF", Source = "Edge", IsCloud = true };
        var cache = new RecentSourceCache();
        cache.Update("office:Word", [document], true);
        cache.Update("edge", [pdf], true);
        check(cache.Update("office:Word", [], false).SequenceEqual(new[] { document })
            && cache.Update("edge", [], true).Count == 0,
            "a failed source keeps its own snapshot while another successful empty source is cleared");
        check(cache.Update("office:Word", [], true).Count == 0 && cache.Update("office:Word", [], false).Count == 0,
            "successful empty reads replace prior snapshots and cannot resurrect them on later failures");
        cache.Update("office:Word", [document], true);
        cache.Retain(new HashSet<string>(StringComparer.Ordinal) { "edge" });
        check(cache.Update("office:Word", [], false).Count == 0, "disabled and removed source identities discard cached snapshots");
        var settings = new AppSettings();
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Word", "OneDrive" };
        check(RecentSourceCache.Filter([document with { Timestamp = now.AddDays(-91) }, pdf], settings, installed, default, now).SequenceEqual(new[] { pdf }),
            "retained snapshots expire old records while preserving valid browser PDF entries");
        var related = document with { RelatedApps = "Word;OneDrive" };
        var oneDriveOnly = RecentSourceCache.Filter([related], settings, new HashSet<string> { "OneDrive" }, default).Single();
        check(oneDriveOnly.App == "OneDrive" && oneDriveOnly.RelatedApps == "OneDrive",
            "uninstalled application tags are removed from retained multi-application documents");
        check(RecentSourceCache.Filter([document with { Location = Path.Combine(root, "deleted.docx") }, document with { Location = Path.Combine(root, "not-installed.txt") }], settings, installed, default).Count == 0,
            "deleted files and unavailable text editors are excluded from retained source snapshots");
        var sourceAwareHub = new HubService((_, _) => Task.FromResult<IReadOnlyList<DetectedApp>>([]),
            (_, _) => Task.FromResult(new RecentResult([], [], true, true)));
        check((await sourceAwareHub.RefreshAsync(true, default))!.Documents.Count == 0,
            "source-aware complete failure does not restore an unfiltered aggregate snapshot");

        var collecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collected = new TaskCompletionSource<RecentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new DetectedApp(AppCatalog.Definitions.Single(app => app.Key == "Word"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), null);
        HubSnapshot? ready = null;
        var fast = new HubService((_, _) => Task.FromResult<IReadOnlyList<DetectedApp>>([app]), (_, _) => { collecting.TrySetResult(); return collected.Task; });
        var refresh = fast.RefreshAsync(true, default, snapshot => ready = snapshot);
        await collecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(ready is { Apps.Count: 1, Documents.Count: 0 } && !refresh.IsCompleted && ready.Apps[0].Definition.Key == "Word",
            "installed applications are published before a slow document source completes");
        collected.SetResult(new([document], [], false));
        check((await refresh)!.Documents.Single() == document, "document collection fills the existing application snapshot afterward");

        var before = DocumentPreview.Refresh(document);
        File.AppendAllText(file, " content changed");
        var after = DocumentPreview.Refresh(document);
        check(before.Timestamp == after.Timestamp && DocumentPreview.Key(before) != DocumentPreview.Key(after),
            "file content metadata invalidates previews even when the recorded access time is unchanged");
        var preview = new PreviewRequestState();
        preview.Update(DocumentPreview.Key(before));
        check(preview.TryBegin(out int oldRequest) && !preview.TryBegin(out _), "visible preview requests coalesce for the same revision");
        preview.Update(DocumentPreview.Key(after));
        check(preview.TryBegin(out int newRequest) && !preview.Complete(oldRequest, true) && preview.Complete(newRequest, true)
            && !preview.Complete(oldRequest, true) && !preview.TryBegin(out _),
            "outdated preview completions cannot overwrite a newer image or request");
        preview.Update("another-revision");
        preview.TryBegin(out int failedRequest);
        check(!preview.Complete(failedRequest, false) && preview.TryBegin(out _), "failed preview replacements keep the old image eligible for a later retry");

        var bounds = new WindowBounds(-1800, -900, 900, 650);
        var window = new WindowStateTracker(new(bounds, 230, false, 96));
        window.Observe(WindowMode.Normal, bounds with { X = -1700 }, 144);
        var restored = window.State.Bounds;
        window.Observe(WindowMode.Maximized, new(-1920, -1080, 1920, 1080), 144);
        window.Observe(WindowMode.Minimized, new(-32000, -32000, 0, 0), 96);
        check(window.State.Maximized && window.State.Bounds == restored && window.State.Dpi == 144,
            "maximized and minimized closes preserve the latest normal bounds and last non-minimized state");
        window.Observe(WindowMode.Normal, restored, 144);
        check(!window.State.Maximized, "restoring a maximized window clears the persisted maximized state");
        var scaled = WindowPlacement.Scale(bounds, 96, 144);
        check(scaled.X == bounds.X && scaled.Y == bounds.Y && scaled.Width == 1350 && scaled.Height == 975
            && WindowPlacement.Scale(bounds, 0, 144) == bounds,
            "DPI restoration scales normal dimensions while retaining negative multi-monitor coordinates and legacy bounds");
        check(WindowPlacement.Restore(new(20, 20, 1800, 900), 144, 96, [new(0, 0, 1280, 720)]).Width == 1200,
            "DPI scaling precedes work-area clamping when restoring onto a smaller display");

        var localMenu = DocumentActions.Menu(document with { Location = new Uri(file).AbsoluteUri });
        check(!localMenu.IsLink && localMenu.CopyText == file && DocumentActions.ContainingLocation(document).Arguments == "/select,\"" + file + "\"",
            "local document actions copy normalized paths and select the file in Explorer without invoking a command shell");
        check(DocumentActions.ContainingLocation(document with { Location = @"\\offline-server\share\folder\doc.docx" }).Arguments.Contains(@"\\offline-server\share\folder\doc.docx"),
            "network document actions preserve UNC targets without probing offline shares");
        check(DocumentActions.Menu(pdf).CopyText == pdf.Location
            && DocumentActions.ContainingLocation(pdf).FileName == "https://example.com/Folder/",
            "web actions copy the complete link while opening a parent location without document tokens");
        check(DocumentActions.ContainingLocation(pdf with { Location = "ms-word:ofe|u|https://example.com/Folder/document.docx?token=isolated" }).FileName == "https://example.com/Folder/"
            && DocumentActions.ContainingLocation(document with { Location = "onenote:" + file }).Arguments.Contains(file),
            "Office protocol and local OneNote locations resolve to their underlying containing location");

        string identity = "EricWorkHub.Tests." + Guid.NewGuid().ToString("N");
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var primary = new SingleInstance(identity))
        {
            check(primary.IsPrimary, "an isolated single-instance scope elects its first process");
            primary.StartServer(() => signalled.TrySetResult());
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { typeof(TodoTests).Assembly.Location, "--instance-client", identity, root }) start.ArgumentList.Add(argument);
            using var child = Process.Start(start)!;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await child.WaitForExitAsync(deadline.Token);
                string output = await child.StandardOutput.ReadToEndAsync();
                string error = await child.StandardError.ReadToEndAsync();
                check(child.ExitCode == 0 && output.Contains("secondary-notified"),
                    "a separate process notifies the primary and exits before creating a UI or scanning applications; exit=" + child.ExitCode + " stdout=" + output + " stderr=" + error);
                await signalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check(signalled.Task.IsCompletedSuccessfully, "the primary receives the bounded activation message from the second process");
            }
            finally { if (!child.HasExited) child.Kill(); }
        }
        using (var reopened = new SingleInstance(identity)) check(reopened.IsPrimary, "primary shutdown releases the instance scope for a clean restart");

        var bundle = BundleFixture();
        check(ReleasePackageValidation.ReadBundle(bundle).Count == 2, "release validation reads the actual .NET bundle header and embedded configuration entries");
        foreach (byte[] invalid in new[] { new byte[64], bundle[..1000], CorruptBundle(bundle) })
        {
            try { ReleasePackageValidation.ReadBundle(invalid); throw new Exception("Invalid bundle was accepted"); }
            catch (InvalidDataException) { check(true, "release validation rejects absent, truncated or out-of-range embedded host configuration"); }
        }
    }

    private static byte[] CorruptBundle(byte[] source)
    {
        var copy = source.ToArray();
        BitConverter.GetBytes(long.MaxValue).CopyTo(copy, 248);
        return copy;
    }

    private static byte[] BundleFixture()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        stream.Position = 248;
        writer.Write(4096L);
        writer.Write(ReleasePackageValidation.BundleMarker);
        stream.Position = 512; writer.Write(Encoding.UTF8.GetBytes("{\"libraries\":{}}"));
        stream.Position = 768; writer.Write(Encoding.UTF8.GetBytes("{\"runtimeOptions\":{}}"));
        stream.Position = 4096;
        writer.Write(6U); writer.Write(0U); writer.Write(2); writer.Write("isolated");
        writer.Write(512L); writer.Write(16L); writer.Write(768L); writer.Write(21L); writer.Write(0UL);
        writer.Write(512L); writer.Write(16L); writer.Write(0L); writer.Write((byte)3); writer.Write("EricWorkHub.deps.json");
        writer.Write(768L); writer.Write(21L); writer.Write(0L); writer.Write((byte)4); writer.Write("EricWorkHub.runtimeconfig.json");
        return stream.ToArray();
    }
}
