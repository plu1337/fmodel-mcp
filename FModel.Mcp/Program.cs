using FModel.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

// Keep the actual stdout handle for MCP; parser libraries occasionally use Console.WriteLine.
Console.SetOut(Console.Error);
try
{
    var options = ServerOptions.Load(args);
    if (options is null) return;
    Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Console(standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose).CreateLogger();
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<PathPolicy>();
    builder.Services.AddSingleton<FModelService>();
    builder.Services.AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "fmodel", Version = "1.0.0" };
        o.ServerInstructions = WorkflowContent.Guide;
    }).WithStdioServerTransport()
      .WithTools<FModelTools>().WithResources<FModelResources>().WithPrompts<FModelPrompts>();
    using var host = builder.Build();
    await host.RunAsync();
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FModel MCP startup failed: {exception.Message}");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
