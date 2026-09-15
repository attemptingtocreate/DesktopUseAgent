using System.Reflection;
using System.Text.Json;
using SemanticDesktop.Agent;
using SemanticDesktop.App;
using SemanticDesktop.App.Lifecycle;
using SemanticDesktop.App.Mcp;
using SemanticDesktop.App.Models;
using SemanticDesktop.App.Persistence;
using SemanticDesktop.App.Providers;
using SemanticDesktop.App.Runtime;
using SemanticDesktop.App.Tools;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.IPC;

namespace SemanticDesktop.App.Tests;

public class AppLayerTests : IDisposable
{
    private readonly List<string> _temps = new();

    public void Dispose()
    {
        foreach (var dir in _temps)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    private string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dua-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _temps.Add(dir);
        return dir;
    }

    private static JsonElement Obj(object value) =>
        JsonSerializer.SerializeToElement(value, JsonDefaults.Options);

    [Fact]
    public async Task ProviderAbstraction_FakeConnectsListsAndCompletesText()
    {
        var fake = new FakeAgentProvider("p1", displayName: "Fake One")
        {
            Script = { new FakeProviderTurn { Events = { AgentProviderEvent.TextDelta("hello"), AgentProviderEvent.Completed("fake-model") } } }
        };
        var status = await fake.ConnectAsync();
        Assert.Equal(ProviderConnectionStatusKind.Connected, status.Kind);
        var models = await fake.ListModelsAsync();
        Assert.Contains(models, m => m.Id == "fake-model");

        var text = "";
        await foreach (var ev in fake.CompleteAsync(new AgentRequest { Messages = new[] { new AgentMessage { Role = "user", Content = "hi" } } }, CancellationToken.None))
        {
            if (ev.Kind == AgentProviderEventKind.TextDelta)
            {
                text += ev.Text;
            }
        }

        Assert.Equal("hello", text);
        Assert.Single(fake.ReceivedRequests);
    }

    [Fact]
    public async Task ProviderSwitching_StoresProviderOnEachTurn()
    {
        var a = new FakeAgentProvider("prov-a", displayName: "A")
        {
            Script = { new FakeProviderTurn { Events = { AgentProviderEvent.TextDelta("from-a"), AgentProviderEvent.Completed() } } }
        };
        var b = new FakeAgentProvider("prov-b", displayName: "B")
        {
            Script = { new FakeProviderTurn { Events = { AgentProviderEvent.TextDelta("from-b"), AgentProviderEvent.Completed() } } }
        };
        var host = AppHost.CreateInMemory(new StaticProviderResolver(a, b), new FakeToolRouter());
        var convo = host.ConversationsApi.Create(providerId: "prov-a");
        await host.ConversationsApi.SendAsync(convo.Id, "one");
        host.ConversationsApi.SetSelectedProvider(convo.Id, "prov-b");
        await host.ConversationsApi.SendAsync(convo.Id, "two");

        var loaded = host.ConversationsApi.Get(convo.Id)!;
        Assert.Equal(2, loaded.Turns.Count);
        Assert.Equal("prov-a", loaded.Turns[0].ProviderId);
        Assert.Equal("prov-b", loaded.Turns[1].ProviderId);
        Assert.Equal("prov-b", loaded.SelectedProviderId);
        Assert.Contains(loaded.Messages, m => m.Content == "from-a");
        Assert.Contains(loaded.Messages, m => m.Content == "from-b");
    }

