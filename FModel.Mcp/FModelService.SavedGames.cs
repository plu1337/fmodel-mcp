using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json.Linq;

namespace FModel.Mcp;

public sealed partial class FModelService
{
    private sealed record SavedGame(string Name, bool Selected, OpenGameOptions Options, string[] MappingCandidates,
        bool ExplicitMapping, bool Allowed)
    {
        public object Summary() => new
        {
            name = Name, selected = Selected, directory = Options.Directory, game = Options.Game,
            texturePlatform = Options.TexturePlatform, allowed = Allowed,
            directoryExists = Directory.Exists(Options.Directory), mappingsPath = Options.MappingsPath,
            mappingCandidates = MappingCandidates, savedKeyCount = Options.AesKeys?.Count ?? 0
        };
    }

    public object ListSavedGames() => new
    {
        configured = _options.FModelSettingsPath is not null,
        settingsPath = _options.FModelSettingsPath,
        games = ReadSavedGames().Select(g => g.Summary()).ToArray()
    };

    public async Task<object> OpenSavedGame(string selector, string? mappingsPath, bool readScriptData, CancellationToken ct)
    {
        var games = ReadSavedGames();
        var matches = games.Where(g => string.IsNullOrWhiteSpace(selector) ? g.Selected :
            string.Equals(g.Name, selector, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(g.Options.Directory, selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Select exactly one saved game by name or directory using fmodel_list_saved_games. An empty selector uses FModel's last saved selection.");
        var saved = matches[0];
        var request = saved.Options;
        // A settings file cannot grant filesystem access. The installer/user must configure roots separately.
        _paths.Input(request.Directory, true);
        if (mappingsPath is not null) request.MappingsPath = mappingsPath;
        else if (!saved.ExplicitMapping && saved.MappingCandidates.Length > 1)
            throw new InvalidOperationException("Several cached mappings match this game. Choose the correct build from mappingCandidates and pass mappingsPath; the server will not guess.");
        request.ReadScriptData = readScriptData;
        return await OpenGame(request, ct);
    }

    private SavedGame[] ReadSavedGames()
    {
        if (_options.FModelSettingsPath is null) return [];
        var settingsPath = _options.FModelSettingsPath;
        PathPolicy.RejectLinks(settingsPath);
        if (!System.IO.File.Exists(settingsPath)) throw new FileNotFoundException("Configured FModel settings file was not found.");
        const int maxSettingsBytes = 16 * 1024 * 1024;
        if (new FileInfo(settingsPath).Length > maxSettingsBytes) throw new ArgumentException("FModel settings exceed the 16 MiB limit.");
        JObject settings;
        try
        {
            var json = System.IO.File.ReadAllText(settingsPath);
            if (json.Length > maxSettingsBytes) throw new ArgumentException("FModel settings exceed the size limit.");
            settings = JObject.Parse(json);
        }
        catch (Newtonsoft.Json.JsonException) { throw new ArgumentException("FModel settings contain invalid JSON. Save the settings in FModel and retry."); }
        var current = (string?)settings["GameDirectory"];
        var output = (string?)settings["OutputDirectory"];
        var cache = output is not null && Path.IsPathFullyQualified(output) ? Path.Combine(output, ".data", "mappings") : null;
        var directories = settings["PerDirectory"] as JObject;
        if (directories is null) return [];
        if (directories.Count > 100) throw new ArgumentException("FModel has more than 100 saved directories; narrow the settings file.");
        return directories.Properties().Select(entry =>
        {
            var value = (JObject)entry.Value;
            var directory = (string?)value["GameDirectory"] ?? entry.Name;
            var name = (string?)value["GameName"] ?? directory;
            if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("A saved game directory is not absolute. Update it in FModel.");
            var allowed = _options.InputRoots.Any(root => PathPolicy.IsWithin(root, directory));
            var game = SavedEnum<EGame>(value["UeVersion"], "engine profile");
            var platform = SavedEnum<ETexturePlatform>(value["TexturePlatform"], "texture platform");
            var endpoint = (value["Endpoints"] as JArray)?.ElementAtOrDefault(1);
            var explicitMapping = (bool?)endpoint?["Overwrite"] == true && !string.IsNullOrWhiteSpace((string?)endpoint?["FilePath"]);
            var mappingPath = explicitMapping ? (string?)endpoint?["FilePath"] : null;
            string[] candidates = [];
            if (!explicitMapping && cache is not null && Directory.Exists(cache) &&
                _options.InputRoots.Any(root => PathPolicy.IsWithin(root, cache)))
            {
                PathPolicy.RejectLinks(cache);
                candidates = Directory.EnumerateFiles(cache).Where(path =>
                    Path.GetFileName(path).Contains(name, StringComparison.OrdinalIgnoreCase) &&
                    (path.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase)))
                    .Order(StringComparer.OrdinalIgnoreCase).Take(201).ToArray();
                if (candidates.Length > 200) throw new InvalidOperationException("Too many cached mapping candidates. Configure a mapping override in FModel.");
                if (candidates.Length == 1) mappingPath = candidates[0];
            }
            var keys = new Dictionary<string, string>();
            var mainKey = (string?)value["AesKeys"]?["MainKey"] ?? (string?)value["AesKeys"]?["mainKey"];
            if (!string.IsNullOrWhiteSpace(mainKey)) keys[new string('0', 32)] = mainKey;
            var dynamicKeys = value["AesKeys"]?["DynamicKeys"] ?? value["AesKeys"]?["dynamicKeys"];
            if (dynamicKeys is JArray dynamicArray)
                foreach (var key in dynamicArray)
                {
                    var guid = (string?)key["Guid"] ?? (string?)key["guid"];
                    var hex = (string?)key["Key"] ?? (string?)key["key"];
                    if (!string.IsNullOrWhiteSpace(guid) && !string.IsNullOrWhiteSpace(hex)) keys[guid] = hex;
                }
            var versioning = value["Versioning"];
            var customVersions = new Dictionary<string, int>();
            if (versioning?["CustomVersions"] is JArray customArray)
                foreach (var version in customArray)
                {
                    var guid = version["Key"]?.ToObject<CUE4Parse.UE4.Objects.Core.Misc.FGuid>();
                    if (guid is not null && version["Version"] is JValue number) customVersions[guid.Value.ToString()] = number.Value<int>();
                }
            return new SavedGame(name, string.Equals(current, directory, StringComparison.OrdinalIgnoreCase), new()
            {
                Directory = directory, Game = game, TexturePlatform = platform, MappingsPath = mappingPath,
                AesKeys = keys, CustomVersions = customVersions,
                VersionOptions = versioning?["Options"]?.ToObject<Dictionary<string, bool>>(),
                MapStructTypes = versioning?["MapStructTypes"]?.ToObject<Dictionary<string, KeyValuePair<string, string>>>()
            }, candidates, explicitMapping, allowed);
        }).ToArray();
    }

    private static string SavedEnum<T>(JToken? token, string label) where T : struct, Enum
    {
        if (token is null || !Enum.TryParse<T>(token.ToString(), true, out var value) || !Enum.IsDefined(value))
            throw new ArgumentException($"A saved {label} is unsupported by this parser. Select a supported profile in FModel or use fmodel_open_game with explicit options.");
        return value.ToString();
    }
}
