namespace SemanticDesktop.Adapters.RobloxStudio;

public sealed class RobloxBridgeOptions
{
    public int Port { get; init; } = ResolvePort();
    public string Host { get; init; } = "127.0.0.1";
    public required string Token { get; init; }
    public int MaxRequestBodyBytes { get; init; } = 256 * 1024;
    public int MaxResponseBytes { get; init; } = 1024 * 1024;
    public TimeSpan CommandTtl { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromSeconds(25);
    public TimeSpan SessionHeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan SessionCleanupInterval { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxConcurrentRequests { get; init; } = 32;

    public static int ResolvePort()
    {
        var raw = Environment.GetEnvironmentVariable("ROBLOX_BRIDGE_PORT");
        if (int.TryParse(raw, out var port) && port is > 1024 and <= 65535)
        {
            return port;
        }

        return 18374;
    }
}

public sealed class RobloxBridgeHealth
{
    public bool Listening { get; init; }
    public bool PluginConnected { get; init; }
    public int Port { get; init; }
    public string? StartError { get; init; }
    public IReadOnlyList<RobloxBridgeSessionInfo> Sessions { get; init; } = Array.Empty<RobloxBridgeSessionInfo>();
}

public sealed class RobloxBridgeSessionInfo
{
    public required string SessionId { get; init; }
    public string? StudioVersion { get; init; }
    public string? PlaceName { get; init; }
    public bool PollingEnabled { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public bool HasOutstandingCommand { get; init; }
}

public sealed class RobloxCommandResult
{
    public bool Ok { get; init; }
    public object? Data { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }

    public static RobloxCommandResult Success(object? data = null) => new() { Ok = true, Data = data };

    public static RobloxCommandResult Fail(string code, string message) => new()
    {
        Ok = false,
        ErrorCode = code,
        Message = message
    };
}

internal sealed class RobloxBridgeCommand
{
    public required string Id { get; init; }
    public required string Operation { get; init; }
    public Dictionary<string, object?>? Params { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed class RobloxBridgeSession
{
    public required string SessionId { get; init; }
    public string? StudioVersion { get; set; }
    public string? PlaceName { get; set; }
    public bool PollingEnabled { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public RobloxBridgeCommand? OutstandingCommand { get; set; }
    public TaskCompletionSource<RobloxCommandResult>? PendingResult { get; set; }
    public readonly object Gate = new();
}

public static class RobloxBridgeOperations
{
    public const string GetHierarchy = "get_hierarchy";
    public const string GetSelection = "get_selection";
    public const string Select = "select";
    public const string SetProperty = "set_property";
    public const string Ping = "ping";

    public static readonly HashSet<string> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        GetHierarchy,
        GetSelection,
        Select,
        SetProperty,
        Ping
    };
}