    [Fact]
    public void ConversationPersistence_RoundTripWithoutSecrets()
    {
        var root = TempRoot();
        var store = new ConversationStore(root);
        var creds = new CredentialStore(root);
        creds.Set(CredentialStore.ProviderApiKey("p1"), "sk-secret-value");
        var convo = store.Save(new Conversation
        {
            Title = "Export pickaxes",
            SelectedProviderId = "p1",
            Messages = { new ChatMessage { Id = "m1", Role = ChatRoles.User, Content = "export them" } }
        });

        var loaded = new ConversationStore(root).Get(convo.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Export pickaxes", loaded!.Title);
        Assert.Equal("export them", loaded.Messages[0].Content);
        var json = File.ReadAllText(Directory.EnumerateFiles(Path.Combine(root, "conversations"), "*.json").Single());
        Assert.DoesNotContain("sk-secret-value", json);
        Assert.Equal("sk-secret-value", creds.Get(CredentialStore.ProviderApiKey("p1")));
        Assert.True(store.Delete(convo.Id));
        Assert.Null(new ConversationStore(root).Get(convo.Id));
    }

    [Fact]
    public async Task ToolCallLoop_RoutesResultBackToProvider()
    {
        var fake = new FakeAgentProvider("p1")
        {
            Script =
            {
                new FakeProviderTurn
                {
                    Events =
                    {
                        AgentProviderEvent.ToolCall("tc1", "filesystem.list", """{"path":"C:\\Temp"}"""),
                        AgentProviderEvent.Completed()
                    }
                },
                new FakeProviderTurn
                {
                    Events = { AgentProviderEvent.TextDelta("listed 3 files"), AgentProviderEvent.Completed() }
                }
            }
        };
        var router = new FakeToolRouter
        {
            ResultsByName = { ["filesystem.list"] = ToolRouterResult.Ok("3 files", @"C:\Temp") }
        };
        var observer = new RecordingAgentRuntimeObserver();
        var host = AppHost.CreateInMemory(new StaticProviderResolver(fake), router, observer: observer);
        var convo = host.ConversationsApi.Create(providerId: "p1");
        var result = await host.ConversationsApi.SendAsync(convo.Id, "list temp");

        Assert.Equal(TurnStatus.Completed, result.Turns[^1].Status);
        Assert.Equal("listed 3 files", result.Messages.Last(m => m.Role == ChatRoles.Assistant).Content);
        Assert.Single(router.Invocations);
        Assert.Equal("filesystem.list", router.Invocations[0].Name);
        Assert.Equal(2, fake.ReceivedRequests.Count);
        Assert.Contains(fake.ReceivedRequests[1].Messages, m => m.Role == "tool" && m.Content!.Contains("3 files"));
        Assert.Contains(observer.Completed, t => t.Tool == "filesystem.list" && t.Success);
    }

    [Fact]
    public async Task ToolResultRouting_CatalogVsExternalMcp()
    {
        var rpc = new RecordingRpc();
        rpc.Results["window.list"] = Obj(new { ok = true, data = new { windows = Array.Empty<object>() } });
        var mcp = new FakeMcpHost
        {
            ToolsByServer =
            {
                ["github"] = new List<AgentToolDefinition>
                {
                    new() { Name = "gh.list_issues", Description = "List issues", JsonSchema = Obj(new { type = "object" }) }
                }
            }
        };
        var router = new AgentToolRouter(rpc, mcp);
        var catalog = await router.InvokeAsync(
            "window.list",
            Obj(new { }),
            new ToolRouterContext { AllowComputerControl = true },
            CancellationToken.None);
        Assert.True(catalog.Success);
        Assert.Contains("window.list", rpc.Calls.Select(c => c.Method));

        var external = await router.InvokeAsync(
            "gh.list_issues",
            Obj(new { }),
            new ToolRouterContext { AllowComputerControl = true, McpServerId = "github" },
            CancellationToken.None);
        Assert.True(external.Success);
        Assert.Contains(mcp.Calls, c => c.McpId == "github" && c.Name == "gh.list_issues");

        var unknown = await router.InvokeAsync(
            "not.a.tool",
            Obj(new { }),
            new ToolRouterContext { AllowComputerControl = true },
            CancellationToken.None);
        Assert.False(unknown.Success);

        var bypass = await mcp.CallToolAsync("github", "window.list", Obj(new { }), CancellationToken.None);
        Assert.False(bypass.Success);
        Assert.Equal("Deny", bypass.PermissionDecision);
    }

    [Fact]
    public async Task Cancellation_StopsGeneration()
    {
        var fake = new FakeAgentProvider("p1")
        {
            Script = { new FakeProviderTurn { WaitForCancellation = true } }
        };
        var host = AppHost.CreateInMemory(new StaticProviderResolver(fake), new FakeToolRouter());
        var convo = host.ConversationsApi.Create(providerId: "p1");
        var send = host.ConversationsApi.SendAsync(convo.Id, "go");
        await Task.Delay(80);
        await host.ConversationsApi.StopAsync(convo.Id);
        var result = await send;
        Assert.Equal(TurnStatus.Cancelled, result.Turns[^1].Status);
    }

    [Fact]
    public async Task PermissionApproval_ContinuesAfterAllow()
    {
        var fake = new FakeAgentProvider("p1")
        {
            Script =
            {
                new FakeProviderTurn
                {
                    Events =
                    {
                        AgentProviderEvent.ToolCall("tc1", "filesystem.write_text", """{"path":"C:\\Temp\\a.txt","contents":"x"}"""),
                        AgentProviderEvent.Completed()
                    }
                },
                new FakeProviderTurn { Events = { AgentProviderEvent.TextDelta("wrote"), AgentProviderEvent.Completed() } }
            }
        };
        var router = new FakeToolRouter { AskThenSucceedTools = { "filesystem.write_text" } };
        var broker = new TestApprovalBroker();
        var observer = new RecordingAgentRuntimeObserver();
        var host = AppHost.CreateInMemory(new StaticProviderResolver(fake), router, observer: observer, approvals: broker);
        var convo = host.ConversationsApi.Create(providerId: "p1");
        var send = host.ConversationsApi.SendAsync(convo.Id, "write");
        for (var i = 0; i < 40 && observer.Approvals.Count == 0; i++)
        {
            await Task.Delay(25);
        }

        Assert.NotEmpty(observer.Approvals);
        broker.Approve("apr_test");
        var result = await send;
        Assert.Equal(TurnStatus.Completed, result.Turns[^1].Status);
        Assert.Equal(2, router.Invocations.Count);
        Assert.True(result.Turns[^1].ToolInvocations[^1].Success);
        Assert.Contains(result.Messages, m => m.Content == "wrote");
    }

    [Fact]
    public void McpAddRemoveEnableDisable_Persists()
    {
        var root = TempRoot();
        var host = new AppHost(root, new StaticProviderResolver(new FakeAgentProvider("p1")), new FakeToolRouter());
        host.Providers.Upsert(new ProviderConfig { Id = "p1", Type = "fake", DisplayName = "P1" });
        var added = host.Mcp.Add(new McpConfiguration { Name = "GitHub", Type = "stdio", Command = "npx", Args = new List<string> { "-y", "@modelcontextprotocol/server-github" } }, "p1");
        Assert.False(added.IsBuiltin);
        Assert.Contains(added.Id, host.Providers.Get("p1")!.EnabledMcpIds);

        host.Mcp.Enable(added.Id, false);
        Assert.False(host.McpConfigs.Get(added.Id)!.Enabled);
        host.Mcp.Enable(added.Id, true);
        Assert.True(new McpConfigStore(root).Get(added.Id)!.Enabled);
        Assert.True(host.Mcp.Remove(added.Id));
        Assert.Null(new McpConfigStore(root).Get(added.Id));
        Assert.DoesNotContain(added.Id, host.Providers.Get("p1")!.EnabledMcpIds);
        Assert.NotNull(host.McpConfigs.Get(McpIds.Builtin));
        Assert.False(host.Mcp.Remove(McpIds.Builtin));
    }

    [Fact]
    public async Task PerAgentMcpAssignment_ChangesToolEnvironment()
    {
        var githubTool = new AgentToolDefinition
        {
            Name = "gh.list_issues",
            Description = "issues",
            JsonSchema = Obj(new { type = "object" })
        };
        var mcp = new FakeMcpHost { ToolsByServer = { ["github"] = new List<AgentToolDefinition> { githubTool } } };
        var host = AppHost.CreateInMemory(new StaticProviderResolver(new FakeAgentProvider("a"), new FakeAgentProvider("b")), new FakeToolRouter(), mcp);
        host.McpConfigs.Upsert(new McpConfiguration { Id = "github", Name = "GitHub", Type = "stdio", Command = "npx", Enabled = true });
        host.Providers.Upsert(new ProviderConfig { Id = "a", Type = "fake", EnabledMcpIds = new List<string> { McpIds.Builtin, "github" } });
        host.Providers.Upsert(new ProviderConfig { Id = "b", Type = "fake", EnabledMcpIds = new List<string> { McpIds.Builtin } });

        Assert.Same(mcp, host.Runtime.McpHost);
        var toolsA = await host.Runtime.BuildToolListAsync("a");
        var toolsB = await host.Runtime.BuildToolListAsync("b");
        Assert.Contains(toolsA, t => t.Name == "window.list");
        Assert.Contains(toolsA, t => t.Name == "gh.list_issues");
        Assert.Contains(toolsB, t => t.Name == "window.list");
        Assert.DoesNotContain(toolsB, t => t.Name == "gh.list_issues");
    }

    [Fact]
    public async Task ServiceStartup_RecoversAfterFailedPing()
    {
        var gateway = new FakeAgentProcessGateway { PingsBeforeSuccess = 1, Running = false };
        var settings = new AppSettingsStore(null);
        var lifecycle = new AgentLifecycle(gateway, settings);
        await lifecycle.EnsureAgentAsync();
        Assert.Equal(1, gateway.StartCount);
        Assert.True(gateway.Running);
        gateway.Running = false;
        await lifecycle.RecoverIfNeededAsync();
        Assert.Equal(2, gateway.StartCount);
        Assert.True(gateway.Running);
        settings.Save(new AppSettings { General = { CloseBehavior = CloseBehavior.ExitAll } });
        await lifecycle.OnAppExitAsync();
        Assert.Equal(1, gateway.StopCount);
    }

    [Fact]
    public async Task ProviderFailure_RecordsErrorStatus()
    {
        var fake = new FakeAgentProvider("p1")
        {
            Script = { new FakeProviderTurn { Throw = new InvalidOperationException("provider down") } }
        };
        var host = AppHost.CreateInMemory(new StaticProviderResolver(fake), new FakeToolRouter());
        var convo = host.ConversationsApi.Create(providerId: "p1");
        var result = await host.ConversationsApi.SendAsync(convo.Id, "hi");
        Assert.Equal(TurnStatus.Error, result.Turns[^1].Status);
        Assert.Contains("provider down", result.Turns[^1].Error);
    }

    [Fact]
    public async Task McpFailure_DoesNotBlockBuiltinTools()
    {
        var mcp = new FakeMcpHost
        {
            ListErrors = { ["github"] = new InvalidOperationException("mcp offline") }
        };
        var host = AppHost.CreateInMemory(new StaticProviderResolver(new FakeAgentProvider("a")), new FakeToolRouter(), mcp);
        host.McpConfigs.Upsert(new McpConfiguration { Id = "github", Name = "GitHub", Enabled = true, Type = "stdio", Command = "npx" });
        host.Providers.Upsert(new ProviderConfig { Id = "a", Type = "fake", EnabledMcpIds = new List<string> { McpIds.Builtin, "github" } });
        var tools = await host.Runtime.BuildToolListAsync("a");
        Assert.Contains(tools, t => t.Name == "ui.invoke");
        Assert.DoesNotContain(tools, t => t.Name.StartsWith("gh.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoSecurityBypass_FromInternalRuntime()
    {
        var names = typeof(AgentRuntime).Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain("SemanticDesktop.UIA", names);
        Assert.DoesNotContain("SemanticDesktop.Win32", names);
        Assert.DoesNotContain("SemanticDesktop.Browser", names);

        var rpc = new RecordingRpc();
        var router = new AgentToolRouter(rpc);
        var denied = await router.InvokeAsync(
            "ui.invoke",
            Obj(new { elementId = "x" }),
            new ToolRouterContext { AllowComputerControl = false },
            CancellationToken.None);
        Assert.False(denied.Success);
        Assert.Empty(rpc.Calls);
    }

    [Fact]
    public async Task DispatcherStillAuthorizes_ComputerControl()
    {
        using var dispatcher = new CommandDispatcher();
        await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "p",
            Method = CommandNames.PermissionPolicySet,
            Params = Obj(new { kind = "capability", capability = "filesystem.write", decision = "Deny" })
        }, CancellationToken.None);

        var router = new DirectDispatcherToolRouter(async (name, args, ct) =>
        {
            var result = await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                Method = name,
                Params = args
            }, ct);
            return Obj(result);
        });

        var outcome = await router.InvokeAsync(
            CommandNames.FilesystemWriteText,
            Obj(new { path = "", contents = "nope" }),
            new ToolRouterContext { AllowComputerControl = true },
            CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Equal("Deny", outcome.PermissionDecision);
    }

    [Fact]
    public void CredentialStore_DpapiRoundTrip()
    {
        var root = TempRoot();
        var store = new CredentialStore(root);
        store.Set(CredentialStore.ProviderApiKey("openai"), "sk-test");
        Assert.Equal("sk-test", new CredentialStore(root).Get(CredentialStore.ProviderApiKey("openai")));
        var secretFiles = Directory.EnumerateFiles(Path.Combine(root, "secrets")).ToList();
        Assert.NotEmpty(secretFiles);
        Assert.DoesNotContain("sk-test", File.ReadAllText(secretFiles[0]));
    }

    [Fact]
    public async Task UnsupportedProvider_CannotComplete()
    {
        var provider = new UnsupportedProvider(new ProviderConfig { Id = "web", Type = "chatgpt-web", DisplayName = "ChatGPT Web" });
        var status = await provider.ConnectAsync();
        Assert.Equal(ProviderConnectionStatusKind.Unsupported, status.Kind);
        var events = new List<AgentProviderEvent>();
        await foreach (var ev in provider.CompleteAsync(new AgentRequest(), CancellationToken.None))
        {
            events.Add(ev);
        }

        Assert.Contains(events, e => e.Kind == AgentProviderEventKind.Error);
    }

    [Fact]
    public void ToolCatalog_MatchesBuiltinMcpIdentity()
    {
        Assert.Equal("desktopuseagent", ToolCatalog.BuiltinMcpId);
        Assert.Equal("DesktopUseAgent", ToolCatalog.BuiltinDisplayName);
        Assert.Contains(ToolCatalog.ComputerControlTools, t => t.Name == "window.list");
        Assert.Contains(ToolCatalog.ComputerControlTools, t => t.Name == "ui.invoke");
        Assert.Contains(ToolCatalog.ComputerControlTools, t => t.Name == "blender.export");
        Assert.Contains(ToolCatalog.ComputerControlTools, t => t.Name == "system.emergency_stop");
        Assert.True(ToolCatalog.ComputerControlTools.Count >= 70);
    }

    [Fact]
    public void ProviderFactory_OnlySupportedTypes()
    {
        var factory = new ProviderFactory();
        var creds = new CredentialStore(null);
        Assert.IsType<OpenAiCompatibleProvider>(factory.Create(new ProviderConfig { Id = "o", Type = "openai" }, creds));
        Assert.IsType<AnthropicProvider>(factory.Create(new ProviderConfig { Id = "a", Type = "anthropic" }, creds));
        Assert.IsType<OllamaProvider>(factory.Create(new ProviderConfig { Id = "l", Type = "ollama" }, creds));
        Assert.IsType<UnsupportedProvider>(factory.Create(new ProviderConfig { Id = "w", Type = "chatgpt-web" }, creds));
    }

    [Fact]
    public void AppAssembly_DoesNotReferenceAutomationStacks()
    {
        var location = typeof(AgentRuntime).Assembly.Location;
        Assert.Contains("DesktopUseAgent.App", location, StringComparison.OrdinalIgnoreCase);
        var referenced = Assembly.LoadFrom(location).GetReferencedAssemblies().Select(a => a.Name ?? "");
        Assert.DoesNotContain(referenced, n => n.Contains("FlaUI", StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class RecordingRpc : IAgentRpc
{
    public List<(string Method, object? Parameters)> Calls { get; } = new();
    public Dictionary<string, JsonElement> Results { get; } = new(StringComparer.Ordinal);

    public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        Calls.Add((method, parameters));
        if (Results.TryGetValue(method, out var result))
        {
            return Task.FromResult(result);
        }

        return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, data = new { } }, JsonDefaults.Options));
    }
}
