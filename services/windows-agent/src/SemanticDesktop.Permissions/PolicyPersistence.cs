using System.Text.Json;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.Core.Security;

namespace SemanticDesktop.Permissions;

public static class PolicyPersistence
{
    public static PermissionPolicy Load(string path)
    {
        if (!File.Exists(path))
        {
            return PermissionPolicy.CreateDefault();
        }

        var json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<PermissionPolicyDto>(json, JsonDefaults.Options);
        return PermissionPolicy.Migrate(loaded?.ToPolicy());
    }

    public static void Save(string path, PermissionPolicy policy)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var dto = PermissionPolicyDto.FromPolicy(policy);
        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonDefaults.Options));
    }
}

public sealed class PermissionPolicyDto
{
    public int SchemaVersion { get; set; }
    public Dictionary<string, PermissionDecisionKind> CapabilityDefaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PathRule> PathRules { get; set; } = new();
    public List<AppPermissionRule> AppRules { get; set; } = new();

    public PermissionPolicy ToPolicy()
    {
        var policy = new PermissionPolicy { SchemaVersion = SchemaVersion };
        foreach (var kv in CapabilityDefaults)
        {
            policy.CapabilityDefaults[kv.Key] = kv.Value;
        }

        policy.PathRules.AddRange(PathRules);
        policy.AppRules.AddRange(AppRules);
        return policy;
    }

    public static PermissionPolicyDto FromPolicy(PermissionPolicy policy) => new()
    {
        SchemaVersion = policy.SchemaVersion,
        CapabilityDefaults = new Dictionary<string, PermissionDecisionKind>(policy.CapabilityDefaults, StringComparer.OrdinalIgnoreCase),
        PathRules = policy.PathRules.ToList(),
        AppRules = policy.AppRules.ToList()
    };
}
