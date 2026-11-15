using Microsoft.Win32;
using System.Globalization;

namespace EricWorkHub;

internal enum WindowMode { Normal, Maximized, Minimized }
internal sealed record WindowState(WindowBounds Bounds, double ColumnWidth, bool Maximized = false, uint Dpi = 0);

internal sealed class WindowStateTracker(WindowState state)
{
    public WindowState State { get; private set; } = state;

    public void Observe(WindowMode mode, WindowBounds bounds, uint dpi)
    {
        State = mode switch
        {
            WindowMode.Normal when bounds.Width > 0 && bounds.Height > 0 => State with { Bounds = bounds, Maximized = false, Dpi = dpi },
            WindowMode.Maximized => State with { Maximized = true },
            _ => State
        };
    }
}
internal static class WindowStateService
{
    private const string RegistryPath = @"Software\EricSoft\EricWorkHub";
    public static WindowState Load(WindowBounds fallback, IReadOnlyList<WindowBounds> displays)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            var saved = new WindowBounds(Convert.ToInt32(key?.GetValue("WindowX", fallback.X) ?? fallback.X),
                Convert.ToInt32(key?.GetValue("WindowY", fallback.Y) ?? fallback.Y),
                Convert.ToInt32(key?.GetValue("WindowWidth", fallback.Width) ?? fallback.Width),
                Convert.ToInt32(key?.GetValue("WindowHeight", fallback.Height) ?? fallback.Height));
            double column = double.TryParse(key?.GetValue("LeftColumnWidth")?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
                && double.IsFinite(width) ? width : 220;
            bool maximized = Convert.ToInt32(key?.GetValue("WindowMaximized", 0) ?? 0) == 1;
            uint dpi = Convert.ToUInt32(key?.GetValue("WindowDpi", 0) ?? 0);
            return new(saved, column, maximized, dpi);
        }
        catch (Exception ex)
        {
            LogService.Write("Window", "Window bounds restore failed", ex, level: LogService.LogLevel.Warn);
            return new(WindowPlacement.Fit(fallback, displays), 220);
        }
    }

    public static void Save(WindowState state)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue("LeftColumnWidth", state.ColumnWidth.ToString(CultureInfo.InvariantCulture));
            key.SetValue("WindowMaximized", state.Maximized ? 1 : 0);
            key.SetValue("WindowDpi", (int)state.Dpi);
            var saved = state.Bounds;
            key.SetValue("WindowX", saved.X);
            key.SetValue("WindowY", saved.Y);
            key.SetValue("WindowWidth", saved.Width);
            key.SetValue("WindowHeight", saved.Height);
        }
        catch (Exception ex) { LogService.Write("Window", "Window bounds save failed", ex, level: LogService.LogLevel.Warn); }
    }
}
