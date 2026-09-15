using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using SemanticDesktop.App.Mcp;
using SemanticDesktop.App.Models;
using SemanticDesktop.App.Persistence;
using SemanticDesktop.App.Providers;
using SemanticDesktop.App.Tools;

namespace SemanticDesktop.App.Runtime;

public sealed class AgentRuntime
{
    public const int DefaultMaxIterations = 25;

    private readonly ConversationStore _conversations;
    private readonly AppSettingsStore _settings;
    private readonly ProviderConfigStore _providers;
    private readonly McpConfigStore _mcpConfigs;
    private readonly IProviderResolver _resolver;
    private readonly IToolRouter _router;
    private readonly IMcpHost? _mcpHost;
    private readonly IAgentRpc? _rpc;
    private readonly IAgentRuntimeObserver _observer;
    private readonly IApprovalCoordinator? _approvals;
    private readonly int _maxIterations;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RuntimeRunState> _states = new(StringComparer.Ordinal);
    private string? _nativeSessionId;

    public IMcpHost? McpHost => _mcpHost;

    public AgentRuntime(
        ConversationStore conversations,
        AppSettingsStore settings,
        ProviderConfigStore providers,
        McpConfigStore mcpConfigs,
        IProviderResolver resolver,
        IToolRouter router,
        IMcpHost? mcpHost = null,
        IAgentRpc? rpc = null,
        IAgentRuntimeObserver? observer = null,
        IApprovalCoordinator? approvals = null,
        int? maxIterations = null)
    {
        _conversations = conversations;
        _settings = settings;
        _providers = providers;
        _mcpConfigs = mcpConfigs;
        _resolver = resolver;
        _router = router;
        _mcpHost = mcpHost;
        _rpc = rpc;
        _observer = observer ?? NullAgentRuntimeObserver.Instance;
        _approvals = approvals;
        _maxIterations = maxIterations ?? Math.Max(1, settings.Get().Advanced.MaxToolIterations);
        if (_maxIterations <= 0)
        {
            _maxIterations = DefaultMaxIterations;
        }
    }

    public RuntimeRunState? GetStatus(string conversationId) =>
        _states.TryGetValue(conversationId, out var state) ? state : null;

