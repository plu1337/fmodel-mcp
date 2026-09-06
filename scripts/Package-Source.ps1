param()
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'FModel.Mcp/FModel.Mcp.csproj') -Raw)).Project.PropertyGroup.Version
$dependencyRoot = Join-Path $repoRoot 'CUE4Parse'
if (!(Test-Path -LiteralPath (Join-Path $dependencyRoot 'CUE4Parse/CUE4Parse.csproj'))) { throw 'Initialize CUE4Parse before bundling source.' }
$files = @(& git -C $repoRoot ls-files)
if ($LASTEXITCODE -ne 0 -or !$files) { throw 'The source bundle requires a Git checkout with tracked source files.' }
$files += @(Get-ChildItem -LiteralPath $dependencyRoot -Recurse -File -Force |
    Where-Object { $_.FullName.Substring($dependencyRoot.Length) -notmatch '[\\/](bin|obj|builddir|\.git|\.vs)[\\/]' } |
    ForEach-Object { $_.FullName.Substring($repoRoot.Length + 1).Replace('\', '/') })
$zipPath = Join-Path $repoRoot "artifacts/fmodel-mcp-$version-source.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in ($files | Sort-Object -Unique)) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $repoRoot $file), $file, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($zipPath + '.sha256'), "$hash  $([IO.Path]::GetFileName($zipPath))`n", [Text.UTF8Encoding]::new($false))
Write-Host "Source bundle: $zipPath ($($files.Count) files)"
