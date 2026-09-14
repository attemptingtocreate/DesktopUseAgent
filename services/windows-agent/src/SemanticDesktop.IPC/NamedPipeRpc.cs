using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SemanticDesktop.Core.Serialization;

namespace SemanticDesktop.IPC;

public static class PipeNames
{
    public const string Default = "semantic-desktop-agent";
}

public sealed class RpcRequest
{
    public required string Id { get; init; }
    public required string Method { get; init; }
    public JsonElement? Params { get; init; }
}

public sealed class NamedPipeServer : IAsyncDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly string _pipeName;
    private readonly Func<RpcRequest, CancellationToken, Task<object>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private NamedPipeServerStream? _listeningPipe;
    private Task? _listenTask;

    public NamedPipeServer(string pipeName, Func<RpcRequest, CancellationToken, Task<object>> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
    }

    public void Start() => _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                lock (_gate)
                {
                    _listeningPipe = pipe;
                }

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                lock (_gate)
                {
                    if (ReferenceEquals(_listeningPipe, pipe))
                    {
                        _listeningPipe = null;
                    }
                }

                await HandleClientAsync(pipe, cancellationToken).ConfigureAwait(false);
                pipe = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                pipe?.Dispose();
                try
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            using (pipe)
            {
                var buffer = new byte[1024 * 64];
                while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
                {
                    var line = await ReadLineAsync(pipe, buffer, cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    object result;
                    string requestId = "unknown";
                    try
                    {
                        var request = JsonSerializer.Deserialize<RpcRequest>(line, JsonDefaults.Options)
                                      ?? throw new InvalidOperationException("Invalid request.");
                        requestId = request.Id;
                        result = await _handler(request, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        result = new
                        {
                            ok = false,
                            error = new { code = "INTERNAL", message = ex.Message, retryable = false },
                            meta = new
                            {
                                requestId,
                                startedAt = DateTimeOffset.UtcNow,
                                completedAt = DateTimeOffset.UtcNow,
                                durationMs = 0
                            }
                        };
                    }

                    var json = JsonSerializer.Serialize(result, JsonDefaults.Options) + "\n";
                    var bytes = Utf8NoBom.GetBytes(json);
                    await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // client disconnected
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var ms = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return ms.Length == 0 ? null : Utf8NoBom.GetString(ms.ToArray());
            }

            if (buffer[0] == (byte)'\n')
            {
                break;
            }

            if (buffer[0] != (byte)'\r')
            {
                ms.WriteByte(buffer[0]);
            }
        }

        return Utf8NoBom.GetString(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        NamedPipeServerStream? listening;
        lock (_gate)
        {
            listening = _listeningPipe;
            _listeningPipe = null;
        }

        listening?.Dispose();
        try
        {
            await using var nudge = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAny(nudge.ConnectAsync(200), Task.Delay(250)).ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        if (_listenTask is not null)
        {
            await Task.WhenAny(_listenTask, Task.Delay(500)).ConfigureAwait(false);
        }

        _cts.Dispose();
    }
}

public sealed class NamedPipeClient : IAsyncDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;

    public NamedPipeClient(string pipeName = PipeNames.Default)
    {
        _pipeName = pipeName;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken, int timeoutMs = 5000)
    {
        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await _pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out connecting to pipe '{_pipeName}'.");
        }
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        if (_pipe is null || !_pipe.IsConnected)
        {
            throw new InvalidOperationException("Client is not connected.");
        }

        var request = new RpcRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            Method = method,
            Params = parameters is null
                ? null
                : JsonSerializer.SerializeToElement(parameters, JsonDefaults.Options)
        };

        var payload = JsonSerializer.Serialize(request, JsonDefaults.Options) + "\n";
        var requestBytes = Utf8NoBom.GetBytes(payload);
        await _pipe.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);
        await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(10000);
        var line = await ReadLineAsync(_pipe, timeoutCts.Token).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("No response from agent.");
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.Clone();
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var ms = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return ms.Length == 0 ? null : Utf8NoBom.GetString(ms.ToArray());
            }

            if (buffer[0] == (byte)'\n')
            {
                break;
            }

            if (buffer[0] != (byte)'\r')
            {
                ms.WriteByte(buffer[0]);
            }
        }

        return Utf8NoBom.GetString(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }
    }
}
