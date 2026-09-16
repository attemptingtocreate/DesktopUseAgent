namespace SemanticDesktop.Adapters.RobloxStudio;

/// <summary>
/// Shared create-class allowlist for Studio graph edits (C# + plugin must stay in sync).
/// </summary>
public static class RobloxInstanceContract
{
    public const int MaxScriptSourceBytes = 256 * 1024;
    public const int MaxBatchOperations = 32;
    public const int DefaultFindMaxResults = 50;
    public const int MaxFindResults = 200;

    public static readonly HashSet<string> CreatableClasses = new(StringComparer.Ordinal)
    {
        "Part", "MeshPart", "SpawnLocation", "Model", "Folder", "Configuration",
        "Script", "LocalScript", "ModuleScript",
        "StringValue", "IntValue", "NumberValue", "BoolValue", "ObjectValue",
        "RemoteEvent", "RemoteFunction", "BindableEvent", "BindableFunction",
        "ScreenGui", "Frame", "TextLabel", "TextButton", "TextBox",
        "ImageLabel", "ImageButton", "ScrollingFrame",
        "UIListLayout", "UIPadding", "UICorner", "UIStroke",
        "BillboardGui", "SurfaceGui",
        "Attachment", "WeldConstraint", "ProximityPrompt", "ClickDetector",
        "Sound", "Animation", "Humanoid", "Accoutrement", "Tool", "Seat", "VehicleSeat"
    };

    public static readonly HashSet<string> ScriptClasses = new(StringComparer.Ordinal)
    {
        "Script", "LocalScript", "ModuleScript"
    };

    public static readonly HashSet<string> ForbiddenParentServices = new(StringComparer.Ordinal)
    {
        "CoreGui", "RobloxPluginGuiService", "PluginGuiService", "CorePackages"
    };

    public static bool IsCreatable(string? className) =>
        !string.IsNullOrWhiteSpace(className) && CreatableClasses.Contains(className);

    public static bool IsScriptClass(string? className) =>
        !string.IsNullOrWhiteSpace(className) && ScriptClasses.Contains(className);
}
