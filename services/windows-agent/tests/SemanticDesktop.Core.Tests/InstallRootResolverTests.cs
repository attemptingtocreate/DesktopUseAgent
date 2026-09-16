using SemanticDesktop.Core.Production;

namespace SemanticDesktop.Core.Tests;

public class InstallRootResolverTests
{
    [Fact]
    public void ResolveInstallRoot_PrefersExplicitOverrideWithManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "dua-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, InstallRootResolver.ManifestFileName), "{}");
        try
        {
            var resolved = InstallRootResolver.ResolveInstallRoot(root, @"C:\Users\x\AppData\Local", null);
            Assert.Equal(root, resolved);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void FindManifestRoot_WalksUpFromAgentDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "dua-walk-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "current");
        var agent = Path.Combine(install, "agent");
        Directory.CreateDirectory(agent);
        File.WriteAllText(Path.Combine(install, InstallRootResolver.ManifestFileName), "{}");
        try
        {
            var found = InstallRootResolver.FindManifestRoot(agent);
            Assert.Equal(install, found);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ResolveInstallRoot_UsesDefaultCurrentFolder()
    {
        var local = Path.Combine(Path.GetTempPath(), "dua-local-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(local, DataRootResolver.ProductFolder, InstallRootResolver.InstallFolderName);
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, InstallRootResolver.ManifestFileName), "{}");
        try
        {
            var resolved = InstallRootResolver.ResolveInstallRoot(null, local, null);
            Assert.Equal(install, resolved);
        }
        finally
        {
            Directory.Delete(local, true);
        }
    }
}
