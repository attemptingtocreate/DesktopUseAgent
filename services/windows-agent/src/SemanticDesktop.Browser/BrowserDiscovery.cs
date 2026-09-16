using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using SemanticDesktop.Core.Models;

namespace SemanticDesktop.Browser;

public sealed class DiscoveredBrowser
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public int? DebugPort { get; init; }
    public bool Running { get; init; }
}

public static class BrowserDiscovery
{
    private static readonly object DiscoverCacheLock = new();
    private static IReadOnlyList<DiscoveredBrowser>? _discoverCache;
    private static DateTimeOffset _discoverCacheExpiry = DateTimeOffset.MinValue;
    private static readonly TimeSpan DiscoverCacheTtl = TimeSpan.FromSeconds(4);
    private static readonly int[] DebugPortRange = Enumerable.Range(9222, 20).ToArray();

    private static readonly string[] ChromeCandidates =
    {
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Google\Chrome\Application\chrome.exe")
    };

    private static readonly string[] EdgeCandidates =
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
    };

    private static readonly string[] BraveCandidates =
    {
        @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe",
        @"C:\Program Files (x86)\BraveSoftware\Brave-Browser\Application\brave.exe"
    };

    public static IReadOnlyList<DiscoveredBrowser> Discover()
    {
        lock (DiscoverCacheLock)
        {
            if (_discoverCache is not null && DateTimeOffset.UtcNow < _discoverCacheExpiry)
            {
                return _discoverCache;
            }
        }

        var results = DiscoverCore();
        lock (DiscoverCacheLock)
        {
            _discoverCache = results;
            _discoverCacheExpiry = DateTimeOffset.UtcNow.Add(DiscoverCacheTtl);
        }

        return results;
    }

    public static int? TryFindLiveDebugPort()
    {
        var livePorts = FindLiveDebugPortsParallel();
        return livePorts.Count > 0 ? livePorts[0] : null;
    }

    private static IReadOnlyList<DiscoveredBrowser> DiscoverCore()
    {
        var ports = DetectDebugPorts();
        var results = new List<DiscoveredBrowser>();

        void Add(string name, string[] candidates)
        {
            foreach (var path in candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var matchingPort = ports.FirstOrDefault(p =>
                    p.Executable is not null &&
                    path.Equals(p.Executable, StringComparison.OrdinalIgnoreCase));
                results.Add(new DiscoveredBrowser
                {
                    Name = name,
                    Path = path,
                    DebugPort = matchingPort.Port != 0 ? matchingPort.Port : ports.FirstOrDefault().Port is var port and > 0 ? port : null,
                    Running = matchingPort.Port != 0 || (ports.Count > 0 && name.Equals("chrome", StringComparison.OrdinalIgnoreCase))
                });
            }
        }

        Add("chrome", ChromeCandidates);
        Add("edge", EdgeCandidates);
        Add("brave", BraveCandidates);

        // De-dupe Running flag more accurately: only mark running if a live debug endpoint responds
        // and we found a port; prefer ports that answered /json/version.
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var live = ports.Where(p => p.Port > 0).Select(p => p.Port).Distinct().FirstOrDefault(IsDebugPortLive);
            results[i] = new DiscoveredBrowser
            {
                Name = r.Name,
                Path = r.Path,
                DebugPort = live > 0 ? live : r.DebugPort,
                Running = live > 0 && IsLikelyOwner(r.Name, live)
            };
        }

        return results;
    }

    public static string? FindPreferredExecutable()
    {
        var discovered = Discover();
        return discovered.FirstOrDefault(d => d.Name == "chrome")?.Path
               ?? discovered.FirstOrDefault(d => d.Name == "edge")?.Path
               ?? discovered.FirstOrDefault()?.Path;
    }

    public static bool IsInstalled(string name) =>
        name.ToLowerInvariant() switch
        {
            "chrome" => ChromeCandidates.Any(File.Exists),
            "edge" => EdgeCandidates.Any(File.Exists),
            "brave" => BraveCandidates.Any(File.Exists),
            _ => false
        };

    public static int FindFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static readonly TimeSpan LivePagesBudget = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan LivePagesHttpTimeout = TimeSpan.FromMilliseconds(400);

    public static IReadOnlyList<GraphBrowserTab> TryListLivePages() =>
        TryListLivePagesAsync(CancellationToken.None, LivePagesBudget).GetAwaiter().GetResult();

    public static async Task<IReadOnlyList<GraphBrowserTab>> TryListLivePagesAsync(
        CancellationToken cancellationToken = default,
        TimeSpan? budget = null)
    {
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(budget ?? LivePagesBudget);

        var livePorts = await FindLiveDebugPortsParallelAsync(budgetCts.Token).ConfigureAwait(false);
        if (livePorts.Count == 0)
        {
            return Array.Empty<GraphBrowserTab>();
        }

        var pages = new List<GraphBrowserTab>();
        var portTasks = livePorts.Select(port => CollectPagesFromPortAsync(port, pages, budgetCts.Token)).ToArray();
        try
        {
            await Task.WhenAll(portTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Budget exceeded; return partial pages collected so far.
        }
        catch
        {
            // Best-effort snapshot; missing CDP is not a graph failure.
        }

        return pages.Count > 30 ? pages.Take(30).ToList() : pages;
    }

    private static async Task CollectPagesFromPortAsync(int port, List<GraphBrowserTab> pages, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var browser = await ReadBrowserProductAsync(port, cancellationToken).ConfigureAwait(false);
            using var client = new HttpClient { Timeout = LivePagesHttpTimeout };
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/json/list", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var firstPage = true;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (cancellationToken.IsCancellationRequested || pages.Count >= 30)
                {
                    return;
                }

                var type = el.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                lock (pages)
                {
                    if (pages.Count >= 30)
                    {
                        return;
                    }

                    pages.Add(new GraphBrowserTab
                    {
                        Id = id,
                        Browser = browser,
                        Title = el.TryGetProperty("title", out var t) ? t.GetString() : null,
                        Url = el.TryGetProperty("url", out var u) ? u.GetString() : null,
                        Active = firstPage
                    });
                }

                firstPage = false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Budget or caller cancellation.
        }
        catch
        {
            // Best-effort per-port snapshot.
        }
    }

    private static async Task<string> ReadBrowserProductAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = LivePagesHttpTimeout };
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/json/version", cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return "Chrome";
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var product = doc.RootElement.TryGetProperty("Browser", out var b) ? b.GetString() ?? "" : "";
            if (product.Contains("Edg", StringComparison.OrdinalIgnoreCase)) return "Edge";
            if (product.Contains("Brave", StringComparison.OrdinalIgnoreCase)) return "Brave";
            if (product.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
            return "Chrome";
        }
        catch
        {
            return "Chrome";
        }
    }

    public static bool IsDebugPortLive(int port)
    {
        if (!IsLocalPortOpen(port, 75))
        {
            return false;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(400) };
            using var response = client.GetAsync($"http://127.0.0.1:{port}/json/version").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLocalPortOpen(int port, int timeoutMs)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var async = socket.BeginConnect(IPAddress.Loopback, port, null, null);
            if (!async.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                try { socket.Close(); } catch { /* ignore */ }
                return false;
            }

            socket.EndConnect(async);
            return socket.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLikelyOwner(string name, int port)
    {
        // Best-effort: any live port counts as chrom* family for chrome/edge/brave discovery flags.
        _ = name;
        return IsDebugPortLive(port);
    }

    private static IReadOnlyList<int> FindLiveDebugPortsParallel()
    {
        var livePorts = new ConcurrentBag<int>();
        Parallel.ForEach(DebugPortRange, port =>
        {
            if (IsDebugPortLive(port))
            {
                livePorts.Add(port);
            }
        });

        return livePorts.OrderBy(static p => p).ToArray();
    }

    private static async Task<IReadOnlyList<int>> FindLiveDebugPortsParallelAsync(CancellationToken cancellationToken)
    {
        var livePorts = new ConcurrentBag<int>();
        var tasks = DebugPortRange.Select(async port =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsDebugPortLiveAsync(port, cancellationToken).ConfigureAwait(false))
            {
                livePorts.Add(port);
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Budget exceeded; return partial live ports.
        }

        return livePorts.OrderBy(static p => p).ToArray();
    }

    private static async Task<bool> IsDebugPortLiveAsync(int port, CancellationToken cancellationToken)
    {
        if (!await IsLocalPortOpenAsync(port, 75, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            using var client = new HttpClient { Timeout = LivePagesHttpTimeout };
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/json/version", cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsLocalPortOpenAsync(int port, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(timeoutMs);
            await socket.ConnectAsync(IPAddress.Loopback, port, connectCts.Token).ConfigureAwait(false);
            return socket.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static List<(int Port, string? Executable)> DetectDebugPorts()
    {
        var found = new List<(int Port, string? Executable)>();
        foreach (var port in FindLiveDebugPortsParallel())
        {
            found.Add((port, null));
        }

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var name = process.ProcessName;
                    if (!name.Contains("chrome", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("msedge", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("brave", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? cmd = null;
                    try
                    {
                        cmd = process.MainModule?.FileName;
                    }
                    catch
                    {
                        // Access denied for some processes.
                    }

                    // Command line not always available without WMI; keep path association only.
                    if (cmd is not null)
                    {
                        found.Add((0, cmd));
                    }
                }
                catch
                {
                    // Ignore inaccessible processes.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Ignore process enumeration failures.
        }

        return found;
    }
}
