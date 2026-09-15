using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SemanticDesktop.App.Models;
using SemanticDesktop.App.Persistence;

namespace SemanticDesktop.App.Providers;

public sealed class AnthropicProvider : IAgentProvider, IDisposable
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";
    public const string ApiVersion = "2023-06-01";

    private readonly ProviderConfig _config;
    private readonly CredentialStore _credentials;
    private readonly HttpClient _http;
    private CancellationTokenSource? _cancel;

    public AnthropicProvider(ProviderConfig config, CredentialStore credentials, HttpMessageHandler? handler = null)
    {
        _config = config;
        _credentials = credentials;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(4);
        Id = config.Id;
        Type = string.IsNullOrWhiteSpace(config.Type) ? "anthropic" : config.Type;
        DisplayName = string.IsNullOrWhiteSpace(config.DisplayName) ? "Anthropic" : config.DisplayName;
    }

    public string Id { get; }
    public string Type { get; }
    public string DisplayName { get; }

    public AgentProviderCapabilities Capabilities { get; } = new(
        Streaming: false,
        Tools: true,
        Mcp: true,
        Vision: true,
        Models: true,
        Local: false,
        Remote: true);

    public string BaseUrl => string.IsNullOrWhiteSpace(_config.BaseUrl) ? DefaultBaseUrl : _config.BaseUrl.TrimEnd('/');

    public Task<ProviderConnectionStatus> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var key = _credentials.Get(CredentialStore.ProviderApiKey(Id));
        if (string.IsNullOrWhiteSpace(key))
        {
            return Task.FromResult(ProviderConnectionStatus.NotConfigured($"API key is not configured for '{DisplayName}'."));
        }

        return Task.FromResult(ProviderConnectionStatus.Connected());
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ModelRecord>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var id = _config.DefaultModel ?? "claude-sonnet-4-5";
        IReadOnlyList<ModelRecord> models = new[] { new ModelRecord { Id = id, DisplayName = id, ProviderId = Id } };
        return Task.FromResult(models);
    }

    public async IAsyncEnumerable<AgentProviderEvent> CompleteAsync(
        AgentRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancel = linked;
        string? error = null;
        JsonDocument? doc = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/messages");
            var key = _credentials.Get(CredentialStore.ProviderApiKey(Id));
            if (!string.IsNullOrWhiteSpace(key))
            {
                req.Headers.TryAddWithoutValidation("x-api-key", key);
            }

            req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
            req.Content = new StringContent(BuildBody(request), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                error = $"Anthropic HTTP {(int)resp.StatusCode}: {Trim(text)}";
            }
            else
            {
                doc = JsonDocument.Parse(text);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (error is not null)
        {
            yield return AgentProviderEvent.ErrorEvent(error);
            yield break;
        }

        try
        {
            var root = doc!.RootElement;
            if (root.TryGetProperty("error", out var errEl))
            {
                yield return AgentProviderEvent.ErrorEvent(errEl.ToString());
                yield break;
            }

            var model = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() : request.Model ?? _config.DefaultModel;
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "text" && block.TryGetProperty("text", out var textEl))
                    {
                        var text = textEl.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            yield return AgentProviderEvent.TextDelta(text);
                        }
                    }
                    else if (type == "tool_use")
                    {
                        var id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "tool" : "tool";
                        var name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                        var args = block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}";
                        yield return AgentProviderEvent.ToolCall(id, name, args);
                    }
                }
            }

            yield return AgentProviderEvent.Completed(model);
        }
        finally
        {
            doc?.Dispose();
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

    public void Dispose() => _http.Dispose();

    private string BuildBody(AgentRequest request)
    {
        string? system = null;
        var messages = new List<object>();
        foreach (var msg in request.Messages)
        {
            if (string.Equals(msg.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                system = msg.Content;
                continue;
            }

            if (msg.ToolCalls is { Count: > 0 })
            {
                var content = new List<object>();
                if (!string.IsNullOrEmpty(msg.Content))
                {
                    content.Add(new { type = "text", text = msg.Content });
                }

                foreach (var call in msg.ToolCalls)
                {
                    object input;
                    try
                    {
                        input = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                    }
                    catch
                    {
                        input = new { };
                    }

                    content.Add(new { type = "tool_use", id = call.Id, name = call.Name, input });
                }

                messages.Add(new { role = "assistant", content });
                continue;
            }

            if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "tool_result",
                            tool_use_id = msg.ToolCallId,
                            content = msg.Content ?? ""
                        }
                    }
                });
                continue;
            }

            messages.Add(new { role = msg.Role == "assistant" ? "assistant" : "user", content = msg.Content ?? "" });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model ?? _config.DefaultModel ?? "claude-sonnet-4-5",
            ["max_tokens"] = 4096,
            ["messages"] = messages
        };
        if (!string.IsNullOrWhiteSpace(system))
        {
            payload["system"] = system;
        }

        if (request.Tools.Count > 0)
        {
            payload["tools"] = request.Tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                input_schema = JsonSerializer.Deserialize<JsonElement>(t.JsonSchema.GetRawText())
            }).ToArray();
        }

        return JsonSerializer.Serialize(payload);
    }

    private static string Trim(string text) => text.Length <= 400 ? text : text[..400];
}
