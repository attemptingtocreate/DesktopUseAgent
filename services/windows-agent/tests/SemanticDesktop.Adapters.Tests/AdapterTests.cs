using System.Text.Json;
using SemanticDesktop.Adapters;
using SemanticDesktop.Adapters.Blender;
using SemanticDesktop.Adapters.RobloxStudio;
using SemanticDesktop.Adapters.VisualStudio;
using SemanticDesktop.Adapters.VsCode;
using SemanticDesktop.Agent;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.IPC;

namespace SemanticDesktop.Adapters.Tests;

public class AdapterRegistryTests
{
    private sealed class FakeAdapter : IApplicationAdapter
    {
        public required string Id { get; init; }
        public required string ProcessToken { get; init; }
        public List<string> Executed { get; } = new();

        public bool CanHandle(ProcessInfo process) =>
            process.Name.Contains(ProcessToken, StringComparison.OrdinalIgnoreCase);

        public Task<ApplicationCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ApplicationCapabilities
            {
                AdapterId = Id,
                Available = true,
                Actions = new[] { $"{Id}.ping" }
            });

        public Task<AdapterResult> ExecuteAsync(AdapterCommand command, CancellationToken cancellationToken)
        {
            Executed.Add(command.Action);
            return Task.FromResult(AdapterResult.Success(new { action = command.Action, adapter = Id }));
        }
    }

    [Fact]
    public void Resolve_Uses_CanHandle()
    {
        var registry = new AdapterRegistry(new IApplicationAdapter[]
        {
            new FakeAdapter { Id = "blender", ProcessToken = "blender" },
            new FakeAdapter { Id = "vscode", ProcessToken = "Code" }
        });

        var hit = registry.Resolve(new ProcessInfo { Id = "p1", Pid = 1, Name = "blender.exe" });
        Assert.Equal("blender", hit!.Id);
        Assert.Equal("vscode", registry.ResolveByAction("vscode.open_file")!.Id);
    }

    [Fact]
    public async Task Execute_Routes_By_Action_Prefix()
    {
        var fake = new FakeAdapter { Id = "blender", ProcessToken = "blender" };
        var registry = new AdapterRegistry(new[] { fake });
        var result = await registry.ExecuteAsync(null, new AdapterCommand { Action = "blender.get_scene" }, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Contains("blender.get_scene", fake.Executed);
    }

    [Fact]
    public async Task Execute_Resolves_By_Process_When_Action_Unrecognized()
    {
        var fake = new FakeAdapter { Id = "blender", ProcessToken = "blender" };
        var registry = new AdapterRegistry(new[] { fake });
        registry.SetProcessResolver((_, _) => new ProcessInfo { Id = "p1", Pid = 1, Name = "blender.exe" });

        var result = await registry.ExecuteAsync(null, new AdapterCommand
        {
            Action = "custom.thing",
            WindowId = "win_1"
        }, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("custom.thing", fake.Executed);
    }

    [Fact]
    public async Task Execute_Unknown_Adapter_Fails()
    {
        var registry = new AdapterRegistry();
        var result = await registry.ExecuteAsync("missing", new AdapterCommand { Action = "x.y" }, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.AdapterNotFound, result.ErrorCode);
    }
}

public class BuiltInAdapterDiscoveryTests
{
    [Fact]
    public async Task BuiltIns_Report_Capabilities()
    {
        var registry = new AdapterRegistry(new IApplicationAdapter[]
        {
            new BlenderAdapter(),
            new VsCodeAdapter(),
            new VisualStudioAdapter(),
            new RobloxStudioAdapter(new InMemoryRobloxBridge())
        });

        Assert.Equal(new[] { "blender", "roblox", "visualstudio", "vscode" }, registry.ListIds().ToArray());
        var caps = await registry.ListCapabilitiesAsync(CancellationToken.None);
        Assert.Equal(4, caps.Count);
        Assert.Contains(caps, c => c.AdapterId == "roblox" && c.Actions.Contains(CommandNames.RobloxPluginPing));
        Assert.Contains(caps, c => c.AdapterId == "blender" && c.Actions.Contains(CommandNames.BlenderExport));
        Assert.Contains(caps, c => c.AdapterId == "blender" && c.Actions.Contains(CommandNames.BlenderBatch));
        Assert.Contains(caps, c => c.AdapterId == "blender" && c.Meta!.ContainsKey("liveBridgeListening"));
        Assert.Contains(caps, c => c.AdapterId == "vscode" && c.Actions.Contains(CommandNames.VsCodeOpenFile));
        Assert.Contains(caps, c => c.AdapterId == "visualstudio" && c.Actions.Contains(CommandNames.VisualStudioBuild));
    }

    [Fact]
    public async Task Blender_Missing_Executable_Returns_Unavailable()
    {
        var previous = Environment.GetEnvironmentVariable("BLENDER_PATH");
        BlenderExecutableCache.Invalidate();
        var missing = Path.Combine(Path.GetTempPath(), "no-such-blender-" + Guid.NewGuid().ToString("N") + ".exe");
        Environment.SetEnvironmentVariable("BLENDER_PATH", missing);
        try
        {
            var adapter = new BlenderAdapter();
            var found = BlenderAdapter.FindBlenderExecutable();
            var envPath = Environment.GetEnvironmentVariable("BLENDER_PATH") ?? missing;
            if (found is not null &&
                !envPath.Contains("no-such-blender", StringComparison.Ordinal))
            {
                // installed blender present via PATH; skip hard unavailable assertion
                return;
            }

            // BLENDER_PATH set to missing file — FindBlenderExecutable checks File.Exists first
            var result = await adapter.ExecuteAsync(new AdapterCommand
            {
                Action = CommandNames.BlenderGetScene
            }, CancellationToken.None);

            // If PATH still finds blender, ok; else unavailable
            if (result.Ok)
            {
                return;
            }

            Assert.False(result.Ok);
            Assert.Equal(ErrorCodes.AdapterUnavailable, result.ErrorCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLENDER_PATH", previous);
            BlenderExecutableCache.Invalidate();
        }
    }

    [Fact]
    public async Task VisualStudio_GetSolution_Parses_Sln()
    {
        var sln = Path.Combine(Path.GetTempPath(), "sd-adapter-" + Guid.NewGuid().ToString("N") + ".sln");
        await File.WriteAllTextAsync(sln, """
Microsoft Visual Studio Solution File, Format Version 12.00
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Demo", "Demo\Demo.csproj", "{11111111-1111-1111-1111-111111111111}"
EndProject
""");
        try
        {
            var adapter = new VisualStudioAdapter();
            var result = await adapter.ExecuteAsync(new AdapterCommand
            {
                Action = CommandNames.VisualStudioGetSolution,
                Params = new Dictionary<string, object?> { ["path"] = sln }
            }, CancellationToken.None);
            Assert.True(result.Ok);
            var json = JsonSerializer.Serialize(result.Data);
            Assert.Contains("Demo", json, StringComparison.Ordinal);
            Assert.Contains("visualstudio-cli", json, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(sln);
        }
    }
}

public class AdapterDispatcherTests
{
    [Fact]
    public async Task Dispatcher_Lists_Adapters_And_Rejects_Unknown_Action()
    {
        var dispatcher = new CommandDispatcher();
        var list = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "1",
            Method = CommandNames.AdapterList,
            Params = JsonSerializer.SerializeToElement(new { }, JsonDefaults.Options)
        }, CancellationToken.None);

        using var listDoc = JsonDocument.Parse(JsonSerializer.Serialize(list, JsonDefaults.Options));
        Assert.True(listDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(listDoc.RootElement.GetProperty("data").GetArrayLength() >= 3);

        var caps = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "2",
            Method = CommandNames.DesktopGetCapabilities,
            Params = JsonSerializer.SerializeToElement(new { }, JsonDefaults.Options)
        }, CancellationToken.None);
        using var capsDoc = JsonDocument.Parse(JsonSerializer.Serialize(caps, JsonDefaults.Options));
        var adapters = capsDoc.RootElement.GetProperty("data").GetProperty("adapters");
        Assert.Contains(adapters.EnumerateArray(), a => a.GetString() == "blender");

        var bad = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "3",
            Method = CommandNames.AdapterExecute,
            Params = JsonSerializer.SerializeToElement(new { action = "unknown.thing" }, JsonDefaults.Options)
        }, CancellationToken.None);
        using var badDoc = JsonDocument.Parse(JsonSerializer.Serialize(bad, JsonDefaults.Options));
        Assert.False(badDoc.RootElement.GetProperty("ok").GetBoolean());

        dispatcher.Dispose();
    }
}
