param()
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path $repoRoot ('artifacts/installer-test-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $repoRoot 'artifacts/fmodel-mcp'
$installer = Join-Path $package 'Install-Mcp.ps1'
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    # Exercise the stock Windows PowerShell host, which end users can run without installing PowerShell 7.
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -PackagePath $package -InstallRoot $testRoot -SkipRegistration
    if ($LASTEXITCODE -ne 0) { throw 'Fresh installation failed.' }
    $configPath = Join-Path $testRoot 'config.json'
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    if (!(Test-Path -LiteralPath $config.inputRoots[0] -PathType Container)) { throw 'Default input folder was not created.' }
    $config.maxSessions = 2
    $config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding utf8
    $before = [IO.File]::ReadAllBytes($configPath)
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -PackagePath $package -InstallRoot $testRoot -SkipRegistration
    if ($LASTEXITCODE -ne 0) { throw 'Update installation failed.' }
    if ([Convert]::ToBase64String($before) -ne [Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath))) { throw 'Update changed existing settings.' }
    $client = Get-Content -LiteralPath (Join-Path $testRoot 'mcp-client.json') -Raw | ConvertFrom-Json
    if (!(Test-Path -LiteralPath $client.mcpServers.fmodel.command)) { throw 'Generated MCP client configuration has no executable.' }
    if ((Get-ChildItem -LiteralPath (Join-Path $testRoot 'versions') -Directory).Count -ne 2) { throw 'Update did not use a separate version directory.' }
    Write-Host 'Installer checks passed: Windows PowerShell 5.1, fresh install, configuration preservation, side-by-side update and generated client configuration.'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $allowed = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing unsafe test cleanup.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
