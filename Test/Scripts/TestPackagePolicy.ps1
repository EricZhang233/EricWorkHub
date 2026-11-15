$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../../.github/scripts/ReleaseTools.psm1') -Force
$workspace = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'eric/ericworkhub/tests'))
$fixture = Join-Path $workspace ('package-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$required = @('EricWorkHub.exe', 'EricWorkHub.dll', 'YamlDotNet.dll', 'e_sqlite3.dll', 'Microsoft.WindowsAppRuntime.Bootstrap.dll')
function New-PackageFixture([string]$path, [string[]]$names) {
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $names) {
            $writer = [IO.StreamWriter]::new($archive.CreateEntry($name).Open())
            try { $writer.Write('isolated non-executable package fixture') } finally { $writer.Dispose() }
        }
    } finally { $archive.Dispose() }
}
try {
    $valid = Join-Path $fixture 'valid.zip'
    New-PackageFixture $valid $required
    Assert-HubReleasePackage -PackagePath $valid
    $validDirectory = Join-Path $fixture 'valid'
    Expand-Archive -LiteralPath $valid -DestinationPath $validDirectory
    Assert-HubReleasePackage -DirectoryPath $validDirectory
    $checks = 2
    foreach ($bad in @('EricWorkHub.Core.dll', 'EricWorkHub.pdb', 'App.xbf', 'MainWindow.XAML', 'EricWorkHub.deps.json', 'EricWorkHub.runtimeconfig.json', 'resources.PRI', 'ico.ico', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'Microsoft.ui.xaml.dll', 'onnxruntime.dll', 'DirectML.dll', 'Microsoft.Windows.AI.Fixture.dll', 'debug.ini', 'Cache/data.txt', 'session.log', 'publish/stale.dll')) {
        $path = Join-Path $fixture ([Guid]::NewGuid().ToString('N') + '.zip')
        New-PackageFixture $path ($required + $bad)
        $rejected = $false
        try { Assert-HubReleasePackage -PackagePath $path } catch { $rejected = $true }
        if (!$rejected) { throw "Invalid package accepted: $bad" }
        $directory = Join-Path $fixture ([Guid]::NewGuid().ToString('N'))
        Expand-Archive -LiteralPath $path -DestinationPath $directory
        $rejected = $false
        try { Assert-HubReleasePackage -DirectoryPath $directory } catch { $rejected = $true }
        if (!$rejected) { throw "Invalid directory accepted: $bad" }
        $checks += 2
    }
    foreach ($bad in @('../escape.txt', '/absolute.txt', 'C:/absolute.txt', 'ERICWORKHUB.EXE')) {
        $path = Join-Path $fixture ([Guid]::NewGuid().ToString('N') + '.zip')
        New-PackageFixture $path ($required + $bad)
        $rejected = $false
        try { Assert-HubReleasePackage -PackagePath $path } catch { $rejected = $true }
        if (!$rejected) { throw "Invalid archive path accepted: $bad" }
        $checks++
    }
    $missing = Join-Path $fixture 'missing.zip'
    New-PackageFixture $missing @('EricWorkHub.exe')
    $rejected = $false
    try { Assert-HubReleasePackage -PackagePath $missing } catch { $rejected = $true }
    if (!$rejected) { throw 'Incomplete package accepted' }
    $checks++
    Write-Output "PASS $checks isolated release package policy checks"
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith($workspace.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
