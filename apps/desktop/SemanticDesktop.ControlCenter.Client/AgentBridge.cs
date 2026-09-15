using System.Diagnostics;
using System.Text.Json;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.IPC;

namespace SemanticDesktop.ControlCenter.Client;

/// <summary>
/// Thin IPC facade for the WinUI control center. All automation/permission mutations
/// go through the agent named-pipe boundary — never bypassed locally.
/// </summary>
public sealed class AgentBridge
{
    private readonly string _pipeName;
    private readonly string? _agentProjectOrDll;

    public AgentBridge(string? pipeName = null, string? agentProjectOrDll = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeNames.Default : pipeName;
        _agentProjectOrDll = agentProjectOrDll;
    }

    public string PipeName => _pipeName;

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await NamedPipeClient.CallOnceAsync(
                CommandNames.SystemPing,
                new { },
                cancellationToken,
                _pipeName,
                connectTimeoutMs: 800,
                responseTimeoutMs: 2000).ConfigureAwait(false);
            return result.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    public async Task EnsureAgentAsync(CancellationToken cancellationToken = default)
    {
        if (await PingAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_agentProjectOrDll))
        {
            throw new InvalidOperationException("Agent is not running and no launch path was configured.");
        }

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (_agentProjectOrDll.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(_agentProjectOrDll);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add($"--pipe={_pipeName}");
        }
        else
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add(_agentProjectOrDll);
            psi.ArgumentList.Add($"--pipe={_pipeName}");
        }

        _ = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start agent process.");

        for (var i = 0; i < 40; i++)
        {
            if (await PingAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for agent to become ready.");
    }

    public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) =>
        NamedPipeClient.CallOnceAsync(method, parameters ?? new { }, cancellationToken, _pipeName);

    public async Task<ControlCenterSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var connected = await PingAsync(cancellationToken).ConfigureAwait(false);
        if (!connected)
        {
            return ControlCenterSnapshot.Disconnected(_pipeName);
        }

        var status = await CallAsync(CommandNames.SystemStatus, new { }, cancellationToken).ConfigureAwait(false);
        var sessions = await CallAsync(CommandNames.SessionList, new { }, cancellationToken).ConfigureAwait(false);
        var pending = await CallAsync(CommandNames.PermissionPending, new { }, cancellationToken).ConfigureAwait(false);
        var audit = await CallAsync(CommandNames.AuditList, new { take = 40 }, cancellationToken).ConfigureAwait(false);
        var policy = await CallAsync(CommandNames.PermissionPolicyGet, new { }, cancellationToken).ConfigureAwait(false);

        return ControlCenterSnapshot.FromRpc(_pipeName, status, sessions, pending, audit, policy);
    }
}

public sealed class ControlCenterSnapshot
{
    public required bool Connected { get; init; }
    public required string PipeName { get; init; }
    public bool EmergencyStopped { get; init; }
    public int PendingApprovals { get; init; }
    public int SessionCount { get; init; }
    public IReadOnlyList<SessionInfo> Sessions { get; init; } = Array.Empty<SessionInfo>();
    public IReadOnlyList<ApprovalInfo> Approvals { get; init; } = Array.Empty<ApprovalInfo>();
    public IReadOnlyList<AuditInfo> AuditEntries { get; init; } = Array.Empty<AuditInfo>();
    public IReadOnlyList<CapabilityInfo> Capabilities { get; init; } = Array.Empty<CapabilityInfo>();
    public IReadOnlyList<AppRuleInfo> AppRules { get; init; } = Array.Empty<AppRuleInfo>();
    public IReadOnlyList<PathRuleInfo> PathRules { get; init; } = Array.Empty<PathRuleInfo>();
    public bool McpSessionPresent { get; init; }

    public static ControlCenterSnapshot Disconnected(string pipeName) => new()
    {
        Connected = false,
        PipeName = pipeName
    };

    public static ControlCenterSnapshot FromRpc(
        string pipeName,
        JsonElement statusResult,
        JsonElement sessionsResult,
        JsonElement pendingResult,
        JsonElement auditResult,
        JsonElement policyResult)
    {
        var status = UnwrapData(statusResult);
        var sessions = UnwrapArray(sessionsResult);
        var pending = UnwrapArray(pendingResult);
        var audit = UnwrapArray(auditResult);
        var policy = UnwrapData(policyResult);

        var sessionInfos = sessions.Select(SessionInfo.Parse).Where(s => s is not null).Cast<SessionInfo>().ToList();
        var mcp = sessionInfos.Any(s =>
            s.ClientId.Contains("mcp", StringComparison.OrdinalIgnoreCase));

        return new ControlCenterSnapshot
        {
            Connected = true,
            PipeName = pipeName,
            EmergencyStopped = status.TryGetProperty("emergencyStopped", out var es) && es.GetBoolean(),
            PendingApprovals = status.TryGetProperty("pendingApprovals", out var pa) && pa.TryGetInt32(out var pan) ? pan : pending.Count,
            SessionCount = status.TryGetProperty("sessionCount", out var sc) && sc.TryGetInt32(out var scn) ? scn : sessionInfos.Count,
            Sessions = sessionInfos,
            Approvals = pending.Select(ApprovalInfo.Parse).Where(a => a is not null).Cast<ApprovalInfo>().ToList(),
            AuditEntries = audit.Select(AuditInfo.Parse).Where(a => a is not null).Cast<AuditInfo>().ToList(),
            Capabilities = policy.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array
                ? caps.EnumerateArray().Select(CapabilityInfo.Parse).Where(c => c is not null).Cast<CapabilityInfo>().ToList()
                : Array.Empty<CapabilityInfo>(),
            AppRules = policy.TryGetProperty("appRules", out var apps) && apps.ValueKind == JsonValueKind.Array
                ? apps.EnumerateArray().Select(AppRuleInfo.Parse).Where(a => a is not null).Cast<AppRuleInfo>().ToList()
                : Array.Empty<AppRuleInfo>(),
            PathRules = policy.TryGetProperty("pathRules", out var paths) && paths.ValueKind == JsonValueKind.Array
                ? paths.EnumerateArray().Select(PathRuleInfo.Parse).Where(p => p is not null).Cast<PathRuleInfo>().ToList()
                : Array.Empty<PathRuleInfo>(),
            McpSessionPresent = mcp
        };
    }

