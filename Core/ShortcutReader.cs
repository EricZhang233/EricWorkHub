using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace EricWorkHub;

internal static class ShortcutReader
{
    public static string? Read(string path)
    {
        try
        {
            if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return File.ReadLines(path, Encoding.GetEncoding(0)).Select(line => line.Trim())
                    .FirstOrDefault(line => line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..].Trim();
            }
            object? instance = null;
            try
            {
                instance = new ShellLink();
                ((IPersistFile)instance).Load(path, 0);
                var builder = new StringBuilder(32768);
                ((IShellLinkW)instance).GetPath(builder, builder.Capacity, out _, 4);
                return builder.Length > 0 ? Environment.ExpandEnvironmentVariables(builder.ToString()) : null;
            }
            finally { if (instance != null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance); }
        }
        catch (Exception ex)
        {
            LogService.Write("Recent", "Shortcut skipped", ex, level: LogService.LogLevel.Debug);
            return null;
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int count, out FindData data, uint flags);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint SizeHigh, SizeLow, Reserved0, Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateName;
    }
}
