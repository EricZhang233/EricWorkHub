using System.Runtime.InteropServices;

namespace EricWorkHub;

internal static class ShellProperties
{
    public static string? AppId(string path)
    {
        var key = new PropertyKey(new("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3"), 5);
        Guid iid = typeof(IPropertyStore).GUID;
        IPropertyStore? store = null;
        try
        {
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out store));
            store.GetValue(ref key, out var value);
            try
            {
                if (value.Type == 31) return Marshal.PtrToStringUni(value.Pointer);
                if (value.Type == 8) return Marshal.PtrToStringBSTR(value.Pointer);
                return null;
            }
            finally { PropVariantClear(ref value); }
        }
        finally { if (store != null) Marshal.FinalReleaseComObject(store); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bind, uint flags, ref Guid iid, out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey(Guid format, uint id) { public Guid Format = format; public uint Id = id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
    }
}
