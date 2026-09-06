using System.Text;
using FModel.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FModel.Mcp.Tests;

public sealed class ServerTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _repo = FindRepo();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "fmodel-mcp-tests-" + Guid.NewGuid().ToString("N"));
    private FModelService _service = null!;
    private FModelTools _tools = null!;
    private string Fixture(params string[] paths) => Path.Combine([_repo, "CUE4Parse", "CUE4Parse.Tests", "Fixtures", "UE5_8", .. paths]);

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_temp);
        var options = new ServerOptions { InputRoots = [_temp, Fixture()], OutputRoot = Path.Combine(_temp, "output") };
        _service = new(options, new(options));
        _tools = new(_service, options);
        return Task.CompletedTask;
    }
    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        // Only delete the exact uniquely-created test directory beneath the OS temp root.
        Assert.True(PathPolicy.IsWithin(Path.GetTempPath(), _temp));
        Directory.Delete(_temp, true);
    }
    private static string FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FModel.Mcp", "FModel.Mcp.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
    private static JObject Data(CallToolResult result)
    {
        Assert.False(result.IsError, string.Join("\n", result.Content.OfType<TextContentBlock>().Select(x => x.Text)));
        return JObject.Parse(result.Content.OfType<TextContentBlock>().First().Text);
    }
    private async Task<string> Open(string directory, string? mappings = null)
    {
        var result = Data(await _tools.OpenGame(new() { Directory = directory, Game = "GAME_UE5_8", MappingsPath = mappings, ReadScriptData = true }, Ct));
        Assert.True((int)result["fileCount"]! > 0);
        return (string)result["sessionId"]!;
    }

    [Fact]
    public async Task PakBrowsingRegistryLocalizationAndRawExport()
    {
        var id = await Open(Fixture("Pak", "Uncompressed"));
        var archives = Data(await _tools.Archives(id, Ct));
        Assert.True((bool)archives["items"]![0]!["mounted"]!);
        Assert.NotEmpty((JArray)Data(await _tools.Browse(id, Ct))["items"]!);
        var search = Data(await _tools.Search(id, Ct, query: "DefaultGame.ini", limit: 1));
        var path = (string)search["items"]![0]!["path"]!;
        var read = Data(await _tools.ReadFile(id, path, Ct));
        Assert.Contains("CulturesToStage", (string)read["text"]!);
        var info = Data(await _tools.AssetInfo(id, path, Ct, hash: true));
        Assert.Equal(64, ((string)info["sha256"]!).Length);
        Data(await _tools.LoadRegistry(id, "CUE4ParseFixtures/AssetRegistry.bin", Ct));
        var registry = Data(await _tools.SearchRegistry(id, Ct, className: "Texture2D"));
        Assert.True((int)registry["total"]! > 0);
        Data(await _tools.LoadLocalization(id, Ct, "de"));
        var localization = Data(await _tools.SearchLocalization(id, Ct, query: "Frankfurt"));
        Assert.Contains("Grüße aus Frankfurt", localization.ToString());
        Data(await _tools.InspectDataFile(id, "CUE4ParseFixtures/Content/Localization/CUE4ParseFixtures/en/CUE4ParseFixtures.locres", "locres", Ct));
        Assert.Equal(4, (int)Data(await _tools.Statistics(id, Ct))["totalFiles"]!);
        var started = Data(await _tools.StartExport(id, new() { Paths = [path], Mode = "raw" }, Ct));
        var jobId = (string)started["jobId"]!;
        var final = await FinishJob(jobId);
        Assert.Equal("completed", (string)final["state"]!);
        var results = Data(await _tools.JobResults(jobId));
        var output = (string)results["results"]!["items"]![0]!["files"]![0]!;
        Assert.Contains("CulturesToStage", await File.ReadAllTextAsync(output));
        var manifest = Data(await _tools.ReadOutput(jobId));
        Assert.Contains("manifest", manifest["path"]!.ToString());
        Assert.True((await _tools.ReadOutput(jobId, "../../escape.txt")).IsError);
        Data(await _tools.CloseGame(id, Ct));
        Assert.True((await _tools.SessionInfo(id, Ct)).IsError);
    }

    [Fact]
    public async Task RealPackagePropertiesImportsPreviewAndConvertedExports()
    {
        var id = await Open(CopyIoStore("Tagged"));
        const string path = "CUE4ParseFixtures/Content/Fixtures/Properties/DA_AllProperties.uasset";
        var summary = Data(await _tools.InspectPackage(id, path, Ct));
        Assert.True((int)summary["exportCount"]! > 0);
        Assert.NotEmpty((JArray)Data(await _tools.InspectPackage(id, path, Ct, "exports"))["items"]!);
        Assert.NotEmpty((JArray)Data(await _tools.InspectPackage(id, path, Ct, "imports"))["items"]!);
        Assert.NotEmpty((JArray)Data(await _tools.InspectPackage(id, path, Ct, "names"))["items"]!);
        var properties = Data(await _tools.Properties(id, path, Ct, objectName: "DA_AllProperties", pointer: "/Properties"));
        Assert.Contains("Fixture_ANSI_123", properties.ToString());
        var pseudocode = Data(await _tools.DecompileBlueprint(id, "CUE4ParseFixtures/Content/Fixtures/Blueprints/BP_Fixture.uasset", Ct));
        Assert.Contains("class ", pseudocode.ToString());
        Assert.NotEmpty((JArray)Data(await _tools.References(id, path, Ct))["data"]!["items"]!);
        Data(await _tools.References(id, path, Ct, "referencers"));
        const string texture = "CUE4ParseFixtures/Content/Fixtures/Textures/T_BC1.uasset";
        var preview = await _tools.PreviewTexture(id, texture, Ct, objectName: "T_BC1", maxSize: 32);
        Assert.False(preview.IsError);
        var png = Assert.Single(preview.Content.OfType<ImageContentBlock>());
        using var bitmap = SKBitmap.Decode(png.Data.ToArray());
        Assert.Equal(32, bitmap.Width);
        Assert.Equal(32, bitmap.Height);
        foreach (var request in new ExportRequest[]
        {
            new() { Paths = [path], Mode = "properties" },
            new() { Paths = [texture], Mode = "converted" },
            new() { Paths = ["CUE4ParseFixtures/Content/Fixtures/Meshes/SM_Fixture.uasset"], Mode = "converted", ExportMaterials = false },
            new() { Paths = ["CUE4ParseFixtures/Content/Fixtures/Audio/SW_Format_PCM.uasset"], Mode = "audio" },
            new() { Paths = ["CUE4ParseFixtures/Content/Fixtures/Animations/SKEL_Fixture.uasset"], Mode = "converted", MeshFormat = "UEFormat" },
            new() { Paths = ["CUE4ParseFixtures/Content/Fixtures/Maps/Instancing.umap"], Mode = "converted", MeshFormat = "USD", ExportMaterials = false }
        })
        {
            var started = Data(await _tools.StartExport(id, request, Ct));
            var jobId = (string)started["jobId"]!;
            var final = await FinishJob(jobId);
            var results = Data(await _tools.JobResults(jobId));
            Assert.True((string)final["state"]! == "completed", results.ToString());
            var files = (JArray)results["results"]!["items"]![0]!["files"]!;
            Assert.NotEmpty(files);
            Assert.All(files, p => Assert.True(new FileInfo((string)p!).Length > 0));
        }
    }

    [Fact]
    public async Task UnversionedMappingsAndIoStoreReferences()
    {
        var directory = CopyIoStore("Unversioned");
        var id = await Open(directory, Fixture("Mappings", "CUE4ParseFixtures-Uncompressed.usmap"));
        var info = Data(await _tools.SessionInfo(id, Ct)); Assert.True((bool)info["hasIoStoreGlobalData"]!);
        const string path = "CUE4ParseFixtures/Content/Fixtures/Textures/T_BC1.uasset";
        Assert.NotEmpty(Data(await _tools.Properties(id, path, Ct, objectName: "T_BC1")));
        Data(await _tools.References(id, path, Ct, "referencers"));
        Data(await _tools.SetMappings(id, Fixture("Mappings", "CUE4ParseFixtures-Zstandard.usmap"), Ct));
        Data(await _tools.LoadVirtualPaths(id, Ct));
    }

    [Fact]
    public async Task ErrorsPagingComparisonAndCancellation()
    {
        var a = Path.Combine(_temp, "A", "Game"); var b = Path.Combine(_temp, "B", "Game");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "same.ini"), "same"); File.WriteAllText(Path.Combine(b, "same.ini"), "same");
        File.WriteAllText(Path.Combine(a, "changed.ini"), "old"); File.WriteAllText(Path.Combine(b, "changed.ini"), "new content");
        File.WriteAllText(Path.Combine(a, "removed.ini"), "gone"); File.WriteAllText(Path.Combine(b, "added.ini"), "added");
        var left = await Open(a); var right = await Open(b);
        var page1 = Data(await _tools.Search(left, Ct, limit: 1));
        var page2 = Data(await _tools.Search(left, Ct, offset: (int)page1["nextOffset"]!, limit: 1));
        Assert.NotEqual((string)page1["items"]![0]!["path"]!, (string)page2["items"]![0]!["path"]!);
        var diff = Data(await _tools.Compare(left, right, Ct));
        Assert.Equal(3, (int)diff["data"]!["total"]!);
        Assert.Equal("metadata_only", (string)diff["comparison"]!);
        Assert.True((await _tools.Search(left, Ct, limit: 0)).IsError);
        Assert.True((await _tools.AssetInfo(left, "missing.uasset", Ct)).IsError);
        Assert.True((await _tools.OpenGame(new() { Directory = Path.GetPathRoot(_temp)!, Game = "GAME_UE5_8" }, Ct)).IsError);
        Assert.True((await _tools.SubmitKeys(left, new() { ["invalid"] = new('A', 64) }, Ct)).IsError);
        var job = Data(await _tools.StartExport(left, new() { Paths = ["Game/same.ini", "Game/changed.ini"], Mode = "raw" }, Ct));
        var jobId = (string)job["jobId"]!;
        Data(await _tools.CancelJob(jobId));
        var status = await FinishJob(jobId);
        Assert.Contains((string)status["state"]!, new[] { "cancelled", "completed" });
        Data(await _tools.CloseGame(left, Ct));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("file:stream")]
    [InlineData("folder/../escape")]
    [InlineData("folder./file")]
    [InlineData("CON.txt")]
    [InlineData("folder/LPT1")]
    public void RejectsUnsafeOutputPaths(string path) => Assert.Throws<ArgumentException>(() => PathPolicy.ValidateRelative(path));

    [Fact]
    public void ConversionDependencyCannotEscapeRoot()
    {
        Assert.Throws<ArgumentException>(() => FModelService.ValidateConversionOutput(_temp, Path.Combine(_temp, "..", "escape.glb")));
        Assert.False(PathPolicy.IsWithin(_temp, _temp + "-sibling/file"));
        Assert.Equal("ok", FModelService.JsonPointer(JObject.Parse("{\"a/b\":{\"~key\":[\"ok\"]}}"), "/a~1b/~0key/0").ToString());
    }

    [Fact]
    public async Task EncryptedPakRequiresKeyAndDoesNotEchoKeyValues()
    {
        var directory = Path.Combine(_temp, "encrypted"); Directory.CreateDirectory(directory);
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        PakFixture.WriteEncryptedIndex(Path.Combine(directory, "fixture.pak"), key);
        var opened = Data(await _tools.OpenGame(new() { Directory = directory, Game = "GAME_UE4_27" }, Ct));
        var id = (string)opened["sessionId"]!;
        Assert.Equal(0, (int)opened["fileCount"]!);
        Assert.NotEmpty((JArray)opened["requiredKeyGuids"]!);
        var wrong = Data(await _tools.SubmitKeys(id, new() { [new string('0', 32)] = new string('0', 64) }, Ct));
        Assert.Equal(0, (int)wrong["fileCount"]!);
        var submitted = await _tools.SubmitKeys(id, new() { [new string('0', 32)] = Convert.ToHexString(key) }, Ct);
        var mounted = Data(submitted);
        Assert.Equal(1, (int)mounted["fileCount"]!);
        Assert.DoesNotContain(Convert.ToHexString(key), submitted.Content.OfType<TextContentBlock>().Single().Text);
        var read = Data(await _tools.ReadFile(id, "SyntheticGame/Config/Test.ini", Ct));
        Assert.Equal("[Fixture]\nValue=42\n", (string)read["text"]!);
    }

    [Fact]
    public async Task LegacyPakRawExportIncludesCompanionFilesAndFailedBatchContinues()
    {
        var id = await Open(Fixture("LegacyPak", "Tagged", "Uncompressed"));
        const string path = "CUE4ParseFixtures/Content/Fixtures/Properties/DA_AllProperties.uasset";
        Data(await _tools.Properties(id, path, Ct, objectName: "DA_AllProperties", pointer: "/Properties"));
        Assert.True((await _tools.References(id, path, Ct, "referencers")).IsError);
        var raw = Data(await _tools.StartExport(id, new() { Paths = [path], Mode = "raw" }, Ct));
        var rawId = (string)raw["jobId"]!;
        Assert.Equal("completed", (string)(await FinishJob(rawId))["state"]!);
        var files = Data(await _tools.JobResults(rawId))["results"]!["items"]![0]!["files"]!.Values<string>().ToArray();
        Assert.Contains(files, p => p!.EndsWith(".uasset"));
        Assert.Contains(files, p => p!.EndsWith(".uexp"));
        var batch = Data(await _tools.StartExport(id, new() { Paths = [path, "CUE4ParseFixtures/Content/Fixtures/Textures/T_Streaming.uasset"], Mode = "converted" }, Ct));
        var batchId = (string)batch["jobId"]!;
        Assert.Equal("completed_with_errors", (string)(await FinishJob(batchId))["state"]!);
        var status = Data(await _tools.JobStatus(batchId));
        Assert.Equal(1, (int)status["failed"]!); Assert.Equal(1, (int)status["succeeded"]!);
    }

    [Fact]
    public async Task OfficialMcpClientDiscoversToolsResourcesPromptsAndCallsServer()
    {
        var config = Path.Combine(_temp, "config.json");
        await File.WriteAllTextAsync(config, JsonConvert.SerializeObject(new ServerOptions { InputRoots = [Fixture()], OutputRoot = Path.Combine(_temp, "stdio-output") }));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.Combine(_repo, "FModel.Mcp", "bin", configuration, "net10.0", "FModel.Mcp.dll");
        var transport = new StdioClientTransport(new() { Command = "dotnet", Arguments = [dll, "--config", config], Name = "FModel test" });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(34, tools.Count);
        Assert.Equal(tools.Count, tools.Select(t => t.Name).Distinct().Count());
        Assert.All(tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Description)));
        var capabilities = Data(await client.CallToolAsync("fmodel_capabilities", cancellationToken: timeout.Token));
        Assert.Equal("stdio", (string)capabilities["transport"]!);
        Assert.NotEmpty(await client.ListResourcesAsync(cancellationToken: timeout.Token));
        Assert.NotEmpty(await client.ListPromptsAsync(cancellationToken: timeout.Token));
        var guide = await client.ReadResourceAsync("fmodel://guide", cancellationToken: timeout.Token);
        Assert.NotEmpty(guide.Contents);
        var prompt = await client.GetPromptAsync("explore_game", new Dictionary<string, object?> { ["directory"] = Fixture("Pak", "Uncompressed"), ["objective"] = "Find textures" }, cancellationToken: timeout.Token);
        Assert.NotEmpty(prompt.Messages);
        var open = Data(await client.CallToolAsync("fmodel_open_game", new Dictionary<string, object?> { ["options"] = new { directory = Fixture("Pak", "Uncompressed"), game = "GAME_UE5_8" } }, cancellationToken: timeout.Token));
        var browse = Data(await client.CallToolAsync("fmodel_browse", new Dictionary<string, object?> { ["sessionId"] = (string)open["sessionId"]! }, cancellationToken: timeout.Token));
        Assert.NotEmpty((JArray)browse["items"]!);
        Assert.NotEmpty((await client.ReadResourceAsync("fmodel://sessions/" + (string)open["sessionId"]!, cancellationToken: timeout.Token)).Contents);
    }

    private async Task<JObject> FinishJob(string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var state = Data(await _tools.JobStatus(id));
            if ((string)state["state"]! is "completed" or "completed_with_errors" or "cancelled" or "failed")
            {
                // Wait until the manifest is finalized, too.
                var output = await _tools.ReadOutput(id);
                if (output.IsError != true) return state;
            }
            await Task.Delay(25, timeout.Token);
        }
    }

    private string CopyIoStore(string serialization)
    {
        var directory = Path.Combine(_temp, "IoStore-" + serialization); Directory.CreateDirectory(directory);
        foreach (var file in Directory.GetFiles(Fixture("IoStore", serialization, "Uncompressed")))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        foreach (var file in Directory.GetFiles(Fixture("IoStore", serialization), "global.*"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        return directory;
    }
}
