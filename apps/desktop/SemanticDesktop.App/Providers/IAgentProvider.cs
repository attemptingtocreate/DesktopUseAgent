using System.Text.Json;
using SemanticDesktop.App.Models;

namespace SemanticDesktop.App.Providers;

public interface IAgentProvider
{
    string Id { get; }
    string Type { get; }
    string DisplayName { get; }
    AgentProviderCapabilities Capabilities { get; }
    Task<ProviderConnectionStatus> ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModelRecord>> ListModelsAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentProviderEvent> CompleteAsync(AgentRequest request, CancellationToken cancellationToken);
    Task CancelAsync(CancellationToken cancellationToken = default);
}

public sealed record AgentProviderCapabilities(
    bool Streaming,
    bool Tools,
    bool Mcp,
    bool Vision,
    bool Models,
    bool Local,
    bool Remote);

public sealed class AgentRequest
{
    public IReadOnlyList<AgentMessage> Messages { get; init; } = Array.Empty<AgentMessage>();
    public IReadOnlyList<AgentToolDefinition> Tools { get; init; } = Array.Empty<AgentToolDefinition>();
    public string? Model { get; init; }
    public IReadOnlyDictionary<string, string>? Extras { get; init; }
}

public sealed class AgentMessage
{
    public string Role { get; init; } = "user";
    public string? Content { get; init; }
    public string? ToolCallId { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<AgentToolCall>? ToolCalls { get; init; }
}

public sealed class AgentToolCall
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string ArgumentsJson { get; init; } = "{}";
}

public sealed class AgentToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public JsonElement JsonSchema { get; init; }
    public string? Source { get; init; }
}

public enum AgentProviderEventKind
{
    TextDelta,
    ToolCall,
    Completed,
    Error,
    Status
}

public sealed class AgentProviderEvent
{
    public AgentProviderEventKind Kind { get; init; }
    public string? Text { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public string? ArgumentsJson { get; init; }
    public string? Error { get; init; }
    public string? Status { get; init; }
    public string? Model { get; init; }

    public static AgentProviderEvent TextDelta(string text) => new()
    {
        Kind = AgentProviderEventKind.TextDelta,
        Text = text
    };

    public static AgentProviderEvent ToolCall(string id, string name, string argumentsJson) => new()
    {
        Kind = AgentProviderEventKind.ToolCall,
        ToolCallId = id,
        ToolName = name,
        ArgumentsJson = argumentsJson
    };

    public static AgentProviderEvent Completed(string? model = null) => new()
    {
        Kind = AgentProviderEventKind.Completed,
        Model = model
    };

    public static AgentProviderEvent ErrorEvent(string message) => new()
    {
        Kind = AgentProviderEventKind.Error,
        Error = message
    };

    public static AgentProviderEvent StatusEvent(string status) => new()
    {
        Kind = AgentProviderEventKind.Status,
        Status = status
    };
}

public enum ProviderConnectionStatusKind
{
    Connected,
    NotConfigured,
    Unsupported,
    Error
}

public sealed class ProviderConnectionStatus
{
    public ProviderConnectionStatusKind Kind { get; init; }
    public string? Message { get; init; }

    public static ProviderConnectionStatus Connected(string? message = null) => new()
    {
        Kind = ProviderConnectionStatusKind.Connected,
        Message = message
    };

    public static ProviderConnectionStatus NotConfigured(string message) => new()
    {
        Kind = ProviderConnectionStatusKind.NotConfigured,
        Message = message
    };

    public static ProviderConnectionStatus Unsupported(string message) => new()
    {
        Kind = ProviderConnectionStatusKind.Unsupported,
        Message = message
    };

    public static ProviderConnectionStatus Error(string message) => new()
    {
        Kind = ProviderConnectionStatusKind.Error,
        Message = message
    };
}

public interface IProviderResolver
{
    IAgentProvider Resolve(string providerId);
}

public sealed class StaticProviderResolver : IProviderResolver
{
    private readonly IReadOnlyDictionary<string, IAgentProvider> _providers;

    public StaticProviderResolver(IReadOnlyDictionary<string, IAgentProvider> providers)
    {
        _providers = providers;
    }

    public StaticProviderResolver(params IAgentProvider[] providers)
        : this(providers.ToDictionary(p => p.Id, StringComparer.Ordinal))
    {
    }

    public IAgentProvider Resolve(string providerId)
    {
        if (_providers.TryGetValue(providerId, out var provider))
        {
            return provider;
        }

        throw new InvalidOperationException($"Unknown provider '{providerId}'.");
    }
}
