using SemanticDesktop.App.Models;
using SemanticDesktop.App.Persistence;
using SemanticDesktop.App.Runtime;

namespace SemanticDesktop.App;

public sealed class ConversationService
{
    private readonly ConversationStore _store;
    private readonly AgentRuntime _runtime;
    private readonly AppSettingsStore _settings;

    public ConversationService(ConversationStore store, AgentRuntime runtime, AppSettingsStore settings)
    {
        _store = store;
        _runtime = runtime;
        _settings = settings;
    }

    public Conversation Create(string? title = null, string? providerId = null, string? model = null)
    {
        var settings = _settings.Get();
        var convo = new Conversation
        {
            Id = "conv_" + Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(title) ? "New conversation" : title,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            SelectedProviderId = providerId ?? settings.Agents.DefaultProviderId,
            SelectedModel = model ?? settings.Agents.DefaultModel
        };
        return _store.Save(convo);
    }

    public IReadOnlyList<ConversationListItem> List() => _store.List();

    public Conversation? Get(string id) => _store.Get(id);

    public bool Delete(string id) => _store.Delete(id);

    public Conversation? Rename(string id, string title) => _store.UpdateTitle(id, title);

    public Conversation? SetSelectedProvider(string id, string providerId, string? model = null)
    {
        var convo = _store.Get(id);
        if (convo is null)
        {
            return null;
        }

        convo.SelectedProviderId = providerId;
        if (model is not null)
        {
            convo.SelectedModel = model;
        }

        return _store.Save(convo);
    }

    public Task<Conversation> SendAsync(
        string conversationId,
        string userMessage,
        string? providerId = null,
        string? model = null,
        CancellationToken cancellationToken = default) =>
        _runtime.SendAsync(conversationId, userMessage, providerId, model, appendUserMessage: true, cancellationToken);

    public async Task<Conversation> RetryLastAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var convo = _store.Get(conversationId) ?? throw new InvalidOperationException("Conversation not found.");
        var lastUser = convo.Messages.LastOrDefault(m => m.Role == ChatRoles.User)
                       ?? throw new InvalidOperationException("No user message to retry.");

        var lastUserIndex = convo.Messages.FindLastIndex(m => m.Role == ChatRoles.User);
        if (lastUserIndex >= 0 && lastUserIndex < convo.Messages.Count - 1)
        {
            convo.Messages.RemoveRange(lastUserIndex + 1, convo.Messages.Count - lastUserIndex - 1);
        }

        if (convo.Turns.Count > 0)
        {
            var lastTurn = convo.Turns[^1];
            if (lastTurn.Status is TurnStatus.Error or TurnStatus.Cancelled or TurnStatus.PermissionDenied or TurnStatus.Streaming)
            {
                convo.Turns.RemoveAt(convo.Turns.Count - 1);
            }
        }

        _store.Save(convo);
        return await _runtime.SendAsync(conversationId, lastUser.Content, convo.SelectedProviderId, convo.SelectedModel, appendUserMessage: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task StopAsync(string conversationId, bool emergency = false, CancellationToken cancellationToken = default) =>
        _runtime.CancelAsync(conversationId, emergency, cancellationToken);
}
