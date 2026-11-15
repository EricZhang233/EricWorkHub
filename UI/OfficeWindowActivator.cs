using System.Runtime.InteropServices;

namespace EricWorkHub;

internal static class OfficeWindowActivator
{
    public static bool Activate(OfficeSetupProcess process)
    {
        IntPtr window = process.Window;
        if (window != IntPtr.Zero)
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != process.Id) window = IntPtr.Zero;
        }
        if (window == IntPtr.Zero || !IsWindow(window))
        {
            EnumWindows((candidate, _) =>
            {
                GetWindowThreadProcessId(candidate, out uint id);
                if (id != process.Id || !IsWindowVisible(candidate)) return true;
                window = candidate;
                return false;
            }, IntPtr.Zero);
        }
        if (window == IntPtr.Zero) return false;
        ShowWindowAsync(window, IsIconic(window) ? 9 : 5);
        bool activated = SetForegroundWindow(window);
        LogService.Write("OfficeManager", $"Existing setup activation requested pid={process.Id} activated={activated}");
        if (!activated)
        {
            var flash = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = window, Flags = 3, Count = 3 };
            FlashWindowEx(ref flash);
        }
        return activated;
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags, Count, Timeout; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlashWindowEx(ref FlashInfo info);
}
