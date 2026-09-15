using System.Text.Json;
using SemanticDesktop.App;
using SemanticDesktop.App.Lifecycle;
using SemanticDesktop.App.Mcp;
using SemanticDesktop.App.Persistence;
using SemanticDesktop.App.Providers;
using SemanticDesktop.App.Runtime;
using SemanticDesktop.App.Tools;

namespace SemanticDesktop.App.Tests;

internal static class TestHarness
{
    public static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dua-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static JsonElement EmptyArgs() => JsonDocument.Parse("{}").RootElement.Clone();

    public static AgentToolDefinition ExternalTool(string name, string mcpId) => new()
    {
        Name = name,
        Description = name,
        JsonSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone(),
        Source = mcpId
    };

    public static AppHost ChatHost(
        IAgentProvider provider,
        IToolRouter? router = null,
        IMcpHost? mcp = null,
        IAgentRpc? rpc = null,
        IAgentRuntimeObserver? observer = null,
        IApprovalCoordinator? approvals = null,
        IAgentProcessGateway? gateway = null,
        string? dataRoot = null)
    {
        var resolver = new StaticProviderResolver(provider);
        var host = new AppHost(
            dataRoot,
            resolver,
            router ?? new FakeToolRouter(),
            mcp,
            rpc,
            observer,
            approvals,
            gateway);
        SeedProvider(host, provider);
        return host;
    }

    public static AppHost ChatHost(
        IReadOnlyList<IAgentProvider> providers,
        IToolRouter? router = null,
        IMcpHost? mcp = null,
        IAgentRpc? rpc = null,
        IAgentRuntimeObserver? observer = null,
        IApprovalCoordinator? approvals = null,
        string? dataRoot = null)
    {
        var resolver = new StaticProviderResolver(providers.ToArray());
        var host = new AppHost(dataRoot, resolver, router ?? new FakeToolRouter(), mcp, rpc, observer, approvals);
        foreach (var provider in providers)
        {
            SeedProvider(host, provider);
        }

        return host;
    }

    public static void SeedProvider(AppHost host, IAgentProvider provider, IEnumerable<string>? mcpIds = null)
    {
        host.Providers.Upsert(new ProviderConfig
        {
            Id = provider.Id,
            Type = provider.Type,
            DisplayName = provider.DisplayName,
            Enabled = true,
            DefaultModel = "fake-model",
            EnabledMcpIds = mcpIds?.ToList() ?? new List<string> { McpIds.Builtin }
        });
        var settings = host.Settings.Get();
        settings.Agents.DefaultProviderId ??= provider.Id;
        settings.Agents.DefaultModel ??= "fake-model";
        host.Settings.Save(settings);
    }

    public static FakeAgentProvider TextProvider(string id, string text)
    {
        var provider = new FakeAgentProvider(id);
        provider.Script.Add(new FakeProviderTurn
        {
            Events = { AgentProviderEvent.TextDelta(text), AgentProviderEvent.Completed() }
        });
        return provider;
    }
}

internal sealed class RecordingAgentRpc : IAgentRpc
{
    public List<(string Method, object? Parameters)> Calls { get; } = new();
    public Func<string, object?, JsonElement>? Handler { get; set; }
    public JsonElement DefaultResult { get; set; } = JsonDocument.Parse("""{"ok":true,"data":{"ok":true}}""").RootElement.Clone();

    public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        Calls.Add((method, parameters));
        if (Handler is not null)
        {
            return Task.FromResult(Handler(method, parameters));
        }

        return Task.FromResult(DefaultResult);
    }
}
