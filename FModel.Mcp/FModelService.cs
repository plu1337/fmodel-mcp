using System.Collections.Concurrent;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.GameTypes.AshEchoes.FileProvider;
using CUE4Parse.GameTypes.HonorOfKings.FileProvider;
using CUE4Parse.GameTypes.LordOfMysteries.FileProvider;
using CUE4Parse.GameTypes.Theia.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Jmap;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;

namespace FModel.Mcp;

public sealed partial class FModelService : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly PathPolicy _paths;
    private readonly ConcurrentDictionary<string, GameSession> _sessions = new();
    private readonly ConcurrentDictionary<string, ExportJob> _jobs = new();
    private readonly SemaphoreSlim _sessionCreation = new(1, 1);
    private readonly object _jobsSync = new();

    public FModelService(ServerOptions options, PathPolicy paths)
    {
        _options = options;
        _paths = paths;
        TextureDecoder.UseAssetRipperTextureDecoder = true;
        PropertyUtil.SearchPropertyInTemplate = true;
        if (options.OodleLibrary is not null)
        {
            if (!System.IO.File.Exists(options.OodleLibrary)) throw new FileNotFoundException("Configured Oodle library does not exist.");
            // Explicit native library loading only; never call the parser's automatic download helper.
            OodleHelper.Initialize(new OodleDotNet.Oodle(options.OodleLibrary));
        }
    }

    public object Capabilities() => new
    {
        name = "FModel MCP", version = "1.0.0", parser = typeof(DefaultFileProvider).Assembly.GetName().Version?.ToString(),
        transport = "stdio", operatingSystem = Environment.OSVersion.ToString(),
        inputRoots = _options.InputRoots, outputRoot = _options.OutputRoot,
        limits = new { _options.MaxSessions, _options.MaxJobs, _options.MaxBatchAssets, _options.MaxReadBytes, _options.MaxResponseChars, maxPageSize = 200 },
        oodleLoaded = OodleHelper.Instance is not null,
        workflows = new[] { "local PAK/IoStore/loose files", "AES and USMAP/JMAP", "browse/search", "package exports/properties/imports/names",
            "IoStore referencers", "asset registry", "localization", "text/binary reads", "texture preview", "raw/JSON/texture/audio/mesh/animation/material/world export", "batch jobs", "session comparison" },
        limitations = new[] { "No control of the desktop FModel UI or 3D viewport", "No Fortnite/Valorant live streaming, remote key lookup or downloads",
            "No repacking or editing game archives", "Cooked assets do not recover original source code",
            "Support depends on the selected CUE4Parse game profile, mappings, keys and installed native codecs",
            "IoStore referencers use container import metadata; they are not a complete soft-reference graph" }
    };

    public object EnumValues(string category, string filter, int offset, int limit)
    {
        string[] values = category.ToLowerInvariant() switch
        {
            "game" => Enum.GetNames<EGame>(), "textureplatform" => Enum.GetNames<ETexturePlatform>(),
            "meshformat" => Enum.GetNames<EMeshFormat>(), "meshquality" => Enum.GetNames<EMeshQuality>(),
            "nanitemeshformat" => Enum.GetNames<ENaniteMeshFormat>(), "textureformat" => Enum.GetNames<ETextureFormat>(),
            "materialdepth" => Enum.GetNames<CUE4Parse.UE4.Assets.Exports.Material.EMaterialDepth>(),
            "socketformat" => Enum.GetNames<ESocketFormat>(), "compressionformat" => Enum.GetNames<EFileCompressionFormat>(),
            _ => throw new ArgumentException("Unknown category. Use game, texturePlatform, meshFormat, meshQuality, naniteMeshFormat, textureFormat, materialDepth, socketFormat or compressionFormat.")
        };
        return Paginate(values.Where(x => Contains(x, filter)).ToArray(), offset, limit);
    }

    public async Task<object> OpenGame(OpenGameOptions request, CancellationToken ct)
    {
        var directory = _paths.Input(request.Directory, true);
        var game = ParseEnum<EGame>(request.Game);
        var platform = ParseEnum<ETexturePlatform>(request.TexturePlatform);
        var mappings = request.MappingsPath is null ? null : LoadMappings(request.MappingsPath);
        var keys = ParseKeys(request.AesKeys ?? []);
        var versions = new VersionContainer(game, platform, customVersions: new FCustomVersionContainer(request.CustomVersions?.Select(v =>
            new FCustomVersion(ParseGuid(v.Key), v.Value))), optionOverrides: request.VersionOptions, mapStructTypesOverrides: request.MapStructTypes);
        await _sessionCreation.WaitAsync(ct);
        try
        {
            if (_sessions.Count >= _options.MaxSessions) throw new InvalidOperationException("Session limit reached. Close a session first.");
            return await Task.Run<object>(() =>
            {
                PathPolicy.CheckInputTree(directory, ct);
                var comparer = game == EGame.GAME_BlackStigma ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
                AbstractVfsFileProvider provider = game switch
                {
                    EGame.GAME_AshEchoes => new AEDefaultFileProvider(directory, SearchOption.AllDirectories, versions, comparer),
                    EGame.GAME_HonorofKingsWorld => new HoKWDefaultFileProvider(directory, SearchOption.AllDirectories, versions, comparer),
                    EGame.GAME_LordOfMysteries => new LoMDefaultFileProvider(directory, SearchOption.AllDirectories, versions, comparer),
                    EGame.GAME_ArcRaiders or EGame.GAME_Highguard or EGame.GAME_MARVELTokonFightingSouls => new TheiaFileProvider(directory, SearchOption.AllDirectories, versions, comparer),
                    _ => new DefaultFileProvider(directory, SearchOption.AllDirectories, versions, comparer)
                };
                try
                {
                    provider.MappingsContainer = mappings;
                    provider.ReadScriptData = request.ReadScriptData;
                    provider.ReadShaderMaps = request.ReadShaderMaps;
                    provider.ReadNaniteData = request.ReadNaniteData;
                    provider.Initialize();
                    provider.Mount();
                    if (keys.Count > 0) provider.SubmitKeys(keys);
                    provider.PostMount();
                    ct.ThrowIfCancellationRequested();
                    var session = new GameSession(Guid.NewGuid().ToString("N"), directory, provider) { MappingsPath = request.MappingsPath };
                    session.RefreshFiles();
                    _sessions[session.Id] = session;
                    return SessionInfo(session);
                }
                catch { provider.Dispose(); throw; }
            }, ct);
        }
        finally { _sessionCreation.Release(); }
    }

    public object ListSessions() => new { sessions = _sessions.Values.Select(s => new { sessionId = s.Id, directory = s.Directory }).ToArray() };
    public Task<object> GetSession(string id, CancellationToken ct) => InSession(id, s => SessionInfo(s), ct);

    private static object SessionInfo(GameSession s) => new
    {
        sessionId = s.Id, directory = s.Directory, projectName = s.Provider.ProjectName, game = s.Provider.Versions.Game.ToString(),
        texturePlatform = s.Provider.Versions.Platform.ToString(), providerType = s.Provider.GetType().Name,
        fileCount = s.Files.Length, mountedArchives = s.Provider.MountedVfs.Count, unloadedArchives = s.Provider.UnloadedVfs.Count,
        requiredKeyGuids = s.Provider.RequiredKeys.Select(k => k.ToString()).ToArray(), suppliedKeyCount = s.Provider.Keys.Count,
        mappingsPath = s.MappingsPath, hasMappings = s.Provider.MappingsContainer is not null,
        culture = s.Provider.Internationalization.Culture, registryLoaded = s.Registry is not null,
        hasIoStoreGlobalData = s.Provider.GlobalData is not null
    };

    public async Task<object> CloseSession(string id, CancellationToken ct)
    {
        var session = Session(id);
        lock (_jobsSync)
        {
            if (_jobs.Values.Any(j => j.SessionId == id && !IsTerminal(j))) throw new InvalidOperationException("Session has an active export job. Cancel it and wait for terminal status before closing.");
            session.Closed = true;
        }
        // Once closed to new calls, finish disposal even if the caller disconnects.
        await session.Gate.WaitAsync(CancellationToken.None);
        try { _sessions.TryRemove(id, out _); session.Provider.Dispose(); return new { sessionId = id, closed = true }; }
        finally { session.Gate.Release(); }
    }

    public Task<object> Archives(string id, string filter, int offset, int limit, CancellationToken ct) => InSession(id, s =>
    {
        var mounted = s.Provider.MountedVfs.ToHashSet();
        var rows = mounted.Concat(s.Provider.UnloadedVfs).Where(a => Contains(a.Name, filter)).OrderBy(a => a.Name).Select(a => new
        {
            name = a.Name, path = a.Path, mounted = mounted.Contains(a), encrypted = a.IsEncrypted, keyGuid = a.EncryptionKeyGuid.ToString(),
            size = a.Length, fileCount = a.FileCount, mountPoint = a.MountPoint, hasDirectoryIndex = a.HasDirectoryIndex,
            compressionMethods = a.CompressionMethods.Select(x => x.ToString()).ToArray()
        }).ToArray();
        return Paginate(rows, offset, limit);
    }, ct);

    public Task<object> SubmitKeys(string id, Dictionary<string, string> keys, CancellationToken ct)
    {
        var parsed = ParseKeys(keys);
        return InSession(id, s => { s.Provider.SubmitKeys(parsed); s.Provider.PostMount(); s.RefreshFiles(); return SessionInfo(s); }, ct);
    }

    public Task<object> SetMappings(string id, string path, CancellationToken ct) => InSession(id, s =>
    {
        s.Provider.MappingsContainer = LoadMappings(path); s.MappingsPath = path; s.Registry = null; return SessionInfo(s);
    }, ct);

    public Task<object> LoadVirtualPaths(string id, CancellationToken ct) => InSession(id, s =>
        new { loaded = s.Provider.LoadVirtualPaths(s.Provider.Versions.Ver, ct), paths = s.Provider.VirtualPaths }, ct);

    private ITypeMappingsProvider LoadMappings(string path)
    {
        path = _paths.Input(path);
        if (new FileInfo(path).Length > _options.MaxReadBytes) throw new ArgumentException("Mappings file exceeds maxReadBytes.");
        return path.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase)
            ? new JmapTypeMappingsProvider(path)
            : path.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase) ? new FileUsmapTypeMappingsProvider(path)
            : throw new ArgumentException("Mappings must be .usmap, .jmap or .jmap.gz.");
    }

    private static Dictionary<FGuid, FAesKey> ParseKeys(Dictionary<string, string> keys)
    {
        if (keys.Count > 256) throw new ArgumentException("At most 256 AES keys per call.");
        try { return keys.ToDictionary(k => ParseGuid(k.Key), k => new FAesKey(k.Value)); }
        catch { throw new ArgumentException("Invalid AES key map. Use GUID strings (all zeros for the main key) and 64 hexadecimal key digits. Key values are never returned."); }
    }
    private static FGuid ParseGuid(string value) => Guid.TryParse(value, out var guid) ? (FGuid)guid : throw new ArgumentException("Invalid GUID.");
    internal static T ParseEnum<T>(string value) where T : struct, Enum => Enum.GetNames<T>().FirstOrDefault(n => n.Equals(value, StringComparison.OrdinalIgnoreCase)) is { } name
        ? Enum.Parse<T>(name) : throw new ArgumentException($"Invalid {typeof(T).Name}: {value}. Use fmodel_list_options for supported values.");
    private GameSession Session(string id) => _sessions.TryGetValue(id, out var s) && !s.Closed ? s : throw new KeyNotFoundException("Unknown or closed sessionId. Use fmodel_open_game first.");
    private async Task<object> InSession(string id, Func<GameSession, object> action, CancellationToken ct)
    {
        var s = Session(id);
        await s.Gate.WaitAsync(ct);
        try { if (s.Closed) throw new InvalidOperationException("Session is closed."); return await Task.Run(() => action(s), ct); }
        finally { s.Gate.Release(); }
    }
    internal static bool Contains(string? text, string? query) => string.IsNullOrEmpty(query) || (text?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    internal static Page<T> Paginate<T>(IReadOnlyList<T> rows, int offset, int limit)
    {
        ValidatePage(offset, limit);
        var items = rows.Skip(offset).Take(limit).ToArray();
        return new(items, offset, rows.Count, (long)offset + items.Length < rows.Count ? offset + items.Length : null);
    }
    internal static void ValidatePage(int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > 200) throw new ArgumentException("offset must be nonnegative; limit must be 1..200.");
    }
    internal static AssetInfo Info(GameFile f) => new(f.Path, f.Size, f.Extension, f.IsUePackage, f.IsEncrypted, f.CompressionMethod.ToString(), (f as VfsEntry)?.Vfs.Name);
    private GameFile File(GameSession s, string path)
    {
        if (!s.Provider.TryGetGameFile(path, out var f)) throw new KeyNotFoundException("Asset not found. Search first and reuse the exact returned path.");
        if (f is OsGameFile loose) _paths.Input(loose.ActualFile.FullName);
        return f;
    }
    private void CheckSize(GameFile f)
    {
        if (f.Size < 0 || f.Size > _options.MaxReadBytes) throw new InvalidOperationException("Asset exceeds maxReadBytes. Increase the server limit for this asset.");
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var job in _jobs.Values) job.Cancellation.Cancel();
        await Task.WhenAll(_jobs.Values.Select(j => j.Task ?? Task.CompletedTask));
        foreach (var session in _sessions.Values)
        {
            await session.Gate.WaitAsync();
            try { session.Closed = true; session.Provider.Dispose(); }
            finally { session.Gate.Release(); }
        }
        foreach (var job in _jobs.Values) job.Cancellation.Dispose();
        _sessions.Clear();
        _sessionCreation.Dispose();
    }
}
