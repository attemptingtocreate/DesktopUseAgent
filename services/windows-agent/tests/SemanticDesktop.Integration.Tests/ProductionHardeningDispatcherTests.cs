using System.Text.Json;
using SemanticDesktop.Agent;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Production;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.IPC;

namespace SemanticDesktop.Integration.Tests;

[Collection(nameof(HardeningSerial))]
public class ProductionHardeningDispatcherTests
{
    [Fact]
    public void Installer_InstallVerifyUpdateAndRejectBadHash()
    {
        var root = Path.Combine(Path.GetTempPath(), "sd-install-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "v1");
        var target = Path.Combine(root, "current");
        var staged = Path.Combine(root, "v2");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "payload.txt"), "one");
        try
        {
            var installed = InstallerService.Install(source, target, "1.12.0");
            using var verified = Parse(UpdateService.VerifyLayout(target, installed));
            Assert.True(verified.RootElement.GetProperty("ok").GetBoolean());

            Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "payload.txt"), "two");
            var next = InstallerService.Install(staged, Path.Combine(root, "staged-copy"), "1.13.0");
            File.Copy(Path.Combine(root, "staged-copy", "install-manifest.json"), Path.Combine(staged, "install-manifest.json"), true);
            var applied = UpdateService.Apply(staged, target, next);
            Assert.Equal("1.13.0", applied.Version);
            Assert.Equal("two", File.ReadAllText(Path.Combine(target, "payload.txt")));

            var bad = new UpdateManifest
            {
                Version = "1.14.0",
                Files = new[] { new ManifestFile { Path = "payload.txt", Sha256 = "deadbeef" } }
            };
            var hashEx = Assert.Throws<InvalidOperationException>(() => UpdateService.Apply(staged, target, bad));
            Assert.StartsWith(ErrorCodes.IntegrityFailed, hashEx.Message);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* cleanup */ }
        }
    }

    [Fact]
    public async Task Telemetry_DefaultsOff_AndSecurityReviewShape()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "sd-data-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var dispatcher = new CommandDispatcher(dataRoot);
            using var telemetry = Parse(await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "t0",
                Method = CommandNames.SystemTelemetryGet
            }, CancellationToken.None));
            Assert.True(telemetry.RootElement.GetProperty("ok").GetBoolean());
            Assert.False(telemetry.RootElement.GetProperty("data").GetProperty("enabled").GetBoolean());
            Assert.Equal("off", telemetry.RootElement.GetProperty("data").GetProperty("level").GetString());

            using var review = Parse(await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "r0",
                Method = CommandNames.SystemSecurityReview
            }, CancellationToken.None));
            Assert.True(review.RootElement.GetProperty("ok").GetBoolean());
            var data = review.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("pipeAclCurrentUserOnly").GetBoolean());
            Assert.True(data.GetProperty("unknownCommandsDenied").GetBoolean());
            Assert.True(data.GetProperty("telemetryDefaultOff").GetBoolean());
            Assert.True(data.GetProperty("schemaMigrated").GetBoolean());
            Assert.Equal(RuntimeCompat.SchemaVersion, data.GetProperty("schemaVersion").GetInt32());

            await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "t1",
                Method = CommandNames.SystemTelemetrySet,
                Params = JsonSerializer.SerializeToElement(new { enabled = true }, JsonDefaults.Options)
            }, CancellationToken.None);
            await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "p1",
                Method = CommandNames.SystemPing
            }, CancellationToken.None);
            using var after = Parse(await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "t2",
                Method = CommandNames.SystemTelemetryGet
            }, CancellationToken.None));
            Assert.True(after.RootElement.GetProperty("data").GetProperty("enabled").GetBoolean());
            Assert.True(after.RootElement.GetProperty("data").GetProperty("toolCalls").GetInt64() >= 1);
        }
        finally
        {
            try { Directory.Delete(dataRoot, true); } catch { /* cleanup */ }
        }
    }

    [Fact]
    public async Task FaultInjector_ReturnsFailureWithoutKillingDispatcher()
    {
        FaultInjector.Method = CommandNames.SystemPing;
        try
        {
            using var dispatcher = new CommandDispatcher();
            using var failed = Parse(await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "f1",
                Method = CommandNames.SystemPing
            }, CancellationToken.None));
            Assert.False(failed.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(ErrorCodes.Internal, failed.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.True(failed.RootElement.GetProperty("error").GetProperty("details").GetProperty("recovered").GetBoolean());

            using var recovered = Parse(await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "f2",
                Method = CommandNames.SystemPing
            }, CancellationToken.None));
            Assert.True(recovered.RootElement.GetProperty("ok").GetBoolean());

            using var status = Parse(await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "f3",
                Method = CommandNames.SystemStatus
            }, CancellationToken.None));
            Assert.True(status.RootElement.GetProperty("data").GetProperty("lastCrash").GetProperty("recovered").GetBoolean());
        }
        finally
        {
            FaultInjector.Method = null;
        }
    }

    [Fact]
    public async Task ConcurrentPingAndCapabilities_DoNotFail()
    {
        using var dispatcher = new CommandDispatcher();
        var tasks = Enumerable.Range(0, 32).Select(async i =>
        {
            var method = i % 2 == 0 ? CommandNames.SystemPing : CommandNames.DesktopGetCapabilities;
            var result = await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "load" + i,
                Method = method
            }, CancellationToken.None);
            using var doc = Parse(result);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        });
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task UpdateCheckApply_ViaDispatcher_UsesTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sd-upd-" + Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var staged = Path.Combine(root, "staged");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "app.txt"), "next");
        var stagedInstall = Path.Combine(root, "staged-built");
        var manifest = InstallerService.Install(staged, stagedInstall, "1.13.0");
        File.Copy(Path.Combine(stagedInstall, "install-manifest.json"), Path.Combine(staged, "install-manifest.json"), true);
        try
        {
            using var dispatcher = new CommandDispatcher(dataRoot);
            using var check = Parse(await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "u1",
                Method = CommandNames.SystemUpdateCheck,
                Params = JsonSerializer.SerializeToElement(
                    new { manifestPath = Path.Combine(staged, "install-manifest.json") },
                    JsonDefaults.Options)
            }, CancellationToken.None));
            Assert.True(check.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(check.RootElement.GetProperty("data").GetProperty("updateAvailable").GetBoolean());

            using var apply = Parse(await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "u2",
                Method = CommandNames.SystemUpdateApply,
                Params = JsonSerializer.SerializeToElement(
                    new { stagedDir = staged, targetRoot = Path.Combine(root, "current") },
                    JsonDefaults.Options)
            }, CancellationToken.None));
            Assert.True(apply.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("1.13.0", apply.RootElement.GetProperty("data").GetProperty("version").GetString());
            Assert.Equal("1.13.0", manifest.Version);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* cleanup */ }
        }
    }

    private static JsonDocument Parse(object result)
    {
        var json = JsonSerializer.Serialize(result, JsonDefaults.Options);
        return JsonDocument.Parse(json);
    }
}

[CollectionDefinition(nameof(HardeningSerial), DisableParallelization = true)]
public class HardeningSerial;
