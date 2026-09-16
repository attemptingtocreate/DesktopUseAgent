namespace SemanticDesktop.Core.Production;

/// <summary>
/// Resolves the on-disk install layout root (directory containing install-manifest.json).
/// Distinct from <see cref="DataRootResolver"/> which resolves persisted state under LocalAppData.
/// </summary>
public static class InstallRootResolver
{
    public const string InstallFolderName = "current";
    public const string ManifestFileName = "install-manifest.json";

    public static string? ResolveInstallRoot(
        string? installOverride,
        string localAppData,
        string? agentBaseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(installOverride))
        {
            var trimmed = installOverride.Trim().TrimEnd('\\', '/');
            if (File.Exists(Path.Combine(trimmed, ManifestFileName)))
            {
                return trimmed;
            }

            if (Directory.Exists(trimmed))
            {
                return trimmed;
            }
        }

        var fromAgent = FindManifestRoot(agentBaseDirectory);
        if (fromAgent is not null)
        {
            return fromAgent;
        }

        var defaultInstall = Path.Combine(localAppData, DataRootResolver.ProductFolder, InstallFolderName);
        if (File.Exists(Path.Combine(defaultInstall, ManifestFileName)) || Directory.Exists(defaultInstall))
        {
            return defaultInstall;
        }

        return null;
    }

    public static string? FindManifestRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(startDirectory));
            for (var depth = 0; depth < 8 && dir is not null; depth++)
            {
                if (File.Exists(Path.Combine(dir.FullName, ManifestFileName)))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    public static string? ResolveManifestPath(string? installRoot) =>
        string.IsNullOrWhiteSpace(installRoot)
            ? null
            : Path.Combine(installRoot, ManifestFileName);
}
