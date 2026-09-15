namespace SemanticDesktop.Core.Models;

public sealed class FileStatInfo
{
    public required string Path { get; init; }
    public string? Name { get; init; }
    public bool Exists { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsFile { get; init; }
    public long? Length { get; init; }
    public DateTimeOffset? LastWriteUtc { get; init; }
}

public sealed class DesktopEvent
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, object?>? Source { get; init; }
    public Dictionary<string, object?>? Data { get; init; }
}

public sealed class DesktopGraphDiff
{
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }
    public bool FocusChanged { get; init; }
    public string? PreviousFocus { get; init; }
    public string? CurrentFocus { get; init; }
    public IReadOnlyList<GraphWindow> AddedWindows { get; init; } = Array.Empty<GraphWindow>();
    public IReadOnlyList<GraphWindow> RemovedWindows { get; init; } = Array.Empty<GraphWindow>();
    public IReadOnlyList<GraphWindow> ChangedWindows { get; init; } = Array.Empty<GraphWindow>();
    public IReadOnlyList<string> AddedApplications { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RemovedApplications { get; init; } = Array.Empty<string>();
    public IReadOnlyList<GraphBrowserTab> AddedTabs { get; init; } = Array.Empty<GraphBrowserTab>();
    public IReadOnlyList<GraphBrowserTab> RemovedTabs { get; init; } = Array.Empty<GraphBrowserTab>();

    public bool IsEmpty =>
        !FocusChanged
        && AddedWindows.Count == 0
        && RemovedWindows.Count == 0
        && ChangedWindows.Count == 0
        && AddedApplications.Count == 0
        && RemovedApplications.Count == 0
        && AddedTabs.Count == 0
        && RemovedTabs.Count == 0;
}

public sealed class BatchCall
{
    public string? Id { get; init; }
    public required string Method { get; init; }
    public System.Text.Json.JsonElement? Params { get; init; }
}
