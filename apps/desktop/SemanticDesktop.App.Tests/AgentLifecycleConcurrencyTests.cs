using SemanticDesktop.App.Lifecycle;
using SemanticDesktop.App.Persistence;

namespace SemanticDesktop.App.Tests;

public class AgentLifecycleConcurrencyTests
{
    [Fact]
    public async Task EnsureAgentAsync_ConcurrentCalls_StartOnce()
    {
        var gateway = new FakeAgentProcessGateway { PingsBeforeSuccess = 2 };
        var lifecycle = new AgentLifecycle(gateway, new AppSettingsStore(null));

        await Task.WhenAll(
            lifecycle.EnsureAgentAsync(),
            lifecycle.EnsureAgentAsync(),
            lifecycle.EnsureAgentAsync());

        Assert.Equal(1, gateway.StartCount);
        Assert.True(gateway.Running);
    }

    [Fact]
    public async Task EnsureAgentAsync_DoublePingBeforeStart_AvoidsDuplicateSpawn()
    {
        var gateway = new CountingPingGateway();
        var lifecycle = new AgentLifecycle(gateway, new AppSettingsStore(null));

        await lifecycle.EnsureAgentAsync();
        Assert.Equal(0, gateway.StartCount);
    }

    private sealed class CountingPingGateway : IAgentProcessGateway
    {
        public int PingCount { get; private set; }
        public int StartCount { get; private set; }

        public Task<bool> PingAsync(CancellationToken cancellationToken = default)
        {
            PingCount++;
            return Task.FromResult(PingCount >= 2);
        }

        public Task<int> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.FromResult(9001);
        }

        public Task StopAsync(int pid, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
