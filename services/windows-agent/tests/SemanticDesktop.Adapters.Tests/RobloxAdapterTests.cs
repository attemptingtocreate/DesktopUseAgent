using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SemanticDesktop.Adapters.RobloxStudio;
using SemanticDesktop.Agent;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Core.Serialization;

namespace SemanticDesktop.Adapters.Tests;

public sealed class InMemoryRobloxBridge : IRobloxBridge
{
    private readonly Dictionary<string, InMemorySession> _sessions = new(StringComparer.Ordinal);
    private bool _listening = true;

    public bool IsListening => _listening;

    public int Port { get; } = 18374;

    public string Token { get; init; } = "test-token";

    public Func<string, Dictionary<string, object?>?, RobloxCommandResult>? Handler { get; set; }

    public void RegisterSession(string sessionId, bool pollingEnabled = true)
    {
        _sessions[sessionId] = new InMemorySession
        {
            SessionId = sessionId,
            PollingEnabled = pollingEnabled,
            LastSeenUtc = DateTimeOffset.UtcNow
        };
    }

    public RobloxBridgeHealth GetHealth()
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = _sessions.Values
            .Where(s => now - s.LastSeenUtc <= TimeSpan.FromSeconds(45))
            .Select(s => new RobloxBridgeSessionInfo
            {
                SessionId = s.SessionId,
                PollingEnabled = s.PollingEnabled,
                LastSeenUtc = s.LastSeenUtc,
                HasOutstandingCommand = s.OutstandingCommand is not null
            })
            .ToList();

        return new RobloxBridgeHealth
        {
            Listening = _listening,
            PluginConnected = sessions.Any(s => s.PollingEnabled),
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

    public async Task<RobloxCommandResult> ExecuteCommandAsync(
        string operation,
        Dictionary<string, object?>? parameters,
        string? sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!_listening)
        {
            return RobloxCommandResult.Fail(ErrorCodes.AdapterUnavailable, "Bridge not listening.");
        }

        if (!RobloxBridgeOperations.Allowlist.Contains(operation))
        {
            return RobloxCommandResult.Fail(ErrorCodes.Unsupported, "Operation not allowed.");
        }

        var session = ResolveSession(sessionId);
        if (session is null)
        {
            return RobloxCommandResult.Fail(ErrorCodes.AdapterUnavailable, "No connected session.");
        }

        if (!session.PollingEnabled)
        {
            return RobloxCommandResult.Fail(ErrorCodes.AdapterUnavailable, "Polling disabled.");
        }

        if (session.OutstandingCommand is not null)
        {
            return RobloxCommandResult.Fail(ErrorCodes.InvalidArgument, "Outstanding command exists.");
        }

        var commandId = Guid.NewGuid().ToString("N");
        session.OutstandingCommand = commandId;
        var tcs = new TaskCompletionSource<RobloxCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PendingResult = tcs;

        _ = Task.Run(async () =>
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            session.LastSeenUtc = DateTimeOffset.UtcNow;
            session.OutstandingCommand = null;
            var result = Handler?.Invoke(operation, parameters)
                         ?? RobloxCommandResult.Success(new { operation });
            session.PendingResult?.TrySetResult(result);
            session.PendingResult = null;
        }, cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        await using var reg = timeoutCts.Token.Register(() =>
            tcs.TrySetResult(RobloxCommandResult.Fail(ErrorCodes.Timeout, "Timed out.")));
        return await tcs.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        _listening = false;
    }

    private InMemorySession? ResolveSession(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return _sessions.TryGetValue(sessionId, out var exact) ? exact : null;
        }

        return _sessions.Values.FirstOrDefault(s => s.PollingEnabled);
    }

    private sealed class InMemorySession
    {
        public required string SessionId { get; init; }
        public bool PollingEnabled { get; init; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public string? OutstandingCommand { get; set; }
        public TaskCompletionSource<RobloxCommandResult>? PendingResult { get; set; }
    }
}

public class RobloxAdapterTests
{
    [Fact]
    public void CanHandle_RobloxStudioBeta()
    {
        var bridge = new InMemoryRobloxBridge();
        var adapter = new RobloxStudioAdapter(bridge);
        Assert.True(adapter.CanHandle(new ProcessInfo { Name = "RobloxStudioBeta.exe", Pid = 1, Id = "p1" }));
    }

