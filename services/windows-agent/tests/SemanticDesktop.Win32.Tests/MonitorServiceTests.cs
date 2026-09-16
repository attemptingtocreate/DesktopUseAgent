using SemanticDesktop.Win32.Monitors;

namespace SemanticDesktop.Win32.Tests;

public class MonitorServiceTests
{
    [Fact]
    public void List_ReturnsAtLeastOneMonitorWithValidBounds()
    {
        var service = new MonitorService();
        var monitors = service.List(forceRefresh: true);

        Assert.NotEmpty(monitors);
        var primary = monitors.FirstOrDefault(m => m.Primary);
        Assert.NotNull(primary);
        Assert.True(primary!.Bounds.Width > 0);
        Assert.True(primary.Bounds.Height > 0);
        Assert.True(primary.WorkArea.Width > 0);
        Assert.True(primary.WorkArea.Height > 0);
        Assert.True(primary.Scale > 0);
        Assert.Equal(0, monitors[0].Index);
    }

    [Fact]
    public void List_UsesShortTtlSnapshot_NotProcessLifetimeCache()
    {
        var service = new MonitorService(TimeSpan.FromMilliseconds(25));
        var first = service.List(forceRefresh: true);
        var cached = service.List();
        Assert.Same(first, cached);

        Thread.Sleep(40);
        var refreshed = service.List();
        Assert.NotSame(first, refreshed);
        Assert.Equal(first.Count, refreshed.Count);
    }

    [Fact]
    public void GetMonitorIndexForPoint_UsesSameSnapshotHandlesAsList()
    {
        var service = new MonitorService();
        var monitors = service.List(forceRefresh: true);
        var primary = monitors.First(m => m.Primary);
        var centerX = (int)(primary.Bounds.X + primary.Bounds.Width / 2);
        var centerY = (int)(primary.Bounds.Y + primary.Bounds.Height / 2);

        var index = service.GetMonitorIndexForPoint(centerX, centerY, forceRefresh: true);
        Assert.NotNull(index);
        Assert.Equal(primary.Index, index.Value);

        var cachedIndex = service.GetMonitorIndexForPoint(centerX, centerY);
        Assert.Equal(index, cachedIndex);
    }
}
