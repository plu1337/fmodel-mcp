using FModel.Mcp;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FModel.Mcp.Tests;

public sealed class SavedGameTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "fmodel-saved-tests-" + Guid.NewGuid().ToString("N"));
    public SavedGameTests() => Directory.CreateDirectory(_temp);
    public void Dispose()
    {
        Assert.True(PathPolicy.IsWithin(Path.GetTempPath(), _temp));
        Directory.Delete(_temp, true);
    }
    private static string Fixture(params string[] paths)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FModel.Mcp", "FModel.Mcp.csproj")))
                return Path.Combine([directory.FullName, "CUE4Parse", "CUE4Parse.Tests", "Fixtures", "UE5_8", .. paths]);
        throw new DirectoryNotFoundException();
    }
    private (ServerOptions Options, JObject Settings, string Path) Setup(string directory, string name = "Fixture", string? mainKey = null)
    {
        var path = Path.Combine(_temp, "AppSettings.json");
        var settings = JObject.FromObject(new
        {
            GameDirectory = directory, OutputDirectory = _temp,
            PerDirectory = new Dictionary<string, object>
            {
                [directory] = new { GameName = name, GameDirectory = directory, UeVersion = (int)CUE4Parse.UE4.Versions.EGame.GAME_UE5_8,
                    TexturePlatform = 0, AesKeys = new { mainKey, dynamicKeys = Array.Empty<object>() },
                    Endpoints = new[] { new { Overwrite = false, FilePath = "" }, new { Overwrite = false, FilePath = "" } } }
            }
        });
        File.WriteAllText(path, settings.ToString());
        return (new ServerOptions { FModelSettingsPath = path, InputRoots = [_temp, Fixture()], OutputRoot = Path.Combine(_temp, "exports") }, settings, path);
    }
    private static JObject Data(CallToolResult response)
    {
        var text = response.Content.OfType<TextContentBlock>().First().Text;
        Assert.False(response.IsError, text);
        return JObject.Parse(text);
    }

    [Fact]
    public async Task SavedKeysMountEncryptedPakWithoutAppearingInResponses()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_temp, "game")).FullName;
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var hex = Convert.ToHexString(key);
        PakFixture.WriteEncryptedIndex(Path.Combine(directory, "encrypted.pak"), key);
        var setup = Setup(directory, mainKey: hex);
        await using var service = new FModelService(setup.Options, new(setup.Options));
        var tools = new FModelTools(service, setup.Options);
        var saved = Data(await tools.ListSavedGames());
        Assert.Equal(1, (int)saved["games"]![0]!["savedKeyCount"]!);
        Assert.DoesNotContain(hex, saved.ToString());
        Assert.Equal("GAME_UE5_8", (string)saved["games"]![0]!["game"]!);
        var opened = Data(await tools.OpenSavedGame(CancellationToken.None));
        Assert.True((int)opened["fileCount"]! > 0);
        Assert.Equal(1, (int)opened["suppliedKeyCount"]!);
        Assert.DoesNotContain(hex, opened.ToString());
        setup.Settings["PerDirectory"]![directory]!["GameName"] = "Renamed";
        File.WriteAllText(setup.Path, setup.Settings.ToString());
        Assert.Equal("Renamed", (string)Data(await tools.ListSavedGames())["games"]![0]!["name"]!);
    }

    [Fact]
    public async Task CachedMappingsRequireAnExplicitChoiceWhenAmbiguous()
    {
        var setup = Setup(Fixture("Pak", "Uncompressed"), "CUE4ParseFixtures");
        var cache = Directory.CreateDirectory(Path.Combine(_temp, ".data", "mappings")).FullName;
        var first = Path.Combine(cache, "CUE4ParseFixtures-first.usmap");
        File.Copy(Fixture("Mappings", "CUE4ParseFixtures-Uncompressed.usmap"), first);
        await using var service = new FModelService(setup.Options, new(setup.Options));
        var tools = new FModelTools(service, setup.Options);
        Assert.Equal(first, (string)Data(await tools.ListSavedGames())["games"]![0]!["mappingsPath"]!);
        File.Copy(first, Path.Combine(cache, "CUE4ParseFixtures-second.usmap"));
        Assert.True((await tools.OpenSavedGame(CancellationToken.None)).IsError);
        var opened = Data(await tools.OpenSavedGame(CancellationToken.None, mappingsPath: first));
        Assert.True((bool)opened["hasMappings"]!);
    }

    [Fact]
    public async Task SavedSettingsCannotExpandTheInputAllowlist()
    {
        var setup = Setup(Fixture("Pak", "Uncompressed"));
        setup.Options.InputRoots = [_temp];
        await using var service = new FModelService(setup.Options, new(setup.Options));
        var tools = new FModelTools(service, setup.Options);
        Assert.False((bool)Data(await tools.ListSavedGames())["games"]![0]!["allowed"]!);
        Assert.True((await tools.OpenSavedGame(CancellationToken.None)).IsError);
    }

    [Fact]
    public async Task InvalidSavedSettingsDoNotEchoTheirContents()
    {
        var setup = Setup(_temp);
        File.WriteAllText(setup.Path, "{private-content-that-must-not-be-echoed");
        await using var service = new FModelService(setup.Options, new(setup.Options));
        var result = await new FModelTools(service, setup.Options).ListSavedGames();
        Assert.True(result.IsError);
        Assert.DoesNotContain("private-content", result.Content.OfType<TextContentBlock>().First().Text);
    }
}
