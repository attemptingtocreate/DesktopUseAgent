using SemanticDesktop.Agent;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.IPC;

namespace SemanticDesktop.Integration.Tests;

public class AgentDispatcherTests
{
    [Fact]
    public async Task Dispatcher_WindowList_ReturnsEnvelopeWithPerformance()
    {
        using var dispatcher = new CommandDispatcher();
        var result = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "req1",
            Method = CommandNames.WindowList,
            Params = null
        }, CancellationToken.None);

        var json = System.Text.Json.JsonSerializer.Serialize(result, Core.Serialization.JsonDefaults.Options);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("window.list", doc.RootElement.GetProperty("performance").GetProperty("operation").GetString());
        Assert.Equal("Win32", doc.RootElement.GetProperty("performance").GetProperty("provider").GetString());
    }
}
