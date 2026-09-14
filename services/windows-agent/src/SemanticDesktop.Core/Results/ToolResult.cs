namespace SemanticDesktop.Core.Results;

public sealed class ToolResult<T>
{
    public bool Ok { get; init; }
    public T? Data { get; init; }
    public ErrorInfo? Error { get; init; }
    public required ResultMeta Meta { get; init; }
    public bool? StateChanged { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
    public PerformanceMeta? Performance { get; init; }

    public static ToolResult<T> Success(
        T data,
        ResultMeta meta,
        PerformanceMeta? performance = null,
        bool? stateChanged = null,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Ok = true,
            Data = data,
            Meta = meta,
            Performance = performance,
            StateChanged = stateChanged,
            Warnings = warnings
        };

    public static ToolResult<T> Failure(
        ErrorInfo error,
        ResultMeta meta,
        PerformanceMeta? performance = null,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Ok = false,
            Error = error,
            Meta = meta,
            Performance = performance,
            Warnings = warnings
        };
}

public sealed class ErrorInfo
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public bool Retryable { get; init; }
    public Dictionary<string, object?>? Details { get; init; }
}

public sealed class ResultMeta
{
    public required string RequestId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required long DurationMs { get; init; }

    public static ResultMeta Create(string requestId, DateTimeOffset startedAt)
    {
        var completed = DateTimeOffset.UtcNow;
        return new ResultMeta
        {
            RequestId = requestId,
            StartedAt = startedAt,
            CompletedAt = completed,
            DurationMs = (long)(completed - startedAt).TotalMilliseconds
        };
    }
}

public sealed class PerformanceMeta
{
    public required string Operation { get; init; }
    public required long DurationMs { get; init; }
    public int ElementsInspected { get; init; }
    public bool? CacheHit { get; init; }
    public required string Provider { get; init; }

    public override string ToString() =>
        $"{Operation}\n{DurationMs} ms\n{ElementsInspected} nodes inspected\ncache hit: {(CacheHit.HasValue ? CacheHit.Value.ToString().ToLowerInvariant() : "n/a")}\nprovider: {Provider}";
}

public sealed class ActionResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public Dictionary<string, object?>? Details { get; init; }
}