    public async Task CancelAsync(string conversationId, bool emergency = false, CancellationToken cancellationToken = default)
    {
        if (_runs.TryGetValue(conversationId, out var cts))
        {
            cts.Cancel();
        }

        _states.AddOrUpdate(conversationId,
            _ => new RuntimeRunState { ConversationId = conversationId, Status = TurnStatus.Cancelled },
            (_, s) =>
            {
                s.Status = TurnStatus.Cancelled;
                return s;
            });

        _states.TryGetValue(conversationId, out var state);
        if (emergency || state?.ComputerControlInFlight == true)
        {
            if (_rpc is not null)
            {
                var tool = state?.InFlightTool;
                if (string.Equals(tool, "plan.execute", StringComparison.OrdinalIgnoreCase) && !emergency)
                {
                    await SafeRpc("plan.cancel", new { }, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SafeRpc("system.emergency_stop", new { }, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    public async Task<Conversation> SendAsync(
        string conversationId,
        string userMessage,
        string? providerId = null,
        string? model = null,
        bool appendUserMessage = true,
        CancellationToken cancellationToken = default)
    {
        var conversation = _conversations.Get(conversationId)
                           ?? throw new InvalidOperationException($"Conversation '{conversationId}' was not found.");
        if (appendUserMessage)
        {
            conversation.Messages.Add(new ChatMessage
            {
                Id = "msg_" + Guid.NewGuid().ToString("N"),
                Role = ChatRoles.User,
                Content = userMessage,
                CreatedAt = DateTimeOffset.UtcNow
            });
            if (conversation.Title == "New conversation" && !string.IsNullOrWhiteSpace(userMessage))
            {
                conversation.Title = userMessage.Length <= 48 ? userMessage : userMessage[..48];
            }
        }

        var settings = _settings.Get();
        var selectedProviderId = providerId ?? conversation.SelectedProviderId ?? settings.Agents.DefaultProviderId;
        if (string.IsNullOrWhiteSpace(selectedProviderId))
        {
            throw new InvalidOperationException("No provider is selected.");
        }

        conversation.SelectedProviderId = selectedProviderId;
        conversation.SelectedModel = model ?? conversation.SelectedModel ?? settings.Agents.DefaultModel;
        var provider = _resolver.Resolve(selectedProviderId);
        var turn = new AssistantTurn
        {
            Id = "turn_" + Guid.NewGuid().ToString("N"),
            ProviderId = provider.Id,
            ProviderType = provider.Type,
            Model = conversation.SelectedModel,
            Status = TurnStatus.Streaming,
            StartedAt = DateTimeOffset.UtcNow
        };
        conversation.Turns.Add(turn);
        _conversations.Save(conversation);

        var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runs[conversationId] = runCts;
        var state = new RuntimeRunState { ConversationId = conversationId, Status = TurnStatus.Streaming };
        _states[conversationId] = state;
        _observer.OnStatus(conversationId, TurnStatus.Streaming);

        try
        {
            await RunLoopAsync(conversation, turn, provider, settings, state, runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            turn.Status = TurnStatus.Cancelled;
            turn.CompletedAt = DateTimeOffset.UtcNow;
            state.Status = TurnStatus.Cancelled;
            _observer.OnStatus(conversationId, TurnStatus.Cancelled);
        }
        catch (Exception ex)
        {
            turn.Status = TurnStatus.Error;
            turn.Error = ex.Message;
            turn.CompletedAt = DateTimeOffset.UtcNow;
            state.Status = TurnStatus.Error;
            _observer.OnStatus(conversationId, TurnStatus.Error);
        }
        finally
        {
            _runs.TryRemove(conversationId, out _);
            runCts.Dispose();
            _conversations.Save(conversation);
        }

        return _conversations.Get(conversationId) ?? conversation;
    }

    public async Task<IReadOnlyList<AgentToolDefinition>> BuildToolListAsync(
        string providerId,
        AppSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        settings ??= _settings.Get();
        var config = _providers.Get(providerId);
        var enabledIds = config?.EnabledMcpIds ?? new List<string> { McpIds.Builtin };
        var tools = new List<AgentToolDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var builtinEnabled = enabledIds.Contains(McpIds.Builtin, StringComparer.OrdinalIgnoreCase)
                             && (_mcpConfigs.Get(McpIds.Builtin)?.Enabled ?? true);
        if (settings.ComputerControl.Enabled && builtinEnabled)
        {
            foreach (var tool in ToolCatalog.ComputerControlTools)
            {
                if (seen.Add(tool.Name))
                {
                    tools.Add(tool);
                }
            }
        }

        foreach (var mcpId in enabledIds.Where(id => !string.Equals(id, McpIds.Builtin, StringComparison.OrdinalIgnoreCase)))
        {
            var mcp = _mcpConfigs.Get(mcpId);
            if (mcp is { Enabled: false })
            {
                continue;
            }

            if (_mcpHost is null)
            {
                continue;
            }

            try
            {
                var listed = await _mcpHost.ListToolsAsync(mcpId, cancellationToken).ConfigureAwait(false);
                foreach (var tool in listed)
                {
                    if (ToolCatalog.IsComputerControl(tool.Name))
                    {
                        continue;
                    }

                    if (seen.Add(tool.Name))
                    {
                        tools.Add(new AgentToolDefinition
                        {
                            Name = tool.Name,
                            Description = tool.Description,
                            JsonSchema = tool.JsonSchema,
                            Source = mcpId
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _mcpHost.RecordError(mcpId, ex.Message);
            }
        }

        return tools;
    }

    private async Task RunLoopAsync(
        Conversation conversation,
        AssistantTurn turn,
        IAgentProvider provider,
        AppSettings settings,
        RuntimeRunState state,
        CancellationToken cancellationToken)
    {
        var working = ToAgentMessages(conversation);
        var tools = await BuildToolListAsync(provider.Id, settings, cancellationToken).ConfigureAwait(false);
        var toolSources = tools.ToDictionary(t => t.Name, t => t.Source, StringComparer.Ordinal);
        var assistantText = new StringBuilder();
        var iterations = 0;

        while (iterations++ < _maxIterations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new AgentRequest
            {
                Messages = working.ToList(),
                Tools = tools,
                Model = turn.Model ?? conversation.SelectedModel
            };

            var pendingCalls = new List<AgentToolCall>();
            var sawComplete = false;
            var sawError = false;
            try
            {
                await foreach (var ev in provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (ev.Kind)
                    {
                        case AgentProviderEventKind.TextDelta when !string.IsNullOrEmpty(ev.Text):
                            assistantText.Append(ev.Text);
                            _observer.OnTextDelta(conversation.Id, ev.Text);
                            break;
                        case AgentProviderEventKind.ToolCall when !string.IsNullOrWhiteSpace(ev.ToolName):
                            pendingCalls.Add(new AgentToolCall
                            {
                                Id = string.IsNullOrWhiteSpace(ev.ToolCallId) ? Guid.NewGuid().ToString("N") : ev.ToolCallId,
                                Name = ev.ToolName,
                                ArgumentsJson = string.IsNullOrWhiteSpace(ev.ArgumentsJson) ? "{}" : ev.ArgumentsJson
                            });
                            break;
                        case AgentProviderEventKind.Error:
                            sawError = true;
                            turn.Status = TurnStatus.Error;
                            turn.Error = ev.Error;
                            turn.CompletedAt = DateTimeOffset.UtcNow;
                            state.Status = TurnStatus.Error;
                            AppendAssistant(conversation, turn, assistantText.ToString());
                            _observer.OnStatus(conversation.Id, TurnStatus.Error);
                            _conversations.Save(conversation);
                            return;
                        case AgentProviderEventKind.Completed:
                            sawComplete = true;
                            if (!string.IsNullOrWhiteSpace(ev.Model))
                            {
                                turn.Model = ev.Model;
                            }
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (sawError)
            {
                return;
            }

            if (pendingCalls.Count == 0)
            {
                turn.Status = TurnStatus.Completed;
                turn.CompletedAt = DateTimeOffset.UtcNow;
                state.Status = TurnStatus.Completed;
                AppendAssistant(conversation, turn, assistantText.ToString());
                _observer.OnStatus(conversation.Id, TurnStatus.Completed);
                _conversations.Save(conversation);
                return;
            }

            working.Add(new AgentMessage
            {
                Role = ChatRoles.Assistant,
                Content = assistantText.ToString(),
                ToolCalls = pendingCalls
            });

            foreach (var call in pendingCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!toolSources.ContainsKey(call.Name) && !ToolCatalog.IsComputerControl(call.Name))
                {
                    var rejected = ToolRouterResult.Fail($"Unknown tool '{call.Name}'.");
                    RecordTool(conversation, turn, call, rejected, started: DateTimeOffset.UtcNow);
                    working.Add(ToolMessage(call, rejected));
                    continue;
                }

                JsonElement args;
                try
                {
                    args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson).RootElement.Clone();
                }
                catch
                {
                    args = JsonDocument.Parse("{}").RootElement.Clone();
                }

                var target = TryTarget(args);
                var invocation = new ToolInvocation
                {
                    Id = call.Id,
                    Tool = call.Name,
                    Target = target,
                    StartedAt = DateTimeOffset.UtcNow
                };
                turn.ToolInvocations.Add(invocation);
                _observer.OnToolStarted(conversation.Id, invocation);
                state.InFlightTool = call.Name;
                state.ComputerControlInFlight = ToolCatalog.IsComputerControl(call.Name);
                _conversations.Save(conversation);

                var ctx = new ToolRouterContext
                {
                    SessionId = await EnsureNativeSessionAsync(cancellationToken).ConfigureAwait(false),
                    ConversationId = conversation.Id,
                    AllowComputerControl = settings.ComputerControl.Enabled,
                    McpServerId = toolSources.TryGetValue(call.Name, out var src) ? src : null
                };

                var result = await _router.InvokeAsync(call.Name, args, ctx, cancellationToken).ConfigureAwait(false);
                if (result.PendingApproval)
                {
                    _observer.OnApprovalNeeded(conversation.Id, result.ApprovalId ?? "", call.Name, result.Target ?? target);
                    if (_approvals is not null && !string.IsNullOrWhiteSpace(result.ApprovalId))
                    {
                        var allowed = await _approvals.WaitAsync(result.ApprovalId, cancellationToken).ConfigureAwait(false);
                        if (!allowed)
                        {
                            result = ToolRouterResult.Denied("Approval was denied.", "Deny");
                        }
                        else
                        {
                            result = await _router.InvokeAsync(call.Name, args, ctx, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else if (_rpc is not null)
                    {
                        result = await WaitForAgentApprovalAsync(call.Name, args, ctx, result, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        result = ToolRouterResult.Denied("Approval required but no coordinator is configured.", "Ask");
                    }
                }

                invocation.CompletedAt = DateTimeOffset.UtcNow;
                invocation.Success = result.Success;
                invocation.PermissionDecision = result.PermissionDecision;
                invocation.Error = result.Error;
                invocation.Summary = result.Summary;
                invocation.Target = result.Target ?? invocation.Target;
                state.ComputerControlInFlight = false;
                state.InFlightTool = null;
                _observer.OnToolCompleted(conversation.Id, invocation);
                working.Add(ToolMessage(call, result));
                _conversations.Save(conversation);

                if (string.Equals(result.PermissionDecision, "Deny", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(turn.Status, TurnStatus.PermissionDenied, StringComparison.Ordinal))
                {
                    if (!result.Success)
                    {
                        turn.Status = TurnStatus.PermissionDenied;
                        turn.Error = result.Error ?? result.Summary;
                        turn.CompletedAt = DateTimeOffset.UtcNow;
                        state.Status = TurnStatus.PermissionDenied;
                        AppendAssistant(conversation, turn, assistantText.ToString());
                        _observer.OnStatus(conversation.Id, TurnStatus.PermissionDenied);
                        _conversations.Save(conversation);
                        return;
                    }
                }
            }

            assistantText.Clear();
            if (!sawComplete && pendingCalls.Count == 0)
            {
                break;
            }
        }

        if (turn.Status == TurnStatus.Streaming)
        {
            turn.Status = TurnStatus.Error;
            turn.Error = "Max tool iterations exceeded.";
            turn.CompletedAt = DateTimeOffset.UtcNow;
            state.Status = TurnStatus.Error;
            AppendAssistant(conversation, turn, assistantText.ToString());
            _observer.OnStatus(conversation.Id, TurnStatus.Error);
            _conversations.Save(conversation);
        }
    }

    private async Task<ToolRouterResult> WaitForAgentApprovalAsync(
        string toolName,
        JsonElement args,
        ToolRouterContext ctx,
        ToolRouterResult pending,
        CancellationToken cancellationToken)
    {
        if (_rpc is null)
        {
            return pending;
        }

        for (var i = 0; i < 60; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var pendingEl = await _rpc.CallAsync("permission.pending", new { }, cancellationToken).ConfigureAwait(false);
                _ = pendingEl;
            }
            catch
            {
                // polling is best-effort
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            var retry = await _router.InvokeAsync(toolName, args, ctx, cancellationToken).ConfigureAwait(false);
            if (!retry.PendingApproval)
            {
                return retry;
            }
        }

        return ToolRouterResult.Denied("Approval timed out.", "Ask");
    }

    private async Task<string?> EnsureNativeSessionAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_nativeSessionId) || _rpc is null)
        {
            return _nativeSessionId;
        }

        try
        {
            var result = await _rpc.CallAsync(
                "session.create",
                new { clientId = "desktopuseagent-chat", autoApproveAsk = false, approvalTimeoutSeconds = 600 },
                cancellationToken).ConfigureAwait(false);
            var data = result.TryGetProperty("data", out var inner) ? inner : result;
            if (data.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String)
            {
                _nativeSessionId = id.GetString();
            }
        }
        catch
        {
            // native chat still works without a dedicated session (default sess_default)
        }

        return _nativeSessionId;
    }

    private async Task SafeRpc(string method, object parameters, CancellationToken cancellationToken)
    {
        if (_rpc is null)
        {
            return;
        }

        try
        {
            await _rpc.CallAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // stop semantics are best-effort
        }
    }

    private static List<AgentMessage> ToAgentMessages(Conversation conversation)
    {
        var list = new List<AgentMessage>();
        foreach (var msg in conversation.Messages)
        {
            if (msg.Role is ChatRoles.User or ChatRoles.Assistant or ChatRoles.System)
            {
                list.Add(new AgentMessage { Role = msg.Role, Content = msg.Content });
            }
        }

        return list;
    }

    private static void AppendAssistant(Conversation conversation, AssistantTurn turn, string text)
    {
        if (string.IsNullOrEmpty(text) && conversation.Messages.Any(m => m.TurnId == turn.Id && m.Role == ChatRoles.Assistant))
        {
            return;
        }

        conversation.Messages.Add(new ChatMessage
        {
            Id = "msg_" + Guid.NewGuid().ToString("N"),
            Role = ChatRoles.Assistant,
            Content = text,
            CreatedAt = DateTimeOffset.UtcNow,
            TurnId = turn.Id,
            ToolResults = turn.ToolInvocations.Select(t => new ToolResultReference
            {
                ToolCallId = t.Id,
                Tool = t.Tool,
                Summary = t.Summary ?? "",
                Success = t.Success
            }).ToList()
        });
    }

    private static AgentMessage ToolMessage(AgentToolCall call, ToolRouterResult result) => new()
    {
        Role = "tool",
        Name = call.Name,
        ToolCallId = call.Id,
        Content = result.ContentForModel ?? result.Summary ?? result.Error ?? ""
    };

    private static void RecordTool(Conversation conversation, AssistantTurn turn, AgentToolCall call, ToolRouterResult result, DateTimeOffset started)
    {
        turn.ToolInvocations.Add(new ToolInvocation
        {
            Id = call.Id,
            Tool = call.Name,
            StartedAt = started,
            CompletedAt = DateTimeOffset.UtcNow,
            Success = result.Success,
            PermissionDecision = result.PermissionDecision,
            Error = result.Error,
            Summary = result.Summary
        });
        _ = conversation;
    }

    private static string? TryTarget(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "path", "url", "windowId", "elementId", "target" })
        {
            if (args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }
}
