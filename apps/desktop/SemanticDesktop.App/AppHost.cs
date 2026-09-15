using SemanticDesktop.App.Lifecycle;
using SemanticDesktop.App.Mcp;
using SemanticDesktop.App.Persistence;
using SemanticDesktop.App.Providers;
using SemanticDesktop.App.Runtime;
using SemanticDesktop.App.Tools;
using SemanticDesktop.ControlCenter.Client;

namespace SemanticDesktop.App;

public sealed class AppHost
{
    public string? DataRoot { get; }
    public ConversationStore Conversations { get; }
    public AppSettingsStore Settings { get; }
    public ProviderConfigStore Providers { get; }
    public McpConfigStore McpConfigs { get; }
    public CredentialStore Credentials { get; }
    public McpManager Mcp { get; }
    public AgentRuntime Runtime { get; }
    public ConversationService ConversationsApi { get; }
    public AgentLifecycle? Lifecycle { get; }
    public OpenAiTunnelLifecycle OpenAiTunnel { get; }
    public McpGatewayStatus McpGatewayStatus { get; }
    public IToolRouter ToolRouter { get; }
    public IProviderResolver ProviderResolver { get; }

    public AppHost(
        string? dataRoot,
        IProviderResolver? providerResolver = null,
        IToolRouter? toolRouter = null,
        IMcpHost? mcpHost = null,
        IAgentRpc? rpc = null,
        IAgentRuntimeObserver? observer = null,
        IApprovalCoordinator? approvals = null,
        IAgentProcessGateway? processGateway = null,
        IOpenAiTunnelProcessGateway? openAiTunnelGateway = null,
        IOpenAiTunnelPathResolver? openAiTunnelPaths = null,
        string? agentProjectOrDll = null,
        string? repoRoot = null)
    {
        DataRoot = dataRoot;
        Conversations = new ConversationStore(dataRoot);
        Settings = new AppSettingsStore(dataRoot);
        Providers = new ProviderConfigStore(dataRoot);
        McpConfigs = new McpConfigStore(dataRoot);
        Credentials = new CredentialStore(dataRoot);
        var agentRpc = rpc ?? (dataRoot is null ? null : new AgentBridgeRpc(new AgentBridge(agentProjectOrDll: agentProjectOrDll)));
        Mcp = new McpManager(McpConfigs, Credentials, Providers, agentRpc);
        ProviderResolver = providerResolver ?? new ConfigProviderResolver(Providers, Credentials);
        ToolRouter = toolRouter ?? new AgentToolRouter(agentRpc, mcpHost ?? Mcp);
        var activeMcp = mcpHost ?? Mcp;
        Runtime = new AgentRuntime(
            Conversations,
            Settings,
            Providers,
            McpConfigs,
            ProviderResolver,
            ToolRouter,
            activeMcp,
            agentRpc,
            observer,
            approvals);
        ConversationsApi = new ConversationService(Conversations, Runtime, Settings);
        if (processGateway is not null)
        {
            Lifecycle = new AgentLifecycle(processGateway, Settings);
        }
        else if (dataRoot is not null)
        {
            Lifecycle = new AgentLifecycle(new AgentBridgeProcessGateway(new AgentBridge(agentProjectOrDll: agentProjectOrDll), agentProjectOrDll), Settings);
        }

        OpenAiTunnel = new OpenAiTunnelLifecycle(
            openAiTunnelGateway ?? new OpenAiTunnelProcessGateway(),
            Settings,
            Credentials,
            openAiTunnelPaths);

        McpGatewayStatus = AgentLifecycle.ProbeMcpGateway(repoRoot);
    }

    public static AppHost CreateProduction(string? dataRoot = null, string? agentProjectOrDll = null, string? repoRoot = null) =>
        new(dataRoot ?? DataRootResolver.Resolve(), agentProjectOrDll: agentProjectOrDll, repoRoot: repoRoot);

    public static AppHost CreateInMemory(
        IProviderResolver providers,
        IToolRouter router,
        IMcpHost? mcpHost = null,
        IAgentRpc? rpc = null,
        IAgentRuntimeObserver? observer = null,
        IApprovalCoordinator? approvals = null,
        IAgentProcessGateway? processGateway = null,
        IOpenAiTunnelProcessGateway? openAiTunnelGateway = null,
        IOpenAiTunnelPathResolver? openAiTunnelPaths = null) =>
        new(null, providers, router, mcpHost, rpc, observer, approvals, processGateway, openAiTunnelGateway, openAiTunnelPaths);
}
