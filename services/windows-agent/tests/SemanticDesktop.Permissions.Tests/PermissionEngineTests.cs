using SemanticDesktop.Audit;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Security;
using SemanticDesktop.Permissions;

namespace SemanticDesktop.Permissions.Tests;

public class PermissionEngineTests
{
    [Fact]
    public void MonitorList_IsAllow_ByDefault()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        var result = engine.Evaluate(session, CommandNames.MonitorList);
        Assert.Equal(PermissionDecisionKind.Allow, result.Decision);
        Assert.Equal(Capabilities.WindowObserve, result.Capability);
        Assert.Equal(RiskClass.Read, result.Risk);
    }

    [Fact]
    public void WindowMove_IsLowRiskWrite()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        var result = engine.Evaluate(session, CommandNames.WindowMove);
        Assert.Equal(PermissionDecisionKind.Allow, result.Decision);
        Assert.Equal(Capabilities.WindowControl, result.Capability);
        Assert.Equal(RiskClass.LowRiskWrite, result.Risk);
    }

    [Fact]
    public void WindowList_IsAllow_ByDefault()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        var result = engine.Evaluate(session, CommandNames.WindowList);
        Assert.Equal(PermissionDecisionKind.Allow, result.Decision);
        Assert.Equal(Capabilities.WindowObserve, result.Capability);
    }

    [Fact]
    public void WindowsPath_IsDenied()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        var windows = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers");
        var result = engine.Evaluate(session, CommandNames.FilesystemWriteText, windows);
        Assert.Equal(PermissionDecisionKind.Deny, result.Decision);
    }

    [Fact]
    public void TempPath_Write_IsAllow()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        var path = Path.Combine(Path.GetTempPath(), "sd-perm-test.txt");
        var result = engine.Evaluate(session, CommandNames.FilesystemWriteText, path);
        Assert.Equal(PermissionDecisionKind.Allow, result.Decision);
    }

    [Fact]
    public void KeePass_Interact_IsDenied()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        var result = engine.Evaluate(session, CommandNames.UiInvoke, application: "KeePass.exe");
        Assert.Equal(PermissionDecisionKind.Deny, result.Decision);
    }

    [Fact]
    public void DesktopDescribe_IsAllowObserve()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        var describe = engine.Evaluate(session, CommandNames.DesktopDescribe);
        Assert.Equal(PermissionDecisionKind.Allow, describe.Decision);
        Assert.Equal(Capabilities.DesktopObserve, describe.Capability);
        Assert.Equal(RiskClass.Read, describe.Risk);

        var graph = engine.Evaluate(session, CommandNames.DesktopGetGraph);
        Assert.Equal(PermissionDecisionKind.Allow, graph.Decision);
        Assert.Equal(Capabilities.DesktopObserve, graph.Capability);
    }

    [Fact]
    public void ShellOpen_IsAsk_ShellExecute()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        var result = engine.Evaluate(session, CommandNames.ShellOpen);
        Assert.Equal(PermissionDecisionKind.Ask, result.Decision);
        Assert.Equal(Capabilities.ShellExecute, result.Capability);
        Assert.Equal(RiskClass.LowRiskWrite, result.Risk);
    }

    [Fact]
    public void ClipboardRead_IsAllow_ClipboardWrite_IsAsk()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        var read = engine.Evaluate(session, CommandNames.ClipboardRead);
        Assert.Equal(PermissionDecisionKind.Allow, read.Decision);
        Assert.Equal(RiskClass.Read, read.Risk);

        var write = engine.Evaluate(session, CommandNames.ClipboardWrite);
        Assert.Equal(PermissionDecisionKind.Ask, write.Decision);
        Assert.Equal(Capabilities.ClipboardWrite, write.Capability);
    }

    [Fact]
    public void FilesystemDelete_IsDestructiveAsk()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        var path = Path.Combine(@"C:\SemanticDesktopUnscoped", "sd-del-test.txt");
        var result = engine.Evaluate(session, CommandNames.FilesystemDelete, path);
        Assert.Equal(PermissionDecisionKind.Ask, result.Decision);
        Assert.Equal(Capabilities.FilesystemDelete, result.Capability);
        Assert.Equal(RiskClass.Destructive, result.Risk);
    }

    [Fact]
    public void SystemPower_IsAskPrivileged()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        var result = engine.Evaluate(session, CommandNames.SystemPower);
        Assert.Equal(PermissionDecisionKind.Ask, result.Decision);
        Assert.Equal(Capabilities.SystemPower, result.Capability);
        Assert.Equal(RiskClass.Privileged, result.Risk);
    }

    [Fact]
    public void SearchFiles_IsAllowRead()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        var result = engine.Evaluate(session, CommandNames.SearchFiles);
        Assert.Equal(PermissionDecisionKind.Allow, result.Decision);
        Assert.Equal(Capabilities.FilesystemRead, result.Capability);
        Assert.Equal(RiskClass.Read, result.Risk);
    }

    [Fact]
    public void SessionGrant_AllowsPreviouslyAskCapability()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        engine.Grant(session, Capabilities.FilesystemWrite, PermissionGrantScope.Session);
        var denied = engine.Evaluate(session, CommandNames.FilesystemWriteText,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "x.txt"));
        Assert.Equal(PermissionDecisionKind.Deny, denied.Decision);
    }
}

