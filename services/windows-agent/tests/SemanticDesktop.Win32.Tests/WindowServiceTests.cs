using SemanticDesktop.Core.Handles;
using SemanticDesktop.Win32.Windows;

namespace SemanticDesktop.Win32.Tests;

public class WindowServiceTests
{
    [Fact]
    public async Task GetAsync_IncludesRestoreBounds()
    {
        var handles = new HandleRegistry();
        var windows = new WindowService(handles);
        var listed = await windows.ListAsync(CancellationToken.None);
        var target = listed.FirstOrDefault();
        if (target is null)
        {
            return;
        }

        var window = await windows.GetAsync(target.Id, CancellationToken.None);
        Assert.NotNull(window);
        Assert.NotNull(window!.RestoreBounds);
        Assert.True(window.RestoreBounds!.Width > 0);
        Assert.True(window.RestoreBounds.Height > 0);
    }
}
