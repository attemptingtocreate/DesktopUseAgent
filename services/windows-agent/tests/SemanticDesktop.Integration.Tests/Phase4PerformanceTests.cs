using System.Text.Json;
using SemanticDesktop.Agent;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.IPC;

namespace SemanticDesktop.Integration.Tests;

public class Phase4PerformanceTests
{
    [Fact]
    public async Task SystemPerformance_RecordsRecentOperations()
    {
        using var dispatcher = new CommandDispatcher();
        _ = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "perf-ping",
            Method = CommandNames.SystemPing,
            Params = null
        }, CancellationToken.None);

        var result = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "perf-get",
            Method = CommandNames.SystemPerformance,
            Params = null
        }, CancellationToken.None);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, JsonDefaults.Options));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var operations = doc.RootElement.GetProperty("data").GetProperty("operations");
        Assert.True(operations.TryGetProperty(CommandNames.SystemPing, out var pingStats));
        Assert.True(pingStats.GetProperty("count").GetInt32() >= 1);
        Assert.False(doc.RootElement.GetProperty("performance").GetProperty("cacheHit").GetBoolean());

        var perfCountBefore = pingStats.GetProperty("count").GetInt32();
        _ = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "perf-get-2",
            Method = CommandNames.SystemPerformance,
            Params = null
        }, CancellationToken.None);
        var perfAgain = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "perf-get-3",
            Method = CommandNames.SystemPerformance,
            Params = null
        }, CancellationToken.None);
        using var doc2 = JsonDocument.Parse(JsonSerializer.Serialize(perfAgain, JsonDefaults.Options));
        var pingAfter = doc2.RootElement.GetProperty("data").GetProperty("operations").GetProperty(CommandNames.SystemPing);
        Assert.Equal(perfCountBefore, pingAfter.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task DesktopGetGraph_CompletesWithoutBlockingOnBrowserDiscovery()
    {
        using var dispatcher = new CommandDispatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var started = DateTimeOffset.UtcNow;
        var result = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "graph-perf",
            Method = CommandNames.DesktopGetGraph,
            Params = JsonSerializer.SerializeToElement(new { forceRefresh = true }, JsonDefaults.Options)
        }, cts.Token);

        var elapsed = DateTimeOffset.UtcNow - started;
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, JsonDefaults.Options));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(elapsed < TimeSpan.FromSeconds(4), $"Graph took too long: {elapsed.TotalMilliseconds}ms");
    }
}
