param([string]$NuGetAudit = 'true')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $root
try {
    foreach ($script in Get-ChildItem -LiteralPath (Join-Path $root '.github'), (Join-Path $root 'Test') -Recurse -File | Where-Object Extension -in '.ps1', '.psm1') {
        $tokens = $null
        $syntaxErrors = $null
        [void][Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$syntaxErrors)
        if ($syntaxErrors.Count) { throw "PowerShell syntax error: $($script.FullName): $syntaxErrors" }
        if ($tokens.Where({ $_.Kind -eq 'Comment' }).Count) { throw "PowerShell comment found: $($script.FullName)" }
    }
    $rejected = $false
    try { & (Join-Path $root '.github/scripts/Package.ps1') -Tag 'v-invalid-test' -OutputDirectory (Join-Path $root 'artifacts/should-not-exist') }
    catch {
        if ($_.Exception.Message -notmatch 'Formal release packages must be built by GitHub Actions|Release tag must match Version.cs exactly') { throw }
        $rejected = $true
    }
    if (!$rejected) { throw 'Package guard accepted an invalid release request' }
    Write-Output 'PASS PowerShell syntax, no-comment policy and release package guards'
    & (Join-Path $root 'Test/Scripts/TestPackagePolicy.ps1')
    if (!$NuGetAudit) { $NuGetAudit = 'true' }
    dotnet restore Test/Regression.csproj --ignore-failed-sources "-p:NuGetAudit=$NuGetAudit"
    if ($LASTEXITCODE -ne 0) { throw 'Test dependency restore failed' }
    dotnet build EricWorkHub.csproj -c Debug --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Debug build failed' }
    dotnet run --project Test/Regression.csproj -c Debug --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Core regression failed' }
} finally { Pop-Location }
