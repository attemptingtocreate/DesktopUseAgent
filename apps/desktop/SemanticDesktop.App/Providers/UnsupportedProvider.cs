using System.Runtime.CompilerServices;
using SemanticDesktop.App.Models;
using SemanticDesktop.App.Persistence;

namespace SemanticDesktop.App.Providers;

public sealed class UnsupportedProvider : IAgentProvider
{
    public UnsupportedProvider(ProviderConfig config)
    {
        Id = config.Id;
        Type = string.IsNullOrWhiteSpace(config.Type) ? "unsupported" : config.Type;
        DisplayName = string.IsNullOrWhiteSpace(config.DisplayName) ? config.Id : config.DisplayName;
    }

    public string Id { get; }
    public string Type { get; }
    public string DisplayName { get; }

    public AgentProviderCapabilities Capabilities { get; } = new(
        Streaming: false,
        Tools: false,
        Mcp: false,
        Vision: false,
        Models: false,
        Local: false,
        Remote: true);

    public Task<ProviderConnectionStatus> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var message = Type.Equals("chatgpt-web", StringComparison.OrdinalIgnoreCase)
            ? "ChatGPT website providers are not supported. Use the official OpenAI API instead."
            : $"Provider type '{Type}' is not supported.";
        return Task.FromResult(ProviderConnectionStatus.Unsupported(message));
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ModelRecord>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModelRecord>>(Array.Empty<ModelRecord>());

    public async IAsyncEnumerable<AgentProviderEvent> CompleteAsync(
        AgentRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return AgentProviderEvent.ErrorEvent($"Provider '{DisplayName}' ({Type}) cannot complete requests.");
    }

    public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
