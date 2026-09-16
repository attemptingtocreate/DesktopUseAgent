using System.Net;
using System.Text;
using System.Text.Json;
using SemanticDesktop.Adapters.Blender;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Core.Serialization;

namespace SemanticDesktop.Adapters.Tests;

public sealed class InMemoryBlenderBridge : IBlenderBridge
{
    private readonly Dictionary<string, InMemoryBlenderSession> _sessions = new(StringComparer.Ordinal);
    private bool _listening = true;

    public bool IsListening => _listening;

    public int Port { get; } = 18375;

    public string Token { get; init; } = "test-blender-token";

    public Func<string, Dictionary<string, object?>?, BlenderCommandResult>? Handler { get; set; }

    public void RegisterSession(string sessionId, bool pollingEnabled = true)
    {
        _sessions[sessionId] = new InMemoryBlenderSession
        {
            SessionId = sessionId,
            PollingEnabled = pollingEnabled,
            LastSeenUtc = DateTimeOffset.UtcNow
        };
    }

    public BlenderBridgeHealth GetHealth()
    {
        var sessions = _sessions.Values
            .Select(s => new BlenderBridgeSessionInfo
            {
                SessionId = s.SessionId,
                PollingEnabled = s.PollingEnabled,
                LastSeenUtc = s.LastSeenUtc,
                HasOutstandingCommand = s.OutstandingCommand is not null
            })
            .ToList();

        return new BlenderBridgeHealth
        {
            Listening = _listening,
            AddonConnected = sessions.Any(s => s.PollingEnabled),
            Port = Port,
            Sessions = sessions
        };
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _listening = false;
        return Task.CompletedTask;
    }

    public async Task<BlenderCommandResult> ExecuteCommandAsync(
        string operation,
        Dictionary<string, object?>? parameters,
        string? sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!_listening)
        {
            return BlenderCommandResult.Fail(ErrorCodes.AdapterUnavailable, "Bridge not listening.");
        }

        if (!BlenderBridgeOperations.Allowlist.Contains(operation))
        {
            return BlenderCommandResult.Fail(ErrorCodes.Unsupported, "Operation not allowed.");
        }

        var session = ResolveSession(sessionId);
        if (session is null || !session.PollingEnabled)
        {
            return BlenderCommandResult.Fail(ErrorCodes.AdapterUnavailable, "No connected session.");
        }

        if (session.OutstandingCommand is not null)
        {
            return BlenderCommandResult.Fail(ErrorCodes.InvalidArgument, "Outstanding command exists.");
        }

        session.OutstandingCommand = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<BlenderCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PendingResult = tcs;

        _ = Task.Run(async () =>
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            session.OutstandingCommand = null;
            var result = Handler?.Invoke(operation, parameters)
                         ?? BlenderCommandResult.Success(new { operation });
            session.PendingResult?.TrySetResult(result);
            session.PendingResult = null;
        }, cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        await using var reg = timeoutCts.Token.Register(() =>
            tcs.TrySetResult(BlenderCommandResult.Fail(ErrorCodes.Timeout, "Timed out.")));
        return await tcs.Task.ConfigureAwait(false);
    }

    public void Dispose() => _listening = false;

    private InMemoryBlenderSession? ResolveSession(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return _sessions.TryGetValue(sessionId, out var exact) ? exact : null;
        }

        return _sessions.Values.FirstOrDefault(s => s.PollingEnabled);
    }

    private sealed class InMemoryBlenderSession
    {
        public required string SessionId { get; init; }
        public bool PollingEnabled { get; init; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public string? OutstandingCommand { get; set; }
        public TaskCompletionSource<BlenderCommandResult>? PendingResult { get; set; }
    }
}

public sealed class FakeBlenderProcessRunner : IBlenderProcessRunner
{
    public List<(string Exe, IReadOnlyList<string> Args)> Invocations { get; } = new();
    public List<(string Exe, string BlendPath)> GuiLaunches { get; } = new();
    public Func<string, IReadOnlyList<string>, BlenderProcessResult>? Handler { get; set; }

    public Task<BlenderProcessResult> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        Invocations.Add((executable, args));
        var result = Handler?.Invoke(executable, args)
                     ?? BlenderProcessResult.Success("SD_JSON:{\"ok\":true,\"provider\":\"blender-python\"}");
        return Task.FromResult(result);
    }

    public Task<BlenderProcessResult> LaunchGuiAsync(string executable, string blendPath, CancellationToken cancellationToken)
    {
        GuiLaunches.Add((executable, blendPath));
        return Task.FromResult(BlenderProcessResult.Success("GUI_LAUNCHED"));
    }
}

