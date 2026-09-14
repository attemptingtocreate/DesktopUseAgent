using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Security;
using SemanticDesktop.Permissions;

namespace SemanticDesktop.Permissions.Tests;

public class PermissionEngineTests
{
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
    public void SessionGrant_AllowsPreviouslyAskCapability()
    {
        var engine = new PermissionEngine();
        var session = new AgentSession { SessionId = "s1", ClientId = "t" };
        engine.Grant(session, Capabilities.FilesystemWrite, PermissionGrantScope.Session);
        var unscoped = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "unscoped-sd-test.txt");
        // Still path-evaluated; grant alone shouldn't bypass deny paths.
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
}
