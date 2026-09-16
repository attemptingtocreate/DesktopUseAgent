namespace SemanticDesktop.Adapters.RobloxStudio;

public interface IRobloxPlaceLauncher
{
    Task<AdapterResult> LaunchPlaceAsync(Dictionary<string, object?>? parameters, CancellationToken cancellationToken);
}
