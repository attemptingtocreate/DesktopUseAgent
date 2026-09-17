namespace SemanticDesktop.Win32.Shell;

/// <summary>Resolve .lnk TargetPath via WScript.Shell COM, with a best-effort binary fallback.</summary>
public static class ShortcutTargetResolver
{
    public static string? Resolve(string lnkPath)
    {
        if (string.IsNullOrWhiteSpace(lnkPath) || !File.Exists(lnkPath))
        {
            return null;
        }

        var viaCom = ResolveViaCom(lnkPath);
        if (!string.IsNullOrWhiteSpace(viaCom) && File.Exists(viaCom))
        {
            return viaCom;
        }

        return TryParseLnkTarget(lnkPath);
    }

    private static string? ResolveViaCom(string lnkPath)
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

    private static string? TryParseLnkTarget(string lnkPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(lnkPath);
            if (bytes.Length < 0x4C)
            {
                return null;
            }

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
                return null;
            }

            var linkInfoSize = BitConverter.ToUInt32(bytes, offset);
            if (linkInfoSize < 0x1C || offset + linkInfoSize > bytes.Length)
            {
                return null;
            }

            var linkInfoFlags = BitConverter.ToUInt32(bytes, offset + 8);
            const uint VolumeIdAndLocalBasePath = 0x01;
            if ((linkInfoFlags & VolumeIdAndLocalBasePath) == 0)
            {
                return null;
            }

            var localBasePathOffset = BitConverter.ToUInt32(bytes, offset + 16);
            var pathStart = offset + (int)localBasePathOffset;
            if (pathStart < offset || pathStart >= bytes.Length)
            {
                return null;
            }

            var end = pathStart;
            while (end < bytes.Length && bytes[end] != 0)
            {
                end++;
            }

            var path = System.Text.Encoding.Default.GetString(bytes, pathStart, end - pathStart);
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }
}
