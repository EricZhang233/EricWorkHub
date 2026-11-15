function Assert-HubReleasePackage {
    [CmdletBinding(DefaultParameterSetName = 'Archive')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Archive')][string]$PackagePath,
        [Parameter(Mandatory, ParameterSetName = 'Directory')][string]$DirectoryPath
    )
    $names = [Collections.Generic.List[string]]::new()
    if ($PSCmdlet.ParameterSetName -eq 'Archive') {
        $archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PackagePath))
        try {
            foreach ($entry in $archive.Entries) {
                if (!$entry.FullName.EndsWith('/') -and !$entry.Length) { throw "Empty package file: $($entry.FullName)" }
                $names.Add($entry.FullName.Replace('\', '/'))
            }
        } finally { $archive.Dispose() }
    } else {
        $root = [IO.Path]::GetFullPath($DirectoryPath)
        foreach ($entry in Get-ChildItem -LiteralPath $root -Recurse -Force) {
            $relative = [IO.Path]::GetRelativePath($root, $entry.FullName).Replace('\', '/')
            if ($entry.PSIsContainer) { $relative += '/' }
            elseif (!$entry.Length) { throw "Empty package file: $relative" }
            $names.Add($relative)
        }
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $names) {
        if ($name -match '(^/|^[a-zA-Z]:|(^|/)\.\.(/|$))') { throw "Unsafe package path: $name" }
        if (!$seen.Add($name)) { throw "Duplicate package path: $name" }
        if ($name -match '(^|/)(LICENSE|README)(\.[^/]+)?$') { throw "Repository documentation is not part of the package: $name" }
        if ($name -match '\.(pdb|xbf|xaml|json|pri)$|(^|/)ico\.ico$') { throw "Loose resource, metadata or debug file: $name" }
        if ($name -match '(^|/)(publish|Data|Cache)(/|$)|\.(log|bak)$|(^|/)(debug\.ini|settings\.yaml|items\.yaml|storage\.mode)$') { throw "Temporary or private package content: $name" }
        if ($name -match '(^|/)(EricWorkHub\.Core|coreclr|hostfxr|hostpolicy|Microsoft\.UI\.Xaml|onnxruntime|DirectML)\.dll$|(^|/)Microsoft\.Windows\.AI\.') { throw "Bundled runtime or unused SDK component: $name" }
    }
    foreach ($required in @('EricWorkHub.exe', 'EricWorkHub.dll', 'YamlDotNet.dll', 'e_sqlite3.dll', 'Microsoft.WindowsAppRuntime.Bootstrap.dll')) {
        if (!$seen.Contains($required)) { throw "Missing package file: $required" }
    }
}

Export-ModuleMember -Function Assert-HubReleasePackage
