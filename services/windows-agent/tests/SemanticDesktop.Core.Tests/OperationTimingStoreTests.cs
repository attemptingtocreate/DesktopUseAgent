using SemanticDesktop.Core.Metrics;

namespace SemanticDesktop.Core.Tests;

public class OperationTimingStoreTests
{
    [Fact]
    public void Snapshot_ComputesPercentilesAndCounts()
    {
        var store = new OperationTimingStore();
        foreach (var ms in new[] { 10L, 20, 30, 40, 100 })
        {
            store.Record("window.list", ms, success: true);
        }

        store.Record("window.list", 500, success: false);

        var snapshot = store.Snapshot();
        Assert.True(snapshot.TryGetValue("window.list", out var stats));
        Assert.Equal(6, stats.Count);
        Assert.Equal(5, stats.SuccessCount);
        Assert.Equal(1, stats.FailureCount);
        Assert.Equal(30, stats.P50Ms);
        Assert.Equal(500, stats.P95Ms);
        Assert.Equal(500, stats.LastMs);
    }

    [Fact]
    public void Record_IsThreadSafe()
    {
        var store = new OperationTimingStore();
        Parallel.For(0, 200, i => store.Record("system.ping", i, success: i % 5 != 0));
        var snapshot = store.Snapshot();
        Assert.True(snapshot.TryGetValue("system.ping", out var stats));
        Assert.Equal(200, stats.Count);
    }
}
