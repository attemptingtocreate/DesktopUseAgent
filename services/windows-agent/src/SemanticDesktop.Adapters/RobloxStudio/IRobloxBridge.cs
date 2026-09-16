namespace SemanticDesktop.Adapters.RobloxStudio;

public interface IRobloxBridge : IDisposable
{
    bool IsListening { get; }

    int Port { get; }

    RobloxBridgeHealth GetHealth();

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task<RobloxCommandResult> ExecuteCommandAsync(
        string operation,
        Dictionary<string, object?>? parameters,
        string? sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
