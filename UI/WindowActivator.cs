using System.Runtime.InteropServices;

namespace EricWorkHub;

internal static class WindowActivator
{
    public static void AllowForeground(int process) => AllowSetForegroundWindow((uint)process);

    public static void Activate(MainWindow window)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (IsIconic(handle)) ShowWindowAsync(handle, 9);
        window.Activate();
        bool activated = SetForegroundWindow(handle);
        LogService.Write("Window", $"Existing main window activated foreground={activated}");
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(IntPtr window, int command);
}
