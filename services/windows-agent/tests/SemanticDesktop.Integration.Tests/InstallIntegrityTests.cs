using System.Text.Json;
using SemanticDesktop.Agent;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Production;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.IPC;

namespace SemanticDesktop.Integration.Tests;

[Collection(nameof(HardeningSerial))]
public class InstallIntegrityTests
{
    [Fact]
    public async Task SystemIntegrity_UsesInstallRoot_NotDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dua-integ-" + Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var source = Path.Combine(root, "source");
        var installRoot = Path.Combine(root, "current");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(Path.Combine(source, "agent"));
        File.WriteAllText(Path.Combine(source, "payload.txt"), "ok");
        var manifest = InstallerService.Install(source, installRoot, RuntimeCompat.ProductVersion);
        try
        {
            Environment.SetEnvironmentVariable("DESKTOPUSEAGENT_INSTALL", installRoot);
            using var dispatcher = new CommandDispatcher(dataRoot);
            using var doc = Parse(await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "i1",
                Method = CommandNames.SystemIntegrity
            }, CancellationToken.None));
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            var data = doc.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("ok").GetBoolean());
            Assert.Empty(data.GetProperty("issues").EnumerateArray());
            Assert.Equal(RuntimeCompat.ProductVersion, manifest.Version);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DESKTOPUSEAGENT_INSTALL", null);
            try { Directory.Delete(root, true); } catch { /* cleanup */ }
        }
    }

    [Fact]
    public async Task SystemIntegrity_ExplicitRoot_StillSupported()
    {
        var root = Path.Combine(Path.GetTempPath(), "dua-explicit-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file.txt"), "x");
        var manifest = InstallerService.Install(source, root, "9.9.9-test");
        try
        {
            using var dispatcher = new CommandDispatcher();
            using var doc = Parse(await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "i2",
                Method = CommandNames.SystemIntegrity,
                Params = JsonSerializer.SerializeToElement(new { root }, JsonDefaults.Options)
            }, CancellationToken.None));
            Assert.True(doc.RootElement.GetProperty("data").GetProperty("ok").GetBoolean());
            Assert.Equal("9.9.9-test", manifest.Version);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static JsonDocument Parse(object result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result, JsonDefaults.Options));
}
