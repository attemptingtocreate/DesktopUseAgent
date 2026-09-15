using CoreResolver = SemanticDesktop.Core.Production.DataRootResolver;

namespace SemanticDesktop.App.Persistence;

public static class DataRootResolver
{
    public const string DesktopUseAgentFolder = CoreResolver.ProductFolder;
    public const string SemanticDesktopFolder = CoreResolver.LegacyProductFolder;

    public static string Resolve()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var neu = Path.Combine(local, DesktopUseAgentFolder);
        var legacy = Path.Combine(local, SemanticDesktopFolder);
        return CoreResolver.ResolveDataRoot(
            Environment.GetEnvironmentVariable("DESKTOPUSEAGENT_DATA"),
            Environment.GetEnvironmentVariable("SEMANTIC_DESKTOP_DATA"),
            local,
            Directory.Exists(neu),
            Directory.Exists(legacy));
    }
}
