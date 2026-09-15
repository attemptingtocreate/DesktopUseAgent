using System.Text.Json;
using SemanticDesktop.Agent;
using SemanticDesktop.ControlCenter.Client;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Production;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.IPC;

namespace SemanticDesktop.ControlCenter.Tests;

public class ControlPlaneTests
{
    private static JsonDocument Doc(object result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result, JsonDefaults.Options));

    [Fact]
    public async Task SystemStatus_And_Policy_RoundTrip_Via_Dispatcher()
    {
        var dispatcher = new CommandDispatcher();
        var status = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "1",
            Method = CommandNames.SystemStatus,
            Params = JsonSerializer.SerializeToElement(new { }, JsonDefaults.Options)
        }, CancellationToken.None);

        using var statusDoc = Doc(status);
        Assert.True(statusDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(statusDoc.RootElement.GetProperty("data").GetProperty("agent").GetBoolean());

        var set = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "2",
            Method = CommandNames.PermissionPolicySet,
            Params = JsonSerializer.SerializeToElement(new
            {
                kind = "capability",
                capability = "filesystem.write",
                decision = "Deny"
            }, JsonDefaults.Options)
        }, CancellationToken.None);

        using var setDoc = Doc(set);
        Assert.True(setDoc.RootElement.GetProperty("ok").GetBoolean());

        var get = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "3",
            Method = CommandNames.PermissionPolicyGet,
            Params = JsonSerializer.SerializeToElement(new { }, JsonDefaults.Options)
        }, CancellationToken.None);

        using var getDoc = Doc(get);
        var caps = getDoc.RootElement.GetProperty("data").GetProperty("capabilities").EnumerateArray()
            .First(c => c.GetProperty("capability").GetString() == "filesystem.write");
        Assert.Equal("Deny", caps.GetProperty("decision").GetString());

        var sessions = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "4",
            Method = CommandNames.SessionList,
            Params = JsonSerializer.SerializeToElement(new { }, JsonDefaults.Options)
        }, CancellationToken.None);
        using var sessionsDoc = Doc(sessions);
        Assert.True(sessionsDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(sessionsDoc.RootElement.GetProperty("data").GetArrayLength() >= 1);

        dispatcher.Dispose();
    }

    [Fact]
    public void Snapshot_Parses_Policy_And_Detects_Mcp_Session()
    {
        var status = JsonDocument.Parse("""{"ok":true,"data":{"agent":true,"emergencyStopped":false,"pendingApprovals":1,"sessionCount":2,"pipe":"semantic-desktop-agent"}}""").RootElement;
        var sessions = JsonDocument.Parse("""{"ok":true,"data":[{"sessionId":"sess_1","clientId":"mcp-gateway","autoApproveAsk":false,"grants":[]}]}""").RootElement;
        var pending = JsonDocument.Parse("""{"ok":true,"data":[{"id":"apr_1","sessionId":"sess_1","action":"filesystem.write_text","capability":"filesystem.write","risk":"LowRiskWrite"}]}""").RootElement;
        var audit = JsonDocument.Parse("""{"ok":true,"data":[{"id":"aud_1","timestamp":"2026-01-01T00:00:00Z","client":"mcp-gateway","sessionId":"sess_1","action":"system.ping","permissionDecision":"Allow","durationMs":1,"success":true}]}""").RootElement;
        var policy = JsonDocument.Parse("""{"ok":true,"data":{"capabilities":[{"capability":"filesystem.write","decision":"Ask"}],"appRules":[{"processName":"KeePass","observe":"Deny","interact":"Deny"}],"pathRules":[{"pathPrefix":"C:\\Temp","allowRead":true,"allowWrite":true,"deny":false}]}}""").RootElement;

        var snapshot = ControlCenterSnapshot.FromRpc("semantic-desktop-agent", status, sessions, pending, audit, policy);
        Assert.True(snapshot.Connected);
        Assert.True(snapshot.McpSessionPresent);
        Assert.Single(snapshot.Approvals);
        Assert.Equal("Ask", snapshot.Capabilities[0].Decision);
        Assert.Equal("KeePass", snapshot.AppRules[0].ProcessName);
        Assert.Contains("Temp", snapshot.PathRules[0].PathPrefix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PolicySet_App_And_Path_Rules()
    {
        var dispatcher = new CommandDispatcher();
        var app = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "a",
            Method = CommandNames.PermissionPolicySet,
            Params = JsonSerializer.SerializeToElement(new
            {
                kind = "app",
                processName = "ContosoBank",
                observe = "Allow",
                interact = "Deny"
            }, JsonDefaults.Options)
        }, CancellationToken.None);
        using var appDoc = Doc(app);
        Assert.True(appDoc.RootElement.GetProperty("ok").GetBoolean());

        var path = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "p",
            Method = CommandNames.PermissionPolicySet,
            Params = JsonSerializer.SerializeToElement(new
            {
                kind = "path",
                pathPrefix = Path.GetTempPath(),
                allowRead = true,
                allowWrite = false,
                deny = false
            }, JsonDefaults.Options)
        }, CancellationToken.None);
        using var pathDoc = Doc(path);
        Assert.True(pathDoc.RootElement.GetProperty("ok").GetBoolean());
        var rules = pathDoc.RootElement.GetProperty("data").GetProperty("appRules").EnumerateArray()
            .Any(r => r.GetProperty("processName").GetString() == "ContosoBank");
        Assert.True(rules);
        dispatcher.Dispose();
    }

    [Fact]
    public void ProductBranding_KeepsCompatIds()
    {
        Assert.Equal("DesktopUseAgent", RuntimeCompat.ProductDisplayName);
        Assert.Equal("SemanticDesktop", RuntimeCompat.Product);
        Assert.Equal("semantic-desktop-agent", PipeNames.Default);
    }

    [Fact]
    public async Task SessionCreate_AcceptsApprovalTimeoutSeconds()
    {
        using var dispatcher = new CommandDispatcher();
        var created = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "sc",
            Method = CommandNames.SessionCreate,
            Params = JsonSerializer.SerializeToElement(new
            {
                clientId = "native-chat",
                autoApproveAsk = false,
                approvalTimeoutSeconds = 120
            }, JsonDefaults.Options)
        }, CancellationToken.None);

        using var createdDoc = Doc(created);
        Assert.True(createdDoc.RootElement.GetProperty("ok").GetBoolean());
        var data = createdDoc.RootElement.GetProperty("data");
        Assert.Equal("native-chat", data.GetProperty("clientId").GetString());
        Assert.False(data.GetProperty("autoApproveAsk").GetBoolean());
        Assert.Equal(120, data.GetProperty("approvalTimeoutSeconds").GetInt32());

        var defaulted = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "sc2",
            Method = CommandNames.SessionCreate,
            Params = JsonSerializer.SerializeToElement(new { clientId = "mcp" }, JsonDefaults.Options)
        }, CancellationToken.None);
        using var defaultDoc = Doc(defaulted);
        Assert.True(defaultDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(30, defaultDoc.RootElement.GetProperty("data").GetProperty("approvalTimeoutSeconds").GetInt32());
    }
}
