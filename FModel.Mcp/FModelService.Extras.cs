using CUE4Parse.UE4.BinaryConfig;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.UObject.Editor;
using Newtonsoft.Json.Linq;

namespace FModel.Mcp;

public sealed partial class FModelService
{
    // CUE4Parse's Blueprint decompiler has static mapping/function state.
    private static readonly object DecompilerLock = new();

    public Task<object> DecompileBlueprint(string id, string path, string? objectName, int offset, int limit, CancellationToken ct) => InSession(id, s =>
    {
        ValidatePage(offset, limit);
        if (!s.Provider.ReadScriptData) throw new InvalidOperationException("Reopen the session with readScriptData=true to deserialize Blueprint bytecode.");
        var package = LoadPackage(s, path);
        var classes = package.GetExports().OfType<UClass>().Where(c => objectName is null || c.Name == objectName).ToArray();
        if (classes.Length == 0) throw new NotSupportedException("No Blueprint class export found. Inspect package exports first.");
        UClassCookedMetaData? metadata = null;
        var file = File(s, path);
        if (s.Provider.TryGetGameFile(file.PathWithoutExtension + ".o.uasset", out var editorFile))
            metadata = LoadPackage(s, editorFile.Path).GetExports().OfType<UClassCookedMetaData>().FirstOrDefault();
        string code;
        lock (DecompilerLock)
        {
            ct.ThrowIfCancellationRequested();
            code = string.Join("\n\n", classes.Select(c => c.DecompileBlueprintToPseudo(metadata)));
        }
        return new { path = file.Path, language = "cpp-like-pseudocode", note = "Generated from cooked Blueprint data. This is approximate pseudocode, not the original C++ or Blueprint source.",
            lines = Paginate(code.Replace("\r\n", "\n").Split('\n'), offset, limit) };
    }, ct);

    public Task<object> InspectDataFile(string id, string path, string format, string? pointer, int offset, int limit, CancellationToken ct) => InSession(id, s =>
    {
        var file = File(s, path); CheckSize(file);
        using var reader = file.CreateReader();
        object value = format.ToLowerInvariant() switch
        {
            "locres" => new FTextLocalizationResource(reader),
            "locmeta" => new FTextLocalizationMetaDataResource(reader),
            "binaryconfig" => new FConfigCacheIni(reader),
            _ => throw new ArgumentException("format must be locres, locmeta or binaryConfig. Use load_registry for AssetRegistry.bin.")
        };
        var json = ToJson(value);
        if (!string.IsNullOrEmpty(pointer)) json = JsonPointer(json, pointer);
        if (json is JArray array) return new { path = file.Path, format, pointer, data = Paginate(array.ToArray(), offset, limit) };
        if (json is JObject map) return new { path = file.Path, format, pointer, data = Paginate(map.Properties().Select(p => new { name = p.Name, value = p.Value }).ToArray(), offset, limit) };
        return new { path = file.Path, format, pointer, value = json };
    }, ct);

    public Task<object> Statistics(string id, string prefix, int offset, int limit, CancellationToken ct) => InSession(id, s =>
    {
        var files = s.Files.Where(f => Prefix(f.Path, prefix)).ToArray();
        return new { totalFiles = files.Length, packages = files.Count(f => f.IsUePackage), totalUncompressedBytes = files.Sum(f => f.Size),
            extensions = Paginate(files.GroupBy(f => f.Extension).OrderByDescending(g => g.Count()).Select(g => new
            { extension = g.Key, count = g.Count(), bytes = g.Sum(f => f.Size) }).ToArray(), offset, limit) };
    }, ct);
}
