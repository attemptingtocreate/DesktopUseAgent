namespace SemanticDesktop.Adapters.Blender;

/// <summary>Shared limits/allowlists for powered Blender mesh ops.</summary>
public static class BlenderMeshOpsContract
{
    public const int MaxExecutePythonBytes = 32 * 1024;

    public static readonly HashSet<string> BooleanOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "UNION", "DIFFERENCE", "INTERSECT"
    };

    public static readonly HashSet<string> MirrorAxes = new(StringComparer.OrdinalIgnoreCase)
    {
        "X", "Y", "Z"
    };

    public static readonly HashSet<string> GeometryModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "VERT", "EDGE", "FACE"
    };

    public static readonly HashSet<string> UvMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "ANGLE_BASED", "CONFORMAL", "SMART"
    };

    public static bool TryValidateExecutePython(string? source, bool confirm, out string? error)
    {
        error = null;
        if (!confirm)
        {
            error = "execute_python requires confirm=true when using the live bridge / powered path.";
            return false;
        }

        if (string.IsNullOrEmpty(source))
        {
            error = "source is required.";
            return false;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(source) > MaxExecutePythonBytes)
        {
            error = $"source exceeds max of {MaxExecutePythonBytes} bytes.";
            return false;
        }

        return true;
    }
}
