using System.Text.Json;
using SemanticDesktop.App.Mcp;
using SemanticDesktop.App.Models;
using SemanticDesktop.App.Providers;
using SemanticDesktop.App.Runtime;
using SemanticDesktop.App.Tools;

namespace SemanticDesktop.App.Tests;

public class RuntimeToolTests
{
    [Fact]
    public async Task Tool_Call_Loop_Routes_Result_Back_To_Provider()
    {
        var provider = new FakeAgentProvider("p1");
        provider.Script.Add(new FakeProviderTurn
        {
            Events = { AgentProviderEvent.ToolCall("call_1", "filesystem.list", """{"path":"C:\\\\Temp"}""") }
        });
        provider.Script.Add(new FakeProviderTurn
        {
            Events = { AgentProviderEvent.TextDelta("listed two files"), AgentProviderEvent.Completed() }
        });
        var router = new FakeToolRouter
        {
            ResultsByName = { ["filesystem.list"] = ToolRouterResult.Ok("2 files") }
        };
        var host = TestHarness.ChatHost(provider, router);
        var convo = host.ConversationsApi.Create();
        var result = await host.ConversationsApi.SendAsync(convo.Id, "list files");

        Assert.Equal(TurnStatus.Completed, result.Turns[0].Status);
        Assert.Contains(result.Messages, m => m.Content.Contains("listed two files"));
        Assert.Equal(2, provider.ReceivedRequests.Count);
        Assert.Contains(provider.ReceivedRequests[1].Messages, m => m.Role == "tool" && m.Content!.Contains("2 files"));
        Assert.Equal("filesystem.list", router.Invocations[0].Name);
    }

    [Fact]
    public async Task Tool_Result_Routing_Rejects_Unknown_Catalog_Goes_Rpc_External_Goes_Mcp()
    {
        var rpc = new RecordingAgentRpc();
        var mcp = new FakeMcpHost
        {
            ToolsByServer =
            {
                ["github"] = new List<AgentToolDefinition> { TestHarness.ExternalTool("github.list_issues", "github") }
            }
        };
        var router = new AgentToolRouter(rpc, mcp);
        var provider = new FakeAgentProvider("p1");
        provider.Script.Add(new FakeProviderTurn
        {
            Events =
            {
                AgentProviderEvent.ToolCall("c0", "not.a.tool", "{}"),
            }
        });
        provider.Script.Add(new FakeProviderTurn
        {
            Events = { AgentProviderEvent.ToolCall("c1", "filesystem.list", "{}") }
        });
        provider.Script.Add(new FakeProviderTurn
        {
            Events = { AgentProviderEvent.ToolCall("c2", "github.list_issues", "{}") }
        });
        provider.Script.Add(new FakeProviderTurn
        {
            Events = { AgentProviderEvent.TextDelta("done"), AgentProviderEvent.Completed() }
        });

        var host = TestHarness.ChatHost(provider, router, mcp, rpc);
        TestHarness.SeedProvider(host, provider, new[] { McpIds.Builtin, "github" });
        host.McpConfigs.Upsert(new McpConfiguration { Id = "github", Name = "GitHub", Type = "stdio", Command = "npx", Enabled = true });
        var convo = host.ConversationsApi.Create();
        var result = await host.ConversationsApi.SendAsync(convo.Id, "mix");

        Assert.Contains(result.Turns[0].ToolInvocations, t => t.Tool == "not.a.tool" && !t.Success);
        Assert.Contains(rpc.Calls, c => c.Method == "filesystem.list");
        Assert.Contains(mcp.Calls, c => c.Name == "github.list_issues");
        Assert.DoesNotContain(rpc.Calls, c => c.Method == "github.list_issues");
        Assert.DoesNotContain(mcp.Calls, c => c.Name == "filesystem.list");
        Assert.DoesNotContain(rpc.Calls, c => c.Method == "not.a.tool");
    }

    [Fact]
    public async Task Cancellation_Stops_Runtime_And_Skips_Further_Tools()
    {
        var provider = new FakeAgentProvider("p1");
        provider.Script.Add(new FakeProviderTurn { WaitForCancellation = true });
        var router = new FakeToolRouter();
        var host = TestHarness.ChatHost(provider, router);
        var convo = host.ConversationsApi.Create();
        var send = host.ConversationsApi.SendAsync(convo.Id, "go");
        await Task.Delay(80);
        await host.ConversationsApi.StopAsync(convo.Id);
        var result = await send;

        Assert.Equal(TurnStatus.Cancelled, result.Turns[0].Status);
        Assert.Empty(router.Invocations);
        Assert.Equal(TurnStatus.Cancelled, host.Runtime.GetStatus(convo.Id)?.Status);
    }

