using System.Diagnostics;
using System.Text.Json;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.IPC;

namespace SemanticDesktop.Cli;

public sealed class PipeBenchmarkAgentClient : IBenchmarkAgentClient, IAsyncDisposable
{
    private readonly NamedPipeClient _client;

    public PipeBenchmarkAgentClient(string pipeName)
    {
        _client = new NamedPipeClient(pipeName);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(cancellationToken, timeoutMs: 10000).ConfigureAwait(false);
    }

    public async Task<BenchmarkCallResult> CallAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await _client.SendAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            var wallMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var ok = result.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            long? toolMs = null;
            if (result.TryGetProperty("performance", out var perf) &&
                perf.TryGetProperty("durationMs", out var durationEl) &&
                durationEl.TryGetInt64(out var duration))
            {
                toolMs = duration;
            }

            string? errorCode = null;
            string? errorMessage = null;
            if (!ok && result.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("code", out var codeEl))
                {
                    errorCode = codeEl.GetString();
                }

                if (error.TryGetProperty("message", out var messageEl))
                {
                    errorMessage = messageEl.GetString();
                }
            }

            return new BenchmarkCallResult
            {
                Ok = ok,
                WallMs = wallMs,
                ToolDurationMs = toolMs,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                Raw = result.Clone()
            };
        }
        catch (Exception ex)
        {
            return new BenchmarkCallResult
            {
                Ok = false,
                WallMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                ErrorCode = "internal",
                ErrorMessage = ex.Message,
                Raw = null
            };
        }
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
