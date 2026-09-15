using System.Text.Json;
using SemanticDesktop.Agent;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.IPC;

namespace SemanticDesktop.Integration.Tests;

public class DesktopGraphDispatcherTests
{
    [Fact]
    public async Task DesktopDescribe_ReturnsCompactTextAndGraph()
    {
        using var dispatcher = new CommandDispatcher();
        var result = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "desc1",
            Method = CommandNames.DesktopDescribe,
            Params = JsonSerializer.SerializeToElement(new { includeControls = false }, JsonDefaults.Options)
        }, CancellationToken.None);

        using var doc = Parse(result);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var data = doc.RootElement.GetProperty("data");
        var text = data.GetProperty("text").GetString();
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.StartsWith("Desktop", text);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("graph").GetProperty("windows").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("graph").GetProperty("applications").ValueKind);
        Assert.True(data.GetProperty("graph").GetProperty("windows").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task DesktopGetGraph_ReturnsApplicationsWindowsAndTabs()
    {
        using var dispatcher = new CommandDispatcher();
        var result = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "graph1",
            Method = CommandNames.DesktopGetGraph,
            Params = JsonSerializer.SerializeToElement(new { includeControls = false, forceRefresh = true }, JsonDefaults.Options)
        }, CancellationToken.None);

        using var doc = Parse(result);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.GetProperty("applications").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("windows").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("browserTabs").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("importantControls").ValueKind);
        Assert.True(data.GetProperty("windows").GetArrayLength() >= 1);
        Assert.True(data.GetProperty("applications").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task DesktopGetCapabilities_IncludesDescribeAndGetGraph()
    {
        using var dispatcher = new CommandDispatcher();
        var result = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "cap-graph",
            Method = CommandNames.DesktopGetCapabilities,
            Params = null
        }, CancellationToken.None);

        using var doc = Parse(result);
        var tools = doc.RootElement.GetProperty("data").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetString())
            .ToHashSet();
        Assert.Contains(CommandNames.DesktopDescribe, tools);
        Assert.Contains(CommandNames.DesktopGetGraph, tools);
    }

    private static JsonDocument Parse(object result)
    {
        var json = JsonSerializer.Serialize(result, JsonDefaults.Options);
        return JsonDocument.Parse(json);
    }
}
