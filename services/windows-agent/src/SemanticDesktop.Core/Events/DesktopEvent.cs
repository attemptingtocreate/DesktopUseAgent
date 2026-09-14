namespace SemanticDesktop.Core.Events;

public sealed class DesktopEvent
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public DesktopEventSource? Source { get; init; }
    public Dictionary<string, object?>? Data { get; init; }
}

public sealed class DesktopEventSource
{
    public string? WindowId { get; init; }
    public string? ElementId { get; init; }
}

public static class DesktopEventTypes
{
    public const string WindowOpened = "window.opened";
    public const string WindowClosed = "window.closed";
    public const string FocusChanged = "focus.changed";
    public const string UiChanged = "ui.changed";
    public const string UiPropertyChanged = "ui.property_changed";
}
