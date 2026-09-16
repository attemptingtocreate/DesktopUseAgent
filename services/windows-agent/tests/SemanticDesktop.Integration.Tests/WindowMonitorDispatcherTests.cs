using System.Text.Json;
using SemanticDesktop.Agent;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.IPC;

namespace SemanticDesktop.Integration.Tests;

public class WindowMonitorDispatcherTests
{
    [Fact]
    public async Task MonitorList_ReturnsMonitorsWithBounds()
    {
        using var dispatcher = new CommandDispatcher();
        var result = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "mon-list",
            Method = CommandNames.MonitorList,
            Params = null
        }, CancellationToken.None);

        using var doc = Parse(result);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var monitors = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        Assert.NotEmpty(monitors);
        Assert.True(monitors[0].GetProperty("bounds").GetProperty("width").GetDouble() > 0);
    }

    [Fact]
    public async Task WindowGet_ReturnsExtendedWindowInfo()
    {
        using var dispatcher = new CommandDispatcher();
        var list = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "win-list",
            Method = CommandNames.WindowList,
            Params = null
        }, CancellationToken.None);

        using var listDoc = Parse(list);
        var first = listDoc.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var windowId = first.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(windowId));

        var get = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "win-get",
            Method = CommandNames.WindowGet,
            Params = JsonSerializer.SerializeToElement(new { windowId }, JsonDefaults.Options)
        }, CancellationToken.None);

        using var getDoc = Parse(get);
        Assert.True(getDoc.RootElement.GetProperty("ok").GetBoolean());
        var data = getDoc.RootElement.GetProperty("data");
        Assert.Equal(windowId, data.GetProperty("id").GetString());
        Assert.True(data.TryGetProperty("showState", out _));
        Assert.True(data.TryGetProperty("maximized", out _));
        Assert.True(data.TryGetProperty("restoreBounds", out var restoreBounds));
        Assert.Equal(JsonValueKind.Object, restoreBounds.ValueKind);
        Assert.True(restoreBounds.GetProperty("width").GetDouble() > 0);
    }

    [Fact]
    public async Task WindowWaitFor_MatchesTitleRegex()
    {
        using var dispatcher = new CommandDispatcher();
        var list = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "win-list",
            Method = CommandNames.WindowList,
            Params = null
        }, CancellationToken.None);

        using var listDoc = Parse(list);
        var title = listDoc.RootElement.GetProperty("data").EnumerateArray()
            .Select(w => w.GetProperty("title").GetString() ?? "")
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var pattern = ".*";
        var wait = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "win-wait-regex",
            Method = CommandNames.WindowWaitFor,
            Params = JsonSerializer.SerializeToElement(new { titleRegex = pattern, timeoutMs = 500 }, JsonDefaults.Options)
        }, CancellationToken.None);

        using var waitDoc = Parse(wait);
        Assert.True(waitDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Object, waitDoc.RootElement.GetProperty("data").GetProperty("window").ValueKind);
    }

    private static JsonDocument Parse(object result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result, JsonDefaults.Options));
}
