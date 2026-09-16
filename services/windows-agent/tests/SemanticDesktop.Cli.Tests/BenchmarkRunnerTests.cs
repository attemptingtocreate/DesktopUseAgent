using System.Text.Json;
using SemanticDesktop.Cli;
using Xunit;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Serialization;

namespace SemanticDesktop.Cli.Tests;

public class BenchmarkRunnerTests
{
    [Fact]
    public void DryRun_ProducesSchemaWithPlannedScenarios()
    {
        var runner = new BenchmarkRunner(new FakeBenchmarkAgentClient());
        var summary = runner.CreateDryRunPlan(new BenchmarkOptions { Live = true, Iterations = 3, Monitor = 1, Url = "about:blank" });
        var json = JsonSerializer.Serialize(summary, JsonDefaults.Options);

        Assert.Contains("\"mode\":\"dry-run\"", json.Replace(" ", ""));
        Assert.Contains("open-url", json);
        Assert.Contains("window-move-monitor", json);
        Assert.Contains("studio-hierarchy", json);
        Assert.Contains("blender-batch-vs-individual", json);
        Assert.Equal(3, summary.Parameters.Iterations);
        Assert.Equal(1, summary.Parameters.Monitor);
        Assert.All(summary.Scenarios, s => Assert.Equal("planned", s.Status));
    }

    [Fact]
    public async Task SafeRun_UsesMockClientAndReportsTimings()
    {
        var client = new FakeBenchmarkAgentClient();
        var runner = new BenchmarkRunner(client);
        var summary = await runner.RunAsync(new BenchmarkOptions { Iterations = 2 }, CancellationToken.None);

        Assert.Equal("safe", summary.Mode);
        Assert.Equal(4, summary.Scenarios.Count);
        Assert.All(summary.Scenarios, s => Assert.Equal("completed", s.Status));
        Assert.All(summary.Scenarios, s => Assert.NotNull(s.WallMs));
        Assert.Equal(2, summary.Scenarios.First(s => s.Name == "ping").Iterations);
        Assert.True(client.CallCount >= 4);
    }

    [Fact]
    public async Task LiveRun_SkipsRobloxWhenPluginDisconnected()
    {
        var client = new FakeBenchmarkAgentClient
        {
            RobloxConnected = false
        };
        var runner = new BenchmarkRunner(client);
        var summary = await runner.RunAsync(new BenchmarkOptions { Live = true, Iterations = 1 }, CancellationToken.None);

        var roblox = summary.Scenarios.Single(s => s.Name == "studio-hierarchy");
        Assert.Equal("skipped", roblox.Status);
        Assert.Contains("not connected", roblox.SkipReason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeBenchmarkAgentClient : IBenchmarkAgentClient
    {
        public int CallCount { get; private set; }
        public bool RobloxConnected { get; init; } = true;

        public Task<BenchmarkCallResult> CallAsync(string method, object? parameters, CancellationToken cancellationToken)
        {
            CallCount++;
            if (method == CommandNames.RobloxPluginPing)
            {
                var pingJson = JsonSerializer.SerializeToElement(new
                {
                    ok = true,
                    data = new { connected = RobloxConnected }
                }, JsonDefaults.Options);
                return Task.FromResult(new BenchmarkCallResult
                {
                    Ok = true,
                    WallMs = 5,
                    ToolDurationMs = 4,
                    Raw = pingJson
                });
            }

            if (method == CommandNames.RobloxGetHierarchy)
            {
                return Task.FromResult(new BenchmarkCallResult
                {
                    Ok = RobloxConnected,
                    WallMs = 7,
                    ToolDurationMs = 6,
                    ErrorMessage = RobloxConnected ? null : "plugin not connected"
                });
            }

            if (method == CommandNames.ProcessLaunch)
            {
                return Task.FromResult(new BenchmarkCallResult { Ok = false, WallMs = 1, ErrorMessage = "mock skip launch" });
            }

            if (method is CommandNames.BlenderBatch or CommandNames.BlenderGetScene or CommandNames.BlenderGetObjects)
            {
                return Task.FromResult(new BenchmarkCallResult
                {
                    Ok = false,
                    WallMs = 2,
                    ErrorMessage = "Blender executable not found"
                });
            }

            if (method == CommandNames.BrowserOpenTab)
            {
                return Task.FromResult(new BenchmarkCallResult
                {
                    Ok = false,
                    WallMs = 3,
                    ErrorMessage = "Browser/CDP not available"
                });
            }

            if (method == CommandNames.WindowWaitFor)
            {
                var waitJson = JsonSerializer.SerializeToElement(new
                {
                    ok = true,
                    data = new { window = new { id = "win_test" } }
                }, JsonDefaults.Options);
                return Task.FromResult(new BenchmarkCallResult
                {
                    Ok = true,
                    WallMs = 8,
                    ToolDurationMs = 7,
                    Raw = waitJson
                });
            }

            return Task.FromResult(new BenchmarkCallResult
            {
                Ok = true,
                WallMs = 10,
                ToolDurationMs = 9,
                Raw = JsonSerializer.SerializeToElement(new { ok = true, data = new { } }, JsonDefaults.Options)
            });
        }
    }
}
