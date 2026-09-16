namespace SemanticDesktop.Adapters.RobloxStudio;

/// <summary>
/// Gates for high-power Roblox bridge operations (Luau exec, publish).
/// </summary>
public static class RobloxPoweredOpsContract
{
    public const int MaxExecuteLuauBytes = 16 * 1024;
    public const long MaxAssetId = 9_007_199_254_740_991; // JS-safe integer upper bound

    public static readonly HashSet<string> TerrainMaterials = new(StringComparer.OrdinalIgnoreCase)
    {
        "Air", "Asphalt", "Basalt", "Brick", "Cobblestone", "Concrete", "CrackedLava",
        "Glacier", "Grass", "Ground", "Ice", "LeafyGrass", "Limestone", "Mud",
        "Pavement", "Rock", "Salt", "Sand", "Sandstone", "Slate", "Snow", "WoodPlanks"
    };

    public static bool TryValidateAssetId(object? raw, out long assetId, out string? error)
    {
        assetId = 0;
        error = null;
        if (raw is null)
        {
            error = "assetId is required.";
            return false;
        }

        long value;
        switch (raw)
        {
            case long l:
                value = l;
                break;
            case int i:
                value = i;
                break;
            case double d when d is >= 1 and <= MaxAssetId && Math.Abs(d - Math.Round(d)) < 0.0001:
                value = (long)Math.Round(d);
                break;
            case string s when long.TryParse(s, out var parsed):
                value = parsed;
                break;
            case System.Text.Json.JsonElement el when el.ValueKind == System.Text.Json.JsonValueKind.Number && el.TryGetInt64(out var n):
                value = n;
                break;
            case System.Text.Json.JsonElement el when el.ValueKind == System.Text.Json.JsonValueKind.String && long.TryParse(el.GetString(), out var ns):
                value = ns;
                break;
            default:
                error = "assetId must be a positive integer.";
                return false;
        }

        if (value < 1 || value > MaxAssetId)
        {
            error = "assetId out of range.";
            return false;
        }

        assetId = value;
        return true;
    }

    public static bool TryValidateExecuteLuau(string? source, bool confirm, out string? error)
    {
        error = null;
        if (!confirm)
        {
            error = "execute_luau requires confirm=true (gated edge-case op).";
            return false;
        }

        if (string.IsNullOrEmpty(source))
        {
            error = "source is required.";
            return false;
        }

        var bytes = System.Text.Encoding.UTF8.GetByteCount(source);
        if (bytes > MaxExecuteLuauBytes)
        {
            error = $"source exceeds max of {MaxExecuteLuauBytes} bytes.";
            return false;
        }

        return true;
    }

    public static bool IsAllowedTerrainMaterial(string? material) =>
        !string.IsNullOrWhiteSpace(material) && TerrainMaterials.Contains(material);
}
