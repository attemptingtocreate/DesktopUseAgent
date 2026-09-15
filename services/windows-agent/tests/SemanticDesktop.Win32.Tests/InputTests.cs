using SemanticDesktop.Win32.Input;

namespace SemanticDesktop.Win32.Tests;

public class ScreenCoordinatesTests
{
    [Fact]
    public void ToAbsoluteNormalized_Maps_Corners_Of_Virtual_Screen()
    {
        var bounds = new VirtualScreenBounds(-1920, 0, 3840, 1080);
        var (nx0, ny0) = ScreenCoordinates.ToAbsoluteNormalized(-1920, 0, bounds);
        var (nx1, ny1) = ScreenCoordinates.ToAbsoluteNormalized(1919, 1079, bounds);
        Assert.Equal(0, nx0);
        Assert.Equal(0, ny0);
        Assert.Equal(65535, nx1);
        Assert.Equal(65535, ny1);
    }

    [Fact]
    public void FromLogical_Applies_Dpi_Scale()
    {
        var p = ScreenCoordinates.FromLogical(100, 200, 1.5);
        Assert.Equal(150, p.X);
        Assert.Equal(300, p.Y);
    }

    [Fact]
    public void Contains_Respects_MultiMonitor_Origin()
    {
        var bounds = new VirtualScreenBounds(-100, -50, 200, 100);
        Assert.True(ScreenCoordinates.Contains(bounds, -100, -50));
        Assert.False(ScreenCoordinates.Contains(bounds, 100, 50));
    }
}

public class InputServiceValidationTests
{
    [Fact]
    public void Key_Rejects_Empty()
    {
        var input = new InputService();
        var ex = Assert.Throws<ArgumentException>(() => input.Key(""));
        Assert.Contains("key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hotkey_Rejects_Empty()
    {
        var input = new InputService();
        Assert.Throws<ArgumentException>(() => input.Hotkey(Array.Empty<string>()));
    }

    [Fact]
    public void TypeText_Rejects_Null()
    {
        var input = new InputService();
        Assert.Throws<ArgumentException>(() => input.TypeText(null!));
    }

    [Fact]
    public void MouseMove_Emits_Fallback_Reason_Metric()
    {
        // Move to current virtual origin — still exercises SendInput path.
        var screen = ScreenCoordinates.GetVirtualScreen();
        var input = new InputService();
        var result = input.MouseMove(screen.Left + 1, screen.Top + 1, fallbackReason: "uia_unavailable");
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.Contains("uia_unavailable", json, StringComparison.Ordinal);
        Assert.Contains("SendInput", json, StringComparison.Ordinal);
        Assert.Contains("fallback_reason", json, StringComparison.Ordinal);
    }
}
