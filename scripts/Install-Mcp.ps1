#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$PackagePath = $PSScriptRoot,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'FModelMcp'),
    [string[]]$InputRoots,
    [string]$OutputRoot,
    [string]$CodexPath,
    [switch]$SkipRegistration
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne 'Win32NT' -or ![Environment]::Is64BitOperatingSystem) {
    throw 'This release requires Windows x64.'
}
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
if ($InstallRoot.TrimEnd('\', '/') -eq $PackagePath.TrimEnd('\', '/') -or
    $InstallRoot.StartsWith($PackagePath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'InstallRoot must be outside the extracted PackagePath to avoid recursive copying.'
}
if (!(Test-Path -LiteralPath (Join-Path $PackagePath 'FModel.Mcp.exe'))) {
    throw 'Run the installer from an extracted release, or pass -PackagePath pointing to the complete published directory.'
}
if (!$SkipRegistration) {
    if (!$CodexPath) {
        $cli = Get-Command codex -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cli) { $CodexPath = $cli.Source }
    }
    if (!$CodexPath) {
        $bundledRoot = Join-Path $env:LOCALAPPDATA 'OpenAI/Codex/bin'
        $cli = Get-ChildItem -Path (Join-Path $bundledRoot '*/codex.exe') -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($cli) { $CodexPath = $cli.FullName }
    }
    if (!$CodexPath -or !(Test-Path -LiteralPath $CodexPath -PathType Leaf)) {
        throw 'Codex CLI was not found. Open the desktop app once, pass -CodexPath, or use -SkipRegistration and the generated client configuration.'
    }
}
$configPath = Join-Path $InstallRoot 'config.json'
$existingConfig = Test-Path -LiteralPath $configPath
if ($existingConfig) { $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json }
else {
    $config = [pscustomobject]@{
        inputRoots = @((Join-Path $InstallRoot 'Inputs'))
        outputRoot = (Join-Path $InstallRoot 'Exports')
        oodleLibrary = $null
        maxSessions = 4
        maxJobs = 32
        maxBatchAssets = 500
        maxReadBytes = 268435456
        maxResponseChars = 100000
    }
}
if ($PSBoundParameters.ContainsKey('InputRoots')) {
    if (!$InputRoots -or $InputRoots.Count -eq 0) { throw 'InputRoots cannot be empty.' }
    $resolvedRoots = @($InputRoots | ForEach-Object {
        if (![IO.Path]::IsPathRooted($_)) { throw "Input root must be absolute: $_" }
        $root = [IO.Path]::GetFullPath($_)
        if (!(Test-Path -LiteralPath $root -PathType Container)) { throw "Input directory does not exist: $root" }
        $root
    })
    $config.inputRoots = $resolvedRoots
}
if ($PSBoundParameters.ContainsKey('OutputRoot')) {
    if (!$OutputRoot -or ![IO.Path]::IsPathRooted($OutputRoot)) { throw 'OutputRoot must be an absolute directory path.' }
    $config.outputRoot = [IO.Path]::GetFullPath($OutputRoot)
}
if (!(Test-Path -LiteralPath $InstallRoot)) { New-Item -ItemType Directory -Path $InstallRoot | Out-Null }
if (!$existingConfig -and !$PSBoundParameters.ContainsKey('InputRoots')) {
    New-Item -ItemType Directory -Path $config.inputRoots[0] -Force | Out-Null
}
# Each installation gets a new directory so a running server cannot leave an update half-copied.
$version = (Get-Item -LiteralPath (Join-Path $PackagePath 'FModel.Mcp.exe')).VersionInfo.ProductVersion.Split('+')[0]
if ($version -notmatch '^\d+\.\d+\.\d+([-.][A-Za-z0-9.]+)?$') { throw 'The release executable has an invalid version.' }
$installId = $version + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$destination = Join-Path $InstallRoot ('versions/' + $installId)
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Get-ChildItem -LiteralPath $PackagePath -Force | Copy-Item -Destination $destination -Recurse
$executable = Join-Path $destination 'FModel.Mcp.exe'
# Validate the copied package before changing the user's client registration.
& (Join-Path $destination 'Smoke-Mcp.ps1') -ServerPath $executable
if ($existingConfig -and ($PSBoundParameters.ContainsKey('InputRoots') -or $PSBoundParameters.ContainsKey('OutputRoot'))) {
    Copy-Item -LiteralPath $configPath -Destination ($configPath + '.' + $installId + '.bak')
}
if (!$existingConfig -or $PSBoundParameters.ContainsKey('InputRoots') -or $PSBoundParameters.ContainsKey('OutputRoot')) {
    [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
}
& (Join-Path $destination 'Smoke-Mcp.ps1') -ServerPath $executable -ConfigPath $configPath
$client = @{ mcpServers = @{ fmodel = @{ command = $executable; args = @('--config', $configPath) } } }
[IO.File]::WriteAllText((Join-Path $InstallRoot 'mcp-client.json'), ($client | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
if (!$SkipRegistration) {
    & $CodexPath mcp add fmodel -- $executable --config $configPath
    if ($LASTEXITCODE -ne 0) { throw 'Files were installed, but Codex registration failed. Use mcp-client.json for manual registration.' }
    & $CodexPath mcp get fmodel --json
    if ($LASTEXITCODE -ne 0) { throw 'Codex registration could not be verified.' }
}
Write-Host "Installed FModel MCP $version at $destination"
Write-Host "Game folders and export settings: $configPath"
Write-Host 'Restart the fmodel server in Settings > MCP servers, or restart the desktop app, to load its tools.'
Write-Host 'To uninstall the registration: codex mcp remove fmodel. Installed files and exports are retained.'
