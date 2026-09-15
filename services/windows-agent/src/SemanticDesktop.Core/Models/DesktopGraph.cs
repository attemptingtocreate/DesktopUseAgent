namespace SemanticDesktop.Core.Models;

public sealed class SemanticDesktopGraph
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? FocusedApplication { get; init; }
    public string? FocusedWindowId { get; init; }
    public IReadOnlyList<GraphApplication> Applications { get; init; } = Array.Empty<GraphApplication>();
    public IReadOnlyList<GraphWindow> Windows { get; init; } = Array.Empty<GraphWindow>();
    public IReadOnlyList<GraphBrowserTab> BrowserTabs { get; init; } = Array.Empty<GraphBrowserTab>();
    public IReadOnlyList<GraphControl> ImportantControls { get; init; } = Array.Empty<GraphControl>();
}

public sealed class GraphApplication
{
    public required string Name { get; init; }
    public string? Process { get; init; }
    public int? Pid { get; init; }
    public bool Focused { get; init; }
    public Dictionary<string, string?> State { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> WindowIds { get; init; } = Array.Empty<string>();
}

public sealed class GraphWindow
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Process { get; init; }
    public int Pid { get; init; }
    public bool Foreground { get; init; }
    public bool Minimized { get; init; }
}

public sealed class GraphBrowserTab
{
    public required string Id { get; init; }
    public string? Browser { get; init; }
    public string? Title { get; init; }
    public string? Url { get; init; }
    public bool Active { get; init; }
}

public sealed class GraphControl
{
    public required string Id { get; init; }
    public string? WindowId { get; init; }
    public string? Name { get; init; }
    public string? ControlType { get; init; }
    public string? AutomationId { get; init; }
    public bool Enabled { get; init; }
}
