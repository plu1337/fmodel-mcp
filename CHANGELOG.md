# Changelog

## 1.1.1

- Fixed inline texture previews sending raw PNG bytes where MCP requires base64 text. Images now display correctly in MCP clients.
- Added a regression test that opens a real Unreal texture through the official stdio MCP client and validates the received PNG encoding and dimensions.
- Full-resolution texture file exports and saved FModel profile integration remain available.

## 1.1.0

- Added saved-game discovery and one-command opening from FModel's local settings (34 tools total).
- Fresh installs reuse the selected game folder, mapping cache and existing Oodle library when available.
- Existing installations can connect with `Install-Mcp.ps1 -ImportFModel`.
- Saved AES keys are read in process without copying them into MCP configuration or tool arguments.
- Added tests for saved encrypted games, key redaction, mapping ambiguity, settings refresh and access boundaries.
- Saved-game discovery reflects saved settings; it does not control the desktop UI or fetch remote keys/mappings.

## 1.0.0

- Initial local stdio MCP server with 32 tools, four resources/resource templates, and four prompts.
- Archive mounting, explicit game profiles, AES keys, mappings, browsing, registry and localization search.
- Package properties, references, Blueprint pseudocode and inline texture previews.
- Background raw/JSON/converted/audio export jobs with cancellation and result manifests.
- Bounded input/output handling and per-session concurrency control.
- Windows x64 self-contained release with Codex/ChatGPT desktop installer.
- Automated tests against real Unreal PAK/IoStore fixtures and the official MCP client.

Game-specific and optional codec support remains dependent on CUE4Parse; see the tool guide.
