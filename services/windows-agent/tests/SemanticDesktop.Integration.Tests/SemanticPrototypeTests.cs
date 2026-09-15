using System.Diagnostics;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Handles;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Core.Targets;
using SemanticDesktop.UIA.Automation;
using SemanticDesktop.Win32.Processes;
using SemanticDesktop.Win32.Windows;

namespace SemanticDesktop.Integration.Tests;

public sealed class FixtureHost : IAsyncLifetime
{
    public HandleRegistry Handles { get; private set; } = null!;
    public WindowService Windows { get; private set; } = null!;
    public ProcessService Processes { get; private set; } = null!;
    public UIAutomationService Uia { get; private set; } = null!;
    public string FixtureWindowId { get; private set; } = null!;

    private Process? _fixtureProcess;

    public async Task InitializeAsync()
    {
        Handles = new HandleRegistry();
        Windows = new WindowService(Handles);
        Processes = new ProcessService(Handles);
        Uia = new UIAutomationService(Handles, Windows);

        var fixtureProject = ResolveFixtureProject();
        Assert.True(File.Exists(fixtureProject), $"Fixture project missing: {fixtureProject}");

        var dotnet = ResolveDotnetPath();
        var build = Process.Start(new ProcessStartInfo(dotnet, $"build \"{fixtureProject}\" -c Debug")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        Assert.NotNull(build);
        await build!.WaitForExitAsync();
        Assert.Equal(0, build.ExitCode);

        var runExe = FindFixtureExe(fixtureProject);
        Assert.True(File.Exists(runExe), $"Fixture exe missing: {runExe}");
        _fixtureProcess = Process.Start(new ProcessStartInfo(runExe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(runExe)!
        });
        Assert.NotNull(_fixtureProcess);

        var windowId = await WaitForWindowAsync("SemanticDesktop UIA Fixture", TimeSpan.FromSeconds(20));
        Assert.False(string.IsNullOrWhiteSpace(windowId));
        FixtureWindowId = windowId!;
    }

    public Task DisposeAsync()
    {
        Uia.Dispose();
        try
        {
            if (_fixtureProcess is { HasExited: false })
            {
                _fixtureProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignored
        }

        _fixtureProcess?.Dispose();
        return Task.CompletedTask;
    }

    public async Task<string?> WaitForWindowAsync(string titleContains, TimeSpan timeout, string? processContains = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var windows = await Windows.ListAsync(CancellationToken.None);
            var match = windows.FirstOrDefault(w =>
                w.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase) &&
                (processContains is null || w.Process.Contains(processContains, StringComparison.OrdinalIgnoreCase)));
            if (match is not null)
            {
                return match.Id;
            }

            await Task.Delay(200);
        }

        return null;
    }

    private static string FindFixtureExe(string fixtureProject)
    {
        var dir = Path.GetDirectoryName(fixtureProject)!;
        return Directory.GetFiles(dir, "UiaTestApp.exe", SearchOption.AllDirectories)
            .Where(p => p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
    }

    private static string ResolveFixtureProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "fixtures", "uia-test-app", "UiaTestApp.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate fixtures/uia-test-app/UiaTestApp.csproj");
    }

    private static string ResolveDotnetPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".tools", "dotnet", "dotnet.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return "dotnet";
    }
}

public class SemanticPrototypeTests : IClassFixture<FixtureHost>
{
    private readonly FixtureHost _fx;

    public SemanticPrototypeTests(FixtureHost fx) => _fx = fx;

