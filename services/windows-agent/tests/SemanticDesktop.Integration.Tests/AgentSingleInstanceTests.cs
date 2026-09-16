using SemanticDesktop.Agent;

namespace SemanticDesktop.Integration.Tests;

public class AgentSingleInstanceTests
{
    [Fact]
    public void Acquire_SecondInstanceReportsAlreadyRunning()
    {
        var pipe = "test-pipe-" + Guid.NewGuid().ToString("N");
        var mutexName = AgentSingleInstance.BuildMutexName(pipe);
        using var blocker = new Mutex(initiallyOwned: true, mutexName, out _);
        var second = AgentSingleInstance.Acquire(pipe);
        Assert.Equal(AgentSingleInstanceAcquireStatus.AlreadyRunning, second.Status);
        Assert.Null(second.Instance);
        Assert.Contains("mutex", second.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Acquire_FirstInstanceSucceedsWhenMutexFree()
    {
        var pipe = "test-pipe-" + Guid.NewGuid().ToString("N");
        var first = AgentSingleInstance.Acquire(pipe);
        Assert.Equal(AgentSingleInstanceAcquireStatus.Acquired, first.Status);
        Assert.NotNull(first.Instance);
        first.Instance!.Dispose();
    }

    [Fact]
    public void BuildMutexName_IsDeterministicForSamePipe()
    {
        var a = AgentSingleInstance.BuildMutexName("semantic-desktop-agent");
        var b = AgentSingleInstance.BuildMutexName("semantic-desktop-agent");
        Assert.Equal(a, b);
        Assert.StartsWith(@"Local\DesktopUseAgent-", a);
        Assert.True(a.Length <= 260);
    }

    [Fact]
    public void BuildMutexName_DiffersForDifferentPipes()
    {
        var a = AgentSingleInstance.BuildMutexName("pipe-a");
        var b = AgentSingleInstance.BuildMutexName("pipe-b");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BuildMutexName_DoesNotEmbedRawPipeName()
    {
        var name = AgentSingleInstance.BuildMutexName(@"unsafe\pipe/name*");
        Assert.DoesNotContain(@"unsafe\pipe", name);
        Assert.DoesNotContain("*", name);
    }
}
