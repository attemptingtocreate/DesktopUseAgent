using System.Diagnostics;
using System.Text.Json;
using SemanticDesktop.Agent;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Plans;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.IPC;

namespace SemanticDesktop.Integration.Tests;

public class PlanExecutionIntegrationTests
{
    [Fact]
    public async Task PlanExecute_NotepadWriteAndSaveFile_Succeeds()
    {
        var savePath = Path.Combine(Path.GetTempPath(), $"sd-phase2-{Guid.NewGuid():N}.txt");
        try
        {
            using var dispatcher = new CommandDispatcher();
            var plan = new
            {
                name = "Open Notepad and write text",
                options = new { stopOnFailure = true, defaultTimeoutMs = 30000 },
                steps = new object[]
                {
                    new
                    {
                        id = "launch",
                        action = CommandNames.ProcessLaunch,
                        args = new { executable = "notepad.exe" },
                        waitAfter = new { type = "window.exists", process = "notepad.exe", timeoutMs = 15000 }
                    },
                    new
                    {
                        id = "find-editor",
                        action = CommandNames.UiFind,
                        args = new { controlType = "Document" },
                        retries = new { count = 3, delayMs = 250 }
                    },
                    new
                    {
                        id = "set-text",
                        action = CommandNames.UiSetValue,
                        args = new { targetFrom = "find-editor", value = "Hello from Semantic Desktop" }
                    },
                    new
                    {
                        id = "save",
                        action = CommandNames.FilesystemWriteText,
                        args = new { path = savePath, contents = "Hello from Semantic Desktop" },
                        waitAfter = new { type = "file.exists", path = savePath, timeoutMs = 5000 }
                    }
                }
            };

            var result = await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "plan-int",
                Method = CommandNames.PlanExecute,
                Params = JsonSerializer.SerializeToElement(plan, JsonDefaults.Options)
            }, CancellationToken.None);

            var json = JsonSerializer.Serialize(result, JsonDefaults.Options);
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), json);
            Assert.Equal(PlanStatus.Succeeded, doc.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.True(File.Exists(savePath));
            Assert.Equal("Hello from Semantic Desktop", File.ReadAllText(savePath));
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName("notepad"))
            {
                try
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(1000))
                    {
                        p.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // ignored
                }
                finally
                {
                    p.Dispose();
                }
            }

            if (File.Exists(savePath))
            {
                File.Delete(savePath);
            }
        }
    }
}
