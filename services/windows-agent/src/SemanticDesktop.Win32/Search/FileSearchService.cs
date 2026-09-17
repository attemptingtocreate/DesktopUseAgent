using System.Collections.Concurrent;
using System.Diagnostics;

namespace SemanticDesktop.Win32.Search;

public sealed class FileSearchRequest
{
    public required string Query { get; init; }
    public string[]? Roots { get; init; }
    public int MaxResults { get; init; } = 25;
    public string[]? Extensions { get; init; }
}

public sealed class FileSearchHit
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? Modified { get; init; }
}

public static class FileSearchService
{
    public static object Search(FileSearchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new ArgumentException("query is required.", nameof(request));
        }

        var started = Stopwatch.StartNew();
        var max = request.MaxResults <= 0 ? 25 : Math.Min(request.MaxResults, 500);
        var query = request.Query.Trim();
        var extensions = NormalizeExtensions(request.Extensions);
        var roots = ResolveRoots(request.Roots);

        var filterRoots = request.Roots is { Length: > 0 } ? roots : null;
        var everything = TryEverything(query, filterRoots, extensions, max, cancellationToken);
        if (everything is not null)
        {
            return new
            {
                results = everything,
                count = everything.Count,
                durationMs = started.ElapsedMilliseconds,
                provider = "everything"
            };
        }

        var bag = new ConcurrentBag<FileSearchHit>();
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2)
        };

        Parallel.ForEach(roots, parallelOptions, root =>
        {
            if (!Directory.Exists(root) || bag.Count >= max)
            {
                return;
            }

            EnumerateMatches(root, query, extensions, max, bag, cancellationToken);
        });

        var results = bag
            .GroupBy(h => h.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();

        return new
        {
            results,
            count = results.Count,
            durationMs = started.ElapsedMilliseconds,
            provider = "filesystem"
        };
    }

    private static List<string> ResolveRoots(string[]? extra)
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(path);
                if (Directory.Exists(full))
                {
                    roots.Add(full);
                }
            }
            catch
            {
                // ignore
            }
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            Add(Path.Combine(profile, "Downloads"));
        }

        if (extra is not null)
        {
            foreach (var r in extra)
            {
                Add(r);
            }
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static HashSet<string>? NormalizeExtensions(string[]? extensions)
    {
        if (extensions is null || extensions.Length == 0)
        {
            return null;
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in extensions)
        {
            if (string.IsNullOrWhiteSpace(ext))
            {
                continue;
            }

            var e = ext.Trim();
            if (!e.StartsWith('.'))
            {
                e = "." + e;
            }

            set.Add(e);
        }

        return set.Count == 0 ? null : set;
    }

    private static void EnumerateMatches(
        string root,
        string query,
        HashSet<string>? extensions,
        int max,
        ConcurrentBag<FileSearchHit> bag,
        CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && bag.Count < max)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch
            {
                files = Array.Empty<string>();
            }

            foreach (var file in files)
            {
                if (bag.Count >= max)
                {
                    return;
                }

                var name = Path.GetFileName(file);
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (extensions is not null && !extensions.Contains(Path.GetExtension(file)))
                {
                    continue;
                }

                bag.Add(ToHit(file, name));
            }

            IEnumerable<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                stack.Push(sub);
            }
        }
    }

    private static FileSearchHit ToHit(string path, string name)
    {
        long? size = null;
        DateTimeOffset? modified = null;
        try
        {
            var info = new FileInfo(path);
            size = info.Length;
            modified = info.LastWriteTimeUtc;
        }
        catch
        {
            // ignore metadata failures
        }

        return new FileSearchHit
        {
            Path = path,
            Name = name,
            Size = size,
            Modified = modified
        };
    }

    /// <summary>Optional Everything IPC via es.exe when present on PATH or Program Files.</summary>
    private static List<FileSearchHit>? TryEverything(
        string query,
        IReadOnlyList<string>? roots,
        HashSet<string>? extensions,
        int max,
        CancellationToken ct)
    {
        var es = FindEverythingCli();
        if (es is null)
        {
            return null;
        }

        try
        {
            var args = $"-n {max} -s \"{query.Replace("\"", "")}\"";
            var psi = new ProcessStartInfo
            {
                FileName = es,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3_000);
            if (!process.HasExited || process.ExitCode != 0)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }

            var hits = new List<FileSearchHit>();
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(line))
                {
                    continue;
                }

                if (roots is { Count: > 0 } &&
                    !roots.Any(r => line.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (extensions is not null && !extensions.Contains(Path.GetExtension(line)))
                {
                    continue;
                }

                hits.Add(ToHit(line, Path.GetFileName(line)));
                if (hits.Count >= max)
                {
                    break;
                }
            }

            return hits.Count > 0 ? hits : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindEverythingCli()
    {
        var env = Environment.GetEnvironmentVariable("EVERYTHING_ES");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidate = Path.Combine(pf, "Everything", "es.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        return null;
    }
}
