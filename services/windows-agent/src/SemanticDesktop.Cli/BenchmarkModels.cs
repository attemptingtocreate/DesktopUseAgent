using System.Text.Json.Serialization;

namespace SemanticDesktop.Cli;

public sealed record BenchmarkOptions
{
    public bool DryRun { get; init; }
    public bool Live { get; init; }
    public int Iterations { get; init; } = 1;
    public int Monitor { get; init; } = 0;
    public string Url { get; init; } = "about:blank";
}

public sealed class BenchmarkSummary
{
    public required DateTimeOffset CapturedAt { get; init; }
    public required string Mode { get; init; }
    public required BenchmarkParameters Parameters { get; init; }
    public required IReadOnlyList<BenchmarkScenarioResult> Scenarios { get; init; }
}

public sealed class BenchmarkParameters
{
    public int Iterations { get; init; }
    public int Monitor { get; init; }
    public required string Url { get; init; }
}

public sealed class BenchmarkScenarioResult
{
    public required string Name { get; init; }
    public required string Method { get; init; }
    public required string Status { get; init; }
    public string? SkipReason { get; init; }
    public bool? Ok { get; init; }
    public int Iterations { get; init; }
    public IReadOnlyList<long>? WallMs { get; init; }
    public IReadOnlyList<long>? ToolDurationMs { get; init; }
    public long? P50Ms { get; init; }
    public long? P95Ms { get; init; }
}

public sealed class BenchmarkCallResult
{
    public required bool Ok { get; init; }
    public required long WallMs { get; init; }
    public long? ToolDurationMs { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public System.Text.Json.JsonElement? Raw { get; init; }
}

public interface IBenchmarkAgentClient
{
    Task<BenchmarkCallResult> CallAsync(string method, object? parameters, CancellationToken cancellationToken);
}

public sealed class NoopBenchmarkAgentClient : IBenchmarkAgentClient
{
    public Task<BenchmarkCallResult> CallAsync(string method, object? parameters, CancellationToken cancellationToken) =>
        Task.FromResult(new BenchmarkCallResult
        {
            Ok = true,
            WallMs = 0,
            ToolDurationMs = 0,
            Raw = null
        });
}
