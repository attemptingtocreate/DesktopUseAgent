using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Production;

namespace SemanticDesktop.Core.Tests;

public class ProductionCompatTests
{
    [Fact]
    public void StateMigrator_V0ToV1_DisablesTelemetryAndAssignsInstallId()
    {
        var migrated = StateMigrator.Migrate(new StateDocument { SchemaVersion = 0 });
        Assert.Equal(1, migrated.SchemaVersion);
        Assert.Equal(RuntimeCompat.ApiVersion, migrated.ApiVersion);
        Assert.False(string.IsNullOrWhiteSpace(migrated.InstallId));
        Assert.False(migrated.Telemetry.Enabled);
        Assert.Equal("off", migrated.Telemetry.Level);
        Assert.Equal(14, migrated.LogRetentionDays);
    }

    [Fact]
    public void StateMigrator_NewerSchema_ThrowsIncompatible()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            StateMigrator.Migrate(new StateDocument { SchemaVersion = RuntimeCompat.SchemaVersion + 1 }));
        Assert.StartsWith(ErrorCodes.IncompatibleSchema, ex.Message);
    }

    [Fact]
    public void VersionHelpers_CompatibleAndNewer()
    {
        Assert.True(StateMigrator.IsCompatible("1.12.0", "1.0.0"));
        Assert.True(StateMigrator.IsCompatible("1.0.0", "1.0.0"));
        Assert.False(StateMigrator.IsCompatible("0.9.0", "1.0.0"));
        Assert.True(StateMigrator.IsNewer("1.13.0", "1.12.0"));
        Assert.False(StateMigrator.IsNewer("1.12.0", "1.12.0"));
        Assert.False(StateMigrator.IsNewer("1.11.0", "1.12.0"));
    }

    [Fact]
    public void RuntimeCompat_KeepsProductId_AndExposesDisplayName()
    {
        Assert.Equal("SemanticDesktop", RuntimeCompat.Product);
        Assert.Equal("SemanticDesktop", RuntimeCompat.LegacyProduct);
        Assert.Equal("DesktopUseAgent", RuntimeCompat.ProductDisplayName);
        Assert.Equal("1.12.0", RuntimeCompat.ApiVersion);
        Assert.Equal("0.1.0-preview.1", RuntimeCompat.ProductVersion);
        Assert.Equal(1, RuntimeCompat.SchemaVersion);
    }

    [Fact]
    public void DataRootResolver_PrefersDesktopUseAgentEnv()
    {
        var root = DataRootResolver.ResolveDataRoot(@"C:\dua-data", @"C:\sd-data", @"C:\Users\x\AppData\Local", true, true);
        Assert.Equal(@"C:\dua-data", root);
    }

    [Fact]
    public void DataRootResolver_FallsBackToSemanticDesktopEnv()
    {
        var root = DataRootResolver.ResolveDataRoot(null, @"C:\sd-data", @"C:\Users\x\AppData\Local", true, true);
        Assert.Equal(@"C:\sd-data", root);
    }

    [Fact]
    public void DataRootResolver_PrefersExistingNewFolder()
    {
        var local = @"C:\Users\x\AppData\Local";
        var root = DataRootResolver.ResolveDataRoot(null, null, local, newExists: true, legacyExists: true);
        Assert.Equal(Path.Combine(local, "DesktopUseAgent"), root);
    }

    [Fact]
    public void DataRootResolver_UsesLegacyFolderWhenNewMissing()
    {
        var local = @"C:\Users\x\AppData\Local";
        var root = DataRootResolver.ResolveDataRoot(null, null, local, newExists: false, legacyExists: true);
        Assert.Equal(Path.Combine(local, "SemanticDesktop"), root);
    }

    [Fact]
    public void DataRootResolver_NewInstallUsesDesktopUseAgent()
    {
        var local = @"C:\Users\x\AppData\Local";
        var root = DataRootResolver.ResolveDataRoot(null, " ", local, newExists: false, legacyExists: false);
        Assert.Equal(Path.Combine(local, "DesktopUseAgent"), root);
    }
}
