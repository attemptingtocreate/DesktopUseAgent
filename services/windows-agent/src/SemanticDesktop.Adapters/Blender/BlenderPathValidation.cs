using SemanticDesktop.Core.Errors;

namespace SemanticDesktop.Adapters.Blender;

internal static class BlenderPathValidation
{
    public static readonly HashSet<string> RenderOutputExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".exr", ".tiff", ".tif", ".tga", ".bmp"
    };

    public static HashSet<string> ExportExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".obj", ".fbx", ".gltf", ".glb", ".stl"
    };

    public static AdapterResult? ValidateAbsolutePath(string? path, string paramName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, $"{paramName} is required.");
        }

        if (!Path.IsPathRooted(path))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, $"{paramName} must be an absolute path.");
        }

        return null;
    }

    public static AdapterResult? ValidateOutputFile(
        string output,
        bool overwrite,
        HashSet<string>? allowedExtensions = null)
    {
        var abs = ValidateAbsolutePath(output, "output");
        if (abs is not null)
        {
            return abs;
        }

        var ext = Path.GetExtension(output);
        if (allowedExtensions is not null &&
            (string.IsNullOrWhiteSpace(ext) || !allowedExtensions.Contains(ext)))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, $"output extension '{ext}' is not supported.");
        }

        if (!overwrite && File.Exists(output))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "output file exists; set overwrite:true to replace.");
        }

        return null;
    }

    public static AdapterResult? ValidateBlendDestination(string destination, bool overwrite, bool requireExtension = true)
    {
        var abs = ValidateAbsolutePath(destination, "destination");
        if (abs is not null)
        {
            return abs;
        }

        if (requireExtension && !destination.EndsWith(".blend", StringComparison.OrdinalIgnoreCase))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "destination must be a .blend file path.");
        }

        if (!overwrite && File.Exists(destination))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "destination file exists; set overwrite:true to replace.");
        }

        return null;
    }

    public static AdapterResult? ValidateExportFormat(string format)
    {
        if (!BlenderMeshFormats.Allowlist.Contains(format))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, $"format '{format}' is not supported.");
        }

        return null;
    }

    public static void EnsureParentDirectory(string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }
}
