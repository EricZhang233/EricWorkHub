using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EricWorkHub;

public static class AppIconService
{
    private const string ResourceName = "EricWorkHub.ico.ico";
    private static readonly Lazy<byte[]> DefaultPng = new(CreateDefaultPng);

    internal static Stream OpenDefaultIconStream()
        => Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName) ?? throw new MissingManifestResourceException(ResourceName);

    public static IntPtr GetDefaultIconHandle()
    {
        try
        {
            using var stream = OpenDefaultIconStream();
            using var icon = new Icon(stream, 256, 256);
            IntPtr handle = CopyIcon(icon.Handle);
            if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            LogService.Write("App", "AppIconService loaded default HICON from embedded resource");
            return handle;
        }
        catch (Exception ex) { LogService.Write("App", "AppIconService GetDefaultIconHandle failed", ex); return IntPtr.Zero; }
    }

    private static byte[] CreatePng(Icon icon)
    {
        using var bitmap = icon.ToBitmap();
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static byte[] CreateDefaultPng()
    {
        using var stream = OpenDefaultIconStream();
        using var icon = new Icon(stream, 256, 256);
        LogService.Write("App", "AppIconService decoded embedded default icon in memory");
        return CreatePng(icon);
    }

    private static BitmapImage CreateBitmapImage(byte[] png)
    {
        using var stream = new MemoryStream(png, false);
        var image = new BitmapImage();
        image.SetSource(stream.AsRandomAccessStream());
        return image;
    }

    public static BitmapImage? GetBitmapImageFromHicon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            using var icon = Icon.FromHandle(hIcon);
            return CreateBitmapImage(CreatePng(icon));
        }
        catch (Exception ex) { LogService.Write("App", "AppIconService GetBitmapImageFromHicon failed", ex); return null; }
    }

    public static BitmapImage? GetBitmapImage()
    {
        try { return CreateBitmapImage(DefaultPng.Value); }
        catch (Exception ex) { LogService.Write("App", "AppIconService GetBitmapImage failed", ex); return null; }
    }

    public static async Task<BitmapImage?> LoadBitmapImageAsync()
    {
        try
        {
            using var stream = new MemoryStream(DefaultPng.Value, false);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream.AsRandomAccessStream());
            return image;
        }
        catch (Exception ex) { LogService.Write("App", "AppIconService LoadBitmapImageAsync failed", ex); return null; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
