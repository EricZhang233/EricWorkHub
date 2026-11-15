namespace EricWorkHub;

internal readonly record struct WindowBounds(int X, int Y, int Width, int Height);
internal static class WindowPlacement
{
    internal static WindowBounds Restore(WindowBounds bounds, uint savedDpi, uint currentDpi, IReadOnlyList<WindowBounds> displays) =>
        Fit(Scale(bounds, savedDpi, currentDpi), displays);

    internal static WindowBounds Scale(WindowBounds bounds, uint savedDpi, uint currentDpi) => savedDpi == 0 || currentDpi == 0 || savedDpi == currentDpi
        ? bounds : bounds with
        {
            Width = (int)Math.Clamp(Math.Round((double)bounds.Width * currentDpi / savedDpi), 1, int.MaxValue),
            Height = (int)Math.Clamp(Math.Round((double)bounds.Height * currentDpi / savedDpi), 1, int.MaxValue)
        };

    internal static WindowBounds Fit(WindowBounds saved, IReadOnlyList<WindowBounds> displays)
    {
        var work = displays.OrderByDescending(display => Intersection(saved, display)).First();
        int width = Math.Clamp(saved.Width, Math.Min(640, work.Width), work.Width);
        int height = Math.Clamp(saved.Height, Math.Min(420, work.Height), work.Height);
        return new(Math.Clamp(saved.X, work.X, work.X + work.Width - width),
            Math.Clamp(saved.Y, work.Y, work.Y + work.Height - height), width, height);
    }
    private static long Intersection(WindowBounds a, WindowBounds b) =>
        (long)Math.Max(0, Math.Min((long)a.X + a.Width, (long)b.X + b.Width) - Math.Max(a.X, b.X))
        * Math.Max(0, Math.Min((long)a.Y + a.Height, (long)b.Y + b.Height) - Math.Max(a.Y, b.Y));
}
