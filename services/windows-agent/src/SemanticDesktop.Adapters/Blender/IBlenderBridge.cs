namespace SemanticDesktop.Adapters.Blender;

public interface IBlenderBridge : IDisposable
{
    bool IsListening { get; }

    int Port { get; }

    BlenderBridgeHealth GetHealth();

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task<BlenderCommandResult> ExecuteCommandAsync(
        string operation,
        Dictionary<string, object?>? parameters,
        string? sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