    [Fact]
    public async Task PluginPing_ReportsBridgeAndSession()
    {
        var bridge = new InMemoryRobloxBridge();
        bridge.RegisterSession("sess-1");
        var adapter = new RobloxStudioAdapter(bridge);
        var result = await adapter.ExecuteAsync(new AdapterCommand
        {
            Action = CommandNames.RobloxPluginPing
        }, CancellationToken.None);

        Assert.True(result.Ok);
        var json = JsonSerializer.Serialize(result.Data);
        Assert.Contains("bridgeListening", json, StringComparison.Ordinal);
        Assert.Contains("pluginConnected", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetHierarchy_RejectsWhenNoSession()
    {
        var bridge = new InMemoryRobloxBridge();
        var adapter = new RobloxStudioAdapter(bridge);
        var result = await adapter.ExecuteAsync(new AdapterCommand
        {
            Action = CommandNames.RobloxGetHierarchy
        }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.AdapterUnavailable, result.ErrorCode);
    }

    [Fact]
    public async Task GetHierarchy_ClampsDepthAndMaxNodes()
    {
        Dictionary<string, object?>? captured = null;
        var bridge = new InMemoryRobloxBridge
        {
            Handler = (_, parameters) =>
            {
                captured = parameters;
                return RobloxCommandResult.Success(new { nodes = Array.Empty<object>() });
            }
        };
        bridge.RegisterSession("sess-1");
        var adapter = new RobloxStudioAdapter(bridge);
        var result = await adapter.ExecuteAsync(new AdapterCommand
        {
            Action = CommandNames.RobloxGetHierarchy,
            Params = new Dictionary<string, object?>
            {
                ["depth"] = 99,
                ["maxNodes"] = 9999
            }
        }, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.NotNull(captured);
        Assert.Equal(RobloxStudioAdapter.MaxHierarchyDepth, captured!["depth"]);
        Assert.Equal(RobloxStudioAdapter.MaxHierarchyNodes, captured["maxNodes"]);
    }

    [Fact]
    public async Task SetProperty_RejectsBlockedAndUnknownProperties()
    {
        var bridge = new InMemoryRobloxBridge();
        bridge.RegisterSession("sess-1");
        var adapter = new RobloxStudioAdapter(bridge);

        var parent = await adapter.ExecuteAsync(new AdapterCommand
        {
            Action = CommandNames.RobloxSetProperty,
            Params = new Dictionary<string, object?>
            {
                ["instanceId"] = "inst_1",
                ["property"] = "Parent",
                ["value"] = "Workspace"
            }
        }, CancellationToken.None);
        Assert.False(parent.Ok);

        var unknown = await adapter.ExecuteAsync(new AdapterCommand
        {
            Action = CommandNames.RobloxSetProperty,
            Params = new Dictionary<string, object?>
            {
                ["instanceId"] = "inst_1",
                ["property"] = "NotAllowedProperty",
                ["value"] = true
            }
        }, CancellationToken.None);
        Assert.False(unknown.Ok);
    }

    [Fact]
    public async Task SetProperty_AcceptsTaggedColor3AndVector3()
    {
        Dictionary<string, object?>? captured = null;
        var bridge = new InMemoryRobloxBridge
        {
            Handler = (_, parameters) =>
            {
                captured = parameters;
                return RobloxCommandResult.Success(new { ok = true });
            }
        };
        bridge.RegisterSession("sess-1");
        var adapter = new RobloxStudioAdapter(bridge);

        var colorJson = JsonSerializer.SerializeToElement(new { type = "Color3", r = 1, g = 0.5, b = 0.25 });
        var colorResult = await adapter.ExecuteAsync(new AdapterCommand
        {
            Action = CommandNames.RobloxSetProperty,
            Params = new Dictionary<string, object?>
            {
                ["instanceId"] = "inst_1",
                ["property"] = "Color",
                ["value"] = colorJson
            }
        }, CancellationToken.None);
        Assert.True(colorResult.Ok);

        var vectorJson = JsonSerializer.SerializeToElement(new { type = "Vector3", x = 1, y = 2, z = 3 });
        var vectorResult = await adapter.ExecuteAsync(new AdapterCommand
        {
            Action = CommandNames.RobloxSetProperty,
            Params = new Dictionary<string, object?>
            {
                ["instanceId"] = "inst_1",
                ["property"] = "Size",
                ["value"] = vectorJson
            }
        }, CancellationToken.None);
        Assert.True(vectorResult.Ok);
        Assert.NotNull(captured);
    }

    [Fact]
    public async Task SetProperty_RejectsInvalidTypedValues()
    {
        var bridge = new InMemoryRobloxBridge();
        bridge.RegisterSession("sess-1");
        var adapter = new RobloxStudioAdapter(bridge);
        var badColor = JsonSerializer.SerializeToElement(new { type = "Color3", r = 2, g = 0, b = 0 });
        var badType = JsonSerializer.SerializeToElement(new { type = "Vector3", x = 1, y = 2, z = 3 });
        var outOfRange = await adapter.ExecuteAsync(new AdapterCommand
        {
            Action = CommandNames.RobloxSetProperty,
            Params = new Dictionary<string, object?>
            {
                ["instanceId"] = "inst_1",
                ["property"] = "Color",
                ["value"] = badColor
            }
        }, CancellationToken.None);
        var wrongType = await adapter.ExecuteAsync(new AdapterCommand
        {
            Action = CommandNames.RobloxSetProperty,
            Params = new Dictionary<string, object?>
            {
                ["instanceId"] = "inst_1",
                ["property"] = "Color",
                ["value"] = badType
            }
        }, CancellationToken.None);
        Assert.False(outOfRange.Ok);
        Assert.False(wrongType.Ok);
    }

    [Fact]
    public void PropertyContract_MatchesPluginAllowlist()
    {
        Assert.Contains("Color", RobloxPropertyContract.AllowedProperties.Keys);
        Assert.Contains("Size", RobloxPropertyContract.AllowedProperties.Keys);
        Assert.Contains("Material", RobloxPropertyContract.AllowedProperties.Keys);
        Assert.Contains("CFrame", RobloxPropertyContract.AllowedProperties.Keys);
        Assert.Contains(RobloxBridgeOperations.CreateInstance, RobloxBridgeOperations.Allowlist);
        Assert.Contains(RobloxBridgeOperations.InsertAsset, RobloxBridgeOperations.Allowlist);
        Assert.Contains(RobloxBridgeOperations.ExecuteLuau, RobloxBridgeOperations.Allowlist);
        Assert.DoesNotContain(RobloxBridgeOperations.ExecuteLuau, RobloxBridgeOperations.BatchAllowlist);
        Assert.DoesNotContain(RobloxBridgeOperations.Batch, RobloxBridgeOperations.BatchAllowlist);
    }

    [Fact]
    public void PluginModelBuilder_ProducesValidRbxmx()
    {
        var pluginSource = File.ReadAllText(Path.Combine(FindRepoRoot(), "plugins", "roblox-studio", "DesktopUseAgent", "init.server.lua"));
        var configSource = RobloxPluginModelBuilder.BuildConfigModuleSource("127.0.0.1", 18374, "abc123TOKEN");
        var xml = RobloxPluginModelBuilder.BuildPluginModel(pluginSource, configSource);
        RobloxPluginModelBuilder.ValidatePluginModel(xml);
        var doc = XDocument.Parse(xml);
        Assert.Equal("roblox", doc.Root!.Name.LocalName);
        Assert.Contains(doc.Descendants("ProtectedString"), e => e.Value.Contains("DesktopUseAgentConfig"));
        Assert.Contains(
            doc.Descendants("token"),
            e => e.Attribute("name")?.Value == "RunContext" && e.Value == "3");
    }

    [Fact]
    public async Task OpenPlaceCoordinator_AppliesWindowPlacement()
    {
        var launcher = new FakeRobloxPlaceLauncher(new Dictionary<string, object?>
        {
            ["opened"] = "game.rbxlx",
            ["pid"] = 4242,
            ["provider"] = "test"
        });
        var placer = new FakeRobloxWindowPlacer(new WindowInfo
        {
            Id = "win-1",
            Title = "game.rbxlx - Roblox Studio",
            Process = "RobloxStudioBeta",
            Pid = 4242
        });
        var coordinator = new RobloxOpenPlaceCoordinator(placer);
        var result = await coordinator.OpenPlaceAsync(launcher, new Dictionary<string, object?>
        {
            ["monitor"] = 1,
            ["placement"] = "normal",
            ["timeoutSeconds"] = 1
        }, CancellationToken.None);

        Assert.True(result.Ok);
        var json = JsonSerializer.Serialize(result.Data);
        Assert.Contains("windowPlacement", json, StringComparison.Ordinal);
        Assert.Contains("applied", json, StringComparison.Ordinal);
        Assert.Single(placer.Moved);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "plugins", "roblox-studio")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName ?? "";
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed class FakeRobloxPlaceLauncher : IRobloxPlaceLauncher
    {
        private readonly Dictionary<string, object?> _data;

        public FakeRobloxPlaceLauncher(Dictionary<string, object?> data) => _data = data;

        public Task<AdapterResult> LaunchPlaceAsync(Dictionary<string, object?>? parameters, CancellationToken cancellationToken) =>
            Task.FromResult(AdapterResult.Success(_data));
    }

    private sealed class FakeRobloxWindowPlacer : IRobloxWindowPlacer
    {
        private readonly WindowInfo _window;

        public FakeRobloxWindowPlacer(WindowInfo window) => _window = window;

        public List<WindowMoveRequest> Moved { get; } = new();

        public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WindowInfo>>(new[] { _window });

        public Task<WindowMutationResult?> MoveWindowAsync(WindowMoveRequest request, CancellationToken cancellationToken)
        {
            Moved.Add(request);
            return Task.FromResult<WindowMutationResult?>(new WindowMutationResult
            {
                Window = _window,
                PriorBounds = new Rect { X = 0, Y = 0, Width = 800, Height = 600 }
            });
        }
    }

    [Fact]
    public async Task Select_RejectsTooManyIds()
    {
        var bridge = new InMemoryRobloxBridge();
        bridge.RegisterSession("sess-1");
        var adapter = new RobloxStudioAdapter(bridge);
        var ids = Enumerable.Range(1, RobloxStudioAdapter.MaxSelectCount + 1).Select(i => $"inst_{i}").ToArray();
        var result = await adapter.ExecuteAsync(new AdapterCommand
        {
            Action = CommandNames.RobloxSelect,
            Params = new Dictionary<string, object?> { ["instanceIds"] = ids }
        }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, result.ErrorCode);
    }

    [Fact]
    public async Task OpenPlace_RejectsMissingAndBadExtension()
    {
        var bridge = new InMemoryRobloxBridge();
        var adapter = new RobloxStudioAdapter(bridge);

        var missing = await adapter.ExecuteAsync(new AdapterCommand
        {
            Action = CommandNames.RobloxOpenPlace,
            Params = new Dictionary<string, object?> { ["path"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".rbxl") }
        }, CancellationToken.None);
        Assert.False(missing.Ok);

        var badExt = Path.GetTempFileName();
        try
        {
            var bad = await adapter.ExecuteAsync(new AdapterCommand
            {
                Action = CommandNames.RobloxOpenPlace,
                Params = new Dictionary<string, object?> { ["path"] = badExt }
            }, CancellationToken.None);
            Assert.False(bad.Ok);
            Assert.Equal(ErrorCodes.InvalidArgument, bad.ErrorCode);
        }
        finally
        {
            File.Delete(badExt);
        }
    }
}

public class RobloxBridgeHttpTests : IAsyncLifetime
{
    private RobloxStudioBridge? _bridge;
    private string _token = "bridge-test-token-" + Guid.NewGuid().ToString("N");
    private int _port;

    public async Task InitializeAsync()
    {
        _port = FindFreePort();
        _bridge = new RobloxStudioBridge(new RobloxBridgeOptions
        {
            Token = _token,
            Port = _port,
            MaxRequestBodyBytes = 1024
        });
        await _bridge.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_bridge is not null)
        {
            await _bridge.StopAsync();
            _bridge.Dispose();
        }
    }

    [Fact]
    public async Task Health_RejectsBadToken()
    {
        var response = await GetAsync("/v1/health?token=wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_RejectsInvalidSessionId()
    {
        var register = await PostAsync("/v1/register", new { sessionId = "bad id with spaces", pollingEnabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, register.StatusCode);
    }

    [Fact]
    public async Task Post_RejectsOversizedBody()
    {
        using var client = new HttpClient();
        var url = $"http://127.0.0.1:{_port}/v1/register?token={Uri.EscapeDataString(_token)}";
        var oversized = new string('a', 2048);
        var response = await client.PostAsync(url, new StringContent($"{{\"sessionId\":\"{oversized}\",\"pollingEnabled\":true}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task RegisterAndPollFlow_Works()
    {
        var sessionId = "sess-http-" + Guid.NewGuid().ToString("N");
        var register = await PostAsync("/v1/register", new { sessionId, pollingEnabled = true, placeName = "TestPlace" });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);

        var commandTask = _bridge!.ExecuteCommandAsync(
            RobloxBridgeOperations.Ping,
            null,
            sessionId,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        await Task.Delay(300);
        var poll = await GetAsync($"/v1/poll?sessionId={Uri.EscapeDataString(sessionId)}");
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        using var pollDoc = JsonDocument.Parse(poll.Body);
        Assert.True(pollDoc.RootElement.TryGetProperty("command", out var commandEl));
        Assert.Equal(JsonValueKind.Object, commandEl.ValueKind);
        var commandId = commandEl.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(commandId));

        var result = await PostAsync("/v1/result", new
        {
            sessionId,
            commandId,
            ok = true,
            data = new { pong = true }
        });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);

        var commandResult = await commandTask;
        Assert.True(commandResult.Ok);
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> GetAsync(string pathAndQuery)
    {
        using var client = new HttpClient();
        var separator = pathAndQuery.Contains('?') ? "&" : "?";
        var url = $"http://127.0.0.1:{_bridge!.Port}{pathAndQuery}{separator}token={Uri.EscapeDataString(_token)}";
        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body);
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> PostAsync(string path, object payload)
    {
        using var client = new HttpClient();
        var url = $"http://127.0.0.1:{_bridge!.Port}{path}?token={Uri.EscapeDataString(_token)}";
        var json = JsonSerializer.Serialize(payload, JsonDefaults.Options);
        var response = await client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body);
    }

    private static int FindFreePort()
    {
        var listener = new HttpListener();
        var port = 19000 + Random.Shared.Next(1000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        listener.Stop();
        return port;
    }
}