    private static JsonElement UnwrapData(JsonElement result)
    {
        if (result.TryGetProperty("data", out var data))
        {
            return data;
        }

        return result;
    }

    private static List<JsonElement> UnwrapArray(JsonElement result)
    {
        var data = UnwrapData(result);
        if (data.ValueKind != JsonValueKind.Array)
        {
            return new List<JsonElement>();
        }

        return data.EnumerateArray().Select(e => e.Clone()).ToList();
    }
}

public sealed record SessionInfo(string SessionId, string ClientId, bool AutoApproveAsk, string[] Grants)
{
    public static SessionInfo? Parse(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = el.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
        var client = el.TryGetProperty("clientId", out var c) ? c.GetString() : null;
        if (id is null || client is null)
        {
            return null;
        }

        var auto = el.TryGetProperty("autoApproveAsk", out var a) && a.ValueKind == JsonValueKind.True;
        var grants = el.TryGetProperty("grants", out var g) && g.ValueKind == JsonValueKind.Array
            ? g.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
            : Array.Empty<string>();
        return new SessionInfo(id, client, auto, grants);
    }
}

public sealed record ApprovalInfo(string Id, string SessionId, string Action, string Capability, string Risk, string? Target, string? Application, string? Reason)
{
    public static ApprovalInfo? Parse(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = el.TryGetProperty("id", out var i) ? i.GetString() : null;
        if (id is null)
        {
            return null;
        }

        return new ApprovalInfo(
            id,
            el.TryGetProperty("sessionId", out var s) ? s.GetString() ?? "" : "",
            el.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "",
            el.TryGetProperty("capability", out var c) ? c.GetString() ?? "" : "",
            el.TryGetProperty("risk", out var r) ? r.ToString() : "",
            el.TryGetProperty("target", out var t) ? t.GetString() : null,
            el.TryGetProperty("application", out var ap) ? ap.GetString() : null,
            el.TryGetProperty("reason", out var re) ? re.GetString() : null);
    }
}

public sealed record AuditInfo(string Id, string Timestamp, string Client, string SessionId, string Action, bool Success, string Decision, string? ErrorCode)
{
    public static AuditInfo? Parse(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = el.TryGetProperty("id", out var i) ? i.GetString() : null;
        if (id is null)
        {
            return null;
        }

        return new AuditInfo(
            id,
            el.TryGetProperty("timestamp", out var ts) ? ts.ToString() : "",
            el.TryGetProperty("client", out var c) ? c.GetString() ?? "" : "",
            el.TryGetProperty("sessionId", out var s) ? s.GetString() ?? "" : "",
            el.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "",
            el.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True,
            el.TryGetProperty("permissionDecision", out var d) ? d.GetString() ?? "" : "",
            el.TryGetProperty("errorCode", out var e) ? e.GetString() : null);
    }
}

public sealed record CapabilityInfo(string Capability, string Decision)
{
    public static CapabilityInfo? Parse(JsonElement el)
    {
        var cap = el.TryGetProperty("capability", out var c) ? c.GetString() : null;
        var dec = el.TryGetProperty("decision", out var d) ? d.GetString() : null;
        return cap is null || dec is null ? null : new CapabilityInfo(cap, dec);
    }
}

public sealed record AppRuleInfo(string ProcessName, string Observe, string Interact)
{
    public static AppRuleInfo? Parse(JsonElement el)
    {
        var name = el.TryGetProperty("processName", out var n) ? n.GetString() : null;
        if (name is null)
        {
            return null;
        }

        return new AppRuleInfo(
            name,
            el.TryGetProperty("observe", out var o) ? o.GetString() ?? "Allow" : "Allow",
            el.TryGetProperty("interact", out var i) ? i.GetString() ?? "Allow" : "Allow");
    }
}

public sealed record PathRuleInfo(string PathPrefix, bool AllowRead, bool AllowWrite, bool Deny)
{
    public static PathRuleInfo? Parse(JsonElement el)
    {
        var path = el.TryGetProperty("pathPrefix", out var p) ? p.GetString() : null;
        if (path is null)
        {
            return null;
        }

        return new PathRuleInfo(
            path,
            !el.TryGetProperty("allowRead", out var r) || r.ValueKind != JsonValueKind.False,
            el.TryGetProperty("allowWrite", out var w) && w.ValueKind == JsonValueKind.True,
            el.TryGetProperty("deny", out var d) && d.ValueKind == JsonValueKind.True);
    }
}
