using SemanticDesktop.Adapters.Discord;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Models;

namespace SemanticDesktop.Adapters.Tests;

public class DiscordExecutableCacheTests
{
    [Fact]
    public void Get_Uses_DISCORD_PATH_When_File_Exists()
    {
        var previous = Environment.GetEnvironmentVariable("DISCORD_PATH");
        DiscordExecutableCache.Invalidate();
        var fake = Path.Combine(Path.GetTempPath(), "discord-cache-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(fake, Array.Empty<byte>());
        try
        {
            Environment.SetEnvironmentVariable("DISCORD_PATH", fake);
            DiscordExecutableCache.Invalidate();
            Assert.Equal(fake, DiscordExecutableCache.Get());
            // Cached
            Assert.Equal(fake, DiscordExecutableCache.Get());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISCORD_PATH", previous);
            DiscordExecutableCache.Invalidate();
            try { File.Delete(fake); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Invalidate_Clears_Cache()
    {
        var previous = Environment.GetEnvironmentVariable("DISCORD_PATH");
        DiscordExecutableCache.Invalidate();
        var fake = Path.Combine(Path.GetTempPath(), "discord-cache-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(fake, Array.Empty<byte>());
        try
        {
            Environment.SetEnvironmentVariable("DISCORD_PATH", fake);
            DiscordExecutableCache.Invalidate();
            Assert.Equal(fake, DiscordExecutableCache.Get());

            Environment.SetEnvironmentVariable("DISCORD_PATH", null);
            DiscordExecutableCache.Invalidate();
            var after = DiscordExecutableCache.Get();
            Assert.True(after is null || File.Exists(after));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISCORD_PATH", previous);
            DiscordExecutableCache.Invalidate();
            try { File.Delete(fake); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void FindNewestDiscordExeUnder_Picks_Newest_App_Folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "discord-root-" + Guid.NewGuid().ToString("N"));
        var older = Path.Combine(root, "app-1.0.9000");
        var newer = Path.Combine(root, "app-1.0.9001");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newer);
        var olderExe = Path.Combine(older, "Discord.exe");
        var newerExe = Path.Combine(newer, "Discord.exe");
        File.WriteAllBytes(olderExe, Array.Empty<byte>());
        File.WriteAllBytes(newerExe, Array.Empty<byte>());
        File.SetLastWriteTimeUtc(olderExe, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(newerExe, DateTime.UtcNow);
        try
        {
            Assert.Equal(newerExe, DiscordExecutableCache.FindNewestDiscordExeUnder(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void FindNewestDiscordExeUnder_Missing_Returns_Null()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-discord-" + Guid.NewGuid().ToString("N"));
        Assert.Null(DiscordExecutableCache.FindNewestDiscordExeUnder(missing));
    }
}

public class DiscordAdapterTests
{
    private sealed class FakeHost : IDiscordDesktopHost
    {
        public List<WindowInfo> Windows { get; set; } = new();
        public List<string> Hotkeys { get; } = new();
        public List<string> Typed { get; } = new();
        public List<string> Keys { get; } = new();
        public List<string> Focused { get; } = new();

        public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WindowInfo>>(Windows);

        public Task<WindowInfo?> FocusAsync(string windowId, CancellationToken cancellationToken)
        {
            Focused.Add(windowId);
            return Task.FromResult(Windows.FirstOrDefault(w => w.Id == windowId));
        }

        public Task<WindowInfo?> MaximizeAsync(string windowId, CancellationToken cancellationToken) =>
            Task.FromResult(Windows.FirstOrDefault(w => w.Id == windowId));

        public Task<WindowMutationResult?> MoveAsync(WindowMoveRequest request, CancellationToken cancellationToken)
        {
            var win = Windows.FirstOrDefault(w => w.Id == request.WindowId);
            return Task.FromResult<WindowMutationResult?>(win is null ? null : new WindowMutationResult { Window = win });
        }

        public void Hotkey(IReadOnlyList<string> keys) => Hotkeys.Add(string.Join("+", keys));

        public void TypeText(string text) => Typed.Add(text);

        public void Key(string key) => Keys.Add(key);
    }

    [Fact]
    public async Task JoinVoice_Requires_Channel()
    {
        var adapter = new DiscordAdapter(new FakeHost());
        var result = await adapter.ExecuteAsync(new AdapterCommand
        {
            Action = CommandNames.DiscordJoinVoice,
            Params = new Dictionary<string, object?>()
        }, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, result.ErrorCode);
    }

    [Fact]
    public async Task JoinVoice_Without_Host_Unavailable()
    {
        var previous = Environment.GetEnvironmentVariable("DISCORD_PATH");
        DiscordExecutableCache.Invalidate();
        var fake = Path.Combine(Path.GetTempPath(), "discord-adapter-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(fake, Array.Empty<byte>());
        try
        {
            Environment.SetEnvironmentVariable("DISCORD_PATH", fake);
            DiscordExecutableCache.Invalidate();
            var adapter = new DiscordAdapter();
            var result = await adapter.ExecuteAsync(new AdapterCommand
            {
                Action = CommandNames.DiscordJoinVoice,
                Params = new Dictionary<string, object?> { ["channel"] = "General" }
            }, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal(ErrorCodes.AdapterUnavailable, result.ErrorCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISCORD_PATH", previous);
            DiscordExecutableCache.Invalidate();
            try { File.Delete(fake); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task JoinVoice_QuickSwitch_And_Verify_Title()
    {
        var previous = Environment.GetEnvironmentVariable("DISCORD_PATH");
        DiscordExecutableCache.Invalidate();
        var fake = Path.Combine(Path.GetTempPath(), "discord-adapter-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(fake, Array.Empty<byte>());
        var host = new FakeHost
        {
            Windows =
            {
                new WindowInfo
                {
                    Id = "win_discord",
                    Title = "#General | My Server | Discord",
                    Process = "Discord.exe",
                    Pid = 42
                }
            }
        };
        try
        {
            Environment.SetEnvironmentVariable("DISCORD_PATH", fake);
            DiscordExecutableCache.Invalidate();
            var adapter = new DiscordAdapter(host);
            var result = await adapter.ExecuteAsync(new AdapterCommand
            {
                Action = CommandNames.DiscordJoinVoice,
                Params = new Dictionary<string, object?>
                {
                    ["channel"] = "General",
                    ["server"] = "My Server"
                }
            }, CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Contains("ctrl+k", host.Hotkeys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("General", host.Typed);
            Assert.Contains("enter", host.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("win_discord", host.Focused);
            var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
            Assert.Contains("\"joined\":true", json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("quick_switch", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISCORD_PATH", previous);
            DiscordExecutableCache.Invalidate();
            try { File.Delete(fake); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Capabilities_List_Discord_Actions()
    {
        var adapter = new DiscordAdapter();
        var caps = await adapter.GetCapabilitiesAsync(CancellationToken.None);
        Assert.Equal("discord", caps.AdapterId);
        Assert.Contains(CommandNames.DiscordOpen, caps.Actions);
        Assert.Contains(CommandNames.DiscordJoinVoice, caps.Actions);
        Assert.Contains(CommandNames.DiscordQuickSwitch, caps.Actions);
    }

    [Fact]
    public void CanHandle_Discord_Process()
    {
        var adapter = new DiscordAdapter();
        Assert.True(adapter.CanHandle(new ProcessInfo { Id = "p", Pid = 1, Name = "Discord.exe" }));
        Assert.False(adapter.CanHandle(new ProcessInfo { Id = "p", Pid = 1, Name = "notepad.exe" }));
    }
}
