using System.Runtime.CompilerServices;
using SemanticDesktop.App.Models;
using SemanticDesktop.App.Persistence;

namespace SemanticDesktop.App.Providers;

public sealed class FakeAgentProvider : IAgentProvider
{
    private CancellationTokenSource? _cancel;
    private int _turn;

    public FakeAgentProvider(string id, string type = "fake", string displayName = "Fake")
    {
        Id = id;
        Type = type;
        DisplayName = displayName;
    }

    public string Id { get; }
    public string Type { get; }
    public string DisplayName { get; }
    public AgentProviderCapabilities Capabilities { get; set; } = new(
        Streaming: true,
        Tools: true,
        Mcp: true,
        Vision: false,
        Models: true,
        Local: true,
        Remote: false);

    public List<FakeProviderTurn> Script { get; } = new();
    public List<AgentRequest> ReceivedRequests { get; } = new();
    public List<ModelRecord> Models { get; } = new();
    public ProviderConnectionStatus ConnectResult { get; set; } = ProviderConnectionStatus.Connected();
    public Exception? ConnectThrow { get; set; }
    public bool Connected { get; private set; }

    public Task<ProviderConnectionStatus> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (ConnectThrow is not null)
        {
            throw ConnectThrow;
        }

        Connected = ConnectResult.Kind == ProviderConnectionStatusKind.Connected;
        return Task.FromResult(ConnectResult);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Connected = false;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ModelRecord>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ModelRecord> list = Models.Count == 0
            ? new[] { new ModelRecord { Id = "fake-model", DisplayName = "Fake Model", ProviderId = Id } }
            : Models;
        return Task.FromResult(list);
    }

    public async IAsyncEnumerable<AgentProviderEvent> CompleteAsync(
        AgentRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ReceivedRequests.Add(request);
        var turn = _turn++;
        if (turn >= Script.Count)
        {
            yield return AgentProviderEvent.ErrorEvent("Fake provider has no scripted turn left.");
            yield break;
        }

        var scripted = Script[turn];
        if (scripted.Throw is not null)
        {
            throw scripted.Throw;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancel = linked;
        try
        {
            if (scripted.WaitForCancellation)
            {
                await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
            }

            foreach (var ev in scripted.Events)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (scripted.DelayPerEvent is { } delay)
                {
                    await Task.Delay(delay, linked.Token).ConfigureAwait(false);
                }

                yield return ev;
            }
        }
        finally
        {
            if (ReferenceEquals(_cancel, linked))
            {
                _cancel = null;
            }
        }
    }

    public Task CancelAsync(CancellationToken cancellationToken = default)
    {
        _cancel?.Cancel();
        return Task.CompletedTask;
    }
}

public sealed class FakeProviderTurn
{
    public List<AgentProviderEvent> Events { get; set; } = new();
    public Exception? Throw { get; set; }
    public TimeSpan? DelayPerEvent { get; set; }
    public bool WaitForCancellation { get; set; }
}
