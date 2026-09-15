using SemanticDesktop.Core.Models;

namespace SemanticDesktop.Core.Tests;

public class DesktopGraphTests
{
    [Fact]
    public void InferState_BlenderTitle_ExtractsFile()
    {
        var state = DesktopGraphSemantics.InferState("blender", "pickaxe.blend - Blender", new[] { "pickaxe.blend - Blender" });
        Assert.Equal("pickaxe.blend", state["file"]);
    }

    [Fact]
    public void InferState_VisualStudioTitle_ExtractsSolution()
    {
        var state = DesktopGraphSemantics.InferState(
            "devenv",
            "BrandMyNewCar.sln - Microsoft Visual Studio",
            new[] { "BrandMyNewCar.sln - Microsoft Visual Studio" });
        Assert.Equal("BrandMyNewCar.sln", state["solution"]);
    }

    [Fact]
    public void InferState_VsCodeTitle_ExtractsFileAndFolder()
    {
        var state = DesktopGraphSemantics.InferState(
            "Code",
            "AgentBridge.cs — DesktopUseAgent — Visual Studio Code",
            new[] { "AgentBridge.cs — DesktopUseAgent — Visual Studio Code" });
        Assert.Equal("AgentBridge.cs", state["file"]);
        Assert.Equal("DesktopUseAgent", state["folder"]);
    }

    [Fact]
    public void FormatCompact_MatchesPlannerShapeWithoutScreenshots()
    {
        var graph = new SemanticDesktopGraph
        {
            FocusedApplication = "Blender",
            Applications = new[]
            {
                new GraphApplication
                {
                    Name = "Blender",
                    Process = "blender",
                    Focused = true,
                    State = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["file"] = "pickaxe.blend",
                        ["title"] = "pickaxe.blend - Blender"
                    }
                },
                new GraphApplication
                {
                    Name = "Chrome",
                    Process = "chrome",
                    State = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["kind"] = "browser",
                        ["title"] = "ChatGPT - Google Chrome"
                    }
                },
                new GraphApplication
                {
                    Name = "Visual Studio",
                    Process = "devenv",
                    State = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["solution"] = "BrandMyNewCar.sln"
                    }
                }
            },
            BrowserTabs = new[]
            {
                new GraphBrowserTab { Id = "t1", Browser = "Chrome", Title = "ChatGPT", Active = true },
                new GraphBrowserTab { Id = "t2", Browser = "Chrome", Title = "Roblox Creator Hub" }
            }
        };

        var text = DesktopGraphSemantics.FormatCompact(graph);
        Assert.Contains("Blender", text);
        Assert.Contains("file: pickaxe.blend", text);
        Assert.Contains("focused: true", text);
        Assert.Contains("Chrome", text);
        Assert.Contains("ChatGPT", text);
        Assert.Contains("Roblox Creator Hub", text);
        Assert.Contains("Visual Studio", text);
        Assert.Contains("solution: BrandMyNewCar.sln", text);
        Assert.DoesNotContain("title:", text);
        Assert.DoesNotContain("kind:", text);
    }
}
