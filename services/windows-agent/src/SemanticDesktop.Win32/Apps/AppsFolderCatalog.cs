using System.Runtime.InteropServices;

namespace SemanticDesktop.Win32.Apps;

public sealed record AppsFolderApp(string DisplayName, string Aumid);

/// <summary>
/// Enumerate Start/AppsFolder entries (primarily Appx AUMIDs) via Shell.Application.
/// Cached; call <see cref="Invalidate"/> after installs/uninstalls.
/// </summary>
public static class AppsFolderCatalog
{
    private static readonly object Gate = new();
    private static IReadOnlyList<AppsFolderApp>? _cache;
    private static readonly Dictionary<string, AppsFolderApp?> ResolveCache = new(StringComparer.OrdinalIgnoreCase);

    public static void Invalidate()
    {
        lock (Gate)
        {
            _cache = null;
            ResolveCache.Clear();
        }
    }

    public static IReadOnlyList<AppsFolderApp> Enumerate(bool forceRefresh = false)
    {
        lock (Gate)
        {
            if (forceRefresh)
            {
                _cache = null;
                ResolveCache.Clear();
            }

            return EnsureCache();
        }
    }

    public static AppsFolderApp? FindBestMatch(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var key = name.Trim();
        lock (Gate)
        {
            if (ResolveCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var apps = EnsureCache();
            var match = AppNameMatcher.PickBest(key, apps, a => a.DisplayName);
            ResolveCache[key] = match;
            return match;
        }
    }

    /// <summary>Test helper: best match against an explicit catalog (no cache).</summary>
    public static AppsFolderApp? FindBestMatchFrom(string name, IEnumerable<AppsFolderApp> apps) =>
        AppNameMatcher.PickBest(name, apps, a => a.DisplayName);

    private static IReadOnlyList<AppsFolderApp> EnsureCache()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        _cache = Discover();
        return _cache;
    }

    private static IReadOnlyList<AppsFolderApp> Discover()
    {
        var list = new List<AppsFolderApp>();
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return list;
            }

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return list;
            }

            dynamic folder = shell.NameSpace("shell:AppsFolder");
            if (folder is null)
            {
                return list;
            }

            dynamic items = folder.Items();
            if (items is null)
            {
                return list;
            }

            var count = (int)items.Count;
            for (var i = 0; i < count; i++)
            {
                try
                {
                    dynamic item = items.Item(i);
                    string? display = item?.Name as string;
                    string? path = item?.Path as string;
                    if (string.IsNullOrWhiteSpace(display) || string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    var aumid = path.Trim();
                    // Keep AUMID-like entries; skip plain filesystem shortcuts (handled by Start Menu cache).
                    if (LooksLikeFilePath(aumid) && !LooksLikeAumid(aumid))
                    {
                        continue;
                    }

                    list.Add(new AppsFolderApp(display.Trim(), aumid));
                }
                catch
                {
                    // skip item
                }
            }
        }
        catch
        {
            // Shell.Application unavailable
        }

        return list
            .GroupBy(a => a.Aumid, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool LooksLikeAumid(string value) =>
        value.Contains('!', StringComparison.Ordinal) ||
        (value.Contains('_', StringComparison.Ordinal) && !LooksLikeFilePath(value));

    private static bool LooksLikeFilePath(string value) =>
        value.Contains(Path.DirectorySeparatorChar) ||
        value.Contains(Path.AltDirectorySeparatorChar) ||
        (value.Length >= 2 && value[1] == ':');
}

/// <summary>Launch an AppsFolder / Appx entry by AUMID.</summary>
public static class AppsFolderLauncher
{
    private static readonly Guid ClsidApplicationActivationManager = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");

    public static object Launch(string aumid, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            throw new ArgumentException("aumid is required.", nameof(aumid));
        }

        var id = aumid.Trim();
        if (TryActivateApplication(id, out var pid))
        {
            return new
            {
                launched = true,
                name = displayName,
                aumid = id,
                pid = pid == 0 ? (int?)null : (int)pid,
                provider = "ActivateApplication",
                executable = (string?)null
            };
        }

        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "shell:AppsFolder\\" + id,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Failed to start explorer for AppsFolder launch.");

        return new
        {
            launched = true,
            name = displayName,
            aumid = id,
            pid = process.Id,
            provider = "shell:AppsFolder",
            executable = (string?)null
        };
    }

    private static bool TryActivateApplication(string aumid, out uint processId)
    {
        processId = 0;
        object? raw = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidApplicationActivationManager, throwOnError: false);
            if (type is null)
            {
                return false;
            }

            raw = Activator.CreateInstance(type);
            if (raw is not IApplicationActivationManager manager)
            {
                return false;
            }

            manager.ActivateApplication(aumid, string.Empty, ActivateOptions.None, out processId);
            return true;
        }
        catch
        {
            processId = 0;
            return false;
        }
        finally
        {
            if (raw is not null && Marshal.IsComObject(raw))
            {
                try { Marshal.ReleaseComObject(raw); } catch { /* ignore */ }
            }
        }
    }

    [Flags]
    private enum ActivateOptions
    {
        None = 0
    }

    [ComImport]
    [Guid("2E941141-7F97-4756-B7D4-9DFDF77F0D4B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        void ActivateApplication(
            [In, MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [In, MarshalAs(UnmanagedType.LPWStr)] string arguments,
            [In] ActivateOptions options,
            out uint processId);
    }
}
