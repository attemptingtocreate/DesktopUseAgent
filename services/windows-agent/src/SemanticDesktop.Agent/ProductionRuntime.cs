using System.Text.Json;
using SemanticDesktop.Audit;
using SemanticDesktop.Core.Production;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.Permissions;

namespace SemanticDesktop.Agent;

public sealed class ProductionRuntime
{
    public string? DataRoot { get; }
    public StateDocument State { get; }
    public PermissionEngine Engine { get; }
    public AuditLog Audit { get; }
    public CrashRecord? LastCrash { get; private set; }
    public bool PipeCurrentUserOnly => true;

    private ProductionRuntime(string? dataRoot, StateDocument state, PermissionEngine engine, AuditLog audit, CrashRecord? lastCrash)
    {
        DataRoot = dataRoot;
        State = state;
        Engine = engine;
        Audit = audit;
        LastCrash = lastCrash;
    }

    public static ProductionRuntime Create(string? dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return new ProductionRuntime(null, new StateDocument { SchemaVersion = RuntimeCompat.SchemaVersion, InstallId = "ephemeral" }, new PermissionEngine(), new AuditLog(), null);
        }

        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(Path.Combine(dataRoot, "logs"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "crashes"));
        var statePath = Path.Combine(dataRoot, "state.json");
        StateDocument? loaded = null;
        if (File.Exists(statePath))
        {
            loaded = JsonSerializer.Deserialize<StateDocument>(File.ReadAllText(statePath), JsonDefaults.Options);
        }

        var state = StateMigrator.Migrate(loaded);
        state.InstalledVersion ??= RuntimeCompat.ApiVersion;
        var engine = new PermissionEngine(PolicyPersistence.Load(Path.Combine(dataRoot, "policy.json")));
        PolicyPersistence.Save(Path.Combine(dataRoot, "policy.json"), engine.Policy);
        SaveState(statePath, state);
        var retention = TimeSpan.FromDays(Math.Clamp(state.LogRetentionDays, 1, 365));
        var audit = new AuditLog(5000, Path.Combine(dataRoot, "logs"), retention);
        var lastCrash = ReadLastCrash(Path.Combine(dataRoot, "crashes"));
        return new ProductionRuntime(dataRoot, state, engine, audit, lastCrash);
    }

    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable("SEMANTIC_DESKTOP_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SemanticDesktop");

    public void SavePolicy()
    {
        if (DataRoot is not null)
        {
            PolicyPersistence.Save(Path.Combine(DataRoot, "policy.json"), Engine.Policy);
        }
    }

    public void SaveState()
    {
        if (DataRoot is not null)
        {
            SaveState(Path.Combine(DataRoot, "state.json"), State);
        }
    }

    public void RecordCrash(Exception ex, bool recovered)
    {
        var record = new CrashRecord
        {
            Type = ex.GetType().FullName ?? "Exception",
            Message = ex.Message,
            Stack = ex.StackTrace,
            Recovered = recovered
        };
        LastCrash = record;
        if (DataRoot is null)
        {
            return;
        }

        try
        {
            var dir = Path.Combine(DataRoot, "crashes");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"crash-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(record, JsonDefaults.Options));
        }
        catch
        {
            // crash logging must not throw
        }
    }

    public void RecordTelemetry(bool success, string method)
    {
        if (!State.Telemetry.Enabled)
        {
            return;
        }

        State.Telemetry.ToolCalls++;
        if (!success)
        {
            State.Telemetry.Failures++;
        }

        if (method.StartsWith("vision.", StringComparison.OrdinalIgnoreCase))
        {
            State.Telemetry.VisionUses++;
        }

        if (method.StartsWith("input.", StringComparison.OrdinalIgnoreCase))
        {
            State.Telemetry.InputFallbacks++;
        }
    }

    public object SecurityReview(bool uiaAvailable)
    {
        return new
        {
            schemaVersion = RuntimeCompat.SchemaVersion,
            apiVersion = RuntimeCompat.ApiVersion,
            telemetryOptIn = State.Telemetry.Enabled,
            telemetryDefaultOff = !State.Telemetry.Enabled || State.Telemetry.Level == "off",
            pipeAclCurrentUserOnly = PipeCurrentUserOnly,
            unknownCommandsDenied = true,
            emergencyStopAvailable = true,
            policyPersisted = DataRoot is not null && File.Exists(Path.Combine(DataRoot, "policy.json")),
            schemaMigrated = State.SchemaVersion == RuntimeCompat.SchemaVersion,
            uiaAvailable,
            lastCrashRecovered = LastCrash?.Recovered,
            readyForRelease = !State.Telemetry.Enabled && PipeCurrentUserOnly && State.SchemaVersion == RuntimeCompat.SchemaVersion
        };
    }

    private static void SaveState(string path, StateDocument state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonDefaults.Options));
    }

    private static CrashRecord? ReadLastCrash(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var latest = Directory.EnumerateFiles(dir, "crash-*.json").OrderByDescending(f => f).FirstOrDefault();
        if (latest is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CrashRecord>(File.ReadAllText(latest), JsonDefaults.Options);
        }
        catch
        {
            return null;
        }
    }
}

public static class FaultInjector
{
    public static string? Method { get; set; }

    public static bool ShouldFail(string method)
    {
        if (string.IsNullOrWhiteSpace(Method))
        {
            return false;
        }

        var match = string.Equals(Method, method, StringComparison.OrdinalIgnoreCase);
        if (match)
        {
            Method = null;
        }

        return match;
    }
}
