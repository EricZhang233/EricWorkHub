using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EricWorkHub;

internal sealed record OfficeSetupProcess(int Id, IntPtr Window);

internal static class OfficeSetupProcesses
{
    private static readonly string[] Names = ["OfficeSetup", "setup", "OfficeClickToRun", "OfficeC2RClient"];
    public static OfficeSetupProcess? Find()
    {
        using var current = Process.GetCurrentProcess();
        OfficeSetupProcess? windowless = null;
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    string name = process.ProcessName;
                    if (!Names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (process.SessionId != current.SessionId || process.HasExited) continue;
                    string? path = GetPath(process);
                    if (path == null || !MicrosoftExecutableTrust.IsOfficeExecutable(path)) continue;
                    IntPtr window = process.MainWindowHandle;
                    if (window == IntPtr.Zero && name.Equals("OfficeC2RClient", StringComparison.OrdinalIgnoreCase)) continue;
                    LogService.Write("OfficeManager", $"Existing Office setup found pid={process.Id} window={window}");
                    if (window != IntPtr.Zero) return new(process.Id, window);
                    windowless ??= new(process.Id, window);
                }
                catch (Exception ex) { LogService.Write("OfficeManager", "Office process inspection unavailable", ex, level: LogService.LogLevel.Debug); }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        return windowless;
    }

    private static string? GetPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (System.ComponentModel.Win32Exception)
        {
            IntPtr handle = OpenProcess(0x1000, false, process.Id);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var path = new StringBuilder(32768);
                uint length = (uint)path.Capacity;
                return QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : null;
            }
            finally { CloseHandle(handle); }
        }
    }
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint length);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
