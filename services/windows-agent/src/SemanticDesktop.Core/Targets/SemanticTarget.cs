namespace SemanticDesktop.Core.Targets;

public sealed class SemanticTarget
{
    public string? WindowId { get; init; }
    public string? RuntimeId { get; init; }
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ControlType { get; init; }
    public string? ClassName { get; init; }
    public int? ProcessId { get; init; }
    public string? FrameworkId { get; init; }
    public SemanticTarget? Ancestor { get; init; }
    public int? Index { get; init; }
}

public sealed class ElementHandle
{
    public required string Id { get; init; }
}
