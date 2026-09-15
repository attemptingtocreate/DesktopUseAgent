using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SemanticDesktop.App.Models;
using SemanticDesktop.App.Persistence;

namespace SemanticDesktop.App.Providers;

public sealed class OllamaProvider : IAgentProvider, IDisposable
{
    public const string DefaultBaseUrl = "http://127.0.0.1:11434";

    private readonly ProviderConfig _config;
    private readonly HttpClient _http;
    private CancellationTokenSource? _cancel;

    public OllamaProvider(ProviderConfig config, HttpMessageHandler? handler = null)
    {
        _config = config;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(10);
        Id = config.Id;
        Type = string.IsNullOrWhiteSpace(config.Type) ? "ollama" : config.Type;
        DisplayName = string.IsNullOrWhiteSpace(config.DisplayName) ? "Ollama" : config.DisplayName;
    }

    public string Id { get; }
    public string Type { get; }
    public string DisplayName { get; }

    public AgentProviderCapabilities Capabilities { get; } = new(
        Streaming: true,
        Tools: true,
        Mcp: true,
        Vision: false,
        Models: true,
        Local: true,
        Remote: false);

    public string BaseUrl => string.IsNullOrWhiteSpace(_config.BaseUrl) ? DefaultBaseUrl : _config.BaseUrl.TrimEnd('/');

    public Task<ProviderConnectionStatus> ConnectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ProviderConnectionStatus.Connected("Local Ollama endpoint."));

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<IReadOnlyList<ModelRecord>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{BaseUrl}/api/tags", cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return DefaultModels();
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                return DefaultModels();
            }

            return models.EnumerateArray()
                .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => new ModelRecord { Id = n!, DisplayName = n!, ProviderId = Id })
                .ToList();
        }
        catch
        {
            return DefaultModels();
        }
    }

    public async IAsyncEnumerable<AgentProviderEvent> CompleteAsync(
        AgentRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancel = linked;
        HttpResponseMessage? resp = null;
        Stream? stream = null;
        StreamReader? reader = null;
        string? error = null;
        try
        {
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/chat");
            httpReq.Content = new StringContent(BuildBody(request), Encoding.UTF8, "application/json");
            resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                error = $"Ollama HTTP {(int)resp.StatusCode}: {Trim(await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false))}";
            }
            else
            {
                stream = await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                reader = new StreamReader(stream);
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
            Cleanup(resp, stream, reader);
            yield break;
        }

        string? model = request.Model ?? _config.DefaultModel;
        var yieldedTools = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (reader is not null)
            {
                linked.Token.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch
                {
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                    {
                        yield return AgentProviderEvent.ErrorEvent(err.GetString() ?? "Ollama error.");
                        yield break;
                    }

                    if (root.TryGetProperty("model", out var modelEl))
                    {
                        model = modelEl.GetString() ?? model;
                    }

                    if (root.TryGetProperty("message", out var message))
                    {
                        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        {
                            var text = content.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                yield return AgentProviderEvent.TextDelta(text);
                            }
                        }

                        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var call in calls.EnumerateArray())
                            {
                                var fn = call.TryGetProperty("function", out var f) ? f : call;
                                var name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                                var args = fn.TryGetProperty("arguments", out var a)
                                    ? a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText()
                                    : "{}";
                                var id = call.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                                var key = id + ":" + name + ":" + args;
                                if (yieldedTools.Add(key))
                                {
                                    yield return AgentProviderEvent.ToolCall(id, name, args);
                                }
                            }
                        }
                    }

                    if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                    {
                        break;
                    }
                }
            }

            yield return AgentProviderEvent.Completed(model);
        }
        finally
        {
            Cleanup(resp, stream, reader);
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
        var messages = request.Messages.Select(m =>
        {
            if (m.ToolCalls is { Count: > 0 })
            {
                return (object)new
                {
                    role = "assistant",
                    content = m.Content ?? "",
                    tool_calls = m.ToolCalls.Select(t => new
                    {
                        id = t.Id,
                        function = new { name = t.Name, arguments = t.ArgumentsJson }
                    }).ToArray()
                };
            }

            if (string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                return new { role = "tool", content = m.Content ?? "", name = m.Name };
            }

            return new { role = m.Role, content = m.Content ?? "" };
        }).ToList();

        object payload = request.Tools.Count == 0
            ? new { model = request.Model ?? _config.DefaultModel ?? "llama3.2", stream = true, messages }
            : new
            {
                model = request.Model ?? _config.DefaultModel ?? "llama3.2",
                stream = true,
                messages,
                tools = request.Tools.Select(t => new
                {
                    type = "function",
                    function = new
                    {
                        name = t.Name,
                        description = t.Description,
                        parameters = JsonSerializer.Deserialize<JsonElement>(t.JsonSchema.GetRawText())
                    }
                }).ToArray()
            };

        return JsonSerializer.Serialize(payload);
    }

    private IReadOnlyList<ModelRecord> DefaultModels()
    {
        var id = _config.DefaultModel ?? "llama3.2";
        return new[] { new ModelRecord { Id = id, DisplayName = id, ProviderId = Id } };
    }

    private static string Trim(string text) => text.Length <= 400 ? text : text[..400];

    private static void Cleanup(HttpResponseMessage? resp, Stream? stream, StreamReader? reader)
    {
        reader?.Dispose();
        stream?.Dispose();
        resp?.Dispose();
    }
}
