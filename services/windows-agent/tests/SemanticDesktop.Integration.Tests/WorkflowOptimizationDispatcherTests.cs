using System.Text.Json;
using SemanticDesktop.Agent;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.IPC;

namespace SemanticDesktop.Integration.Tests;

public class WorkflowOptimizationDispatcherTests
{
    [Fact]
    public async Task FilesystemInspect_ReturnsEntriesAndStats()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sd-inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var a = Path.Combine(dir, "a.txt");
        var b = Path.Combine(dir, "b.txt");
        File.WriteAllText(a, "one");
        File.WriteAllText(b, "two");
        try
        {
            using var dispatcher = new CommandDispatcher();
            var result = await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "insp1",
                Method = CommandNames.FilesystemInspect,
                Params = JsonSerializer.SerializeToElement(new { path = dir, paths = new[] { a, b } }, JsonDefaults.Options)
            }, CancellationToken.None);

            using var doc = Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            var data = doc.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("entries").GetArrayLength() >= 2);
            Assert.Equal(2, data.GetProperty("stats").GetArrayLength());
            Assert.True(data.GetProperty("stats")[0].GetProperty("exists").GetBoolean());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* cleanup */ }
        }
    }

    [Fact]
    public async Task DesktopBatch_FusesExistsAndReturnsResults()
    {
        var dir = Path.GetTempPath();
        using var dispatcher = new CommandDispatcher();
        var result = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "batch1",
            Method = CommandNames.DesktopBatch,
            Params = JsonSerializer.SerializeToElement(new
            {
                calls = new object[]
                {
                    new { id = "l", method = CommandNames.FilesystemList, @params = new { path = dir } },
                    new { id = "e1", method = CommandNames.FilesystemExists, @params = new { path = dir } },
                    new { id = "e2", method = CommandNames.FilesystemStat, @params = new { path = dir } }
                }
            }, JsonDefaults.Options)
        }, CancellationToken.None);

        using var doc = Parse(result);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var data = doc.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("fused").GetInt32() >= 1);
        Assert.True(data.GetProperty("results").GetArrayLength() >= 1);
        Assert.True(data.GetProperty("results")[0].GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task WindowList_SecondCall_IsCacheHit()
    {
        using var dispatcher = new CommandDispatcher();
        var first = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "w1",
            Method = CommandNames.WindowList,
            Params = null
        }, CancellationToken.None);
        var second = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "w2",
            Method = CommandNames.WindowList,
            Params = null
        }, CancellationToken.None);

        using var doc = Parse(second);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("performance").GetProperty("cacheHit").GetBoolean());
    }

    [Fact]
    public async Task Events_SubscribeWritePoll_SeesStateChanged()
    {
        using var dispatcher = new CommandDispatcher();
        var sub = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "sub1",
            Method = CommandNames.EventsSubscribe,
            Params = JsonSerializer.SerializeToElement(new { types = new[] { "state.changed" } }, JsonDefaults.Options)
        }, CancellationToken.None);
        using var subDoc = Parse(sub);
        var subscriptionId = subDoc.RootElement.GetProperty("data").GetProperty("subscriptionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(subscriptionId));

        var path = Path.Combine(Path.GetTempPath(), "sd-evt-" + Guid.NewGuid().ToString("N") + ".txt");
        await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "w",
            Method = CommandNames.FilesystemWriteText,
            Params = JsonSerializer.SerializeToElement(new { path, contents = "x" }, JsonDefaults.Options)
        }, CancellationToken.None);

        var poll = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "p",
            Method = CommandNames.EventsPoll,
            Params = JsonSerializer.SerializeToElement(new { subscriptionId, max = 10 }, JsonDefaults.Options)
        }, CancellationToken.None);
        using var pollDoc = Parse(poll);
        Assert.True(pollDoc.RootElement.GetProperty("ok").GetBoolean());
        var events = pollDoc.RootElement.GetProperty("data").GetProperty("events");
        Assert.True(events.GetArrayLength() >= 1);
        Assert.Equal("state.changed", events[0].GetProperty("type").GetString());
        try { File.Delete(path); } catch { /* cleanup */ }
    }

    [Fact]
    public async Task DesktopDiff_ReturnsDiffShape()
    {
        using var dispatcher = new CommandDispatcher();
        await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "g1",
            Method = CommandNames.DesktopGetGraph,
            Params = JsonSerializer.SerializeToElement(new { includeControls = false, forceRefresh = true }, JsonDefaults.Options)
        }, CancellationToken.None);
        var diff = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "d1",
            Method = CommandNames.DesktopDiff,
            Params = JsonSerializer.SerializeToElement(new { includeControls = false }, JsonDefaults.Options)
        }, CancellationToken.None);
        using var doc = Parse(diff);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.GetProperty("addedWindows").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("removedWindows").ValueKind);
    }

    private static JsonDocument Parse(object result)
    {
        var json = JsonSerializer.Serialize(result, JsonDefaults.Options);
        return JsonDocument.Parse(json);
    }
}
