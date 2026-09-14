namespace SemanticDesktop.Core.Models;

public sealed class Rect
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

public sealed class WindowInfo
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Process { get; init; }
    public int Pid { get; init; }
    public bool Foreground { get; init; }
    public bool Minimized { get; init; }
    public Rect? Bounds { get; init; }
}

public sealed class UIElementSummary
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? AutomationId { get; init; }
    public string? ControlType { get; init; }
    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
    public bool Enabled { get; init; }
    public bool Focused { get; init; }
    public bool Offscreen { get; init; }
    public Rect? Bounds { get; init; }
    public IReadOnlyList<string> SupportedPatterns { get; init; } = Array.Empty<string>();
    public double? Confidence { get; init; }
}

public sealed class UIElement
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? AutomationId { get; init; }
    public string? ControlType { get; init; }
    public string? ClassName { get; init; }
    public string? FrameworkId { get; init; }
    public int ProcessId { get; init; }
    public string? WindowId { get; init; }
    public bool Enabled { get; init; }
    public bool Focused { get; init; }
    public bool Offscreen { get; init; }
    public Rect? Bounds { get; init; }
    public IReadOnlyList<string> SupportedPatterns { get; init; } = Array.Empty<string>();
    public string? Value { get; init; }
    public string? Text { get; init; }
}

public sealed class UITreeNode
{
    public required UIElementSummary Element { get; init; }
    public IReadOnlyList<UITreeNode> Children { get; init; } = Array.Empty<UITreeNode>();
}

public sealed class UITree
{
    public required UITreeNode Root { get; init; }
    public int NodeCount { get; init; }
    public string TreeMode { get; init; } = "control";
}

public sealed class ProcessInfo
{
    public required string Id { get; init; }
    public int Pid { get; init; }
    public required string Name { get; init; }
    public string? Path { get; init; }
}
