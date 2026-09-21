namespace SemanticDesktop.Adapters.Blender;

public sealed class BlenderBridgeOptions
{
    public int Port { get; init; } = ResolvePort();
    public string Host { get; init; } = "127.0.0.1";
    public required string Token { get; init; }
    public int MaxRequestBodyBytes { get; init; } = 256 * 1024;
    public int MaxResponseBytes { get; init; } = 1024 * 1024;
    public TimeSpan CommandTtl { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromSeconds(25);
    public TimeSpan SessionHeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan SessionCleanupInterval { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxConcurrentRequests { get; init; } = 32;

    public static int ResolvePort()
    {
        var raw = Environment.GetEnvironmentVariable("BLENDER_BRIDGE_PORT");
        if (int.TryParse(raw, out var port) && port is > 1024 and <= 65535)
        {
            return port;
        }

        return 18375;
    }
}

public sealed class BlenderBridgeHealth
{
    public bool Listening { get; init; }
    public bool AddonConnected { get; init; }
    public int Port { get; init; }
    public string? StartError { get; init; }
    public IReadOnlyList<BlenderBridgeSessionInfo> Sessions { get; init; } = Array.Empty<BlenderBridgeSessionInfo>();
}

public sealed class BlenderBridgeSessionInfo
{
    public required string SessionId { get; init; }
    public string? BlenderVersion { get; init; }
    public string? FileName { get; init; }
    public bool PollingEnabled { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public bool HasOutstandingCommand { get; init; }
}

public sealed class BlenderCommandResult
{
    public bool Ok { get; init; }
    public object? Data { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }

    public static BlenderCommandResult Success(object? data = null) => new() { Ok = true, Data = data };

    public static BlenderCommandResult Fail(string code, string message) => new()
    {
        Ok = false,
        ErrorCode = code,
        Message = message
    };
}

internal sealed class BlenderBridgeCommand
{
    public required string Id { get; init; }
    public required string Operation { get; init; }
    public Dictionary<string, object?>? Params { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed class BlenderBridgeSession
{
    public required string SessionId { get; init; }
    public string? BlenderVersion { get; set; }
    public string? FileName { get; set; }
    public bool PollingEnabled { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public BlenderBridgeCommand? OutstandingCommand { get; set; }
    public TaskCompletionSource<BlenderCommandResult>? PendingResult { get; set; }
    public readonly object Gate = new();
}

public static class BlenderBridgeOperations
{
    public const string Ping = "ping";
    public const string GetScene = "get_scene";
    public const string GetObjects = "get_objects";
    public const string SelectObject = "select_object";
    public const string Export = "export";
    public const string Save = "save";
    public const string Render = "render";
    public const string ImportMesh = "import_mesh";
    public const string CreateMesh = "create_mesh";
    public const string ExportForRoblox = "export_for_roblox";
    public const string ApplyTransform = "apply_transform";
    public const string Join = "join";
    public const string MeshExtrude = "mesh_extrude";
    public const string MeshInset = "mesh_inset";
    public const string MeshBevel = "mesh_bevel";
    public const string MeshLoopCut = "mesh_loop_cut";
    public const string ModifierBoolean = "modifier_boolean";
    public const string ModifierMirror = "modifier_mirror";
    public const string ModifierArray = "modifier_array";
    public const string MaterialSet = "material_set";
    public const string UvUnwrap = "uv_unwrap";
    public const string SelectGeometry = "select_geometry";
    public const string ExecutePython = "execute_python";
    public const string AnimationApply = "animation_apply";
    public const string AnimationInspect = "animation_inspect";
    public const string AnimationPreview = "animation_preview";
    public const string AssetValidate = "asset_validate";

    public static readonly HashSet<string> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        Ping,
        GetScene,
        GetObjects,
        SelectObject,
        Export,
        ExportForRoblox,
        Save,
        Render,
        ImportMesh,
        CreateMesh,
        ApplyTransform,
        Join,
        MeshExtrude,
        MeshInset,
        MeshBevel,
        MeshLoopCut,
        ModifierBoolean,
        ModifierMirror,
        ModifierArray,
        MaterialSet,
        UvUnwrap,
        SelectGeometry,
        AnimationApply,
        AnimationInspect,
        AnimationPreview,
        AssetValidate,
        ExecutePython
    };

    public static readonly HashSet<string> BatchAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        GetScene,
        GetObjects,
        SelectObject,
        Export,
        ExportForRoblox,
        Save,
        Render,
        ImportMesh,
        CreateMesh,
        ApplyTransform,
        Join,
        MeshExtrude,
        MeshInset,
        MeshBevel,
        MeshLoopCut,
        ModifierBoolean,
        ModifierMirror,
        ModifierArray,
        MaterialSet,
        UvUnwrap,
        SelectGeometry
        // execute_python excluded from batch
    };
}

public static class BlenderRenderEngines
{
    public static readonly HashSet<string> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "CYCLES",
        "BLENDER_EEVEE",
        "BLENDER_EEVEE_NEXT",
        "BLENDER_WORKBENCH"
    };
}

public static class BlenderMeshFormats
{
    public static readonly HashSet<string> Allowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj",
        "fbx",
        "gltf",
        "glb",
        "stl"
    };
}
