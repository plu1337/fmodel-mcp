param([string]$ServerPath, [string]$ConfigPath, [switch]$OpenSavedGame, [int]$ResponseTimeoutSeconds = 180)
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
    $deadline = [DateTime]::UtcNow.AddSeconds($ResponseTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $line = $process.StandardOutput.ReadLineAsync()
        if (!$line.Wait($ResponseTimeoutSeconds * 1000)) { throw "Timed out awaiting MCP response to $($Message.method)." }
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
    if ($tools.tools.Count -ne 34) { throw "Expected 34 tools; found $($tools.tools.Count)." }
    $capabilities = Send-Rpc @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'fmodel_capabilities'; arguments = @{} } }
    if ($capabilities.isError) { throw 'Capabilities call failed.' }
    $resources = Send-Rpc @{ jsonrpc = '2.0'; id = 4; method = 'resources/list'; params = @{} }
    $prompts = Send-Rpc @{ jsonrpc = '2.0'; id = 5; method = 'prompts/list'; params = @{} }
    if ($resources.resources.Count -lt 2 -or $prompts.prompts.Count -ne 4) { throw 'Missing resources or prompts.' }
    if ($OpenSavedGame) {
        $saved = Send-Rpc @{ jsonrpc = '2.0'; id = 6; method = 'tools/call'; params = @{ name = 'fmodel_list_saved_games'; arguments = @{} } }
        if ($saved.isError) { throw ($saved.content[0].text) }
        Write-Host $saved.content[0].text
        $opened = Send-Rpc @{ jsonrpc = '2.0'; id = 7; method = 'tools/call'; params = @{ name = 'fmodel_open_saved_game'; arguments = @{} } }
        if ($opened.isError) { throw ($opened.content[0].text) }
        $session = $opened.content[0].text | ConvertFrom-Json
        Write-Host $opened.content[0].text
        if ($session.fileCount -lt 1) { throw 'Saved game mounted no files.' }
        $search = Send-Rpc @{ jsonrpc = '2.0'; id = 8; method = 'tools/call'; params = @{ name = 'fmodel_search_assets'; arguments = @{ sessionId = $session.sessionId; query = 'DefaultGame.ini'; limit = 1 } } }
        if ($search.isError) { throw ($search.content[0].text) }
        Write-Host $search.content[0].text
        $found = $search.content[0].text | ConvertFrom-Json
        if ($found.items.Count -gt 0) {
            $read = Send-Rpc @{ jsonrpc = '2.0'; id = 9; method = 'tools/call'; params = @{ name = 'fmodel_read_file'; arguments = @{ sessionId = $session.sessionId; path = $found.items[0].path; count = 512 } } }
            if ($read.isError) { throw ($read.content[0].text) }
            Write-Host 'Verified reading and decompressing a mounted configuration file.'
        }
        $closed = Send-Rpc @{ jsonrpc = '2.0'; id = 10; method = 'tools/call'; params = @{ name = 'fmodel_close_game'; arguments = @{ sessionId = $session.sessionId } } }
        if ($closed.isError) { throw ($closed.content[0].text) }
    }
    $process.StandardInput.Close()
    if (!$process.WaitForExit(10000)) { throw 'MCP process did not exit after stdin closed.' }
    if ($process.ExitCode -ne 0) { throw "MCP exited with code $($process.ExitCode): $($errors.GetAwaiter().GetResult())" }
    Write-Host "MCP smoke passed: $($initialized.serverInfo.name), protocol $($initialized.protocolVersion), 34 tools, 4 prompts, resources, clean stdout and graceful EOF."
} finally {
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $process.Dispose()
}
