param([switch]$SkipTests, [string]$PackagePath)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $PSScriptRoot 'Initialize-Mcp.ps1')
$testProject = Join-Path $repoRoot 'FModel.Mcp.Tests/FModel.Mcp.Tests.csproj'
$serverProject = Join-Path $repoRoot 'FModel.Mcp/FModel.Mcp.csproj'
if (!$PackagePath) { $PackagePath = Join-Path $repoRoot '.packages' }
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
& dotnet restore $testProject --packages $packagePath -p:CUE4PARSE_SKIP_NATIVE=true
if ($LASTEXITCODE -ne 0) { throw 'MCP dependency restore failed.' }
if (!$SkipTests) {
    & dotnet test $testProject --no-restore -c Release -m:1 -nr:false -p:CUE4PARSE_SKIP_NATIVE=true -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw 'MCP tests failed.' }
}
$outputPath = Join-Path $repoRoot 'artifacts/fmodel-mcp'
if (Test-Path -LiteralPath $outputPath) {
    $resolved = [IO.Path]::GetFullPath($outputPath)
    $allowed = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing unsafe publish-directory cleanup.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
& dotnet publish $serverProject -c Release -r win-x64 --self-contained true -o $outputPath -m:1 -nr:false -p:CUE4PARSE_SKIP_NATIVE=true -p:DebugType=None -p:DebugSymbols=false "-p:RestorePackagesPath=$PackagePath" -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'MCP publish failed.' }
& (Join-Path $PSScriptRoot 'Smoke-Mcp.ps1') -ServerPath (Join-Path $outputPath 'FModel.Mcp.exe')
foreach ($name in @('Install-Mcp.ps1','Smoke-Mcp.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $outputPath }
foreach ($name in @('README.md','LICENSE','NOTICE','CHANGELOG.md')) { Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $outputPath }
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $outputPath -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'examples') -Destination $outputPath -Recurse -Force
& (Join-Path $PSScriptRoot 'Write-Notices.ps1') -PublishPath $outputPath -PackagePath $PackagePath
$version = ([xml](Get-Content -LiteralPath $serverProject -Raw)).Project.PropertyGroup.Version
$zip = Join-Path $repoRoot "artifacts/fmodel-mcp-$version-win-x64.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($outputPath, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($zip + '.sha256'), "$hash  $([IO.Path]::GetFileName($zip))`n", [Text.UTF8Encoding]::new($false))
Write-Host "Built MCP server: $outputPath/FModel.Mcp.exe"
Write-Host "Release archive: $zip"