public class SensitiveRedactorTests
{
    [Fact]
    public void PasswordControl_IsSensitive()
    {
        Assert.True(SensitiveRedactor.LooksSensitive("Password", "Password", null, null));
        Assert.Equal("<redacted>", SensitiveRedactor.RedactValue("secret"));
    }
}

public class EmergencyStopTests
{
    [Fact]
    public void StopAndClear_Toggle()
    {
        var gate = new EmergencyStopGate();
        Assert.False(gate.IsStopped);
        gate.Stop();
        Assert.True(gate.IsStopped);
        gate.Clear();
        Assert.False(gate.IsStopped);
    }

    [Fact]
    public void VisionOcr_IsAsk_VisionCapture()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        var result = engine.Evaluate(session, CommandNames.VisionOcr);
        Assert.Equal(PermissionDecisionKind.Ask, result.Decision);
        Assert.Equal(Capabilities.VisionCapture, result.Capability);
        Assert.Equal(RiskClass.Read, result.Risk);
    }

    [Fact]
    public void MediaTransport_IsAsk_InputKeyboard_LowRisk()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        var result = engine.Evaluate(session, CommandNames.MediaTransport);
        Assert.Equal(PermissionDecisionKind.Ask, result.Decision);
        Assert.Equal(Capabilities.InputKeyboard, result.Capability);
        Assert.Equal(RiskClass.LowRiskWrite, result.Risk);
    }

    [Fact]
    public void OfficeMailCompose_IsAsk_AdapterInteract()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        var result = engine.Evaluate(session, CommandNames.OfficeMailCompose);
        Assert.Equal(PermissionDecisionKind.Ask, result.Decision);
        Assert.Equal(Capabilities.AdapterInteract, result.Capability);
    }
}

public class PermissionMigrationTests
{
    [Fact]
    public void Migrate_FillsMissingInputAndVisionKeys()
    {
        var incoming = new PermissionPolicy();
        incoming.CapabilityDefaults.Clear();
        incoming.CapabilityDefaults[Capabilities.DesktopObserve] = PermissionDecisionKind.Deny;
        var migrated = PermissionPolicy.Migrate(incoming);
        Assert.Equal(PermissionDecisionKind.Deny, migrated.CapabilityDefaults[Capabilities.DesktopObserve]);
        Assert.True(migrated.CapabilityDefaults.ContainsKey(Capabilities.InputKeyboard));
        Assert.True(migrated.CapabilityDefaults.ContainsKey(Capabilities.InputMouse));
        Assert.True(migrated.CapabilityDefaults.ContainsKey(Capabilities.VisionCapture));
        Assert.Equal(PermissionDecisionKind.Ask, migrated.CapabilityDefaults[Capabilities.InputKeyboard]);
    }

    [Fact]
    public void PolicyPersistence_RoundTripsInTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sd-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "policy.json");
        try
        {
            var policy = PermissionPolicy.CreateDefault();
            policy.CapabilityDefaults[Capabilities.FilesystemWrite] = PermissionDecisionKind.Allow;
            PolicyPersistence.Save(path, policy);
            var loaded = PolicyPersistence.Load(path);
            Assert.Equal(1, loaded.SchemaVersion);
            Assert.Equal(PermissionDecisionKind.Allow, loaded.CapabilityDefaults[Capabilities.FilesystemWrite]);
            Assert.True(loaded.CapabilityDefaults.ContainsKey(Capabilities.VisionCapture));
            Assert.NotEmpty(loaded.PathRules);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* cleanup */ }
        }
    }
}

public class AuditRetentionTests
{
    [Fact]
    public void AuditLog_WritesJsonlAndPrunesOldFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sd-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = new AuditLog(32, dir, TimeSpan.FromDays(1));
            log.Record(new AuditEntry
            {
                Id = "aud_1",
                Timestamp = DateTimeOffset.UtcNow,
                Client = "test",
                SessionId = "s1",
                Action = CommandNames.SystemPing,
                PermissionDecision = "Allow",
                DurationMs = 1,
                Success = true
            });
            var today = Path.Combine(dir, $"audit-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            Assert.True(File.Exists(today));
            Assert.Contains("system.ping", File.ReadAllText(today), StringComparison.OrdinalIgnoreCase);

            var old = Path.Combine(dir, "audit-20000101.jsonl");
            File.WriteAllText(old, "{}\n");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
            Assert.True(log.Prune() >= 1);
            Assert.False(File.Exists(old));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* cleanup */ }
        }
    }
}
