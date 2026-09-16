using System.Text.Json;
using SemanticDesktop.Agent;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.IPC;

namespace SemanticDesktop.Integration.Tests;

public class BenchmarkScenarioTests
{
    [Fact]
    public async Task BenchmarkScenarios_ProduceJsonSummary()
    {
        using var dispatcher = new CommandDispatcher();
        var scenarios = new List<object>();

        async Task<object> Run(string name, string method, object? parameters = null)
        {
            var started = DateTimeOffset.UtcNow;
            var result = await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "bench-" + name,
                Method = method,
                Params = parameters is null ? null : JsonSerializer.SerializeToElement(parameters, JsonDefaults.Options)
            }, CancellationToken.None);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, JsonDefaults.Options));
            var ok = doc.RootElement.GetProperty("ok").GetBoolean();
            var durationMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
            if (doc.RootElement.TryGetProperty("performance", out var perf) &&
                perf.TryGetProperty("durationMs", out var perfMs))
            {
                durationMs = perfMs.GetInt64();
            }

            return new { name, method, ok, durationMs };
        }

        scenarios.Add(await Run("ping", CommandNames.SystemPing));
        scenarios.Add(await Run("capabilities", CommandNames.DesktopGetCapabilities));
        scenarios.Add(await Run("window-list", CommandNames.WindowList));
        scenarios.Add(await Run("graph", CommandNames.DesktopGetGraph, new { forceRefresh = true }));

        var perf = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "bench-perf",
            Method = CommandNames.SystemPerformance,
            Params = null
        }, CancellationToken.None);

        var summary = new
        {
            capturedAt = DateTimeOffset.UtcNow,
            scenarios,
            performance = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(perf, JsonDefaults.Options))
        };

        var json = JsonSerializer.Serialize(summary, JsonDefaults.Options);
        Assert.Contains("scenarios", json);
        Assert.Contains("ping", json);
    }
}
