param()
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dependencyRoot = Join-Path $repoRoot 'CUE4Parse'
$revision = '2bcceaf7bf129e4a52bc36d0a174dd3efb5fbfe0'
$expectedHash = 'B93A68CE9E88027DD5C2E9DDF72C9A56112B4E92DF56885DC7C9570CC2A1FA0F'

if (!(Test-Path -LiteralPath (Join-Path $dependencyRoot 'CUE4Parse/CUE4Parse.csproj'))) {
    if ((Test-Path -LiteralPath $dependencyRoot) -and (Get-ChildItem -LiteralPath $dependencyRoot -Force | Measure-Object).Count -gt 0) {
        throw 'CUE4Parse is nonempty but incomplete. Preserve that directory and restore the dependency manually; this script will not overwrite it.'
    }
    [IO.Directory]::CreateDirectory($dependencyRoot) | Out-Null
    $archivePath = Join-Path ([IO.Path]::GetTempPath()) ('fmodel-cue4parse-' + [Guid]::NewGuid().ToString('N') + '.zip')
    try {
        Write-Host "Downloading pinned CUE4Parse revision $revision"
        Invoke-WebRequest -Uri "https://codeload.github.com/FabianFG/CUE4Parse/zip/$revision" -OutFile $archivePath
        if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $expectedHash) { throw 'CUE4Parse archive checksum mismatch.' }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            foreach ($entry in $archive.Entries) {
                $parts = $entry.FullName.Split('/', 2)
                if ($parts.Count -lt 2 -or !$parts[1]) { continue }
                $destination = [IO.Path]::GetFullPath((Join-Path $dependencyRoot $parts[1]))
                if (!$destination.StartsWith($dependencyRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe archive entry.' }
                if ($entry.FullName.EndsWith('/')) { [IO.Directory]::CreateDirectory($destination) | Out-Null }
                else {
                    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $false)
                }
            }
        } finally { $archive.Dispose() }
    } finally {
        if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath }
    }
}

# Three small, idempotent source integrations. Existing unrelated changes are preserved.
function Add-SourceIntegration([string]$RelativePath, [string]$Marker, [string]$Anchor, [string]$Replacement) {
    $path = Join-Path $dependencyRoot $RelativePath
    $content = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    if ($content.Contains($Marker)) { return }
    if (!$content.Contains($Anchor)) { throw "Unsupported CUE4Parse revision: expected source anchor missing in $RelativePath. Tested revision is $revision." }
    [IO.File]::WriteAllText($path, $content.Replace($Anchor, $Replacement), [Text.UTF8Encoding]::new($false))
}
$sessionPath = 'CUE4Parse-Conversion/ExportSession.cs'
$anchor = '    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;'
Add-SourceIntegration $sessionPath 'public Func<string, string>? OutputPathValidator' $anchor ($anchor + "`n`n    // Hosts can constrain every output, including automatically queued dependencies.`n    public Func<string, string>? OutputPathValidator { get; init; }")
$anchor = "        var fullPath = Path.Combine(BaseDirectory.FullName, savePath) + nameSuffix + '.' + ext.ToLower();"
Add-SourceIntegration $sessionPath 'OutputPathValidator?.Invoke(fullPath)' $anchor ($anchor + "`n        fullPath = OutputPathValidator?.Invoke(fullPath) ?? fullPath;")
Add-SourceIntegration 'CUE4Parse/CUE4Parse.csproj' 'Include="Microsoft.Bcl.Memory"' '    <PackageReference Include="Newtonsoft.Json" />' "    <PackageReference Include=`"Newtonsoft.Json`" />`n    <PackageReference Include=`"Microsoft.Bcl.Memory`" />"
Add-SourceIntegration 'Directory.Packages.props' 'Include="Microsoft.Bcl.Memory"' '    <PackageVersion Include="Newtonsoft.Json" Version="13.0.4" />' "    <PackageVersion Include=`"Newtonsoft.Json`" Version=`"13.0.4`" />`n    <PackageVersion Include=`"Microsoft.Bcl.Memory`" Version=`"10.0.10`" />"
Write-Host 'CUE4Parse MCP integrations are ready.'
