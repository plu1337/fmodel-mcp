<div align="center">

# FModel MCP

**Explore Unreal Engine game assets with your AI assistant.**

[![Build](https://github.com/plu1337/fmodel-mcp/actions/workflows/build.yml/badge.svg)](https://github.com/plu1337/fmodel-mcp/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/plu1337/fmodel-mcp)](https://github.com/plu1337/fmodel-mcp/releases/latest)
![Windows x64](https://img.shields.io/badge/platform-Windows_x64-0078D4)
[![License: GPL v3](https://img.shields.io/badge/license-GPL_v3-blue)](LICENSE)

[Download](https://github.com/plu1337/fmodel-mcp/releases/latest) · [Tool reference](docs/mcp.md) · [Architecture](docs/mcp-design.md) · [Report an issue](https://github.com/plu1337/fmodel-mcp/issues)

</div>

FModel MCP is a local Model Context Protocol server for Unreal Engine archives and assets. It uses
the same [CUE4Parse](https://github.com/FabianFG/CUE4Parse) parsing and conversion libraries as
[FModel](https://github.com/4sval/FModel), exposing **32 tools**, **four resources/resource templates**,
and **four workflow prompts** to MCP clients. No running FModel window, GPU, or Unreal Editor is required.

This is an independent community project, unaffiliated with the FModel or CUE4Parse maintainers.

## What you can do

| Task | Available capabilities |
| --- | --- |
| Open archives | PAK, IoStore, loose assets, engine/game profiles, local mappings, in-memory AES keys |
| Find assets | Directory browsing, paginated path search, asset registry queries, archive statistics |
| Understand packages | Exports/imports/names, JSON properties and pointers, references, Blueprint pseudocode |
| Examine content | Inline texture previews, localization, registry and binary configuration inspection |
| Export assets | Raw files with sidecars, JSON, supported textures, meshes, skeletons, animations, materials, worlds and audio |
| Manage work | Background batches, cancellation, progress, per-asset results, manifests and version comparisons |

Actual decoding and conversion depend on the selected game profile, mappings, encryption keys and
available codecs. The [support matrix](docs/mcp-design.md) and [limitations](docs/mcp.md#limitations-and-non-goals)
describe the boundaries. The server does not edit or repack game archives.

## Install in Codex / ChatGPT desktop

**Requires Windows x64.** Release ZIPs include .NET 10; you do not need to install the SDK or runtime.

1. Download `fmodel-mcp-1.0.0-win-x64.zip` and its `.sha256` file from [Releases](https://github.com/plu1337/fmodel-mcp/releases/latest).
2. Check the ZIP with `Get-FileHash .\fmodel-mcp-1.0.0-win-x64.zip -Algorithm SHA256` against the published checksum, then extract it to a folder.
3. Open PowerShell in that extracted folder and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-Mcp.ps1
```

The installer copies the complete release to `%LOCALAPPDATA%\FModelMcp`, tests its MCP connection,
and registers `fmodel` using the Codex CLI (including the CLI bundled with the desktop app).
It creates an `Inputs` folder for archives and mappings and an `Exports` folder for results.
To allow an existing game folder instead, run the installer from PowerShell with:

```powershell
.\Install-Mcp.ps1 -InputRoots 'D:\Games\MyGame\Content\Paks', 'D:\Mappings'
```

4. Restart `fmodel` in **Settings → MCP servers**, or restart the desktop app. Type `/mcp` to check it.
5. Ask your assistant:

> Use FModel to show its capabilities and the allowed input folders. Help me select the right engine profile, open my archives, and find texture assets.

The [desktop app, CLI, and IDE extension share local MCP configuration](https://learn.chatgpt.com/docs/extend/mcp).
ChatGPT web does not read this local configuration; this release is a local stdio server.

### Configuration, updates and removal

Edit `%LOCALAPPDATA%\FModelMcp\config.json` to change allowed `inputRoots`, `outputRoot`, limits,
or the path to a locally supplied Oodle library. Restart the server after changes.
Input directories must exist. Mapping files must be inside an allowed input root.

Run the installer from a newer extracted release to update. Existing settings are preserved unless
you pass replacement folder parameters, in which case the previous configuration is backed up.
Versioned installation directories keep a running server from interrupting file copies.

To remove the registration, run `codex mcp remove fmodel`, or remove it in the app's MCP settings.
Then remove unwanted installation folders yourself; keep any inputs or exports you need.

### Other MCP clients

Run `Install-Mcp.ps1 -SkipRegistration` and use the generated
`%LOCALAPPDATA%\FModelMcp\mcp-client.json`, or configure the executable directly:

```json
{
  "mcpServers": {
    "fmodel": {
      "command": "C:/Tools/fmodel-mcp/FModel.Mcp.exe",
      "args": ["--config", "C:/Tools/fmodel-mcp/config.json"]
    }
  }
}
```

Keep all extracted files together. Use absolute paths and copy/edit the [example server configuration](examples/fmodel-mcp.config.json).
Starting the executable manually waits for MCP input; that is expected.

## Example workflows

| Ask your assistant | Tool sequence |
| --- | --- |
| “Find and preview the game's logo.” | capabilities → list_options → open_game → search_assets → inspect_package → preview_texture |
| “Export these meshes as glTF.” | inspect_package → start_export → job_status → job_results |
| “Explain this Blueprint's data and script.” | open_game with `readScriptData` → inspect_package → get_properties → decompile_blueprint |
| “What changed between these two builds?” | open both builds → compare_sessions → inspect changed packages |

All tool names begin with `fmodel_`. The server supplies model instructions and prompts that teach
the sequence, pagination, profile selection, session cleanup and export-result checks. See the
[full AI command plan and argument examples](docs/mcp.md#ai-command-plan).

## Build from source

Install the **.NET 10 SDK** and **PowerShell 7**, then:

```powershell
git clone https://github.com/plu1337/fmodel-mcp.git
cd fmodel-mcp
./scripts/Build-Mcp.ps1
```

The script downloads a checksum-verified, pinned CUE4Parse source revision, applies the documented
host integrations, restores NuGet packages, runs the tests, and creates a self-contained ZIP with
notices and its SHA-256 checksum under `artifacts/`. The downloaded dependency and build products
are ignored by Git. Optional upstream C++ codec compilation is skipped; see the [codec setup guide](docs/mcp.md#compression-and-native-libraries).

Tests cover real PAK and IoStore fixtures, encrypted-index mounting, package inspection, texture
preview, localization, exports, cancellation, path confinement, and the official MCP client protocol.
See [CONTRIBUTING.md](CONTRIBUTING.md) for focused development commands.

## License and credits

The server is distributed under [GPL-3.0](LICENSE). FModel inspired the workflow; CUE4Parse provides
the parsing and conversion engine under Apache-2.0. Dependencies retain their own licenses.
Release archives include generated third-party notices and bundled license files. Releases also
include a source bundle with the pinned CUE4Parse source and the applied integrations.
No game archives, mappings, AES keys, or proprietary Oodle DLLs are included in the runtime release.
