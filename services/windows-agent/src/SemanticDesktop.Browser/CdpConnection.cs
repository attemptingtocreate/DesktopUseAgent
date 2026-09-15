using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SemanticDesktop.Core.Serialization;

namespace SemanticDesktop.Browser;

internal sealed class CdpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sendGate = new();
    private int _nextId;
    private Task? _receiveTask;

    public event Action<string, JsonElement?, string?>? EventReceived;

    public bool IsConnected => _socket.State == WebSocketState.Open;

    public async Task ConnectAsync(string webSocketUrl, CancellationToken cancellationToken)
    {
        await _socket.ConnectAsync(new Uri(webSocketUrl), cancellationToken).ConfigureAwait(false);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_lifetime.Token));
    }

    public async Task<JsonElement> SendAsync(
        string method,
        object? parameters = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (_socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("CDP websocket is not connected.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        using var doc = JsonSerializer.SerializeToDocument(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters ?? new Dictionary<string, object?>(),
            ["sessionId"] = sessionId
        }, JsonDefaults.Options);

        // Rebuild without null sessionId
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            if (parameters is null)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                JsonSerializer.Serialize(writer, parameters, JsonDefaults.Options);
            }

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                writer.WriteString("sessionId", sessionId);
            }

            writer.WriteEndObject();
        }

        var bytes = stream.ToArray();
        lock (_sendGate)
        {
            _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false).GetAwaiter().GetResult();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            return await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 256];
        var message = new MemoryStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
                {
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        if (root.TryGetProperty("error", out var error))
                        {
                            var messageText = error.TryGetProperty("message", out var m) ? m.GetString() : "CDP error";
                            tcs.TrySetException(new InvalidOperationException(messageText ?? "CDP error"));
                        }
                        else
                        {
                            var resultPayload = root.TryGetProperty("result", out var r)
                                ? r.Clone()
                                : default;
                            tcs.TrySetResult(resultPayload);
                        }
                    }

                    continue;
                }

                if (root.TryGetProperty("method", out var methodEl))
                {
                    var method = methodEl.GetString() ?? "";
                    JsonElement? paramsEl = root.TryGetProperty("params", out var p) ? p.Clone() : null;
                    string? sessionId = root.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String
                        ? s.GetString()
                        : null;
                    EventReceived?.Invoke(method, paramsEl, sessionId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch
        {
            // Connection dropped; fail pending callers.
            foreach (var kv in _pending)
            {
                kv.Value.TrySetException(new InvalidOperationException("CDP connection closed."));
            }

            _pending.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore close errors.
        }

        _socket.Dispose();
        _lifetime.Dispose();
        if (_receiveTask is not null)
        {
            try { await _receiveTask.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }
}
