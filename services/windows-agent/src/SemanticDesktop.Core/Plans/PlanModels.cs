using System.Text.Json;

namespace SemanticDesktop.Core.Plans;

public sealed class ExecutionPlan
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public List<PlanStep> Steps { get; set; } = new();
    public PlanOptions? Options { get; set; }
}

public sealed class PlanOptions
{
    public bool StopOnFailure { get; set; } = true;
    public int DefaultTimeoutMs { get; set; } = 30_000;
    public bool Optimize { get; set; } = true;
    public bool ParallelSafeReads { get; set; } = true;
}

public sealed class PlanStep
{
    public required string Id { get; set; }
    public required string Action { get; set; }
    public Dictionary<string, JsonElement>? Args { get; set; }
    public int? TimeoutMs { get; set; }
    public RetryOptions? Retries { get; set; }
    public Condition? When { get; set; }
    public Condition? WaitAfter { get; set; }
    public string? OnFailure { get; set; }
}

public sealed class RetryOptions
{
    public int Count { get; set; }
    public int? DelayMs { get; set; }
    public double? Backoff { get; set; }
}

public sealed class Condition
{
    public required string Type { get; set; }
    public string? Process { get; set; }
    public string? TitleContains { get; set; }
    public string? TitleRegex { get; set; }
    public string? WindowId { get; set; }
    public string? Path { get; set; }
    public string? Name { get; set; }
    public string? AutomationId { get; set; }
    public string? ControlType { get; set; }
    public string? Value { get; set; }
    public string? Property { get; set; }
    public int? DelayMs { get; set; }
    public int? TimeoutMs { get; set; }
    public Dictionary<string, JsonElement>? Selector { get; set; }
}

public sealed class PlanExecutionState
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public required string Status { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<StepExecutionResult> Steps { get; } = new();
    public Dictionary<string, JsonElement> Outputs { get; } = new(StringComparer.Ordinal);
    public ErrorInfoDto? Error { get; set; }
    public int FusedCount { get; set; }
    public int ParallelGroupCount { get; set; }
}

public sealed class StepExecutionResult
{
    public required string StepId { get; init; }
    public required string Action { get; init; }
    public required string Status { get; set; }
    public int Attempts { get; set; }
    public long DurationMs { get; set; }
    public JsonElement? Output { get; set; }
    public ErrorInfoDto? Error { get; set; }
}

public sealed class ErrorInfoDto
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public bool Retryable { get; init; }
}

public static class PlanStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Skipped = "skipped";
}
