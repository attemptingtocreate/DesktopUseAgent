using SemanticDesktop.App.Lifecycle;
using SemanticDesktop.App.Mcp;
using SemanticDesktop.App.Models;
using SemanticDesktop.App.Persistence;
using SemanticDesktop.App.Providers;
using SemanticDesktop.App.Tools;

namespace SemanticDesktop.App.Tests;

public class McpAndLifecycleTests
{
    [Fact]
    public void Mcp_Add_Remove_Enable_Disable_Persist()
    {
        var root = TestHarness.TempRoot();
        try
        {
            var mcpStore = new McpConfigStore(root);
            var providers = new ProviderConfigStore(root);
            providers.Upsert(new ProviderConfig { Id = "default", Type = "openai", DisplayName = "Default" });
            var creds = new CredentialStore(root);
            var manager = new McpManager(mcpStore, creds, providers);

            var added = manager.Add(new McpConfiguration
            {
                Id = "github",
                Name = "GitHub",
                Type = "stdio",
                Command = "npx",
                Args = new List<string> { "-y", "@modelcontextprotocol/server-github" },
                EnvKeys = new List<string> { "GITHUB_TOKEN" },
                Enabled = true
            }, defaultProviderId: "default");

            Assert.Equal("github", added.Id);
            Assert.Contains("github", providers.Get("default")!.EnabledMcpIds);
            Assert.True(manager.Enable("github", false)!.Enabled == false);
            Assert.True(manager.Enable("github", true)!.Enabled);

            var reopened = new McpConfigStore(root);
            Assert.NotNull(reopened.Get("github"));
            Assert.Equal("stdio", reopened.Get("github")!.Type);
            Assert.Contains("GITHUB_TOKEN", reopened.Get("github")!.EnvKeys!);
            Assert.DoesNotContain("sk-", File.ReadAllText(Path.Combine(root, "mcp-servers.json")));

            Assert.True(manager.Remove("github"));
            Assert.Null(new McpConfigStore(root).Get("github"));
            Assert.False(manager.Remove(McpIds.Builtin));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Per_Agent_Mcp_Assignment_Changes_Runtime_Tool_List()
    {
        var providerA = TestHarness.TextProvider("prov-a", "a");
        var providerB = TestHarness.TextProvider("prov-b", "b");
        var mcp = new FakeMcpHost
        {
            ToolsByServer =
            {
                ["github"] = new List<AgentToolDefinition> { TestHarness.ExternalTool("github.list_issues", "github") }
            }
        };
        var host = TestHarness.ChatHost(new IAgentProvider[] { providerA, providerB }, mcp: mcp);
        TestHarness.SeedProvider(host, providerA, new[] { McpIds.Builtin, "github" });
        TestHarness.SeedProvider(host, providerB, new[] { McpIds.Builtin });
        host.McpConfigs.Upsert(new McpConfiguration { Id = "github", Name = "GitHub", Type = "stdio", Command = "npx", Enabled = true });
        Assert.Same(mcp, host.Runtime.McpHost);
        Assert.Contains("github", host.Providers.Get("prov-a")!.EnabledMcpIds);

        var convoA = host.ConversationsApi.Create(providerId: "prov-a");
        await host.ConversationsApi.SendAsync(convoA.Id, "a");
        var convoB = host.ConversationsApi.Create(providerId: "prov-b");
        await host.ConversationsApi.SendAsync(convoB.Id, "b");

        Assert.Contains(providerA.ReceivedRequests[0].Tools, t => t.Name == "github.list_issues");
        Assert.Contains(providerA.ReceivedRequests[0].Tools, t => t.Name == "window.list");
        Assert.DoesNotContain(providerB.ReceivedRequests[0].Tools, t => t.Name == "github.list_issues");
        Assert.Contains(providerB.ReceivedRequests[0].Tools, t => t.Name == "window.list");
    }

    [Fact]
    public async Task Mcp_List_Failure_Records_Error_And_Chat_Still_Works_With_Builtin()
    {
        var provider = TestHarness.TextProvider("p1", "still works");
        var mcp = new FakeMcpHost
        {
            ListErrors = { ["github"] = new InvalidOperationException("tools/list failed") }
        };
        var host = TestHarness.ChatHost(provider, mcp: mcp);
        TestHarness.SeedProvider(host, provider, new[] { McpIds.Builtin, "github" });
        host.McpConfigs.Upsert(new McpConfiguration { Id = "github", Name = "GitHub", Type = "stdio", Command = "npx", Enabled = true });
        var convo = host.ConversationsApi.Create();
        var result = await host.ConversationsApi.SendAsync(convo.Id, "hello");

        Assert.Equal(TurnStatus.Completed, result.Turns[0].Status);
        Assert.Contains(provider.ReceivedRequests[0].Tools, t => t.Name == "window.list");
        Assert.DoesNotContain(provider.ReceivedRequests[0].Tools, t => t.Name.StartsWith("github", StringComparison.Ordinal));
        Assert.Equal("error", mcp.GetStatus("github").State);
    }

    [Fact]
    public void Builtin_Mcp_Id_And_Catalog_Names()
    {
        Assert.Equal("desktopuseagent", ToolCatalog.BuiltinMcpId);
        Assert.Equal("DesktopUseAgent", ToolCatalog.BuiltinDisplayName);
        Assert.Contains(ToolCatalog.ComputerControlTools, t => t.Name == "window.list");
        Assert.Contains(ToolCatalog.ComputerControlTools, t => t.Name == "ui.invoke");
        Assert.Contains(ToolCatalog.ComputerControlTools, t => t.Name == "blender.export");
        Assert.Contains(ToolCatalog.ComputerControlTools, t => t.Name == "system.emergency_stop");
        Assert.Equal(McpIds.Builtin, McpConfigStore.Builtin().Id);
    }

    [Fact]
    public async Task Lifecycle_Recovers_After_Ping_Failure_Then_Start()
    {
        var gateway = new FakeAgentProcessGateway { PingsBeforeSuccess = 1 };
        var settings = new AppSettingsStore(null);
        var lifecycle = new AgentLifecycle(gateway, settings);

        await lifecycle.EnsureAgentAsync();
        Assert.Equal(1, gateway.StartCount);
        Assert.True(gateway.Running);

        await lifecycle.EnsureAgentAsync();
        Assert.Equal(1, gateway.StartCount);

        gateway.Running = false;
        await lifecycle.RecoverIfNeededAsync();
        Assert.Equal(2, gateway.StartCount);

        settings.Save(new AppSettings { General = { CloseBehavior = CloseBehavior.ExitAll } });
        await lifecycle.OnAppExitAsync();
        Assert.Equal(1, gateway.StopCount);

        var leaveGateway = new FakeAgentProcessGateway { PingsBeforeSuccess = 1 };
        var leaveSettings = new AppSettingsStore(null);
        leaveSettings.Save(new AppSettings { General = { CloseBehavior = CloseBehavior.LeaveAgentRunning } });
        var leave = new AgentLifecycle(leaveGateway, leaveSettings);
        await leave.EnsureAgentAsync();
        await leave.OnAppExitAsync();
        Assert.Equal(0, leaveGateway.StopCount);
        Assert.True(leaveGateway.Running);
    }
}
