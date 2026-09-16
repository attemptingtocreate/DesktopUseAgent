namespace SemanticDesktop.Adapters.Blender;

public static class BlenderExecutableCache
{
    private static readonly object Gate = new();
    private static string? _cachedPath;
    private static bool _searched;

    public static string? Get()
    {
        lock (Gate)
        {
            if (_searched)
            {
                return _cachedPath;
            }

            _cachedPath = Discover();
            _searched = true;
            return _cachedPath;
        }
    }

    public static void Invalidate()
    {
        lock (Gate)
        {
            _cachedPath = null;
            _searched = false;
        }
    }

    private static string? Discover()
    {
        var env = Environment.GetEnvironmentVariable("BLENDER_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        foreach (var name in new[] { "blender", "blender.exe" })
        {
            var onPath = FindOnPath(name);
            if (onPath is not null)
            {
                return onPath;
            }
        }

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var blenderRoot = Path.Combine(root, "Blender Foundation");
            if (!Directory.Exists(blenderRoot))
            {
                continue;
            }

            var hit = Directory.GetFiles(blenderRoot, "blender.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // ignore invalid PATH entries
            }
        }

        return null;
    }
}
