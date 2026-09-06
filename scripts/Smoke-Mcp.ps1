param([string]$ServerPath, [string]$ConfigPath)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$ServerPath) { $ServerPath = Join-Path $repoRoot 'artifacts/fmodel-mcp/FModel.Mcp.exe' }
$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = [IO.Path]::GetFullPath($ServerPath)
if ($ConfigPath) { $info.Arguments = '--config "' + [IO.Path]::GetFullPath($ConfigPath) + '"' }
$info.WorkingDirectory = $repoRoot
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($info)
$errors = $process.StandardError.ReadToEndAsync()
function Send-Rpc([hashtable]$Message) {
    $process.StandardInput.WriteLine(($Message | ConvertTo-Json -Depth 30 -Compress))
    $process.StandardInput.Flush()
    if (!$Message.ContainsKey('id')) { return }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        $line = $process.StandardOutput.ReadLineAsync()
        if (!$line.Wait(15000)) { throw "Timed out awaiting MCP response to $($Message.method)." }
        if ($null -eq $line.Result) { throw "MCP stdout closed unexpectedly: $($errors.GetAwaiter().GetResult())" }
        $response = $line.Result | ConvertFrom-Json
        if ($response.id -eq $Message.id) {
            if ($response.error) { throw ($response.error | ConvertTo-Json -Compress) }
            return $response.result
        }
    }
    throw 'No matching MCP response.'
}
try {
    $initialized = Send-Rpc @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'fmodel-smoke'; version = '1.0' } } }
    Send-Rpc @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
    $tools = Send-Rpc @{ jsonrpc = '2.0'; id = 2; method = 'tools/list'; params = @{} }
    if ($tools.tools.Count -ne 32) { throw "Expected 32 tools; found $($tools.tools.Count)." }
    $capabilities = Send-Rpc @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'fmodel_capabilities'; arguments = @{} } }
    if ($capabilities.isError) { throw 'Capabilities call failed.' }
    $resources = Send-Rpc @{ jsonrpc = '2.0'; id = 4; method = 'resources/list'; params = @{} }
    $prompts = Send-Rpc @{ jsonrpc = '2.0'; id = 5; method = 'prompts/list'; params = @{} }
    if ($resources.resources.Count -lt 2 -or $prompts.prompts.Count -ne 4) { throw 'Missing resources or prompts.' }
    $process.StandardInput.Close()
    if (!$process.WaitForExit(10000)) { throw 'MCP process did not exit after stdin closed.' }
    if ($process.ExitCode -ne 0) { throw "MCP exited with code $($process.ExitCode): $($errors.GetAwaiter().GetResult())" }
    Write-Host "MCP smoke passed: $($initialized.serverInfo.name), protocol $($initialized.protocolVersion), 32 tools, 4 prompts, resources, clean stdout and graceful EOF."
} finally {
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $process.Dispose()
}
