using System.Text.Json;
using SemanticDesktop.Agent;
using SemanticDesktop.App.Tools;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.IPC;

namespace SemanticDesktop.App.Tests;

public class DispatcherAuthorizeTests
{
    [Fact]
    public async Task DirectDispatcherToolRouter_Honors_Deny()
    {
        using var dispatcher = new CommandDispatcher();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "dua-deny-" + Guid.NewGuid().ToString("N"));
        var temp = Path.Combine(dir, "file.txt");
        await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "p",
            Method = CommandNames.PermissionPolicySet,
            Params = JsonSerializer.SerializeToElement(new
            {
                kind = "path",
                pathPrefix = dir,
                allowRead = true,
                allowWrite = false,
                deny = true
            }, JsonDefaults.Options)
        }, CancellationToken.None);

        var created = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "s",
            Method = CommandNames.SessionCreate,
            Params = JsonSerializer.SerializeToElement(new { clientId = "app-tests", autoApproveAsk = false }, JsonDefaults.Options)
        }, CancellationToken.None);
        using var createdDoc = JsonDocument.Parse(JsonSerializer.Serialize(created, JsonDefaults.Options));
        var sessionId = createdDoc.RootElement.GetProperty("data").GetProperty("sessionId").GetString();

        var router = new DirectDispatcherToolRouter(async (method, parameters, ct) =>
        {
            var result = await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                Method = method,
                Params = parameters
            }, ct);
            return JsonSerializer.SerializeToElement(result, JsonDefaults.Options);
        });
        var args = JsonSerializer.SerializeToElement(new { path = "", contents = "nope" }, JsonDefaults.Options);
        var result = await router.InvokeAsync(
            CommandNames.FilesystemWriteText,
            args,
            new ToolRouterContext { SessionId = sessionId, AllowComputerControl = true },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Deny", result.PermissionDecision);
    }

    [Fact]
    public async Task DirectDispatcherToolRouter_Ask_Continues_After_Approve()
    {
        using var dispatcher = new CommandDispatcher();
        await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "p",
            Method = CommandNames.PermissionPolicySet,
            Params = JsonSerializer.SerializeToElement(new
            {
                kind = "capability",
                capability = "filesystem.write",
                decision = "Ask"
            }, JsonDefaults.Options)
        }, CancellationToken.None);

        var created = await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "s",
            Method = CommandNames.SessionCreate,
            Params = JsonSerializer.SerializeToElement(new { clientId = "app-tests", autoApproveAsk = false }, JsonDefaults.Options)
        }, CancellationToken.None);
        using var createdDoc = JsonDocument.Parse(JsonSerializer.Serialize(created, JsonDefaults.Options));
        var sessionId = createdDoc.RootElement.GetProperty("data").GetProperty("sessionId").GetString();

        var router = new DirectDispatcherToolRouter(async (method, parameters, ct) =>
        {
            var result = await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                Method = method,
                Params = parameters
            }, ct);
            return JsonSerializer.SerializeToElement(result, JsonDefaults.Options);
        });

        var temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "dua-ask-" + Guid.NewGuid().ToString("N") + ".txt");
        var args = JsonSerializer.SerializeToElement(new { path = temp, contents = "hello" }, JsonDefaults.Options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var invoke = router.InvokeAsync(
            CommandNames.FilesystemWriteText,
            args,
            new ToolRouterContext { SessionId = sessionId, AllowComputerControl = true },
            cts.Token);

        string? approvalId = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (approvalId is null && DateTime.UtcNow < deadline)
        {
            var pending = await dispatcher.DispatchAsync(new RpcRequest
            {
                Id = "pend",
                Method = CommandNames.PermissionPending,
                Params = JsonSerializer.SerializeToElement(new { }, JsonDefaults.Options)
            }, CancellationToken.None);
            using var pendDoc = JsonDocument.Parse(JsonSerializer.Serialize(pending, JsonDefaults.Options));
            var data = pendDoc.RootElement.GetProperty("data");
            if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
            {
                approvalId = data[0].GetProperty("id").GetString();
                break;
            }

            await Task.Delay(50);
        }

        Assert.False(string.IsNullOrWhiteSpace(approvalId));
        await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "appr",
            Method = CommandNames.PermissionApprove,
            Params = JsonSerializer.SerializeToElement(new { approvalId, sessionId, scope = "once" }, JsonDefaults.Options)
        }, CancellationToken.None);

        var result = await invoke;
        Assert.True(result.Success);
        Assert.True(File.Exists(temp));
        File.Delete(temp);
    }

    [Fact]
    public async Task AgentToolRouter_Does_Not_Skip_Dispatcher_Authorize()
    {
        using var dispatcher = new CommandDispatcher();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "dua-bypass-" + Guid.NewGuid().ToString("N"));
        var temp = Path.Combine(dir, "file.txt");
        await dispatcher.DispatchAsync(new RpcRequest
        {
            Id = "p",
            Method = CommandNames.PermissionPolicySet,
            Params = JsonSerializer.SerializeToElement(new
            {
                kind = "path",
                pathPrefix = dir,
                allowRead = true,
                allowWrite = false,
                deny = true
            }, JsonDefaults.Options)
        }, CancellationToken.None);

        IAgentRpc rpc = new DispatcherAgentRpc(dispatcher);
        var router = new AgentToolRouter(rpc);
        var args = JsonSerializer.SerializeToElement(new { path = "", contents = "x" }, JsonDefaults.Options);
        var result = await router.InvokeAsync(
            CommandNames.FilesystemWriteText,
            args,
            new ToolRouterContext { AllowComputerControl = true },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Deny", result.PermissionDecision);
    }

    private sealed class DispatcherAgentRpc : IAgentRpc
    {
        private readonly CommandDispatcher _dispatcher;

        public DispatcherAgentRpc(CommandDispatcher dispatcher) => _dispatcher = dispatcher;

        public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            JsonElement? paramEl = parameters switch
            {
                JsonElement el => el,
                null => JsonSerializer.SerializeToElement(new { }, JsonDefaults.Options),
                _ => JsonSerializer.SerializeToElement(parameters, JsonDefaults.Options)
            };
            var result = await _dispatcher.DispatchAsync(new RpcRequest
            {
                Id = Guid.NewGuid().ToString("N"),
                Method = method,
                Params = paramEl
            }, cancellationToken);
            return JsonSerializer.SerializeToElement(result, JsonDefaults.Options);
        }
    }
}