    [Fact]
    public async Task Fixture_InvokeButton_And_SetTextBoxValue()
    {
        var buttons = await _fx.Uia.FindAsync(new UIFindQuery
        {
            WindowId = _fx.FixtureWindowId,
            Selector = new UIFindSelector
            {
                AutomationId = "PrimaryButton",
                ControlType = "Button"
            },
            MaxResults = 3
        }, CancellationToken.None);
        Assert.Single(buttons);

        var invoke = await _fx.Uia.InvokeAsync(new ElementHandle { Id = buttons[0].Id }, CancellationToken.None);
        Assert.True(invoke.Success);

        var boxes = await _fx.Uia.FindAsync(new UIFindQuery
        {
            WindowId = _fx.FixtureWindowId,
            Selector = new UIFindSelector { AutomationId = "NameBox" },
            MaxResults = 3
        }, CancellationToken.None);
        Assert.Single(boxes);

        var set = await _fx.Uia.SetValueAsync(new ElementHandle { Id = boxes[0].Id }, "Hello", CancellationToken.None);
        Assert.True(set.Success);

        var text = await _fx.Uia.GetTextAsync(new ElementHandle { Id = boxes[0].Id }, CancellationToken.None);
        Assert.Equal("Hello", text);
    }

    [Fact]
    public async Task Notepad_Launch_FindDocument_SetValue()
    {
        var before = (await _fx.Windows.ListAsync(CancellationToken.None))
            .Where(w => w.Process.Contains("notepad", StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Id)
            .ToHashSet(StringComparer.Ordinal);

        var launched = await _fx.Processes.LaunchAsync(
            new ProcessLaunchRequest { Executable = "notepad.exe" },
            CancellationToken.None);
        Assert.True(launched.Pid > 0);

        string? notepadId = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && notepadId is null)
        {
            var windows = await _fx.Windows.ListAsync(CancellationToken.None);
            notepadId = windows.FirstOrDefault(w =>
                w.Process.Contains("notepad", StringComparison.OrdinalIgnoreCase) &&
                (w.Pid == launched.Pid || !before.Contains(w.Id)))?.Id;
            if (notepadId is null)
            {
                await Task.Delay(200);
            }
        }

        Assert.False(string.IsNullOrWhiteSpace(notepadId));

        IReadOnlyList<UIElement> docs = Array.Empty<UIElement>();
        for (var attempt = 0; attempt < 10 && docs.Count == 0; attempt++)
        {
            docs = await _fx.Uia.FindAsync(new UIFindQuery
            {
                WindowId = notepadId,
                Selector = new UIFindSelector { ControlType = "Document" },
                MaxResults = 5
            }, CancellationToken.None);

            if (docs.Count == 0)
            {
                docs = await _fx.Uia.FindAsync(new UIFindQuery
                {
                    WindowId = notepadId,
                    Selector = new UIFindSelector { ControlType = "Edit" },
                    MaxResults = 5
                }, CancellationToken.None);
            }

            if (docs.Count == 0)
            {
                await Task.Delay(250);
            }
        }

        Assert.NotEmpty(docs);
        var set = await _fx.Uia.SetValueAsync(
            new ElementHandle { Id = docs[0].Id },
            "Semantic desktop",
            CancellationToken.None);
        Assert.True(set.Success);

        try
        {
            foreach (var proc in Process.GetProcessesByName("notepad"))
            {
                try
                {
                    if (proc.Id == launched.Pid || proc.MainWindowTitle.Contains("Untitled", StringComparison.OrdinalIgnoreCase))
                    {
                        proc.CloseMainWindow();
                        if (!proc.WaitForExit(1000))
                        {
                            proc.Kill(entireProcessTree: true);
                        }
                    }
                }
                catch
                {
                    // ignored
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    [Fact]
    public async Task FiftySemanticActions_WithoutScreenshot()
    {
        var actions = 0;
        for (var i = 0; i < 25; i++)
        {
            var windows = await _fx.Windows.ListAsync(CancellationToken.None);
            Assert.NotEmpty(windows);
            actions++;

            var found = await _fx.Uia.FindAsync(new UIFindQuery
            {
                WindowId = _fx.FixtureWindowId,
                Selector = new UIFindSelector { AutomationId = "PrimaryButton" },
                MaxResults = 1
            }, CancellationToken.None);
            Assert.Single(found);
            actions++;
        }

        Assert.True(actions >= 50);
    }
}
