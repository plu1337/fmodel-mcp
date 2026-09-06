using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse_Conversion.Options;
using System.ComponentModel;

namespace FModel.Mcp;

public sealed class OpenGameOptions
{
    [Description("Absolute OS directory under configured inputRoots, containing the game's archives or loose packages.")]
    public required string Directory { get; set; }
    [Description("Explicit CUE4Parse GAME_* engine/game profile. Discover with fmodel_list_options; do not guess.")]
    public required string Game { get; set; }
    public string TexturePlatform { get; set; } = "DesktopMobile";
    [Description("Absolute local .usmap/.jmap/.jmap.gz file under inputRoots, if required by the game.")]
    public string? MappingsPath { get; set; }
    [Description("Authorized AES key map: GUID to 64 hexadecimal digits. All-zero GUID denotes the main key.")]
    public Dictionary<string, string>? AesKeys { get; set; }
    public Dictionary<string, int>? CustomVersions { get; set; }
    public Dictionary<string, bool>? VersionOptions { get; set; }
    public Dictionary<string, KeyValuePair<string, string>>? MapStructTypes { get; set; }
    public bool ReadScriptData { get; set; }
    public bool ReadShaderMaps { get; set; }
    public bool ReadNaniteData { get; set; } = true;
}

public sealed class ExportRequest
{
    [Description("Explicit virtual asset paths returned by search. At most maxBatchAssets, no duplicates.")]
    public string[] Paths { get; set; } = [];
    [Description("raw, properties, converted or audio. Converted uses the matching CUE4Parse exporter.")]
    public string Mode { get; set; } = "converted";
    public string? ObjectName { get; set; }
    [Description("Gltf2, ActorX, UEFormat or USD. Worlds require USD; animations support ActorX/UEFormat/USD.")]
    public string MeshFormat { get; set; } = "Gltf2";
    public string MeshQuality { get; set; } = "Highest";
    public string NaniteMeshFormat { get; set; } = "NoNanite";
    public string TextureFormat { get; set; } = "Png";
    public int TextureQuality { get; set; } = 100;
    public bool ExportAllTextureMips { get; set; }
    public bool ExportHdrTexturesAsHdr { get; set; } = true;
    public bool ExportMaterials { get; set; } = true;
    public string MaterialDepth { get; set; } = "TopLayerOnly";
    public bool ExportMorphTargets { get; set; } = true;
    public string SocketFormat { get; set; } = "Bone";
    public string CompressionFormat { get; set; } = "None";
    public bool DecompressAudio { get; set; } = true;
    public bool IncludeStreamingLevels { get; set; }
}

internal sealed class GameSession(string id, string directory, AbstractVfsFileProvider provider)
{
    public string Id { get; } = id;
    public string Directory { get; } = directory;
    public AbstractVfsFileProvider Provider { get; } = provider;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public volatile bool Closed;
    public string? MappingsPath { get; set; }
    public GameFile[] Files { get; set; } = [];
    public FAssetRegistryState? Registry { get; set; }
    public void RefreshFiles() => Files = Provider.Files.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
}

public sealed record AssetInfo(string Path, long Size, string Extension, bool IsPackage, bool Encrypted, string Compression, string? Archive);
public sealed record Page<T>(IReadOnlyList<T> Items, int Offset, int Total, int? NextOffset);
public sealed record ExportItem(string Path, bool Success, string[] Files, string? Error = null);

internal sealed class ExportJob(string id, string sessionId, string outputDirectory, int total)
{
    public string Id { get; } = id;
    public string SessionId { get; } = sessionId;
    public string OutputDirectory { get; } = outputDirectory;
    public int Total { get; } = total;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public CancellationTokenSource Cancellation { get; } = new();
    public Task? Task { get; set; }
    public object Sync { get; } = new();
    public string State { get; set; } = "queued";
    public string? CurrentPath { get; set; }
    public List<ExportItem> Results { get; } = [];
    public string? Error { get; set; }
    public bool Terminal => State is "completed" or "completed_with_errors" or "cancelled" or "failed";
    public object Snapshot(string? stateOverride = null)
    {
        lock (Sync) return new { jobId = Id, sessionId = SessionId, state = stateOverride ?? State, total = Total, completed = Results.Count,
            succeeded = Results.Count(r => r.Success), failed = Results.Count(r => !r.Success), currentPath = CurrentPath,
            outputDirectory = OutputDirectory, createdAt = CreatedAt, error = Error };
    }
}
