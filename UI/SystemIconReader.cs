using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace EricWorkHub;

internal static class SystemIconReader
{
    public static byte[]? ForApplication(DetectedApp app) => WithCom(() =>
    {
        if (!app.Definition.PreferPackagedVersion && AppAvailability.ExecutableExists(app.Executable)
            && (ReadImage(app.Executable!) ?? ReadFileIcon(app.Executable!, false)) is { } classic)
            return Selected(app.Definition.Key, "executable", classic);
        if (app.AppUserModelId is { Length: > 0 } id && ReadImage("shell:AppsFolder\\" + id) is { } packaged)
            return Selected(app.Definition.Key, "package", packaged);
        if (!AppAvailability.ExecutableExists(app.Executable))
        {
            LogService.Write("Icon", $"Application icon has no usable source app={app.Definition.Key}", level: LogService.LogLevel.Debug);
            return null;
        }
        var fallback = ReadImage(app.Executable!) ?? ReadFileIcon(app.Executable!, false);
        return fallback == null ? null : Selected(app.Definition.Key, "executable-fallback", fallback);
    });

    private static byte[] Selected(string app, string source, byte[] bytes)
    {
        LogService.Write("Icon", $"Application icon loaded app={app} source={source} bytes={bytes.Length}", level: LogService.LogLevel.Debug);
        return bytes;
    }

    public static byte[]? ForExtension(string extension) => string.IsNullOrEmpty(extension) ? null
        : WithCom(() => ReadFileIcon(extension, true));

    private static byte[]? WithCom(Func<byte[]?> read)
    {
        int initialized = CoInitializeEx(IntPtr.Zero, 0);
        try
        {
            if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
            return read();
        }
        catch (Exception ex) { LogService.Write("Icon", "System icon unavailable", ex, level: LogService.LogLevel.Debug); return null; }
        finally { if (initialized >= 0) CoUninitialize(); }
    }

    private static byte[]? ReadImage(string target)
    {
        IShellItemImageFactory? factory = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            Guid id = typeof(IShellItemImageFactory).GUID;
            int result = SHCreateItemFromParsingName(target, IntPtr.Zero, ref id, out factory);
            if (result < 0 || factory == null)
            {
                LogService.Write("Icon", $"Shell image source lookup failed hr=0x{result:X8}", level: LogService.LogLevel.Debug);
                return null;
            }
            result = factory.GetImage(new(64, 64), 0x4, out bitmap);
            if (result < 0 || bitmap == IntPtr.Zero)
            {
                LogService.Write("Icon", $"Shell image extraction failed hr=0x{result:X8}", level: LogService.LogLevel.Debug);
                return null;
            }
            using var image = CopyBitmap(bitmap);
            return Encode(image);
        }
        catch (Exception ex) { LogService.Write("Icon", "Shell application image unavailable", ex, level: LogService.LogLevel.Debug); return null; }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (factory != null && Marshal.IsComObject(factory)) Marshal.ReleaseComObject(factory);
        }
    }

    private static Bitmap CopyBitmap(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out var info) != 0 && info.BitsPixel == 32 && info.Bits != IntPtr.Zero)
        {
            using var source = new Bitmap(info.Width, info.Height, info.WidthBytes, PixelFormat.Format32bppArgb, info.Bits);
            var copy = new Bitmap(source);
            copy.RotateFlip(RotateFlipType.RotateNoneFlipY);
            return copy;
        }
        return Bitmap.FromHbitmap(bitmap);
    }

    private static byte[]? ReadFileIcon(string target, bool fileType)
    {
        var info = new ShellFileInfo();
        try
        {
            if (SHGetFileInfo(target, fileType ? 0x80u : 0u, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100u | (fileType ? 0x10u : 0u)) == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
            using var icon = Icon.FromHandle(info.Icon);
            using var bitmap = icon.ToBitmap();
            return Encode(bitmap);
        }
        finally { if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon); }
    }

    private static byte[] Encode(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativeSize(int Width, int Height);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string name, IntPtr bindingContext, ref Guid id, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr value, int size, out NativeBitmap bitmap);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}
