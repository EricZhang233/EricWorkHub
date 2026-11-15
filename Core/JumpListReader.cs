using System.Runtime.InteropServices;
using System.Text;

namespace EricWorkHub;

internal static class JumpListReader
{
    public static IReadOnlyDictionary<string, string[]> FindAppIds(CancellationToken token)
    {
        var ids = AppCatalog.Definitions.ToDictionary(app => app.Key, _ => new HashSet<string>(StringComparer.Ordinal));
        foreach (var folder in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
        {
            string root = Environment.GetFolderPath(folder);
            if (!Directory.Exists(root)) continue;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (string link in Directory.EnumerateFiles(root, "*.lnk", options))
                {
                    token.ThrowIfCancellationRequested();
                    string? target = ShortcutReader.Read(link);
                    string? executable = target == null ? null : Path.GetFileName(target);
                    var app = AppCatalog.Definitions.FirstOrDefault(app => app.ExeNames.Contains(executable, StringComparer.OrdinalIgnoreCase));
                    if (app == null) continue;
                    try { if (ShellProperties.AppId(link) is { Length: > 0 } id) ids[app.Key].Add(id); }
                    catch (Exception ex) { LogService.Write("Recent", "Shortcut application ID unavailable", ex, level: LogService.LogLevel.Debug); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { LogService.Write("Recent", "Start menu application ID discovery failed", ex, level: LogService.LogLevel.Warn); }
        }
        return ids.ToDictionary(pair => pair.Key, pair => pair.Value.Order(StringComparer.Ordinal).ToArray());
    }

    public static bool Read(IReadOnlyList<DetectedApp> apps, AppSettings settings, List<RecentItem> items, List<string> warnings, CancellationToken token)
    {
        int attempted = 0, successful = 0;
        foreach (var app in apps.Where(app => app.IsInstalled || !settings.UseInstalledAppsOnly))
        foreach (string id in (app.RecentAppIds ?? []).Concat(app.AppUserModelId == null ? [] : new[] { app.AppUserModelId }).Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            attempted++;
            try
            {
                items.AddRange(ReadItems(app, id, settings, token));
                successful++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                warnings.Add(Text.F("AppRecentFailed", app.Definition.Name));
                LogService.Write("Recent", $"Jump list unavailable app={app.Definition.Key}", ex, level: LogService.LogLevel.Warn);
            }
        }
        return attempted == 0 || successful > 0;
    }

    internal static IReadOnlyList<RecentItem> ReadItems(DetectedApp app, string id, AppSettings settings, CancellationToken token)
    {
        var items = new List<RecentItem>();
        foreach (string target in ReadTargets(id, settings.MaxCacheItems, token))
        {
            if (!AppCatalog.IsDocument(target, app.Definition.Key)) continue;
            string source = DocumentLocation.Extension(target) == ".txt" ? Text.T("SourceJumpList") : Text.F("AppJumpList", app.Definition.Name);
            if (Recent.TryCreate(target, app.Definition.Key, source, null, out var item)) items.Add(item!);
        }
        return items;
    }

    internal static IReadOnlyList<string> ReadTargets(string appId, int maximum, CancellationToken token)
    {
        object? instance = null;
        IObjectArray? array = null;
        var result = new List<string>();
        try
        {
            instance = new ApplicationDocumentLists();
            var lists = (IApplicationDocumentLists)instance;
            lists.SetAppID(appId);
            Guid arrayId = typeof(IObjectArray).GUID;
            lists.GetList(0, (uint)Math.Clamp(maximum, 1, 2000), ref arrayId, out array);
            array.GetCount(out uint count);
            for (uint index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                object? item = null;
                try
                {
                    Guid unknown = new("00000000-0000-0000-c000-000000000046");
                    array.GetAt(index, ref unknown, out item);
                    if (item is IShellItem shell)
                    {
                        IntPtr text = IntPtr.Zero;
                        try
                        {
                            int hr = shell.GetDisplayName(0x80028000, out text);
                            if (hr >= 0 && Marshal.PtrToStringUni(text) is string path && DocumentLocation.TryParse(path, out _, out _)) result.Add(path);
                        }
                        finally { if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text); }
                    }
                    else if (item is IShellLinkW link)
                    {
                        var buffer = new StringBuilder(32768);
                        link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 4);
                        string path = buffer.ToString();
                        if (AppCatalog.IsDocument(path, "Clipchamp")) result.Add(path);
                        else
                        {
                            buffer.Clear();
                            link.GetArguments(buffer, buffer.Capacity);
                            result.AddRange(DocumentArguments(buffer.ToString()));
                        }
                    }
                }
                catch (COMException ex) { LogService.Write("Recent", "Jump list destination skipped", ex, level: LogService.LogLevel.Debug); }
                finally { if (item != null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item); }
            }
            return result;
        }
        finally
        {
            if (array != null) Marshal.FinalReleaseComObject(array);
            if (instance != null) Marshal.FinalReleaseComObject(instance);
        }
    }

    internal static IReadOnlyList<string> DocumentArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return [];
        IntPtr argv = CommandLineToArgvW("document-handler.exe " + arguments, out int count);
        if (argv == IntPtr.Zero) return [];
        try
        {
            var result = new List<string>();
            for (int index = 1; index < count; index++)
            {
                string? value = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size));
                if (value != null && DocumentLocation.TryParse(value, out _, out _)) result.Add(value);
            }
            return result;
        }
        finally { LocalFree(argv); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string text, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [ComImport, Guid("86bec222-30f2-47e0-9f25-60d11cd75c28")] private class ApplicationDocumentLists { }
    [ComImport, Guid("3c594f9f-9f30-47a1-979a-c9e83d3d0a06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationDocumentLists
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string id);
        void GetList(uint type, uint maximum, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IObjectArray array);
    }
    [ComImport, Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object item);
    }
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr value);
        void GetParent(out IntPtr parent);
        [PreserveSig] int GetDisplayName(uint type, out IntPtr name);
    }
    [ComImport, Guid("000214f9-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
    }
}
