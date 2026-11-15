namespace EricWorkHub;

internal sealed record OfficeAppDefinition(string Key, string Name, string[] ExeNames, string[] ExtraPaths,
    string[] PackageNames, bool HasRecentDocuments = false, bool IsOfficeManager = false)
{
    public string Description => Text.T("AppDescription_" + Key);
    public string ToolTipText => IsOfficeManager ? Text.T("OfficeManagerTooltip") : Text.F("AppDescriptionTooltip", Name, Description);
    public bool PreferPackagedVersion => Key is "Notepad" or "Paint";
}
internal sealed record DetectedApp(OfficeAppDefinition Definition, string? Executable, string? AppUserModelId, string[]? RecentAppIds = null)
{
    public bool IsInstalled => AppAvailability.ExecutableExists(Executable) || !string.IsNullOrWhiteSpace(AppUserModelId);
}

internal static class AppCatalog
{
    public static readonly DetectedApp OfficeManager = new(new("OfficeManager", "Office Setup", [], [], [], IsOfficeManager: true), null, null);
    public static readonly string[] OfficeVersions = ["16.0", "15.0", "14.0"];
    private static string Local(params string[] parts) => Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), .. parts]);
    private static string SystemFile(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);
    private static string WordPadPath(Environment.SpecialFolder folder) => Path.Combine(Environment.GetFolderPath(folder), "Windows NT", "Accessories", "wordpad.exe");
    public static readonly OfficeAppDefinition[] Definitions =
    [
        new("Notepad", "Notepad", ["NOTEPAD.EXE"], [SystemFile("notepad.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe")], ["Microsoft.WindowsNotepad"]),
        new("Paint", "Paint", ["MSPAINT.EXE"], [SystemFile("mspaint.exe")], ["Microsoft.Paint"]),
        new("WordPad", "WordPad", ["WORDPAD.EXE"], [WordPadPath(Environment.SpecialFolder.ProgramFiles), WordPadPath(Environment.SpecialFolder.ProgramFilesX86)], []),
        new("Word", "Word", ["WINWORD.EXE"], [], [], true),
        new("Excel", "Excel", ["EXCEL.EXE"], [], [], true),
        new("PowerPoint", "PowerPoint", ["POWERPNT.EXE"], [], [], true),
        new("Outlook", "Outlook", ["OUTLOOK.EXE"], [], ["Microsoft.OutlookForWindows"], true),
        new("OneNote", "OneNote", ["ONENOTE.EXE"], [], ["Microsoft.Office.OneNote"], true),
        new("Access", "Access", ["MSACCESS.EXE"], [], [], true),
        new("Publisher", "Publisher", ["MSPUB.EXE"], [], [], true),
        new("Teams", "Teams", ["TEAMS.EXE", "MS-TEAMS.EXE"], [Local("Microsoft", "Teams", "current", "Teams.exe")], ["MSTeams", "MicrosoftTeams"]),
        new("OneDrive", "OneDrive", ["ONEDRIVE.EXE"],
            [Local("Microsoft", "OneDrive", "OneDrive.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive", "OneDrive.exe"),
             Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft OneDrive", "OneDrive.exe")], []),
        new("Copilot", "Microsoft 365 Copilot", ["M365COPILOT.EXE", "COPILOTOFFICE.EXE"], [], ["Microsoft.MicrosoftOfficeHub"]),
        new("Clipchamp", "Clipchamp", ["CLIPCHAMP.EXE"], [], ["Clipchamp.Clipchamp"]),
        new("Project", "Project", ["WINPROJ.EXE"], [], [], true),
        new("Visio", "Visio", ["VISIO.EXE"], [], [], true),
        new("Skype", "Skype for Business", ["LYNC.EXE"], [], [])
    ];

    public static string? FromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" => "Notepad",
        ".doc" or ".docx" or ".docm" or ".dot" or ".dotx" or ".dotm" or ".rtf" or ".odt" or ".docb" => "Word",
        ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".xlt" or ".xltx" or ".xltm" or ".csv" or ".tsv" or ".ods" => "Excel",
        ".ppt" or ".pptx" or ".pptm" or ".pps" or ".ppsx" or ".ppsm" or ".pot" or ".potx" or ".potm" or ".odp" => "PowerPoint",
        ".vsd" or ".vsdx" or ".vsdm" or ".vdx" or ".vss" or ".vssx" or ".vssm" or ".vst" or ".vstx" or ".vstm" => "Visio",
        ".pub" => "Publisher",
        ".mpp" or ".mpt" => "Project",
        ".one" or ".onetoc2" or ".onepkg" => "OneNote",
        ".accdb" or ".mdb" or ".accde" or ".accdr" or ".accdt" or ".mde" or ".adp" or ".ade" => "Access",
        ".msg" or ".eml" or ".oft" or ".ics" or ".vcf" => "Outlook",
        ".clipchamp" => "Clipchamp",
        ".pdf" => "PDF",
        _ => null
    };

    public static bool IsDocument(string target, string? owner = null)
    {
        if (!DocumentLocation.TryParse(target, out _, out _)) return false;
        if (target.StartsWith("onenote:", StringComparison.OrdinalIgnoreCase)) return true;
        string extension = DocumentLocation.Extension(target);
        if (FromExtension(extension) != null) return true;
        return owner switch
        {
            "Paint" => extension is ".png" or ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".bmp" or ".dib" or ".gif" or ".tif" or ".tiff",
            "Clipchamp" => extension is ".mp4" or ".mov" or ".webm" or ".mkv" or ".gif" or ".mp3" or ".wav" or ".m4a",
            _ => false
        };
    }
}
