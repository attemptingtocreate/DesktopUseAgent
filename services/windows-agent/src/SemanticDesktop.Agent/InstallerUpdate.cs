using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Production;

namespace SemanticDesktop.Agent;

public static class InstallerService
{
    public static UpdateManifest Install(string sourceLayout, string targetRoot, string version)
    {
        if (!Directory.Exists(sourceLayout))
        {
            throw new ArgumentException($"{ErrorCodes.NotFound}: source layout '{sourceLayout}' not found.");
        }

        Directory.CreateDirectory(targetRoot);
        var files = new List<ManifestFile>();
        foreach (var file in Directory.EnumerateFiles(sourceLayout, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceLayout, file);
            var dest = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            files.Add(new ManifestFile { Path = relative.Replace('\\', '/'), Sha256 = StateMigrator.Sha256File(dest) });
        }

        var manifest = new UpdateManifest
        {
            SchemaVersion = RuntimeCompat.SchemaVersion,
            Version = version,
            MinCompatibleApi = RuntimeCompat.MinCompatibleApi,
            Files = files
        };
        File.WriteAllText(Path.Combine(targetRoot, "install-manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest, Core.Serialization.JsonDefaults.Options));
        return manifest;
    }

    public static void Uninstall(string targetRoot)
    {
        if (Directory.Exists(targetRoot))
        {
            Directory.Delete(targetRoot, recursive: true);
        }
    }
}

public static class UpdateService
{
    public static object Check(UpdateManifest current, UpdateManifest available)
    {
        var compatible = StateMigrator.IsCompatible(available.Version, available.MinCompatibleApi)
                         && available.SchemaVersion <= RuntimeCompat.SchemaVersion;
        var newer = StateMigrator.IsNewer(available.Version, current.Version);
        return new
        {
            current = current.Version,
            available = available.Version,
            compatible,
            updateAvailable = newer && compatible,
            channel = available.Channel
        };
    }

    public static UpdateManifest Apply(string stagedDir, string targetRoot, UpdateManifest available, bool allowDowngrade = false)
    {
        if (available.SchemaVersion > RuntimeCompat.SchemaVersion)
        {
            throw new InvalidOperationException($"{ErrorCodes.IncompatibleSchema}: update schema {available.SchemaVersion}.");
        }

        var currentPath = Path.Combine(targetRoot, "install-manifest.json");
        UpdateManifest? current = null;
        if (File.Exists(currentPath))
        {
            current = System.Text.Json.JsonSerializer.Deserialize<UpdateManifest>(
                File.ReadAllText(currentPath), Core.Serialization.JsonDefaults.Options);
        }

        if (current is not null && !allowDowngrade && !StateMigrator.IsNewer(available.Version, current.Version)
            && !string.Equals(available.Version, current.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{ErrorCodes.UpdateFailed}: refusing downgrade from {current.Version} to {available.Version}.");
        }

        foreach (var file in available.Files)
        {
            var source = Path.Combine(stagedDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source))
            {
                throw new InvalidOperationException($"{ErrorCodes.IntegrityFailed}: missing {file.Path}.");
            }

            var hash = StateMigrator.Sha256File(source);
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{ErrorCodes.IntegrityFailed}: hash mismatch for {file.Path}.");
            }
        }

        return InstallerService.Install(stagedDir, targetRoot, available.Version);
    }

    public static object VerifyLayout(string root, UpdateManifest manifest)
    {
        var issues = new List<string>();
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                issues.Add($"missing:{file.Path}");
                continue;
            }

            var hash = StateMigrator.Sha256File(path);
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"hash:{file.Path}");
            }
        }

        return new
        {
            signed = false,
            signer = (string?)null,
            reason = "Authenticode is applied by scripts/sign.ps1 when a code-signing certificate is configured.",
            ok = issues.Count == 0,
            issues
        };
    }

    public static UpdateManifest LoadManifest(string path) =>
        System.Text.Json.JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(path), Core.Serialization.JsonDefaults.Options)
        ?? throw new InvalidOperationException($"{ErrorCodes.InvalidArgument}: invalid update manifest.");
}

public static class LogRetention
{
    public static int Prune(string directory, TimeSpan retention, DateTimeOffset? now = null)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var cutoff = (now ?? DateTimeOffset.UtcNow) - retention;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch
            {
                // ignore
            }
        }

        return removed;
    }
}
