using System.Text.Json;

namespace FModel.Mcp;

public sealed class ServerOptions
{
    public string[] InputRoots { get; set; } = [Environment.CurrentDirectory];
    public string OutputRoot { get; set; } = Path.Combine(Environment.CurrentDirectory, "Exports", "Mcp");
    public string? OodleLibrary { get; set; }
    public string? FModelSettingsPath { get; set; }
    public int MaxSessions { get; set; } = 4;
    public int MaxJobs { get; set; } = 32;
    public int MaxBatchAssets { get; set; } = 500;
    public long MaxReadBytes { get; set; } = 256 * 1024 * 1024;
    public int MaxResponseChars { get; set; } = 100_000;

    public static ServerOptions? Load(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.Error.WriteLine("FModel.Mcp [--config <absolute-json-path>]\nLocal stdio MCP server. See docs/mcp.md for setup and commands.");
            return null;
        }
        if (args.Length != 0 && !(args.Length == 2 && args[0] == "--config"))
            throw new ArgumentException("Use --config <path> or --help.");
        var path = args.Length == 2 ? Path.GetFullPath(args[1]) : Environment.GetEnvironmentVariable("FMODEL_MCP_CONFIG");
        var options = path is null ? new ServerOptions() : JsonSerializer.Deserialize<ServerOptions>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        }) ?? throw new ArgumentException("Configuration must be a JSON object.");
        var basePath = path is null ? Environment.CurrentDirectory : Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (options.InputRoots is not { Length: > 0 } || options.InputRoots.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Configure at least one nonempty inputRoots directory.");
        options.InputRoots = options.InputRoots.Select(p => Path.GetFullPath(p, basePath)).ToArray();
        options.OutputRoot = Path.GetFullPath(options.OutputRoot, basePath);
        if (options.OodleLibrary is not null) options.OodleLibrary = Path.GetFullPath(options.OodleLibrary, basePath);
        if (options.FModelSettingsPath is not null) options.FModelSettingsPath = Path.GetFullPath(options.FModelSettingsPath, basePath);
        if (options.MaxSessions is < 1 or > 16 || options.MaxJobs is < 1 or > 1000 || options.MaxBatchAssets is < 1 or > 10000 ||
            options.MaxReadBytes is < 1024 or > 2147483647 || options.MaxResponseChars is < 1024 or > 1_000_000)
            throw new ArgumentException("Invalid server limits; see docs/mcp.md.");
        return options;
    }
}
