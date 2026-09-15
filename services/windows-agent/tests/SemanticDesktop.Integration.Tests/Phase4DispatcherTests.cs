using System.Text.Json;
using SemanticDesktop.Agent;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.IPC;

namespace SemanticDesktop.Integration.Tests;

public class Phase4DispatcherTests
{
    [Fact]
    public async Task DesktopGetCapabilities_IncludesCoreTools_AndBrowserChromeWhenInstalled()
    {
        using var dispatcher = new CommandDispatcher();
        var result = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "cap1",
            Method = CommandNames.DesktopGetCapabilities,
            Params = null
        }, CancellationToken.None);

        using var doc = Parse(result);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var data = doc.RootElement.GetProperty("data");
        var tools = data.GetProperty("tools").EnumerateArray().Select(t => t.GetString()).ToHashSet();
        Assert.Contains(CommandNames.WindowList, tools);
        Assert.Contains(CommandNames.DesktopGetState, tools);
        Assert.Contains(CommandNames.BrowserNavigate, tools);
        Assert.Contains(CommandNames.BrowserQuery, tools);

        var browser = data.GetProperty("browser");
        Assert.True(browser.GetProperty("chrome").GetBoolean());
        Assert.False(browser.GetProperty("firefox").GetBoolean());
    }

    [Fact]
    public async Task DesktopGetState_ReturnsWindowsArray()
    {
        using var dispatcher = new CommandDispatcher();
        var result = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "state1",
            Method = CommandNames.DesktopGetState,
            Params = null
        }, CancellationToken.None);

        using var doc = Parse(result);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("data").GetProperty("windows").ValueKind);
    }

    [Fact]
    public async Task ProcessList_ReturnsNonEmptyProcHandles()
    {
        using var dispatcher = new CommandDispatcher();
        var result = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "proc1",
            Method = CommandNames.ProcessList,
            Params = null
        }, CancellationToken.None);

        using var doc = Parse(result);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var processes = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        Assert.NotEmpty(processes);
        Assert.All(processes, p => Assert.StartsWith("proc_", p.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Filesystem_WriteReadListExists_RoundTripUnderTemp()
    {
        using var dispatcher = new CommandDispatcher();
        var dir = Path.Combine(Path.GetTempPath(), "sd-phase4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "roundtrip.txt");
        const string contents = "phase4-filesystem-roundtrip";

        try
        {
            var write = await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "fs-write",
                Method = CommandNames.FilesystemWriteText,
                Params = JsonSerializer.SerializeToElement(new { path, contents }, JsonDefaults.Options)
            }, CancellationToken.None);
            using (var writeDoc = Parse(write))
            {
                Assert.True(writeDoc.RootElement.GetProperty("ok").GetBoolean());
            }

            var read = await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "fs-read",
                Method = CommandNames.FilesystemReadText,
                Params = JsonSerializer.SerializeToElement(new { path }, JsonDefaults.Options)
            }, CancellationToken.None);
            using (var readDoc = Parse(read))
            {
                Assert.True(readDoc.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal(contents, readDoc.RootElement.GetProperty("data").GetProperty("contents").GetString());
            }

            var list = await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "fs-list",
                Method = CommandNames.FilesystemList,
                Params = JsonSerializer.SerializeToElement(new { path = dir }, JsonDefaults.Options)
            }, CancellationToken.None);
            using (var listDoc = Parse(list))
            {
                Assert.True(listDoc.RootElement.GetProperty("ok").GetBoolean());
                var names = listDoc.RootElement.GetProperty("data").GetProperty("entries")
                    .EnumerateArray()
                    .Select(e => e.GetProperty("name").GetString())
                    .ToList();
                Assert.Contains("roundtrip.txt", names);
            }

            var exists = await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "fs-exists",
                Method = CommandNames.FilesystemExists,
                Params = JsonSerializer.SerializeToElement(new { path }, JsonDefaults.Options)
            }, CancellationToken.None);
            using (var existsDoc = Parse(exists))
            {
                Assert.True(existsDoc.RootElement.GetProperty("ok").GetBoolean());
                Assert.True(existsDoc.RootElement.GetProperty("data").GetProperty("exists").GetBoolean());
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task WindowWaitFor_MatchesExistingWindowTitleSubstring()
    {
        using var dispatcher = new CommandDispatcher();
        var list = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "win-list",
            Method = CommandNames.WindowList,
            Params = null
        }, CancellationToken.None);

        using var listDoc = Parse(list);
        Assert.True(listDoc.RootElement.GetProperty("ok").GetBoolean());
        var windows = listDoc.RootElement.GetProperty("data").EnumerateArray()
            .Select(w => w.GetProperty("title").GetString() ?? "")
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        Assert.NotEmpty(windows);

        var title = windows[0];
        var titleContains = title.Length <= 8 ? title : title[..8];

        var wait = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "win-wait",
            Method = CommandNames.WindowWaitFor,
            Params = JsonSerializer.SerializeToElement(new { titleContains, timeoutMs = 500 }, JsonDefaults.Options)
        }, CancellationToken.None);

        using var waitDoc = Parse(wait);
        Assert.True(waitDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Object, waitDoc.RootElement.GetProperty("data").GetProperty("window").ValueKind);
    }

    [Fact]
    public async Task UiWaitFor_ImpossibleSelector_TimesOutRetryable()
    {
        using var dispatcher = new CommandDispatcher();
        var result = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "ui-wait",
            Method = CommandNames.UiWaitFor,
            Params = JsonSerializer.SerializeToElement(new
            {
                name = "sd-impossible-selector-" + Guid.NewGuid().ToString("N"),
                timeoutMs = 300
            }, JsonDefaults.Options)
        }, CancellationToken.None);

        using var doc = Parse(result);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal(ErrorCodes.Timeout, error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
    }

    private static JsonDocument Parse(object result)
    {
        var json = JsonSerializer.Serialize(result, JsonDefaults.Options);
        return JsonDocument.Parse(json);
    }
}
