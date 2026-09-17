using Microsoft.Win32;
using SemanticDesktop.Win32.Shell;

namespace SemanticDesktop.Win32.Apps;

/// <summary>Resolve a display name / exe stem to an executable path (Start Menu, App Paths, fuzzy scan).</summary>
public static class AppExecutableCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var key = name.Trim();
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var resolved = Discover(key);
            Cache[key] = resolved;
            return resolved;
        }
    }

    public static void Invalidate()
    {
        lock (Gate)
        {
            Cache.Clear();
        }

        AppsFolderCatalog.Invalidate();
    }

    /// <summary>Test helper: resolve without caching against an explicit Start Menu root.</summary>
    public static string? ResolveFromStartMenuRoot(string name, string startMenuProgramsRoot)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(startMenuProgramsRoot))
        {
            return null;
        }

        return FindStartMenuExact(name.Trim(), new[] { startMenuProgramsRoot });
    }

    private static string? Discover(string name)
    {
        if (LooksLikePath(name) && File.Exists(name))
        {
            return Path.GetFullPath(name);
        }

        var withExe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        if (File.Exists(withExe))
        {
            return Path.GetFullPath(withExe);
        }

        var startMenu = FindStartMenuExact(name, EnumerateStartMenuProgramRoots());
        if (startMenu is not null)
        {
            return startMenu;
        }

        var appPaths = FindAppPaths(name);
        if (appPaths is not null)
        {
            return appPaths;
        }

        var fuzzy = FuzzyFindInKnownRoots(name);
        if (fuzzy is not null)
        {
            return fuzzy;
        }

        return null;
    }

    private static bool LooksLikePath(string name) =>
        name.Contains(Path.DirectorySeparatorChar) ||
        name.Contains(Path.AltDirectorySeparatorChar) ||
        (name.Length >= 2 && name[1] == ':');

    private static IEnumerable<string> EnumerateStartMenuProgramRoots()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs")
        };

        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            yield return root;
            var programs = Path.Combine(root, "Programs");
            if (Directory.Exists(programs))
            {
                yield return programs;
            }
        }
    }

    private static string? FindStartMenuExact(string name, IEnumerable<string> roots)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        string? best = null;
        var bestScore = int.MinValue;

        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> lnks;
            try
            {
                lnks = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var lnk in lnks)
            {
                var fileStem = Path.GetFileNameWithoutExtension(lnk);
                if (!string.Equals(fileStem, stem, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(fileStem, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = ShortcutTargetResolver.Resolve(lnk);
                if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
                {
                    continue;
                }

                var score = string.Equals(fileStem, stem, StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = target;
                }
            }
        }

        return best;
    }

    private static string? FindAppPaths(string name)
    {
        var candidates = new[]
        {
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe",
            Path.GetFileNameWithoutExtension(name) + ".exe"
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var exeName in candidates)
            {
                try
                {
                    using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
                    var value = key?.GetValue(null) as string
                                ?? key?.GetValue("Path") as string;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                    if (File.Exists(expanded))
                    {
                        return Path.GetFullPath(expanded);
                    }

                    // Some App Paths store a directory in Path and default value is the exe name.
                    var dir = key?.GetValue("Path") as string;
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        var combined = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')), exeName);
                        if (File.Exists(combined))
                        {
                            return Path.GetFullPath(combined);
                        }
                    }
                }
                catch
                {
                    // ignore registry access failures
                }
            }
        }

        return null;
    }

    private static string? FuzzyFindInKnownRoots(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var roots = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            roots.Add(local);
        }

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(pf))
        {
            roots.Add(pf);
        }

        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pf86))
        {
            roots.Add(pf86);
        }

        string? best = null;
        var bestWrite = DateTime.MinValue;

        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    if (!Path.GetFileName(dir).Contains(stem, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var exe in SafeEnumerateExes(dir, maxDepth: 3))
                    {
                        var exeStem = Path.GetFileNameWithoutExtension(exe);
                        if (!exeStem.Contains(stem, StringComparison.OrdinalIgnoreCase) &&
                            !stem.Contains(exeStem, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        try
                        {
                            var write = File.GetLastWriteTimeUtc(exe);
                            if (best is null || write > bestWrite)
                            {
                                best = exe;
                                bestWrite = write;
                            }
                        }
                        catch
                        {
                            best ??= exe;
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        return best;
    }

    private static IEnumerable<string> SafeEnumerateExes(string root, int maxDepth)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (path, depth) = stack.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(path, "*.exe");
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(path))
                {
                    stack.Push((dir, depth + 1));
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
