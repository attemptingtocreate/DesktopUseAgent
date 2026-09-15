using SemanticDesktop.Core.Models;

namespace SemanticDesktop.Adapters;

public interface IApplicationAdapter
{
    string Id { get; }

    bool CanHandle(ProcessInfo process);

    Task<ApplicationCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken);

    Task<AdapterResult> ExecuteAsync(AdapterCommand command, CancellationToken cancellationToken);
}

public sealed class AdapterCommand
{
    public required string Action { get; init; }
    public Dictionary<string, object?>? Params { get; init; }
    public string? ProcessId { get; init; }
    public string? WindowId { get; init; }
}

public sealed class AdapterResult
{
    public bool Ok { get; init; }
    public object? Data { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }

    public static AdapterResult Success(object? data = null) => new() { Ok = true, Data = data };

    public static AdapterResult Fail(string code, string message) => new()
    {
        Ok = false,
        ErrorCode = code,
        Message = message
    };
}

public sealed class ApplicationCapabilities
{
    public required string AdapterId { get; init; }
    public required IReadOnlyList<string> Actions { get; init; }
    public bool Available { get; init; }
    public Dictionary<string, object?>? Meta { get; init; }
}
