using SemanticDesktop.Win32.Apps;
using SemanticDesktop.Win32.Search;
using SemanticDesktop.Win32.Shell;

namespace SemanticDesktop.Win32.Tests;

public class AppNameMatcherTests
{
    [Fact]
    public void Score_Exact_Beats_Contains()
    {
        Assert.True(AppNameMatcher.Score("Calculator", "Calculator") >
                    AppNameMatcher.Score("Calculator", "Windows Calculator"));
    }

    [Fact]
    public void PickBest_Prefers_Contains_Match()
    {
        var apps = new[]
        {
            new AppsFolderApp("Photos", "Photos_abc!App"),
            new AppsFolderApp("Windows Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
            new AppsFolderApp("Calendar", "Calendar_abc!App")
        };

        var best = AppsFolderCatalog.FindBestMatchFrom("calculator", apps);
        Assert.NotNull(best);
        Assert.Equal("Windows Calculator", best!.DisplayName);
    }

    [Fact]
    public void Score_Returns_Negative_When_Unrelated()
    {
        Assert.True(AppNameMatcher.Score("Notion", "Spotify") < 0);
    }
}

public class AppExecutableCacheTests
{
    [Fact]
    public void ResolveFromStartMenuRoot_MatchesExactLnkStem()
    {
        var root = Path.Combine(Path.GetTempPath(), "sd-app-cache-" + Guid.NewGuid().ToString("N"));
        var programs = Path.Combine(root, "Programs");
        Directory.CreateDirectory(programs);

        try
        {
            var targetExe = Path.Combine(root, "FakeApp.exe");
            File.WriteAllBytes(targetExe, Array.Empty<byte>());

            // Minimal .lnk is hard to fabricate; exercise resolver via direct ShortcutTargetResolver path
            // and cache start-menu scan with a real COM-created shortcut when available.
            var lnk = Path.Combine(programs, "FakeApp.lnk");
            if (!TryCreateShortcut(lnk, targetExe))
            {
                // Skip when WScript.Shell unavailable in CI.
                return;
            }

            AppExecutableCache.Invalidate();
            var resolved = AppExecutableCache.ResolveFromStartMenuRoot("FakeApp", programs);
            Assert.NotNull(resolved);
            Assert.True(File.Exists(resolved));
            Assert.Equal(Path.GetFullPath(targetExe), Path.GetFullPath(resolved!), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
            AppExecutableCache.Invalidate();
        }
    }

    private static bool TryCreateShortcut(string lnkPath, string targetPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return false;
            }

            var shell = Activator.CreateInstance(shellType);
            var create = shellType.GetMethod("CreateShortcut");
            var shortcut = create?.Invoke(shell, new object[] { lnkPath });
            if (shortcut is null)
            {
                return false;
            }

            shortcut.GetType().GetProperty("TargetPath")?.SetValue(shortcut, targetPath);
            shortcut.GetType().GetMethod("Save")?.Invoke(shortcut, null);
            return File.Exists(lnkPath);
        }
        catch
        {
            return false;
        }
    }
}

public class ShortcutTargetResolverTests
{
    [Fact]
    public void Resolve_ReturnsNull_ForMissingFile()
    {
        Assert.Null(ShortcutTargetResolver.Resolve(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".lnk")));
    }
}

public class FileSearchServiceTests
{
    [Fact]
    public void Search_FindsFileByNameContains()
    {
        var root = Path.Combine(Path.GetTempPath(), "sd-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = "sduniq" + Guid.NewGuid().ToString("N")[..8];
        var file = Path.Combine(root, marker + "-report.txt");
        File.WriteAllText(file, "x");

        try
        {
            var result = FileSearchService.Search(new FileSearchRequest
            {
                Query = marker,
                Roots = new[] { root },
                MaxResults = 10
            }, CancellationToken.None);

            var json = System.Text.Json.JsonSerializer.Serialize(result);
            Assert.Contains(marker, json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("report.txt", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}

public class PowerServiceTests
{
    [Fact]
    public void Shutdown_WithoutConfirm_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SemanticDesktop.Win32.Power.PowerService.Execute("shutdown", confirm: false));
        Assert.Contains("confirm=true", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
