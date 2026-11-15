using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace EricWorkHub;

internal static class MicrosoftExecutableTrust
{
    public static bool IsOfficeExecutable(string path)
    {
        if (!AppAvailability.ExecutableExists(path)) return false;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            if (!string.Equals(version.CompanyName?.Trim(), "Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                || !(version.ProductName?.Contains("Office", StringComparison.OrdinalIgnoreCase) ?? false)) return false;
            return IsMicrosoftSigned(path);
        }
        catch (Exception ex) { LogService.Write("OfficeManager", "Office executable identity unavailable", ex, level: LogService.LogLevel.Debug); return false; }
    }

    public static bool IsMicrosoftSigned(string path)
    {
        if (!AppAvailability.ExecutableExists(path)) return false;
        IntPtr infoPointer = IntPtr.Zero;
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        try
        {
            infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
            Marshal.StructureToPtr(file, infoPointer, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1,
                FileInfo = infoPointer, StateAction = 1, ProviderFlags = 0x1010
            };
            Guid action = new("00aac56b-cd44-11d0-8cc2-00c04fc295ee");
            try
            {
                int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                if (result != 0)
                {
                    LogService.Write("OfficeManager", $"Executable signature verification failed hr=0x{result:X8}", level: LogService.LogLevel.Debug);
                    return false;
                }
                IntPtr provider = WTHelperProvDataFromStateData(data.StateData);
                if (provider == IntPtr.Zero) return false;
                IntPtr signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
                if (signer == IntPtr.Zero) return false;
                IntPtr certificate = WTHelperGetProvCertFromChain(signer, 0);
                if (certificate == IntPtr.Zero) return false;
                var header = Marshal.PtrToStructure<ProviderCertificate>(certificate);
                if (header.Context == IntPtr.Zero) return false;
                using var leaf = new X509Certificate2(header.Context);
                return leaf.Subject.Split(',').Any(part => part.Trim().Equals("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase));
            }
            finally { data.StateAction = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data); }
        }
        catch (Exception ex) { LogService.Write("OfficeManager", "Executable signature unavailable", ex, level: LogService.LogLevel.Debug); return false; }
        finally
        {
            if (infoPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<TrustFile>(infoPointer);
                Marshal.FreeHGlobal(infoPointer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr FileHandle, KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallback, SipClient;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderCertificate { public uint Size; public IntPtr Context; }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counter);
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificate);
}