public class BlenderAdapterTests
{
    [Fact]
    public async Task Capabilities_Include_Live_Readiness()
    {
        var bridge = new InMemoryBlenderBridge();
        bridge.RegisterSession("s1");
        var adapter = new BlenderAdapter(bridge);
        var caps = await adapter.GetCapabilitiesAsync(CancellationToken.None);
        Assert.Contains(CommandNames.BlenderBatch, caps.Actions);
        Assert.Contains(CommandNames.BlenderRender, caps.Actions);
        Assert.True(caps.Meta!.ContainsKey("backgroundAvailable"));
        Assert.True(caps.Meta.ContainsKey("liveBridgeListening"));
        Assert.True(caps.Meta.ContainsKey("liveSessionConnected"));
    }

    [Fact]
    public async Task GetScene_Live_Mode_Routes_To_Bridge()
    {
        var bridge = new InMemoryBlenderBridge();
        bridge.RegisterSession("live1");
        bridge.Handler = (_, _) => BlenderCommandResult.Success(new { name = "Scene", provider = "blender-live" });
        var adapter = new BlenderAdapter(bridge, new FakeBlenderProcessRunner());

        var result = await adapter.ExecuteAsync(new AdapterCommand
        {
            Action = CommandNames.BlenderGetScene,
            Params = new Dictionary<string, object?> { ["mode"] = "live", ["sessionId"] = "live1" }
        }, CancellationToken.None);

        Assert.True(result.Ok);
        var json = JsonSerializer.Serialize(result.Data);
        Assert.Contains("Scene", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_Uses_Single_Process_Invocation()
    {
        var runner = new FakeBlenderProcessRunner();
        var adapter = new BlenderAdapter(null, runner);
        Environment.SetEnvironmentVariable("BLENDER_PATH", CreateFakeBlenderExe());

        try
        {
            var result = await adapter.ExecuteAsync(new AdapterCommand
            {
                Action = CommandNames.BlenderBatch,
                Params = new Dictionary<string, object?>
                {
                    ["operations"] = new object[]
                    {
                        new Dictionary<string, object?> { ["op"] = "get_scene" },
                        new Dictionary<string, object?> { ["op"] = "get_objects" }
                    }
                }
            }, CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Single(runner.Invocations);
            Assert.Contains("--python", runner.Invocations[0].Args);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLENDER_PATH", null);
            BlenderExecutableCache.Invalidate();
        }
    }

    [Fact]
    public async Task Batch_Rejects_Non_Allowlisted_Operation()
    {
        var adapter = new BlenderAdapter(null, new FakeBlenderProcessRunner());
        Environment.SetEnvironmentVariable("BLENDER_PATH", CreateFakeBlenderExe());
        try
        {
            var result = await adapter.ExecuteAsync(new AdapterCommand
            {
                Action = CommandNames.BlenderBatch,
                Params = new Dictionary<string, object?>
                {
                    ["operations"] = new object[]
                    {
                        new Dictionary<string, object?> { ["op"] = "execute_python" }
                    }
                }
            }, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal(ErrorCodes.InvalidArgument, result.ErrorCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLENDER_PATH", null);
            BlenderExecutableCache.Invalidate();
        }
    }

    [Fact]
    public async Task Render_Blocks_Overwrite_By_Default()
    {
        var output = Path.Combine(Path.GetTempPath(), "sd-blender-render-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllTextAsync(output, "existing");
        var adapter = new BlenderAdapter(null, new FakeBlenderProcessRunner());
        Environment.SetEnvironmentVariable("BLENDER_PATH", CreateFakeBlenderExe());
        try
        {
            var result = await adapter.ExecuteAsync(new AdapterCommand
            {
                Action = CommandNames.BlenderRender,
                Params = new Dictionary<string, object?> { ["output"] = output }
            }, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Contains("overwrite", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(output);
            Environment.SetEnvironmentVariable("BLENDER_PATH", null);
            BlenderExecutableCache.Invalidate();
        }
    }

    [Fact]
    public async Task ImportMesh_Validates_Format()
    {
        var input = Path.Combine(Path.GetTempPath(), "mesh.bad");
        await File.WriteAllTextAsync(input, "x");
        var adapter = new BlenderAdapter(null, new FakeBlenderProcessRunner());
        Environment.SetEnvironmentVariable("BLENDER_PATH", CreateFakeBlenderExe());
        try
        {
            var result = await adapter.ExecuteAsync(new AdapterCommand
            {
                Action = CommandNames.BlenderImportMesh,
                Params = new Dictionary<string, object?> { ["path"] = input }
            }, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal(ErrorCodes.InvalidArgument, result.ErrorCode);
        }
        finally
        {
            File.Delete(input);
            Environment.SetEnvironmentVariable("BLENDER_PATH", null);
            BlenderExecutableCache.Invalidate();
        }
    }

    [Fact]
    public async Task ExecutePython_Never_Uses_Live_Bridge()
    {
        var runner = new FakeBlenderProcessRunner();
        var bridge = new InMemoryBlenderBridge();
        bridge.RegisterSession("s1");
        bridge.Handler = (_, _) => throw new InvalidOperationException("bridge should not be called");
        var adapter = new BlenderAdapter(bridge, runner);
        Environment.SetEnvironmentVariable("BLENDER_PATH", CreateFakeBlenderExe());
        try
        {
            Assert.DoesNotContain("execute_python", BlenderBridgeOperations.Allowlist);
            var result = await adapter.ExecuteAsync(new AdapterCommand
            {
                Action = CommandNames.BlenderExecutePython,
                Params = new Dictionary<string, object?> { ["code"] = "print(1)" }
            }, CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Single(runner.Invocations);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLENDER_PATH", null);
            BlenderExecutableCache.Invalidate();
        }
    }

    [Fact]
    public async Task Registry_Resolves_Adapter_By_Window_Context()
    {
        var registry = new AdapterRegistry(new IApplicationAdapter[] { new BlenderAdapter(null, new FakeBlenderProcessRunner()) });
        registry.SetProcessResolver((_, _) => new ProcessInfo { Id = "p1", Pid = 1, Name = "blender.exe" });

        var result = await registry.ExecuteAsync(null, new AdapterCommand
        {
            Action = "unknown.custom",
            WindowId = "win_abc"
        }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.Unsupported, result.ErrorCode);
    }

    [Fact]
    public async Task ImportMesh_Does_Not_Pass_Mesh_Path_As_Blend_Arg()
    {
        var runner = new FakeBlenderProcessRunner();
        var adapter = new BlenderAdapter(null, runner);
        Environment.SetEnvironmentVariable("BLENDER_PATH", CreateFakeBlenderExe());
        var mesh = Path.Combine(Path.GetTempPath(), "mesh-" + Guid.NewGuid().ToString("N") + ".obj");
        await File.WriteAllTextAsync(mesh, "o x");
        try
        {
            var result = await adapter.ExecuteAsync(new AdapterCommand
            {
                Action = CommandNames.BlenderImportMesh,
                Params = new Dictionary<string, object?> { ["path"] = mesh, ["format"] = "obj" }
            }, CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Single(runner.Invocations);
            Assert.Equal("--background", runner.Invocations[0].Args[0]);
            Assert.Equal("--python", runner.Invocations[0].Args[1]);
            Assert.Equal(3, runner.Invocations[0].Args.Count);
            Assert.DoesNotContain(mesh, runner.Invocations[0].Args);
        }
        finally
        {
            File.Delete(mesh);
            Environment.SetEnvironmentVariable("BLENDER_PATH", null);
            BlenderExecutableCache.Invalidate();
        }
    }

    [Fact]
    public async Task Live_Mode_Does_Not_Fallback_To_Background()
    {
        var runner = new FakeBlenderProcessRunner();
        var bridge = new InMemoryBlenderBridge();
        var adapter = new BlenderAdapter(bridge, runner);
        Environment.SetEnvironmentVariable("BLENDER_PATH", CreateFakeBlenderExe());
        try
        {
            var result = await adapter.ExecuteAsync(new AdapterCommand
            {
                Action = CommandNames.BlenderGetScene,
                Params = new Dictionary<string, object?> { ["mode"] = "live", ["sessionId"] = "missing" }
            }, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal(ErrorCodes.AdapterUnavailable, result.ErrorCode);
            Assert.Empty(runner.Invocations);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLENDER_PATH", null);
            BlenderExecutableCache.Invalidate();
        }
    }

    [Fact]
    public async Task Auto_Mode_Without_Context_Uses_Background()
    {
        var runner = new FakeBlenderProcessRunner();
        var bridge = new InMemoryBlenderBridge();
        var adapter = new BlenderAdapter(bridge, runner);
        Environment.SetEnvironmentVariable("BLENDER_PATH", CreateFakeBlenderExe());
        try
        {
            var result = await adapter.ExecuteAsync(new AdapterCommand
            {
                Action = CommandNames.BlenderGetScene,
                Params = new Dictionary<string, object?> { ["mode"] = "auto" }
            }, CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Single(runner.Invocations);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BLENDER_PATH", null);
            BlenderExecutableCache.Invalidate();
        }
    }

    [Fact]
    public async Task Open_Gui_Mode_Launches_Visible_Process()
    {
        var runner = new FakeBlenderProcessRunner();
        var adapter = new BlenderAdapter(null, runner);
        Environment.SetEnvironmentVariable("BLENDER_PATH", CreateFakeBlenderExe());
        var blend = Path.Combine(Path.GetTempPath(), "scene-" + Guid.NewGuid().ToString("N") + ".blend");
        await File.WriteAllTextAsync(blend, "fake");
        try
        {
            var result = await adapter.ExecuteAsync(new AdapterCommand
            {
                Action = CommandNames.BlenderOpen,
                Params = new Dictionary<string, object?> { ["path"] = blend, ["mode"] = "gui" }
            }, CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Empty(runner.Invocations);
            Assert.Single(runner.GuiLaunches);
            Assert.Equal(blend, runner.GuiLaunches[0].BlendPath);
        }
        finally
        {
            File.Delete(blend);
            Environment.SetEnvironmentVariable("BLENDER_PATH", null);
            BlenderExecutableCache.Invalidate();
        }
    }

    [Fact]
    public async Task Open_Background_Mode_Uses_Runner()
    {
        var runner = new FakeBlenderProcessRunner();
        var adapter = new BlenderAdapter(null, runner);
        Environment.SetEnvironmentVariable("BLENDER_PATH", CreateFakeBlenderExe());
        var blend = Path.Combine(Path.GetTempPath(), "scene-" + Guid.NewGuid().ToString("N") + ".blend");
        await File.WriteAllTextAsync(blend, "fake");
        try
        {
            var result = await adapter.ExecuteAsync(new AdapterCommand
            {
                Action = CommandNames.BlenderOpen,
                Params = new Dictionary<string, object?> { ["path"] = blend, ["mode"] = "background" }
            }, CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Single(runner.Invocations);
            Assert.Contains("--background", runner.Invocations[0].Args);
        }
        finally
        {
            File.Delete(blend);
            Environment.SetEnvironmentVariable("BLENDER_PATH", null);
            BlenderExecutableCache.Invalidate();
        }
    }

    private static string CreateFakeBlenderExe()
    {
        var path = Path.Combine(Path.GetTempPath(), "fake-blender-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(path, "fake");
        return path;
    }
}

public class BlenderBridgeHttpTests
{
    [Fact]
    public async Task Bridge_Rejects_Invalid_Token()
    {
        var port = AllocatePort();
        var bridge = new BlenderStudioBridge(new BlenderBridgeOptions { Token = "secret-token-value-here!!", Port = port });
        await bridge.StartAsync();
        try
        {
            using var client = new HttpClient();
            var response = await client.GetAsync($"http://127.0.0.1:{port}/v1/health?token=wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await bridge.StopAsync();
            bridge.Dispose();
        }
    }

    [Fact]
    public async Task Bridge_Rejects_Oversized_Result()
    {
        const string token = "01234567890123456789012345678901";
        var port = AllocatePort();
        var bridge = new BlenderStudioBridge(new BlenderBridgeOptions
        {
            Token = token,
            Port = port,
            MaxResponseBytes = 128
        });
        await bridge.StartAsync();
        try
        {
            var sessionId = "testsession001";
            using var client = new HttpClient();
            var registerBody = JsonSerializer.Serialize(new
            {
                sessionId,
                blenderVersion = "4.0",
                fileName = "scene.blend",
                pollingEnabled = true
            });
            var registerResponse = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/register?token={Uri.EscapeDataString(token)}",
                new StringContent(registerBody, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);

            var huge = new string('x', 256);
            var resultBody = JsonSerializer.Serialize(new
            {
                sessionId,
                commandId = "missing",
                ok = true,
                data = new { blob = huge }
            });
            var resultResponse = await client.PostAsync(
                $"http://127.0.0.1:{port}/v1/result?token={Uri.EscapeDataString(token)}",
                new StringContent(resultBody, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resultResponse.StatusCode);
        }
        finally
        {
            await bridge.StopAsync();
            bridge.Dispose();
        }
    }

    private static int AllocatePort()
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:0/");
        // HttpListener doesn't support port 0 on all platforms; use random high port instead.
        return Random.Shared.Next(48000, 58000);
    }
}
