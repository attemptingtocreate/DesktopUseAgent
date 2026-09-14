using SemanticDesktop.Core.Handles;
using SemanticDesktop.UIA.Automation;
using SemanticDesktop.Win32.Windows;

namespace SemanticDesktop.UIA.Tests;

public class UIAutomationServiceSmokeTests
{
    [Fact]
    public async Task GetWindows_ReturnsTopLevelWindows()
    {
        var handles = new HandleRegistry();
        var windows = new WindowService(handles);
        using var uia = new UIAutomationService(handles, windows);
        var list = await uia.GetWindowsAsync(CancellationToken.None);
        Assert.NotNull(list);
        Assert.All(list, w => Assert.StartsWith("win_", w.Id));
    }
}