    [Fact]
    public async Task Permission_Approval_Continuation_Succeeds_After_Approve()
    {
        var provider = new FakeAgentProvider("p1");
        provider.Script.Add(new FakeProviderTurn
        {
            Events = { AgentProviderEvent.ToolCall("c1", "filesystem.write_text", """{"path":"C:\\\\Temp\\\\a.txt"}""") }
        });
        provider.Script.Add(new FakeProviderTurn
        {
            Events = { AgentProviderEvent.TextDelta("wrote"), AgentProviderEvent.Completed() }
        });
        var router = new FakeToolRouter();
        router.AskThenSucceedTools.Add("filesystem.write_text");
        router.ResultsByName["filesystem.write_text"] = ToolRouterResult.Ok("wrote file", permission: "Allow");
        var broker = new TestApprovalBroker();
        var observer = new RecordingAgentRuntimeObserver();
        var host = TestHarness.ChatHost(provider, router, observer: observer, approvals: broker);
        var convo = host.ConversationsApi.Create();
        var send = host.ConversationsApi.SendAsync(convo.Id, "write");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (observer.Approvals.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.NotEmpty(observer.Approvals);
        broker.Approve(observer.Approvals[0].ApprovalId);
        var result = await send;

        Assert.Equal(TurnStatus.Completed, result.Turns[0].Status);
        Assert.True(result.Turns[0].ToolInvocations[0].Success);
        Assert.Equal(2, router.Invocations.Count);
        Assert.Contains(observer.Completed, t => t.Success);
    }

    [Fact]
    public async Task Provider_Failure_Stores_Error_Status_Without_Crash()
    {
        var provider = new FakeAgentProvider("p1");
        provider.Script.Add(new FakeProviderTurn { Throw = new InvalidOperationException("boom") });
        var host = TestHarness.ChatHost(provider);
        var convo = host.ConversationsApi.Create();
        var result = await host.ConversationsApi.SendAsync(convo.Id, "hi");
        Assert.Equal(TurnStatus.Error, result.Turns[0].Status);
        Assert.Equal("boom", result.Turns[0].Error);
        Assert.NotNull(host.ConversationsApi.Get(convo.Id));
    }

    [Fact]
    public async Task Computer_Control_Denied_When_Not_Allowed()
    {
        var rpc = new RecordingAgentRpc();
        var router = new AgentToolRouter(rpc);
        var result = await router.InvokeAsync(
            "window.list",
            TestHarness.EmptyArgs(),
            new ToolRouterContext { AllowComputerControl = false },
            CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("Deny", result.PermissionDecision);
        Assert.Empty(rpc.Calls);
    }

    [Fact]
    public void AgentRuntime_Does_Not_Reference_Uia_Win32_Or_Browser()
    {
        var names = typeof(AgentRuntime).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.DoesNotContain(names, n => n.Contains("UIA", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Win32", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Equals("SemanticDesktop.Browser", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Equals("SemanticDesktop.Agent", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("DesktopUseAgent.App", typeof(AgentRuntime).Assembly.GetName().Name);
    }

    [Fact]
    public async Task AgentRuntime_Invokes_Only_IToolRouter()
    {
        var provider = new FakeAgentProvider("p1");
        provider.Script.Add(new FakeProviderTurn
        {
            Events = { AgentProviderEvent.ToolCall("c1", "window.list", "{}"), AgentProviderEvent.Completed() }
        });
        provider.Script.Add(new FakeProviderTurn
        {
            Events = { AgentProviderEvent.TextDelta("ok"), AgentProviderEvent.Completed() }
        });
        var router = new FakeToolRouter();
        var rpc = new RecordingAgentRpc();
        var host = TestHarness.ChatHost(provider, router, rpc: rpc);
        var convo = host.ConversationsApi.Create();
        await host.ConversationsApi.SendAsync(convo.Id, "windows");
        Assert.Single(router.Invocations);
        Assert.DoesNotContain(rpc.Calls, c => c.Method != "session.create");
    }
}
