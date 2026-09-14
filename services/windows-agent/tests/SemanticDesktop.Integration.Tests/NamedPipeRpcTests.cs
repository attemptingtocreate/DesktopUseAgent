using SemanticDesktop.Core.Commands;
using SemanticDesktop.IPC;

namespace SemanticDesktop.Integration.Tests;

public class NamedPipeRpcTests
{
    [Fact]
    public async Task Ping_RoundTrips()
    {
        var pipe = "sd-rpc-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServer(pipe, (request, _) =>
        {
            Assert.Equal(CommandNames.SystemPing, request.Method);
            return Task.FromResult<object>(new { ok = true, data = new { pong = true } });
        });
        server.Start();

        await using var client = new NamedPipeClient(pipe);
        await client.ConnectAsync(CancellationToken.None, 3000);
        var result = await client.SendAsync(CommandNames.SystemPing, new { }, CancellationToken.None);
        Assert.True(result.GetProperty("ok").GetBoolean());
    }
}
