using System.Net;
using System.Text.Json;
using SemanticDesktop.Browser;
using SemanticDesktop.Core.Commands;

namespace SemanticDesktop.Browser.Tests;

public sealed class FixtureHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly string _root;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public FixtureHttpServer(string root)
    {
        _root = root;
        var port = BrowserDiscovery.FindFreeTcpPort();
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUri.ToString());
        _listener.Start();
        _loop = Task.Run(ListenAsync);
    }

    public Uri BaseUri { get; }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath.TrimStart('/') ?? "index.html";
            if (string.IsNullOrWhiteSpace(path))
            {
                path = "index.html";
            }

            var full = Path.GetFullPath(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            var bytes = File.ReadAllBytes(full);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8",
                ".txt" => "text/plain; charset=utf-8",
                _ => "application/octet-stream"
            };
            if (path.Contains("download", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.AddHeader("Content-Disposition", "attachment; filename=\"sample.txt\"");
            }

            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { /* ignore */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        }

        _cts.Dispose();
    }
}

public class BrowserServiceIntegrationTests
{
    private static string FixtureRoot()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..", "fixtures", "browser-test-site")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "fixtures", "browser-test-site")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "fixtures", "browser-test-site")),
            @"c:\Users\Administrator\Desktop\DesktopUseAgent\fixtures\browser-test-site"
        };
        var dir = candidates.FirstOrDefault(Directory.Exists);
        Assert.True(dir is not null, "Fixture root missing. Tried: " + string.Join(" | ", candidates));
        return dir!;
    }

    [Fact]
    public void Discovery_FindsChromeOnThisMachine()
    {
        Assert.True(BrowserDiscovery.IsInstalled("chrome"));
        var list = BrowserDiscovery.Discover();
        Assert.Contains(list, d => d.Name == "chrome" && File.Exists(d.Path));
    }

    [Fact]
    public async Task FormFlow_QueryFillSelectClickWaitGetText()
    {
        await using var server = new FixtureHttpServer(FixtureRoot());
        using var browser = new BrowserService();
        var ensure = await browser.EnsureSessionAsync(headless: true);
        Assert.True(ensure.Ok, ensure.Error?.Message);

        var nav = await browser.NavigateAsync(new Uri(server.BaseUri, "form.html").ToString());
        Assert.True(nav.Ok, nav.Error?.Message);

        var name = await browser.QueryAsync(new BrowserSelector { TestId = "name-input" });
        Assert.True(name.Ok, name.Error?.Message);

        var fillName = await browser.FillAsync("Ada Lovelace", selector: new BrowserSelector { TestId = "name-input" });
        Assert.True(fillName.Ok, fillName.Error?.Message);

        var fillEmail = await browser.FillAsync("ada@example.com", selector: new BrowserSelector { Label = "Email" });
        Assert.True(fillEmail.Ok, fillEmail.Error?.Message);

        var select = await browser.SelectAsync("ca", selector: new BrowserSelector { TestId = "country-select" });
        Assert.True(select.Ok, select.Error?.Message);

        var click = await browser.ClickAsync(null, null, new BrowserSelector { Role = "button", Name = "Submit form" });
        Assert.True(click.Ok, click.Error?.Message);

        var wait = await browser.WaitForAsync(new BrowserSelector { TestId = "form-result" }, timeoutMs: 10000);
        Assert.True(wait.Ok, wait.Error?.Message);

        var text = await browser.GetTextAsync(selector: new BrowserSelector { TestId = "form-result" });
        Assert.True(text.Ok, text.Error?.Message);
        var json = JsonSerializer.Serialize(text.Data);
        using var doc = JsonDocument.Parse(json);
        Assert.Contains("Submitted:Ada Lovelace|ada@example.com|ca", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Tabs_Navigate_AndAccessibilityTree()
    {
        await using var server = new FixtureHttpServer(FixtureRoot());
        using var browser = new BrowserService();
        Assert.True((await browser.EnsureSessionAsync(headless: true)).Ok);

        var open = await browser.OpenTabAsync(server.BaseUri.ToString());
        Assert.True(open.Ok, open.Error?.Message);

        var tabs = await browser.TabsAsync();
        Assert.True(tabs.Ok, tabs.Error?.Message);

        var nav = await browser.NavigateAsync(new Uri(server.BaseUri, "index.html").ToString());
        Assert.True(nav.Ok, nav.Error?.Message);

        var ax = await browser.GetAccessibilityTreeAsync(maxDepth: 6);
        Assert.True(ax.Ok, ax.Error?.Message);
        Assert.NotNull(ax.Data);
    }

    [Fact]
    public async Task DelayedElement_WaitFor_Succeeds()
    {
        await using var server = new FixtureHttpServer(FixtureRoot());
        using var browser = new BrowserService();
        Assert.True((await browser.EnsureSessionAsync(headless: true)).Ok);
        Assert.True((await browser.NavigateAsync(server.BaseUri.ToString())).Ok);
        Assert.True((await browser.ClickAsync(null, null, new BrowserSelector { TestId = "show-delayed" })).Ok);
        var wait = await browser.WaitForAsync(new BrowserSelector { TestId = "delayed-ready" }, timeoutMs: 10000);
        Assert.True(wait.Ok, wait.Error?.Message);
    }

    [Fact]
    public async Task Download_Detection_BestEffort()
    {
        await using var server = new FixtureHttpServer(FixtureRoot());
        using var browser = new BrowserService();
        Assert.True((await browser.EnsureSessionAsync(headless: true)).Ok);
        Assert.True((await browser.NavigateAsync(server.BaseUri.ToString())).Ok);
        Assert.True((await browser.ClickAsync(null, null, new BrowserSelector { TestId = "download-link" })).Ok);
        await Task.Delay(1500);
        var downloads = browser.GetDownloads();
        Assert.True(downloads.Ok);
        // Best-effort: may be empty if Browser domain events unavailable; still must succeed.
        Assert.Equal("CDP", downloads.Performance!.Provider);
    }
}
