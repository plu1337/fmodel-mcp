# FModel MCP server

FModel is an Unreal Engine archive explorer. This server gives AI assistants direct access to its
CUE4Parse parsing and conversion pipeline through **34 MCP tools**, four resources/resource templates,
and four guided workflow prompts. It runs independently of the WPF application using local stdio.

## Build and connect

Source-build requirements: **Windows x64, .NET 10 SDK, PowerShell 7**, and internet access for the initial
dependency restore. Published release ZIPs include the .NET runtime and an installer compatible with
Windows PowerShell 5.1. See the [installation guide](../README.md#install-in-codex--chatgpt-desktop).
A GPU, Unreal Editor, and an open FModel window are unnecessary.

From the repository root:

```powershell
./scripts/Build-Mcp.ps1
```

This initializes the missing CUE4Parse source dependency when necessary, checks its download checksum,
applies the small host integrations, runs the tests, publishes to `artifacts/fmodel-mcp`, and smoke-tests
the published executable over MCP (including graceful shutdown).
`-SkipTests` is available for subsequent local packaging. Keep the complete published directory together.

Copy `examples/fmodel-mcp.config.json` to `fmodel-mcp.local.json` and edit the allowed game/mapping roots:

```json
{
  "inputRoots": ["D:/Games/MyGame", "D:/FModel/Mappings"],
  "outputRoot": "./Exports/Mcp",
  "oodleLibrary": null,
  "fmodelSettingsPath": null,
  "maxSessions": 4,
  "maxJobs": 32,
  "maxBatchAssets": 500,
  "maxReadBytes": 268435456,
  "maxResponseChars": 100000
}
```

Relative configuration paths resolve against the configuration file's directory. Tool arguments for
game directories and mapping files must be absolute. Without a configuration file, input access is
limited to the process working directory and exports go to `Exports/Mcp` beneath it.
`FMODEL_MCP_CONFIG` is an alternative to `--config`; an explicit CLI argument takes precedence.

Connect your MCP client using its stdio-server configuration. `examples/mcp-client.json` contains a
generic example; substitute actual absolute paths:

```json
{
  "mcpServers": {
    "fmodel": {
      "command": "C:/path/to/fmodel-mcp/artifacts/fmodel-mcp/FModel.Mcp.exe",
      "args": ["--config", "C:/path/to/fmodel-mcp/fmodel-mcp.local.json"]
    }
  }
}
```

Launching the executable manually waits for JSON-RPC on stdin; this is normal. `--help` prints usage.
Do not configure the client to run a build command: build output is not MCP traffic.
Diagnostics go to stderr, including incidental parser console output.

### Compression and native libraries

The build script skips optional CUE4Parse C++ compilation. Managed decoding and packaged native
dependencies support the included tests. For Oodle-compressed games, set `oodleLibrary` to an existing,
compatible x64 Oodle library supplied by your installation (such as `oodle-data-shared.dll` or
`oo2core_9_win64.dll`). The server does not fetch native code or game files at runtime.
Some animation/audio/texture codecs additionally require the corresponding CUE4Parse native libraries
beside the executable. The upstream native build and its submodules can be used for these; the source
ZIP bootstrap does not recursively populate native submodules. A successful archive mount does not
guarantee every asset or codec is supported.

## AI command plan

When capabilities reports `savedGamesConfigured=true`, first use `fmodel_list_saved_games` and
`fmodel_open_saved_game`. An empty selector opens FModel's last saved selection with saved local keys,
profile overrides and a local mapping override or a single matching cached mapping. Keys are not
returned or copied to MCP config. Multiple mapping candidates require an explicit `mappingsPath`.
The configured input allowlist still applies. Reuse existing session IDs when already mounted.
The manual workflow below remains available for games without a saved profile.

1. Call `fmodel_capabilities`; check access roots, limits, and Oodle availability.
2. Call `fmodel_list_options` with `category=game` and a game/engine filter. Select the known correct
   profile. `fmodel_open_game` requires an explicit `options.game`; it does not infer an engine version.
3. Open the local archive directory. Retain `sessionId` and inspect missing key GUIDs/mount counts.
   Submit authorized keys or local mappings when needed. Include `global.utoc` and its data files for IoStore.
4. Browse/search virtual paths. For class-based search, locate and load `AssetRegistry.bin` first.
   Registry search yields object paths; package inspection accepts those paths through provider resolution.
5. Inspect a package's exports, then read a selected object's properties, preview a texture, inspect
   imports/referencers, or generate Blueprint pseudocode. Use JSON pointers and pagination to narrow data.
6. Export explicit paths with the right mode and format. Save `jobId`; poll at least one second apart.
   Wait through `finalizing`, then inspect per-asset results and the output manifest before claiming success.
7. Close sessions after jobs finish to release archive handles.

Minimal tool-call arguments:

```json
// fmodel_open_game
{"options":{"directory":"D:/Games/MyGame/Content/Paks","game":"GAME_UE5_4","mappingsPath":"D:/FModel/Mappings/MyGame.usmap","readScriptData":true}}

// fmodel_search_assets
{"sessionId":"RETURNED_ID","query":"T_Logo","packagesOnly":true,"limit":20}

// fmodel_inspect_package
{"sessionId":"RETURNED_ID","path":"MyGame/Content/UI/T_Logo.uasset","section":"exports"}

// fmodel_preview_texture
{"sessionId":"RETURNED_ID","path":"MyGame/Content/UI/T_Logo.uasset","objectName":"T_Logo","maxSize":512}

// fmodel_start_export
{"sessionId":"RETURNED_ID","request":{"paths":["MyGame/Content/UI/T_Logo.uasset"],"mode":"converted","textureFormat":"Png"}}

// fmodel_job_status / fmodel_job_results
{"jobId":"RETURNED_JOB_ID"}
```

Use the actual paths returned by the server. The examples are illustrative, not installed game paths.
Each call is a JSON object; the comment lines above are labels and are not part of the request.

## Command reference

Additional saved-profile commands:

| Tool | Arguments and behavior |
| --- | --- |
| `fmodel_list_saved_games` | No arguments. Returns saved names, directories, selected profile, access status, local mapping candidates and key counts. Requires `fmodelSettingsPath` configured by the installer or user. |
| `fmodel_open_saved_game` | Optional `selector` (name or directory; empty = last saved selection), `mappingsPath`, `readScriptData` (default true). Returns an ordinary session handle for all asset tools. |

All tool names start with `fmodel_`. Read/list operations expose `readOnlyHint`; state-changing calls
are marked nondestructive. Tools do not send messages or make network requests.

| Command | Purpose and key arguments |
| --- | --- |
| `capabilities` | Server purpose, access roots, limits, codec availability, limitations. |
| `list_options` | Enumerated `category`, substring `filter`, `offset`, `limit`. |
| `open_game` | `options`: required `directory`, `game`; optional mappings, keys, platform, version overrides, parser flags. |
| `list_sessions` | IDs and directories of open sessions. |
| `session_info` | Profile, provider, files, mounted/unloaded archives, missing key GUIDs, mappings, culture, registry/global-data state. |
| `close_game` | Release a `sessionId`; rejects active export jobs. |
| `list_archives` | Archive names, paths, sizes, compression, encryption, mount status; name `filter`. |
| `submit_keys` | `keys` object maps GUIDs to AES key hex. All-zero GUID is the main key. |
| `set_mappings` | Absolute local `.usmap`, `.jmap`, or `.jmap.gz` `path`. |
| `load_virtual_paths` | Resolve plugin mount aliases after mounting. |
| `browse` | Direct child folders/files beneath a virtual `path`; empty lists roots. |
| `search_assets` | Path `query`, folder `prefix`, exact `extension`, exact `archive`, `packagesOnly`. |
| `asset_info` | Metadata, companion payloads, optional `hash` of this file's decompressed bytes. |
| `read_file` | Bounded virtual-file bytes: `encoding=utf8/utf16/base64/hex`, byte `offset` and `count`. |
| `inspect_package` | `section=summary/exports/imports/names`; export indexes are zero-based. |
| `get_properties` | `objectName` or `exportIndex`; optional RFC6901 `pointer`; page object fields/array elements. |
| `find_references` | `direction=dependencies` for outgoing imports or `referencers` for IoStore container imports. |
| `load_registry` | Parse a virtual `AssetRegistry.bin` `path` into the session. |
| `search_registry` | Object-path `query`, `className` substring, optional `includeTags`. |
| `load_localization` | Set active `culture`, e.g. `en`, `de`, `pt-BR`. |
| `search_localization` | Search keys/values with `query` and optional `namespaceFilter`. |
| `preview_texture` | Inline PNG image of `objectName`/`exportIndex`; `maxSize=32..2048`. |
| `start_export` | Start explicit `request.paths`; choose mode and conversion options; returns `jobId`. |
| `list_jobs` | Retained job status, counts, timestamps, output directories. |
| `job_status` | Queued/running/cancelling/finalizing or terminal status. |
| `job_results` | Paginated per-requested-asset `success`, `files`, and error/skip notes. |
| `cancel_job` | Cooperative cancellation; existing files remain. |
| `read_output` | Completed job's `relativePath` (default `manifest.json`), `utf8/base64`, byte offsets. |
| `compare_sessions` | Added/removed/metadata-changed paths between `leftSessionId` and `rightSessionId`; optional prefix. |
| `decompile_blueprint` | Approximate C++-like pseudocode; requires `readScriptData=true`; paginated lines. |
| `inspect_data_file` | Parse `format=locres/locmeta/binaryConfig`; optional pointer and pagination. |
| `statistics` | File/package/byte totals and extension groups under a prefix. |

The advertised schemas contain complete defaults and descriptions. Page results use `items`, `total`,
`offset`, and `nextOffset` (`null` at the end). Page size is 1–200. Some operations wrap the page in `data`,
`results`, `lines`, or `extensions`. Property names *inside Unreal JSON* retain Unreal's original casing;
ordinary protocol wrappers use camelCase. JSON pointers are case-sensitive and escape `/` as `~1` and `~` as `~0`.

### Game options

`texturePlatform` defaults to `DesktopMobile`. `customVersions` maps GUID to integer version;
`versionOptions` maps parser option names to booleans. `mapStructTypes` maps names to `{key,value}`
type pairs. `readScriptData` and `readShaderMaps` default to false; `readNaniteData` defaults to true.
Changing the game/profile/parser flags requires opening a new session. AES and mappings can be updated
in-place. The initial key map and subsequent submissions remain in memory for the session lifetime.
Do not put key values in client command-line arguments. MCP hosts may retain tool arguments in their
own conversation history even though the server does not echo key values.

### Export options and results

| Mode | Output |
| --- | --- |
| `raw` | Original decompressed file bytes; packages include available `.uexp`, `.ubulk`, and `.uptnl` companions. |
| `properties` | Serialized package exports as a JSON array. |
| `audio` | Decoded SoundWave/SoundNodeWave/AkMediaAssetData where supported; extension reflects the actual codec/container. |
| `converted` | Matching CUE4Parse texture, material, static/skinned mesh, skeleton, animation, pose, geometry, world, landscape, spline, or DNA exporter. |

`objectName` limits a single-package export; it is invalid with `raw` or multiple paths.
Mesh formats: `Gltf2` (binary glTF), `ActorX`, `UEFormat`, `USD`. **Worlds require USD**.
Animations support ActorX/UEFormat/USD and require their skeleton and compression dependencies.
Other settings are `meshQuality`, `naniteMeshFormat`, `textureFormat`, `textureQuality` (1–100),
`exportAllTextureMips`, `exportHdrTexturesAsHdr`, `exportMaterials`, `materialDepth`,
`exportMorphTargets`, `socketFormat`, `compressionFormat`, `decompressAudio`, and `includeStreamingLevels`.
Discover exact enum values using `list_options`; unsupported type/format combinations become explicit job errors.

Each job writes into a unique directory under `outputRoot`, retaining virtual folders under
`raw/`, `properties/`, `audio/`, or `converted/`. The conversion pipeline can queue dependencies;
those files appear in the root asset's result and the manifest. Unsupported unrelated export types
in a package are reported as skip notes; if none can be converted, the asset fails.
World sublevels are referenced but not exported by default. Enabling sublevels can substantially
increase work and output size. Empty worlds can legitimately yield no convertible output.

Terminal states are `completed`, `completed_with_errors`, `cancelled`, and `failed`. `completed`
means every requested path succeeded; inspect result notes for skipped nonconvertible objects.
`manifest.json` includes status, per-asset results, and an inventory that includes partial files.
Cancellation can wait for the current synchronous/native parser operation and does not roll back files.
Jobs are serialized within a session; different sessions can work concurrently. Status/cancellation do
not wait for a session's parser lock. Old finished job records are evicted at `maxJobs`; their files remain.

### Resources and prompts

- `fmodel://guide`: AI command-planning guide and troubleshooting.
- `fmodel://capabilities`: active server capabilities.
- `fmodel://sessions/{sessionId}`: current session state.
- `fmodel://jobs/{jobId}`: current job status.
- Prompts: `explore_game(directory, objective)`, `export_assets(sessionId, objective)`,
  `diagnose_asset(sessionId, assetPath)`, and `compare_game_versions(leftSessionId, rightSessionId, prefix)`.

## Limits and troubleshooting

Input roots are an explicit allowlist. Symlinks/junctions in selected input trees and output paths are
rejected. Exports use unique directories and validate dependency output paths before writing. The game
archives are read-only. This is a local trusted-process integration, not an isolation boundary for hostile
native code or concurrently modified game files.

`maxReadBytes` limits explicitly selected files and mappings; it is not a total parser-memory, decompressed
dependency, or output-disk quota. Texture preview size bounds the returned image, not every intermediate
decode allocation. Large dependent assets can still consume substantial memory/disk. Valid configuration
ranges: sessions 1–16, jobs 1–1000, batch assets 1–10000, read bytes 1024–2147483647, response characters
1024–1000000. Byte reads return at most 32768 bytes per call; text chunks can split multibyte characters.

Tool failures set `isError=true` with `error` and `message`. Codes include `invalid_argument`, `not_found`,
`invalid_state`, `unsupported`, `parser_or_io_error`, and `response_too_large`. For oversized results,
reduce the page, use a deeper JSON pointer, or export properties and read output chunks.

| Symptom | Check |
| --- | --- |
| No or few files | Correct directory, encrypted/unloaded archives, required key GUIDs, archive directory index availability. |
| Unversioned properties fail | Correct local mappings and game profile; changing mappings cannot correct a wrong engine version. |
| IoStore package fails | Matching `global.utoc`/`global.ucas`, correct game profile and all companion containers. A global container can appear unloaded because it has no normal directory index. |
| Decompression error | Required Oodle/native codec; server never downloads one silently. |
| Texture/mesh exports fail | Correct platform/profile; missing bulk payloads, unsupported formats or codecs. |
| Animation compression data unavailable | Missing compression-settings/codec assets; properties/raw export can still work. |
| World format unsupported | Set `meshFormat` to `USD`; decide whether sublevels should be included. |
| Pseudocode unavailable | Open with `readScriptData=true`; confirm a Blueprint class export exists. Cooked data may omit information. |
| Empty reference search | PAK/loose reverse references are unsupported; soft references are not exhaustively indexed. |
| Same metadata across versions | Metadata equality does not imply byte equality; hash files individually, including payloads. |

## Limitations and non-goals

Desktop viewport/window control, streamed Fortnite/Valorant providers, external Wwise/FMOD/CriWare bank
conversion, per-game icon creators, Lua native decompilation, shader-specialized viewers, repacking, and
game modification are not exposed. These boundaries are advertised rather than represented as working tools.

## Validation

```powershell
dotnet test FModel.Mcp.Tests/FModel.Mcp.Tests.csproj -m:1 -nr:false -p:CUE4PARSE_SKIP_NATIVE=true
./scripts/Smoke-Mcp.ps1
```

The 20 tests use the pinned upstream UE5.8 PAK/IoStore fixtures and an independently generated encrypted PAK.
They exercise official-SDK stdio discovery/tool calls/resources, mappings, localization, registry search,
package inspection, Blueprint pseudocode, inline PNGs, raw payloads, JSON/audio/texture/mesh/skeleton/USD
world exports, partial failures, pagination, cancellation, and path restrictions. This does not certify
every supported game, native codec, engine profile, or asset type. Upstream parser projects currently emit
compiler warnings; the MCP project itself builds without warnings.

Implementation rationale and coverage: [mcp-design.md](mcp-design.md).
