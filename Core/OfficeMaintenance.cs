using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace EricWorkHub;

internal static class OfficeMaintenance
{
    public static ProcessStartInfo FindCommand()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (string name in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var entry = uninstall!.OpenSubKey(name);
                    string display = entry?.GetValue("DisplayName") as string ?? "";
                    if (!display.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                        || !(display.Contains("Office", StringComparison.OrdinalIgnoreCase) || display.Contains("365", StringComparison.OrdinalIgnoreCase))) continue;
                    if (entry?.GetValue("ModifyPath") is not string modify) continue;
                    if (TryCreateCommand(modify, out var command))
                    {
                        LogService.Write("OfficeManager", "Official Office maintenance command found");
                        return command!;
                    }
                }
            }
            catch (Exception ex) { LogService.Write("OfficeManager", "Office maintenance registration unavailable", ex, level: LogService.LogLevel.Debug); }
        }
        LogService.Write("OfficeManager", "Office maintenance registration missing; opening Windows installed apps");
        return new("ms-settings:appsfeatures") { UseShellExecute = true };
    }

    internal static bool TryCreateCommand(string command, out ProcessStartInfo? result, Func<string, bool>? trust = null)
    {
        result = null;
        string[] parts = Split(command);
        if (parts.Length < 2 || !Path.IsPathFullyQualified(parts[0])) return false;
        string name = Path.GetFileName(parts[0]);
        if (name.Equals("OfficeClickToRun.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (!parts.Skip(1).Any(part => part.Equals("scenario=repair", StringComparison.OrdinalIgnoreCase))) return false;
            foreach (string argument in parts.Skip(1))
                if (!Regex.IsMatch(argument, @"^(?:scenario=repair|(?:platform|system)=(?:x86|x64|arm64)|culture=[a-zA-Z-]+|displaylevel=true)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
            if (!(trust ?? MicrosoftExecutableTrust.IsOfficeExecutable)(parts[0])) return false;
        }
        else if (name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            string arguments = string.Concat(parts.Skip(1));
            if (!Regex.IsMatch(arguments, @"^/I\{[0-9A-Fa-f-]{36}\}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || !(trust ?? MicrosoftExecutableTrust.IsMicrosoftSigned)(parts[0])) return false;
        }
        else return false;
        result = new(parts[0]) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(parts[0])! };
        foreach (string argument in parts.Skip(1)) result.ArgumentList.Add(argument);
        return true;
    }

    private static string[] Split(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return [];
        IntPtr arguments = CommandLineToArgvW(command, out int count);
        if (arguments == IntPtr.Zero) return [];
        try
        {
            var result = new string[count];
            for (int index = 0; index < count; index++) result[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, index * IntPtr.Size)) ?? "";
            return result;
        }
        finally { LocalFree(arguments); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
