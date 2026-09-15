using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SemanticDesktop.App.Models;
using SemanticDesktop.App.Persistence;

namespace SemanticDesktop.App.Providers;

public sealed class OpenAiCompatibleProvider : IAgentProvider, IDisposable
{
    public const string DefaultBaseUrl = "https://api.openai.com";

    private readonly ProviderConfig _config;
    private readonly CredentialStore _credentials;
    private readonly HttpClient _http;
    private CancellationTokenSource? _cancel;

    public OpenAiCompatibleProvider(ProviderConfig config, CredentialStore credentials, HttpMessageHandler? handler = null)
    {
        _config = config;
        _credentials = credentials;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(4);
        Id = config.Id;
        Type = string.IsNullOrWhiteSpace(config.Type) ? "openai" : config.Type;
        DisplayName = string.IsNullOrWhiteSpace(config.DisplayName) ? "OpenAI" : config.DisplayName;
    }

    public string Id { get; }
    public string Type { get; }
    public string DisplayName { get; }

    public AgentProviderCapabilities Capabilities { get; } = new(
        Streaming: true,
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

    public async Task<IReadOnlyList<ModelRecord>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, Combine("models"));
            using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return DefaultModels();
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return DefaultModels();
            }

            return data.EnumerateArray()
                .Select(e => e.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => new ModelRecord { Id = id!, DisplayName = id!, ProviderId = Id })
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
        var failed = false;
        string? failMessage = null;
        try
        {
            var body = BuildBody(request);
            using var req = CreateRequest(HttpMethod.Post, Combine("chat/completions"));
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                failed = true;
                failMessage = $"OpenAI HTTP {(int)resp.StatusCode}: {Trim(err)}";
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
            failed = true;
            failMessage = ex.Message;
        }

        if (failed)
        {
            yield return AgentProviderEvent.ErrorEvent(failMessage ?? "OpenAI request failed.");
            Cleanup(resp, stream, reader);
            yield break;
        }

        var toolAccum = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
        string? model = request.Model ?? _config.DefaultModel;
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

                if (line.Length == 0 || line.StartsWith(':'))
                {
                    continue;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var payload = line[5..].Trim();
                if (payload == "[DONE]")
                {
                    break;
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(payload);
                }
                catch
                {
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var error))
                    {
                        yield return AgentProviderEvent.ErrorEvent(error.ToString());
                        yield break;
                    }

                    if (root.TryGetProperty("model", out var modelEl))
                    {
                        model = modelEl.GetString() ?? model;
                    }

                    if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        {
                            var text = content.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                yield return AgentProviderEvent.TextDelta(text);
                            }
                        }

                        AccumulateToolCalls(delta, toolAccum);
                    }
                }
            }

            foreach (var tool in toolAccum.Values)
            {
                yield return AgentProviderEvent.ToolCall(tool.Id, tool.Name, tool.Args.ToString());
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

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        var key = _credentials.Get(CredentialStore.ProviderApiKey(Id));
        if (!string.IsNullOrWhiteSpace(key))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return req;
    }

    private string Combine(string relative)
    {
        var baseUrl = BaseUrl;
        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return $"{baseUrl}/{relative}";
        }

        return $"{baseUrl}/v1/{relative}";
    }

    private string BuildBody(AgentRequest request)
    {
        var messages = new List<object>();
        foreach (var msg in request.Messages)
        {
            if (msg.ToolCalls is { Count: > 0 })
            {
                messages.Add(new
                {
                    role = "assistant",
                    content = msg.Content,
                    tool_calls = msg.ToolCalls.Select(t => new
                    {
                        id = t.Id,
                        type = "function",
                        function = new { name = t.Name, arguments = t.ArgumentsJson }
                    }).ToArray()
                });
                continue;
            }

            if (string.Equals(msg.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(new
                {
                    role = "tool",
                    tool_call_id = msg.ToolCallId,
                    content = msg.Content ?? ""
                });
                continue;
            }

            messages.Add(new { role = msg.Role, content = msg.Content ?? "" });
        }

        object payload = request.Tools.Count == 0
            ? new
            {
                model = request.Model ?? _config.DefaultModel ?? "gpt-4o",
                stream = true,
                messages
            }
            : new
            {
                model = request.Model ?? _config.DefaultModel ?? "gpt-4o",
                stream = true,
                messages,
                tools = request.Tools.Select(ToOpenAiTool).ToArray()
            };

        return JsonSerializer.Serialize(payload);
    }

    private static object ToOpenAiTool(AgentToolDefinition tool) => new
    {
        type = "function",
        function = new
        {
            name = tool.Name,
            description = tool.Description,
            parameters = JsonSerializer.Deserialize<JsonElement>(tool.JsonSchema.GetRawText())
        }
    };

    private static void AccumulateToolCalls(
        JsonElement delta,
        Dictionary<int, (string Id, string Name, StringBuilder Args)> toolAccum)
    {
        if (!delta.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var call in calls.EnumerateArray())
        {
            var index = call.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i) ? i : toolAccum.Count;
            if (!toolAccum.TryGetValue(index, out var acc))
            {
                acc = ("", "", new StringBuilder());
            }

            if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                acc.Id = id.GetString() ?? acc.Id;
            }

            if (call.TryGetProperty("function", out var fn))
            {
                if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    acc.Name = name.GetString() ?? acc.Name;
                }

                if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                {
                    acc.Args.Append(args.GetString());
                }
            }

            if (string.IsNullOrEmpty(acc.Id))
            {
                acc.Id = "call_" + index;
            }

            toolAccum[index] = acc;
        }
    }

    private IReadOnlyList<ModelRecord> DefaultModels()
    {
        var id = _config.DefaultModel ?? "gpt-4o";
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
