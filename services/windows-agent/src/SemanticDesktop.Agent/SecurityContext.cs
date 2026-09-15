using System.Text.Json;
using SemanticDesktop.Audit;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Security;
using SemanticDesktop.Permissions;

namespace SemanticDesktop.Agent;

internal sealed class SecurityContext
{
    public required PermissionEngine Engine { get; init; }
    public required SessionManager Sessions { get; init; }
    public required ApprovalBroker Approvals { get; init; }
    public required EmergencyStopGate Emergency { get; init; }
    public required AuditLog Audit { get; init; }

    public static readonly HashSet<string> EmergencyExemptions = new(StringComparer.OrdinalIgnoreCase)
    {
        CommandNames.SystemPing,
        CommandNames.SystemStatus,
        CommandNames.SystemEmergencyStopClear,
        CommandNames.PermissionApprove,
        CommandNames.PermissionDeny,
        CommandNames.PermissionPending,
        CommandNames.PermissionPolicyGet,
        CommandNames.PermissionPolicySet,
        CommandNames.AuditList,
        CommandNames.SessionGet,
        CommandNames.SessionList,
        CommandNames.SystemUpdateCheck,
        CommandNames.SystemUpdateApply,
        CommandNames.SystemTelemetryGet,
        CommandNames.SystemTelemetrySet,
        CommandNames.SystemSecurityReview,
        CommandNames.SystemIntegrity
    };

    public AgentSession ResolveSession(JsonElement? parameters)
    {
        var sessionId = GetString(parameters, "sessionId");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return Sessions.Get(sessionId) ?? Sessions.GetOrCreateDefault();
        }

        return Sessions.GetOrCreateDefault();
    }

    public async Task<PermissionGateResult> AuthorizeAsync(
        AgentSession session,
        string action,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        if (Emergency.IsStopped && !EmergencyExemptions.Contains(action))
        {
            return PermissionGateResult.Denied(ErrorCodes.EmergencyStopped, "Emergency stop is active.");
        }

        if (action is CommandNames.SystemPing
            or CommandNames.SessionCreate or CommandNames.SessionGet or CommandNames.SessionList
            or CommandNames.PermissionApprove or CommandNames.PermissionDeny or CommandNames.PermissionPending
            or CommandNames.PermissionPolicyGet or CommandNames.PermissionPolicySet
            or CommandNames.AuditList
            or CommandNames.SystemStatus
            or CommandNames.SystemEmergencyStop or CommandNames.SystemEmergencyStopClear
            or CommandNames.SystemUpdateCheck or CommandNames.SystemUpdateApply
            or CommandNames.SystemTelemetryGet or CommandNames.SystemTelemetrySet
            or CommandNames.SystemSecurityReview or CommandNames.SystemIntegrity)
        {
            return PermissionGateResult.Allowed(new PermissionEvaluation
            {
                Capability = Capabilities.DesktopObserve,
                Decision = PermissionDecisionKind.Allow,
                Risk = RiskClass.Read,
                Reason = "Control plane."
            });
        }

        var path = GetString(parameters, "path");
        var application = GetString(parameters, "application") ?? GetString(parameters, "process");
        var evaluation = Engine.Evaluate(session, action, path, application);

        if (evaluation.Decision == PermissionDecisionKind.Allow)
        {
            return PermissionGateResult.Allowed(evaluation);
        }

        if (evaluation.Decision == PermissionDecisionKind.Deny)
        {
            var code = evaluation.Reason?.Contains("Path", StringComparison.OrdinalIgnoreCase) == true
                ? ErrorCodes.PathNotAllowed
                : ErrorCodes.PermissionDenied;
            return PermissionGateResult.Denied(code, evaluation.Reason ?? "Denied by policy.", evaluation);
        }

        if (session.AutoApproveAsk)
        {
            Engine.Grant(session, evaluation.Capability, PermissionGrantScope.Session);
            return PermissionGateResult.Allowed(AsAllow(evaluation));
        }

        var approval = Approvals.Create(session, action, evaluation);
        var allowed = await Approvals.WaitAsync(approval.Id, session.ApprovalTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!allowed)
        {
            var status = approval.Status == "timeout" ? ErrorCodes.ApprovalTimeout : ErrorCodes.ApprovalDenied;
            return PermissionGateResult.Denied(status, $"Approval '{approval.Id}' was not granted.", evaluation, approval.Id);
        }

        return PermissionGateResult.Allowed(AsAllow(evaluation), approval.Id);
    }

    public void WriteAudit(
        AgentSession session,
        string action,
        string decision,
        long durationMs,
        bool success,
        string? requestId,
        string? target,
        string? errorCode)
    {
        Audit.Record(new AuditEntry
        {
            Id = "aud_" + Guid.NewGuid().ToString("N")[..12],
            Timestamp = DateTimeOffset.UtcNow,
            Client = session.ClientId,
            SessionId = session.SessionId,
            Action = action,
            Target = target,
            PermissionDecision = decision,
            DurationMs = durationMs,
            Success = success,
            ErrorCode = errorCode,
            RequestId = requestId
        });
    }

    private static PermissionEvaluation AsAllow(PermissionEvaluation evaluation) => new()
    {
        Capability = evaluation.Capability,
        Decision = PermissionDecisionKind.Allow,
        Risk = evaluation.Risk,
        Target = evaluation.Target,
        Application = evaluation.Application,
        Reason = evaluation.Reason
    };

    private static string? GetString(JsonElement? parameters, string name)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return parameters.Value.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

internal sealed class PermissionGateResult
{
    public bool Ok { get; init; }
    public PermissionEvaluation? Evaluation { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ApprovalId { get; init; }

    public static PermissionGateResult Allowed(PermissionEvaluation evaluation, string? approvalId = null) => new()
    {
        Ok = true,
        Evaluation = evaluation,
        ApprovalId = approvalId
    };

    public static PermissionGateResult Denied(
        string code,
        string message,
        PermissionEvaluation? evaluation = null,
        string? approvalId = null) => new()
    {
        Ok = false,
        ErrorCode = code,
        ErrorMessage = message,
        Evaluation = evaluation,
        ApprovalId = approvalId
    };
}
