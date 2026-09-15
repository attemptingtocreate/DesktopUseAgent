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
}
