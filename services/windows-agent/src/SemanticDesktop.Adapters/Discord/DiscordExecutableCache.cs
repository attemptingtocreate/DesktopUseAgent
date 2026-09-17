namespace SemanticDesktop.Adapters.Discord;

public static class DiscordExecutableCache
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

    /// <summary>Newest Discord.exe under a Discord install root (…\Discord\app-*\Discord.exe).</summary>
    public static string? FindNewestDiscordExeUnder(string discordRoot)
    {
        if (string.IsNullOrWhiteSpace(discordRoot) || !Directory.Exists(discordRoot))
        {
            return null;
        }

        try
        {
            return Directory.GetDirectories(discordRoot, "app-*")
                .Select(dir => Path.Combine(dir, "Discord.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Resolve a .lnk target via WScript.Shell COM, with a best-effort binary fallback.</summary>
    public static string? ResolveShortcutTarget(string lnkPath)
    {
        if (string.IsNullOrWhiteSpace(lnkPath) || !File.Exists(lnkPath))
        {
            return null;
        }

        var viaCom = ResolveShortcutViaCom(lnkPath);
        if (!string.IsNullOrWhiteSpace(viaCom) && File.Exists(viaCom))
        {
            return viaCom;
        }

        return TryParseLnkTarget(lnkPath);
    }

    private static string? Discover()
    {
        var env = Environment.GetEnvironmentVariable("DISCORD_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        foreach (var lnk in EnumerateStartMenuShortcuts())
        {
            var target = ResolveShortcutTarget(lnk);
            if (!string.IsNullOrWhiteSpace(target) &&
                target.EndsWith("Discord.exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(target))
            {
                return target;
            }
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = FindNewestDiscordExeUnder(Path.Combine(local, "Discord"));
        if (current is not null)
        {
            return current;
        }

        return DiscoverFromOtherUserProfiles(local);
    }

    private static string? DiscoverFromOtherUserProfiles(string currentLocalAppData)
    {
        string usersRoot;
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? @"C:\";
            usersRoot = Path.Combine(root, "Users");
            if (!Directory.Exists(usersRoot))
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        string? best = null;
        var bestWrite = DateTime.MinValue;
        try
        {
            string? normalizedCurrent = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(currentLocalAppData))
                {
                    normalizedCurrent = Path.GetFullPath(Path.Combine(currentLocalAppData, "Discord"));
                }
            }
            catch
            {
                // ignore
            }

            foreach (var userDir in Directory.EnumerateDirectories(usersRoot))
            {
                var discordRoot = Path.Combine(userDir, "AppData", "Local", "Discord");
                string normalizedCandidate;
                try
                {
                    normalizedCandidate = Path.GetFullPath(discordRoot);
                }
                catch
                {
                    continue;
                }

                if (normalizedCurrent is not null &&
                    string.Equals(normalizedCurrent, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var hit = FindNewestDiscordExeUnder(discordRoot);
                if (hit is null)
                {
                    continue;
                }

                try
                {
                    var write = File.GetLastWriteTimeUtc(hit);
                    if (best is null || write > bestWrite)
                    {
                        best = hit;
                        bestWrite = write;
                    }
                }
                catch
                {
                    best ??= hit;
                }
            }
        }
        catch
        {
            // ignore enumeration failures (permissions)
        }

        return best;
    }

    private static IEnumerable<string> EnumerateStartMenuShortcuts()
    {
        var names = new[]
        {
            Path.Combine("Discord Inc", "Discord.lnk"),
            "Discord.lnk"
        };

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
            foreach (var name in names)
            {
                var path = Path.Combine(root, name);
                if (File.Exists(path))
                {
                    yield return path;
                }

                var programs = Path.Combine(root, "Programs", name);
                if (File.Exists(programs))
                {
                    yield return programs;
                }
            }
        }
    }

    private static string? ResolveShortcutViaCom(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            var shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            var createShortcut = shellType.GetMethod("CreateShortcut");
            if (createShortcut is null)
            {
                return null;
            }

            var shortcut = createShortcut.Invoke(shell, new object[] { lnkPath });
            if (shortcut is null)
            {
                return null;
            }

            var targetProp = shortcut.GetType().GetProperty("TargetPath");
            var target = targetProp?.GetValue(shortcut) as string;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Best-effort LocalBasePath extraction from a Shell Link (.lnk) file.</summary>
    private static string? TryParseLnkTarget(string lnkPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(lnkPath);
            if (bytes.Length < 0x4C)
            {
                return null;
            }

            // ShellLinkHeader LinkFlags at offset 0x14
            var linkFlags = BitConverter.ToUInt32(bytes, 0x14);
            var offset = 0x4C;
            const uint HasLinkTargetIdList = 0x01;
            const uint HasLinkInfo = 0x02;

            if ((linkFlags & HasLinkTargetIdList) != 0)
            {
                if (offset + 2 > bytes.Length)
                {
                    return null;
                }

                var idListSize = BitConverter.ToUInt16(bytes, offset);
                offset += 2 + idListSize;
            }

            if ((linkFlags & HasLinkInfo) == 0 || offset + 0x1C > bytes.Length)
            {
                return SearchDiscordExeAscii(bytes);
            }

            var linkInfoSize = BitConverter.ToUInt32(bytes, offset);
            if (linkInfoSize < 0x1C || offset + linkInfoSize > bytes.Length)
            {
                return SearchDiscordExeAscii(bytes);
            }

            var linkInfoFlags = BitConverter.ToUInt32(bytes, offset + 8);
            const uint VolumeIdAndLocalBasePath = 0x01;
            if ((linkInfoFlags & VolumeIdAndLocalBasePath) == 0)
            {
                return SearchDiscordExeAscii(bytes);
            }

            var localBasePathOffset = BitConverter.ToUInt32(bytes, offset + 16);
            var pathStart = offset + (int)localBasePathOffset;
            if (pathStart < offset || pathStart >= bytes.Length)
            {
                return SearchDiscordExeAscii(bytes);
            }

            var end = pathStart;
            while (end < bytes.Length && bytes[end] != 0)
            {
                end++;
            }

            var path = System.Text.Encoding.Default.GetString(bytes, pathStart, end - pathStart);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }

            return SearchDiscordExeAscii(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static string? SearchDiscordExeAscii(byte[] bytes)
    {
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        var marker = "Discord.exe";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var start = idx;
        while (start > 0 && text[start - 1] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '\\' or ':' or '.' or '-' or '_' or ' ')
        {
            start--;
        }

        var candidate = text[start..(idx + marker.Length)].Trim();
        return File.Exists(candidate) ? candidate : null;
    }
}
