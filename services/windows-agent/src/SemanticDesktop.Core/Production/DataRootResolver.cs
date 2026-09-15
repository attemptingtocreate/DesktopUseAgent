namespace SemanticDesktop.Core.Production;

/// <summary>
/// Resolves the on-disk data root. SemanticDesktop is the legacy folder name.
/// </summary>
public static class DataRootResolver
{
    public const string ProductFolder = "DesktopUseAgent";
    public const string LegacyProductFolder = "SemanticDesktop";

    public static string ResolveDataRoot(
        string? desktopUseAgentData,
        string? semanticDesktopData,
        string localAppData,
        bool newExists,
        bool legacyExists)
    {
        if (!string.IsNullOrWhiteSpace(desktopUseAgentData))
        {
            return desktopUseAgentData.Trim();
        }

        if (!string.IsNullOrWhiteSpace(semanticDesktopData))
        {
            return semanticDesktopData.Trim();
        }

        var neu = Path.Combine(localAppData, ProductFolder);
        // SemanticDesktop is the legacy root for existing installs; do not migrate files.
        var legacy = Path.Combine(localAppData, LegacyProductFolder);
        if (newExists)
        {
            return neu;
        }

        if (legacyExists)
        {
            return legacy;
        }

        return neu;
    }
}
