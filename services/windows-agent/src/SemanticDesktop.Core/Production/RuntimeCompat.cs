using System.Security.Cryptography;
using System.Text.Json;
using SemanticDesktop.Core.Serialization;

namespace SemanticDesktop.Core.Production;

public static class RuntimeCompat
{
    public const int SchemaVersion = 1;
    public const string ApiVersion = "1.12.0";
    public const string MinCompatibleApi = "1.0.0";
    /// <summary>Compatibility product id used by persisted state and existing clients.</summary>
    public const string Product = "SemanticDesktop";
    public const string ProductDisplayName = "DesktopUseAgent";
    public const string LegacyProduct = "SemanticDesktop";
}

public sealed class StateDocument
{
    public int SchemaVersion { get; set; }
    public string ApiVersion { get; set; } = RuntimeCompat.ApiVersion;
    public TelemetrySettings Telemetry { get; set; } = new();
    public int LogRetentionDays { get; set; } = 14;
    public string? InstallId { get; set; }
    public string? InstalledVersion { get; set; }
}

public sealed class TelemetrySettings
{
    public bool Enabled { get; set; }
    public string Level { get; set; } = "off";
    public long ToolCalls { get; set; }
    public long Failures { get; set; }
    public long VisionUses { get; set; }
    public long InputFallbacks { get; set; }
}

public sealed class UpdateManifest
{
    public int SchemaVersion { get; set; } = RuntimeCompat.SchemaVersion;
    public required string Version { get; set; }
    public string Channel { get; set; } = "stable";
    public string MinCompatibleApi { get; set; } = RuntimeCompat.MinCompatibleApi;
    public IReadOnlyList<ManifestFile> Files { get; set; } = Array.Empty<ManifestFile>();
}

public sealed class ManifestFile
{
    public required string Path { get; set; }
    public required string Sha256 { get; set; }
}

public sealed class CrashRecord
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public required string Type { get; init; }
    public required string Message { get; init; }
    public string? Stack { get; init; }
    public bool Recovered { get; init; }
}

public static class StateMigrator
{
    public static StateDocument Migrate(StateDocument? raw)
    {
        var state = raw ?? new StateDocument { SchemaVersion = 0 };
        if (state.SchemaVersion < 1)
        {
            state.InstallId ??= Guid.NewGuid().ToString("N");
            state.ApiVersion = RuntimeCompat.ApiVersion;
            state.Telemetry ??= new TelemetrySettings();
            state.Telemetry.Enabled = false;
            if (string.IsNullOrWhiteSpace(state.Telemetry.Level) ||
                state.Telemetry.Level.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                state.Telemetry.Level = "off";
            }

            if (state.LogRetentionDays <= 0)
            {
                state.LogRetentionDays = 14;
            }

            state.SchemaVersion = 1;
        }

        if (state.SchemaVersion > RuntimeCompat.SchemaVersion)
        {
            throw new InvalidOperationException(
                $"{SemanticDesktop.Core.Errors.ErrorCodes.IncompatibleSchema}: persisted schema {state.SchemaVersion} is newer than runtime {RuntimeCompat.SchemaVersion}.");
        }

        state.ApiVersion = RuntimeCompat.ApiVersion;
        return state;
    }

    public static bool IsCompatible(string version, string minCompatible)
    {
        if (!Version.TryParse(Normalize(version), out var v) ||
            !Version.TryParse(Normalize(minCompatible), out var min))
        {
            return false;
        }

        return v >= min;
    }

    public static bool IsNewer(string candidate, string current)
    {
        if (!Version.TryParse(Normalize(candidate), out var a) ||
            !Version.TryParse(Normalize(current), out var b))
        {
            return false;
        }

        return a > b;
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonDefaults.Options), JsonDefaults.Options)!;

    private static string Normalize(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 3 ? version : version + string.Concat(Enumerable.Repeat(".0", 3 - parts.Length));
    }
}
