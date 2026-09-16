using System.Text.Json;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Core.Plans;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.Core.Workflow;

namespace SemanticDesktop.Core.Tests;

public class WorkflowOptimizationTests
{
    [Fact]
    public void FuseCalls_ListAndExists_BecomeInspect()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sd-fuse");
        var a = Path.Combine(dir, "a.txt");
        var b = Path.Combine(dir, "b.txt");
        var calls = new[]
        {
            new BatchCall { Id = "l", Method = CommandNames.FilesystemList, Params = JsonSerializer.SerializeToElement(new { path = dir }, JsonDefaults.Options) },
            new BatchCall { Id = "a", Method = CommandNames.FilesystemExists, Params = JsonSerializer.SerializeToElement(new { path = a }, JsonDefaults.Options) },
            new BatchCall { Id = "b", Method = CommandNames.FilesystemExists, Params = JsonSerializer.SerializeToElement(new { path = b }, JsonDefaults.Options) }
        };

        var fused = CommandFusion.FuseCalls(calls);
        Assert.Single(fused);
        Assert.Equal(CommandNames.FilesystemInspect, fused[0].Method);
        Assert.Equal("l", fused[0].Id);
        Assert.True(fused[0].Params.HasValue);
        Assert.Equal(dir, fused[0].Params.Value.GetProperty("path").GetString());
        Assert.Equal(2, fused[0].Params.Value.GetProperty("paths").GetArrayLength());
    }

    [Fact]
    public void FuseSteps_GetStateThenDescribe_KeepsDescribe()
    {
        var steps = new List<PlanStep>
        {
            new() { Id = "s", Action = CommandNames.DesktopGetState },
            new() { Id = "d", Action = CommandNames.DesktopDescribe }
        };
        var fused = CommandFusion.FuseSteps(steps);
        Assert.Single(fused);
        Assert.Equal(CommandNames.DesktopDescribe, fused[0].Action);
        Assert.Equal("d", fused[0].Id);
    }

    [Fact]
    public void Diff_DetectsAddedWindowAndFocusChange()
    {
        var before = new SemanticDesktopGraph
        {
            CapturedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            FocusedApplication = "Chrome",
            Windows = new[]
            {
                new GraphWindow { Id = "w1", Title = "A", Process = "chrome", Pid = 1 }
            }
        };
        var after = new SemanticDesktopGraph
        {
            CapturedAt = DateTimeOffset.UtcNow,
            FocusedApplication = "Blender",
            FocusedWindowId = "w2",
            Windows = new[]
            {
                new GraphWindow { Id = "w1", Title = "A", Process = "chrome", Pid = 1 },
                new GraphWindow { Id = "w2", Title = "pickaxe.blend", Process = "blender", Pid = 2, Foreground = true }
            }
        };

        var diff = DesktopGraphSemantics.Diff(before, after);
        Assert.True(diff.FocusChanged);
        Assert.Contains(diff.AddedWindows, w => w.Process == "blender");
        Assert.Empty(diff.RemovedWindows);
    }

    [Fact]
    public void Diff_HandlesDuplicateWindowTitles()
    {
        var before = new SemanticDesktopGraph
        {
            CapturedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            Windows = new[]
            {
                new GraphWindow { Id = "w1", Title = "Untitled", Process = "notepad", Pid = 100 },
                new GraphWindow { Id = "w2", Title = "Untitled", Process = "notepad", Pid = 100 }
            }
        };
        var after = new SemanticDesktopGraph
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Windows = before.Windows
        };

        var diff = DesktopGraphSemantics.Diff(before, after);
        Assert.Empty(diff.AddedWindows);
        Assert.Empty(diff.RemovedWindows);
    }

    [Fact]
    public void ReadSafety_InspectIsParallelAndCacheable()
    {
        Assert.True(ReadSafety.IsSafeRead(CommandNames.FilesystemInspect));
        Assert.True(ReadSafety.IsParallelSafe(CommandNames.FilesystemStat));
        Assert.True(ReadSafety.IsCacheable(CommandNames.FilesystemInspect));
        Assert.False(ReadSafety.IsParallelSafe(CommandNames.WindowList));
        Assert.True(ReadSafety.IsMutation(CommandNames.FilesystemWriteText));
    }
}
