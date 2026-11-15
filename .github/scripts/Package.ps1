param([Parameter(Mandatory)][string]$Tag, [Parameter(Mandatory)][string]$OutputDirectory, [switch]$Preview, [ValidateSet('true', 'false')][string]$NuGetAudit = 'true')
$ErrorActionPreference = 'Stop'
if (!$Preview -and $env:GITHUB_ACTIONS -ne 'true') { throw 'Formal release packages must be built by GitHub Actions' }
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$source = Get-Content -LiteralPath (Join-Path $root 'Version.cs') -Raw
$version = [regex]::Match($source, 'Version\s*=\s*"(\d+\.\d+\.\d+)"').Groups[1].Value
if (!$version -or $Tag -cne "v$version") { throw 'Release tag must match Version.cs exactly' }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $destination) -and (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1)) { throw 'Package output directory must be empty' }
$staging = Join-Path $destination ('stage-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $staging 'app'
$buildRoot = (Join-Path $staging 'build') + [IO.Path]::DirectorySeparatorChar
New-Item -ItemType Directory -Path $payload -Force | Out-Null
try {
    dotnet build (Join-Path $root 'EricWorkHub.csproj') -c Release -r win-x64 -p:SelfContained=false -p:WindowsAppSDKSelfContained=false "-p:HubOutputRoot=$buildRoot" "-p:NuGetAudit=$NuGetAudit" --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }
    $buildOutput = Join-Path $buildRoot 'EricWorkHub/Release/net10.0-windows10.0.26100/win-x64'
    Get-ChildItem -LiteralPath $buildOutput -Force | Copy-Item -Destination $payload -Recurse
    $assemblyVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'EricWorkHub.dll')).FileVersion
    if ($assemblyVersion -ne "$version.0") { throw 'Published assembly version does not match Version.cs' }
    Assert-HubReleasePackage -DirectoryPath $payload
    $name = "EricWorkHub_$version.zip"
    $archive = Join-Path $destination $name
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    Assert-HubReleasePackage -PackagePath $archive
    dotnet run --project (Join-Path $root 'Test/Regression.csproj') -c Debug --no-restore -- --validate-release $archive $root
    if ($LASTEXITCODE -ne 0) { throw 'Actual release package validation failed' }
    if (!$Preview -and $env:GITHUB_OUTPUT) {
        @("version=$version", "artifact_name=EricWorkHub-$version", "package_name=$name", "package_path=$archive") | Out-File -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    }
    Write-Output "Package: $archive"
} finally {
    $resolved = [IO.Path]::GetFullPath($staging)
    if (!$resolved.StartsWith($destination.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
