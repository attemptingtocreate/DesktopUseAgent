using System.Text.Json;
using SemanticDesktop.Core.Handles;
using SemanticDesktop.Core.Results;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.Core.Targets;

namespace SemanticDesktop.Core.Tests;

public class CoreContractTests
{
    [Fact]
    public void HandleIds_HaveExpectedPrefixes()
    {
        Assert.StartsWith("win_", HandleRegistry.CreateId(HandleKind.Window));
        Assert.StartsWith("uia_", HandleRegistry.CreateId(HandleKind.Element));
        Assert.StartsWith("proc_", HandleRegistry.CreateId(HandleKind.Process));
        Assert.StartsWith("brw_", HandleRegistry.CreateId(HandleKind.Browser));
        Assert.StartsWith("tab_", HandleRegistry.CreateId(HandleKind.Tab));
    }

    [Fact]
    public void HandleRegistry_AllocatesUniqueIds()
    {
        var registry = new HandleRegistry();
        var a = registry.Allocate(HandleKind.Window, 1);
        var b = registry.Allocate(HandleKind.Window, 2);
        Assert.NotEqual(a, b);
        Assert.True(registry.TryResolve<int>(a, out var native, out _));
        Assert.Equal(1, native);
    }

    [Fact]
    public void HandleRegistry_MissingId_ReturnsStaleTarget()
    {
        var registry = new HandleRegistry();
        Assert.False(registry.TryResolve<int>("win_missing", out _, out var code));
        Assert.Equal("STALE_TARGET", code);
    }

    [Fact]
    public void ToolResult_AndSemanticTarget_RoundTripJson()
    {
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var result = ToolResult<SemanticTarget>.Success(
            new SemanticTarget { WindowId = "win_1", ControlType = "Button", Name = "Export" },
            new ResultMeta
            {
                RequestId = "req_1",
                StartedAt = started,
                CompletedAt = started.AddMilliseconds(12),
                DurationMs = 12
            },
            new PerformanceMeta
            {
                Operation = "ui.find",
                DurationMs = 12,
                ElementsInspected = 3,
                CacheHit = false,
                Provider = "UIA"
            });

        var json = JsonSerializer.Serialize(result, JsonDefaults.Options);
        var back = JsonSerializer.Deserialize<ToolResult<SemanticTarget>>(json, JsonDefaults.Options);
        Assert.NotNull(back);
        Assert.True(back!.Ok);
        Assert.Equal("win_1", back.Data!.WindowId);
        Assert.Equal("Button", back.Data.ControlType);
        Assert.Equal("UIA", back.Performance!.Provider);
    }

    [Fact]
    public void PerformanceMeta_FormatsPhase1Log()
    {
        var meta = new PerformanceMeta
        {
            Operation = "ui.find",
            DurationMs = 31,
            ElementsInspected = 43,
            CacheHit = false,
            Provider = "UIA"
        };
        var text = meta.ToString();
        Assert.Contains("ui.find", text);
        Assert.Contains("31 ms", text);
        Assert.Contains("43 nodes inspected", text);
        Assert.Contains("cache hit: false", text);
        Assert.Contains("provider: UIA", text);
    }
}
