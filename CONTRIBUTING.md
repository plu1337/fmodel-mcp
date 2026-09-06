# Contributing

Use Windows x64, .NET 10 SDK, PowerShell 7 and Git. Clone the repository and run
`./scripts/Build-Mcp.ps1` once to initialize the pinned parser dependency and run the full checks.

## Development loop

```powershell
dotnet test FModel.Mcp.Tests/FModel.Mcp.Tests.csproj --no-restore -c Release -p:CUE4PARSE_SKIP_NATIVE=true -m:1 -nr:false
./scripts/Build-Mcp.ps1 -SkipTests
./scripts/Test-Installer.ps1
```

The test suite uses upstream fixtures plus a generated encrypted-index PAK. Avoid committing game
content, local configuration, key material, build products or the bootstrapped `CUE4Parse/` tree.
Describe any profile/codec requirement in an issue or pull request without attaching copyrighted assets.

Keep stdout exclusively for MCP JSON-RPC. Diagnostics belong on stderr. Add tool guidance for any new
model-facing capability, and update the tool catalog, protocol discovery checks and support matrix.
Prefer bounded responses, explicit file selections and session-scoped work. Do not add a shell-execution
tool or automatic downloads of keys or native libraries.

## Dependency updates

Change the revision and ZIP hash in `scripts/Initialize-Mcp.ps1`, review its small source integrations,
and update `NOTICE` and the design guide. Build in a fresh checkout and rerun the fixture and protocol
checks. Review the resolved package/license inventory produced by the build.

## Releases

Update the version in the server project, changelog and README download examples. Push a `vX.Y.Z` tag
whose version matches the project. The release workflow builds and tests Windows x64, checks the
installer, and uploads binary/source ZIPs and their checksums to a GitHub release. Releases are unsigned.
Never put game data, credentials or machine-local paths in release notes or artifacts.
