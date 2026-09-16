namespace SemanticDesktop.Core.Metrics;

public sealed class OperationTimingStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OperationStats> _operations = new(StringComparer.Ordinal);
    private const int MaxSamplesPerOperation = 256;

    public void Record(string operation, long durationMs, bool success)
    {
        if (string.IsNullOrWhiteSpace(operation))
        {
            return;
        }

        lock (_gate)
        {
            if (!_operations.TryGetValue(operation, out var stats))
            {
                stats = new OperationStats();
                _operations[operation] = stats;
            }

            stats.Record(durationMs, success, MaxSamplesPerOperation);
        }
    }

    public IReadOnlyDictionary<string, OperationTimingSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return _operations.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToSnapshot(),
                StringComparer.Ordinal);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _operations.Clear();
        }
    }
}

public sealed class OperationTimingSnapshot
{
    public required int Count { get; init; }
    public required int SuccessCount { get; init; }
    public required int FailureCount { get; init; }
    public required long P50Ms { get; init; }
    public required long P95Ms { get; init; }
    public required long LastMs { get; init; }
}

internal sealed class OperationStats
{
    private readonly List<long> _durations = new();
    private long _lastMs;
    private int _successCount;
    private int _failureCount;

    public void Record(long durationMs, bool success, int maxSamples)
    {
        _lastMs = durationMs;
        if (success)
        {
            _successCount++;
        }
        else
        {
            _failureCount++;
        }

        _durations.Add(durationMs);
        if (_durations.Count > maxSamples)
        {
            _durations.RemoveAt(0);
        }
    }

    public OperationTimingSnapshot ToSnapshot()
    {
        var sorted = _durations.OrderBy(static d => d).ToArray();
        return new OperationTimingSnapshot
        {
            Count = _successCount + _failureCount,
            SuccessCount = _successCount,
            FailureCount = _failureCount,
            P50Ms = Percentile(sorted, 0.50),
            P95Ms = Percentile(sorted, 0.95),
            LastMs = _lastMs
        };
    }

    private static long Percentile(long[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        index = Math.Clamp(index, 0, sorted.Length - 1);
        return sorted[index];
    }
}
