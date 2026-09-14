using System.Text.Json;
using SemanticDesktop.Agent;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.IPC;

namespace SemanticDesktop.Integration.Tests;

public class PermissionAuditIntegrationTests
{
    [Fact]
    public async Task DeniedWindowsWrite_IsBlockedAndAudited()
    {
        using var dispatcher = new CommandDispatcher();
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "sd-should-fail.txt");
        var result = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "perm1",
            Method = CommandNames.FilesystemWriteText,
            Params = JsonSerializer.SerializeToElement(new { path, contents = "nope" }, JsonDefaults.Options)
        }, CancellationToken.None);

        var json = JsonSerializer.Serialize(result, JsonDefaults.Options);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.PathNotAllowed, doc.RootElement.GetProperty("error").GetProperty("code").GetString());

        var audit = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "aud1",
            Method = CommandNames.AuditList,
            Params = JsonSerializer.SerializeToElement(new { take = 20 }, JsonDefaults.Options)
        }, CancellationToken.None);
        var auditJson = JsonSerializer.Serialize(audit, JsonDefaults.Options);
        using var auditDoc = JsonDocument.Parse(auditJson);
        Assert.True(auditDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains(auditDoc.RootElement.GetProperty("data").EnumerateArray(),
            e => e.GetProperty("action").GetString() == CommandNames.FilesystemWriteText &&
                 e.GetProperty("success").GetBoolean() == false);
    }

    [Fact]
    public async Task EmergencyStop_BlocksActions_UntilCleared()
    {
        using var dispatcher = new CommandDispatcher();
        var stop = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "stop",
            Method = CommandNames.SystemEmergencyStop,
            Params = null
        }, CancellationToken.None);
        Assert.True(IsOk(stop));

        var blocked = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "list",
            Method = CommandNames.WindowList,
            Params = null
        }, CancellationToken.None);
        var blockedJson = JsonSerializer.Serialize(blocked, JsonDefaults.Options);
        using var blockedDoc = JsonDocument.Parse(blockedJson);
        Assert.False(blockedDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(ErrorCodes.EmergencyStopped, blockedDoc.RootElement.GetProperty("error").GetProperty("code").GetString());

        var clear = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "clear",
            Method = CommandNames.SystemEmergencyStopClear,
            Params = null
        }, CancellationToken.None);
        Assert.True(IsOk(clear));

        var allowed = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "list2",
            Method = CommandNames.WindowList,
            Params = null
        }, CancellationToken.None);
        Assert.True(IsOk(allowed));
    }

    private static bool IsOk(object result)
    {
        var json = JsonSerializer.Serialize(result, JsonDefaults.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("ok").GetBoolean();
    }
}
