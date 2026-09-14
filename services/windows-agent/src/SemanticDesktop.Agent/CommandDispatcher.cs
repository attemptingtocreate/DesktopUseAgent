using System.Text.Json;
using SemanticDesktop.Audit;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Handles;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Core.Plans;
using SemanticDesktop.Core.Results;
using SemanticDesktop.Core.Security;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.Core.Targets;
using SemanticDesktop.Execution.Conditions;
using SemanticDesktop.Execution.Plans;
using SemanticDesktop.Files;
using SemanticDesktop.IPC;
using SemanticDesktop.Permissions;
using SemanticDesktop.UIA.Automation;
using SemanticDesktop.Win32.Processes;
using SemanticDesktop.Win32.Windows;

namespace SemanticDesktop.Agent;

public sealed class CommandDispatcher : IDisposable
{
    private readonly HandleRegistry _handles = new();
    private readonly WindowService _windows;
    private readonly ProcessService _processes;
    private readonly UIAutomationService _uia;
    private readonly FileService _files = new();
    private readonly PlanExecutor _plans;
    private readonly ConditionEvaluator _conditions;
    private readonly SecurityContext _security;

    public CommandDispatcher()
    {
        _windows = new WindowService(_handles);
        _processes = new ProcessService(_handles);
        _uia = new UIAutomationService(_handles, _windows);
        _conditions = new ConditionEvaluator(new ConditionProbe(_windows, _uia), _files);
        _plans = new PlanExecutor(_conditions, RunActionAsJsonAsync);
        _security = new SecurityContext
        {
            Engine = new PermissionEngine(),
            Sessions = new SessionManager(),
            Approvals = new ApprovalBroker(),
            Emergency = new EmergencyStopGate(),
            Audit = new AuditLog()
        };
        _ = _security.Sessions.GetOrCreateDefault();
    }

    public async Task<object> DispatchAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var requestId = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id;
        var session = _security.ResolveSession(request.Params);
        PermissionGateResult? gate = null;

        try
        {
            gate = await _security.AuthorizeAsync(session, request.Method, request.Params, cancellationToken)
                .ConfigureAwait(false);
            if (!gate.Ok)
            {
                _security.WriteAudit(
                    session,
                    request.Method,
                    gate.Evaluation?.Decision.ToString() ?? "Deny",
                    Elapsed(started),
                    false,
                    requestId,
                    gate.Evaluation?.Target,
                    gate.ErrorCode);

                return ToolResult<object>.Failure(
                    new ErrorInfo
                    {
                        Code = gate.ErrorCode ?? ErrorCodes.PermissionDenied,
                        Message = gate.ErrorMessage ?? "Permission denied.",
                        Retryable = false,
                        Details = gate.ApprovalId is null
                            ? null
                            : new Dictionary<string, object?> { ["approvalId"] = gate.ApprovalId }
                    },
                    ResultMeta.Create(requestId, started));
            }

            var result = await ExecuteAuthorizedAsync(request, requestId, started, session, cancellationToken)
                .ConfigureAwait(false);

            var success = result is not null && IsOk(result);
            _security.WriteAudit(
                session,
                request.Method,
                gate.Evaluation?.Decision.ToString() ?? "Allow",
                Elapsed(started),
                success,
                requestId,
                gate.Evaluation?.Target,
                success ? null : TryGetErrorCode(result));

            return result!;
        }
        catch (Exception ex)
        {
            _security.WriteAudit(
                session,
                request.Method,
                gate?.Evaluation?.Decision.ToString() ?? "Allow",
                Elapsed(started),
                false,
                requestId,
                gate?.Evaluation?.Target,
                MapException(ex).Code);

            return ToolResult<object>.Failure(
                MapException(ex),
                ResultMeta.Create(requestId, started),
                BuildPerformance(request.Method, started));
        }
    }

    private async Task<object> ExecuteAuthorizedAsync(
        RpcRequest request,
        string requestId,
        DateTimeOffset started,
        AgentSession session,
        CancellationToken cancellationToken)
    {
        return request.Method switch
        {
            CommandNames.WindowList => await WindowListAsync(requestId, started, cancellationToken).ConfigureAwait(false),
            CommandNames.WindowFocus => await WindowFocusAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.UiGetTree => await UiGetTreeAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.UiFind => await UiFindAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.UiInvoke => await UiInvokeAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.UiSetValue => await UiSetValueAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.UiGetText => await UiGetTextAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.ProcessLaunch => await ProcessLaunchAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.FilesystemWriteText => await FilesystemWriteTextAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.FilesystemExists => await FilesystemExistsAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.FilesystemList => await FilesystemListAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.FilesystemReadText => await FilesystemReadTextAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.ProcessList => await ProcessListAsync(requestId, started, cancellationToken).ConfigureAwait(false),
            CommandNames.DesktopGetState => await DesktopGetStateAsync(requestId, started, cancellationToken).ConfigureAwait(false),
            CommandNames.DesktopGetCapabilities => DesktopGetCapabilities(requestId, started),
            CommandNames.WindowWaitFor => await WindowWaitForAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.UiWaitFor => await UiWaitForAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.PlanExecute => await PlanExecuteAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.PlanGet => PlanGet(requestId, started, request.Params),
            CommandNames.PlanCancel => PlanCancel(requestId, started, request.Params),
            CommandNames.SessionCreate => SessionCreate(requestId, started, request.Params),
            CommandNames.SessionGet => SessionGet(requestId, started, request.Params, session),
            CommandNames.PermissionApprove => PermissionResolve(requestId, started, request.Params, allow: true),
            CommandNames.PermissionDeny => PermissionResolve(requestId, started, request.Params, allow: false),
            CommandNames.PermissionPending => PermissionPending(requestId, started),
            CommandNames.AuditList => AuditList(requestId, started, request.Params),
            CommandNames.SystemEmergencyStop => EmergencyStop(requestId, started),
            CommandNames.SystemEmergencyStopClear => EmergencyClear(requestId, started),
            CommandNames.SystemPing => ToolResult<object>.Success(
                new { pong = true },
                ResultMeta.Create(requestId, started),
                new PerformanceMeta
                {
                    Operation = CommandNames.SystemPing,
                    DurationMs = 0,
                    ElementsInspected = 0,
                    CacheHit = true,
                    Provider = "Agent"
                }),
            _ => ToolResult<object>.Failure(
                new ErrorInfo
                {
                    Code = ErrorCodes.Unsupported,
                    Message = $"Unknown method '{request.Method}'.",
                    Retryable = false
                },
                ResultMeta.Create(requestId, started))
        };
    }

    private async Task<JsonElement> RunActionAsJsonAsync(string action, JsonElement args, CancellationToken ct)
    {
        var result = await DispatchAsync(new RpcRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            Method = action,
            Params = args
        }, ct).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, JsonDefaults.Options);
    }

    private async Task<object> WindowListAsync(string requestId, DateTimeOffset started, CancellationToken ct)
    {
        var windows = await _windows.ListAsync(ct).ConfigureAwait(false);
        return ToolResult<IReadOnlyList<WindowInfo>>.Success(
            windows,
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.WindowList,
                DurationMs = Elapsed(started),
                ElementsInspected = windows.Count,
                CacheHit = false,
                Provider = "Win32"
            });
    }

    private async Task<object> WindowFocusAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var req = Deserialize<WindowFocusRequest>(parameters);
        var window = await _windows.FocusAsync(req.WindowId, ct).ConfigureAwait(false);
        if (window is null)
        {
            return ToolResult<WindowInfo>.Failure(
                new ErrorInfo { Code = ErrorCodes.StaleTarget, Message = "Window handle is stale.", Retryable = true },
                ResultMeta.Create(requestId, started),
                BuildPerformance(CommandNames.WindowFocus, started, provider: "Win32"));
        }

        return ToolResult<WindowInfo>.Success(
            window,
            ResultMeta.Create(requestId, started),
            BuildPerformance(CommandNames.WindowFocus, started, provider: "Win32"),
            stateChanged: true);
    }

    private async Task<object> UiGetTreeAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var query = Deserialize<UITreeQuery>(parameters);
        var tree = await _uia.GetTreeAsync(query, ct).ConfigureAwait(false);
        return ToolResult<UITree>.Success(
            tree,
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.UiGetTree,
                DurationMs = Elapsed(started),
                ElementsInspected = _uia.LastElementsInspected,
                CacheHit = _uia.LastCacheHit,
                Provider = "UIA"
            });
    }

    private async Task<object> UiFindAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var query = NormalizeFindQuery(parameters);
        var elements = await _uia.FindAsync(query, ct).ConfigureAwait(false);
        var summaries = elements.Select(e => new UIElementSummary
        {
            Id = e.Id,
            Name = e.Name,
            AutomationId = e.AutomationId,
            ControlType = e.ControlType,
            ClassName = e.ClassName,
            FrameworkId = e.FrameworkId,
            Enabled = e.Enabled,
            Focused = e.Focused,
            Offscreen = e.Offscreen,
            Bounds = e.Bounds,
            SupportedPatterns = e.SupportedPatterns,
            Confidence = elements.Count == 1 ? 1.0 : elements.Count == 0 ? 0 : 1.0 / elements.Count
        }).ToList();

        var payload = new UIFindResult
        {
            Elements = summaries,
            MatchCount = summaries.Count,
            Confidence = summaries.Count == 1 ? 1.0 : summaries.Count == 0 ? 0 : summaries[0].Confidence
        };

        return ToolResult<UIFindResult>.Success(
            payload,
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.UiFind,
                DurationMs = Elapsed(started),
                ElementsInspected = _uia.LastElementsInspected,
                CacheHit = _uia.LastCacheHit,
                Provider = "UIA"
            });
    }

    private async Task<object> UiInvokeAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var req = Deserialize<ElementActionRequest>(parameters);
        var result = await _uia.InvokeAsync(new ElementHandle { Id = req.ElementId }, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return ToolResult<ActionResult>.Failure(
                new ErrorInfo
                {
                    Code = result.Message ?? ErrorCodes.PatternUnavailable,
                    Message = result.Message ?? "Invoke failed.",
                    Retryable = false
                },
                ResultMeta.Create(requestId, started),
                BuildPerformance(CommandNames.UiInvoke, started, 1, _uia.LastCacheHit));
        }

        return ToolResult<ActionResult>.Success(
            result,
            ResultMeta.Create(requestId, started),
            BuildPerformance(CommandNames.UiInvoke, started, 1, _uia.LastCacheHit),
            stateChanged: true);
    }

    private async Task<object> UiSetValueAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var req = Deserialize<SetValueRequest>(parameters);
        var result = await _uia.SetValueAsync(new ElementHandle { Id = req.ElementId }, req.Value, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return ToolResult<ActionResult>.Failure(
                new ErrorInfo
                {
                    Code = result.Message ?? ErrorCodes.PatternUnavailable,
                    Message = result.Message ?? "SetValue failed.",
                    Retryable = false
                },
                ResultMeta.Create(requestId, started),
                BuildPerformance(CommandNames.UiSetValue, started, 1, _uia.LastCacheHit));
        }

        // Never echo submitted values into responses/logs.
        return ToolResult<object>.Success(
            new { success = true, message = result.Message },
            ResultMeta.Create(requestId, started),
            BuildPerformance(CommandNames.UiSetValue, started, 1, _uia.LastCacheHit),
            stateChanged: true);
    }

    private async Task<object> UiGetTextAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var req = Deserialize<ElementActionRequest>(parameters);
        var text = await _uia.GetTextAsync(new ElementHandle { Id = req.ElementId }, ct).ConfigureAwait(false);
        _uia.TryDescribe(req.ElementId, out var controlType, out var name, out var automationId, out var isPassword);
        var sensitive = isPassword || SensitiveRedactor.LooksSensitive(controlType, name, automationId, null);
        return ToolResult<object>.Success(
            new
            {
                text = sensitive ? SensitiveRedactor.Redacted : text,
                sensitive
            },
            ResultMeta.Create(requestId, started),
            BuildPerformance(CommandNames.UiGetText, started, 1, _uia.LastCacheHit));
    }

    private async Task<object> ProcessLaunchAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var req = Deserialize<ProcessLaunchRequest>(parameters);
        var info = await _processes.LaunchAsync(req, ct).ConfigureAwait(false);
        return ToolResult<ProcessInfo>.Success(
            info,
            ResultMeta.Create(requestId, started),
            BuildPerformance(CommandNames.ProcessLaunch, started, provider: "Win32"),
            stateChanged: true);
    }

    private async Task<object> FilesystemWriteTextAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var path = parameters is null ? null : GetString(parameters.Value, "path");
        var contents = parameters is null ? null : GetString(parameters.Value, "contents") ?? GetString(parameters.Value, "value") ?? "";
        var result = await _files.WriteTextAsync(path ?? "", contents ?? "", ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return ToolResult<object>.Failure(
                result.Error!,
                ResultMeta.Create(requestId, started),
                result.Performance);
        }

        return ToolResult<object>.Success(
            result.Data!,
            ResultMeta.Create(requestId, started),
            result.Performance,
            result.StateChanged);
    }

    private async Task<object> FilesystemExistsAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var path = parameters is null ? null : GetString(parameters.Value, "path");
        var result = await _files.ExistsAsync(path ?? "", ct).ConfigureAwait(false);
        return ToolResult<object>.Success(
            result.Data!,
            ResultMeta.Create(requestId, started),
            result.Performance);
    }

    private async Task<object> FilesystemListAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var path = parameters is null ? "." : GetString(parameters.Value, "path") ?? ".";
        var result = await _files.ListAsync(path, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return ToolResult<object>.Failure(result.Error!, ResultMeta.Create(requestId, started), result.Performance);
        }

        return ToolResult<object>.Success(result.Data!, ResultMeta.Create(requestId, started), result.Performance);
    }

    private async Task<object> FilesystemReadTextAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var path = parameters is null ? null : GetString(parameters.Value, "path");
        var result = await _files.ReadTextAsync(path ?? "", ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return ToolResult<object>.Failure(result.Error!, ResultMeta.Create(requestId, started), result.Performance);
        }

        return ToolResult<object>.Success(result.Data!, ResultMeta.Create(requestId, started), result.Performance);
    }

    private async Task<object> ProcessListAsync(string requestId, DateTimeOffset started, CancellationToken ct)
    {
        var processes = await _processes.ListAsync(ct).ConfigureAwait(false);
        return ToolResult<object>.Success(
            processes,
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.ProcessList,
                DurationMs = Elapsed(started),
                ElementsInspected = processes.Count,
                CacheHit = false,
                Provider = "Win32"
            });
    }

    private async Task<object> DesktopGetStateAsync(string requestId, DateTimeOffset started, CancellationToken ct)
    {
        var windows = await _windows.ListAsync(ct).ConfigureAwait(false);
        return ToolResult<object>.Success(
            new
            {
                windows,
                emergencyStopped = _security.Emergency.IsStopped,
                sessionId = _security.Sessions.GetOrCreateDefault().SessionId
            },
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.DesktopGetState,
                DurationMs = Elapsed(started),
                ElementsInspected = windows.Count,
                CacheHit = false,
                Provider = "Agent"
            });
    }

    private object DesktopGetCapabilities(string requestId, DateTimeOffset started)
    {
        return ToolResult<object>.Success(
            new
            {
                uia = true,
                browser = new { chrome = false, edge = false, firefox = false },
                shell = false,
                vision = false,
                plans = true,
                permissions = true,
                audit = true,
                adapters = Array.Empty<string>(),
                tools = new[]
                {
                    CommandNames.DesktopGetState,
                    CommandNames.DesktopGetCapabilities,
                    CommandNames.WindowList,
                    CommandNames.WindowFocus,
                    CommandNames.WindowWaitFor,
                    CommandNames.UiGetTree,
                    CommandNames.UiFind,
                    CommandNames.UiInvoke,
                    CommandNames.UiSetValue,
                    CommandNames.UiGetText,
                    CommandNames.UiWaitFor,
                    CommandNames.ProcessList,
                    CommandNames.ProcessLaunch,
                    CommandNames.FilesystemList,
                    CommandNames.FilesystemReadText,
                    CommandNames.FilesystemWriteText,
                    CommandNames.FilesystemExists,
                    CommandNames.PlanExecute,
                    CommandNames.PlanCancel,
                    CommandNames.SystemEmergencyStop
                }
            },
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.DesktopGetCapabilities,
                DurationMs = Elapsed(started),
                ElementsInspected = 0,
                CacheHit = true,
                Provider = "Agent"
            });
    }

    private async Task<object> WindowWaitForAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var condition = new Core.Plans.Condition
        {
            Type = "window.exists",
            Process = parameters is null ? null : GetString(parameters.Value, "process"),
            TitleContains = parameters is null
                ? null
                : GetString(parameters.Value, "titleContains") ?? GetString(parameters.Value, "title"),
            TimeoutMs = parameters is not null && parameters.Value.TryGetProperty("timeoutMs", out var t) && t.TryGetInt32(out var ms)
                ? ms
                : 15000
        };
        await _conditions.WaitAsync(condition, condition.TimeoutMs ?? 15000, ct).ConfigureAwait(false);
        var windows = await _windows.ListAsync(ct).ConfigureAwait(false);
        var match = windows.FirstOrDefault(w =>
            (condition.Process is null || w.Process.Contains(condition.Process, StringComparison.OrdinalIgnoreCase)) &&
            (condition.TitleContains is null || w.Title.Contains(condition.TitleContains, StringComparison.OrdinalIgnoreCase)));
        return ToolResult<object>.Success(
            new { window = match },
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.WindowWaitFor,
                DurationMs = Elapsed(started),
                ElementsInspected = windows.Count,
                CacheHit = false,
                Provider = "Win32"
            });
    }

    private async Task<object> UiWaitForAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        Dictionary<string, JsonElement>? selector = null;
        if (parameters is not null && parameters.Value.TryGetProperty("selector", out var sel) && sel.ValueKind == JsonValueKind.Object)
        {
            selector = sel.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        }
        else if (parameters is not null)
        {
            selector = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var key in new[] { "name", "nameContains", "automationId", "controlType", "type", "className" })
            {
                var value = GetString(parameters.Value, key);
                if (value is not null)
                {
                    selector[key == "type" ? "controlType" : key] = JsonSerializer.SerializeToElement(value);
                }
            }
        }

        var condition = new Core.Plans.Condition
        {
            Type = "ui.exists",
            WindowId = parameters is null ? null : GetString(parameters.Value, "windowId"),
            Selector = selector,
            TimeoutMs = parameters is not null && parameters.Value.TryGetProperty("timeoutMs", out var t) && t.TryGetInt32(out var ms)
                ? ms
                : 15000
        };
        await _conditions.WaitAsync(condition, condition.TimeoutMs ?? 15000, ct).ConfigureAwait(false);
        return ToolResult<object>.Success(
            new { ready = true },
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.UiWaitFor,
                DurationMs = Elapsed(started),
                ElementsInspected = _uia.LastElementsInspected,
                CacheHit = _uia.LastCacheHit,
                Provider = "UIA"
            });
    }

    private async Task<object> PlanExecuteAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var plan = Deserialize<ExecutionPlan>(parameters);
        var state = await _plans.ExecuteAsync(plan, ct).ConfigureAwait(false);
        var ok = state.Status == PlanStatus.Succeeded;
        if (!ok)
        {
            return ToolResult<PlanExecutionState>.Failure(
                new ErrorInfo
                {
                    Code = state.Error?.Code ?? ErrorCodes.StepFailed,
                    Message = state.Error?.Message ?? $"Plan ended with status '{state.Status}'.",
                    Retryable = state.Error?.Retryable ?? false
                },
                ResultMeta.Create(requestId, started),
                new PerformanceMeta
                {
                    Operation = CommandNames.PlanExecute,
                    DurationMs = Elapsed(started),
                    ElementsInspected = state.Steps.Count,
                    CacheHit = false,
                    Provider = "Execution"
                });
        }

        return ToolResult<PlanExecutionState>.Success(
            state,
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.PlanExecute,
                DurationMs = Elapsed(started),
                ElementsInspected = state.Steps.Count,
                CacheHit = false,
                Provider = "Execution"
            },
            stateChanged: true);
    }

    private object PlanGet(string requestId, DateTimeOffset started, JsonElement? parameters)
    {
        var planId = parameters is null ? null : GetString(parameters.Value, "planId") ?? GetString(parameters.Value, "id");
        if (string.IsNullOrWhiteSpace(planId))
        {
            return ToolResult<PlanExecutionState>.Failure(
                new ErrorInfo { Code = ErrorCodes.InvalidArgument, Message = "planId is required.", Retryable = false },
                ResultMeta.Create(requestId, started));
        }

        var state = _plans.Get(planId);
        if (state is null)
        {
            return ToolResult<PlanExecutionState>.Failure(
                new ErrorInfo { Code = ErrorCodes.NotFound, Message = $"Plan '{planId}' not found.", Retryable = false },
                ResultMeta.Create(requestId, started));
        }

        return ToolResult<PlanExecutionState>.Success(state, ResultMeta.Create(requestId, started));
    }

    private object PlanCancel(string requestId, DateTimeOffset started, JsonElement? parameters)
    {
        var planId = parameters is null ? null : GetString(parameters.Value, "planId") ?? GetString(parameters.Value, "id");
        if (string.IsNullOrWhiteSpace(planId))
        {
            return ToolResult<object>.Failure(
                new ErrorInfo { Code = ErrorCodes.InvalidArgument, Message = "planId is required.", Retryable = false },
                ResultMeta.Create(requestId, started));
        }

        var cancelled = _plans.Cancel(planId);
        return ToolResult<object>.Success(
            new { planId, cancelled },
            ResultMeta.Create(requestId, started),
            stateChanged: cancelled);
    }

    private object SessionCreate(string requestId, DateTimeOffset started, JsonElement? parameters)
    {
        var clientId = parameters is null ? "local" : GetString(parameters.Value, "clientId") ?? "local";
        var autoApprove = parameters is not null &&
                          parameters.Value.TryGetProperty("autoApproveAsk", out var a) &&
                          a.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                          a.GetBoolean();
        var session = _security.Sessions.Create(clientId, autoApprove);
        return ToolResult<object>.Success(
            new { session.SessionId, session.ClientId, session.AutoApproveAsk },
            ResultMeta.Create(requestId, started));
    }

    private object SessionGet(string requestId, DateTimeOffset started, JsonElement? parameters, AgentSession current)
    {
        var sessionId = parameters is null ? current.SessionId : GetString(parameters.Value, "sessionId") ?? current.SessionId;
        var session = _security.Sessions.Get(sessionId!) ?? current;
        return ToolResult<object>.Success(
            new
            {
                session.SessionId,
                session.ClientId,
                session.AutoApproveAsk,
                emergencyStopped = _security.Emergency.IsStopped,
                grants = session.SessionGrants.ToArray()
            },
            ResultMeta.Create(requestId, started));
    }

    private object PermissionResolve(string requestId, DateTimeOffset started, JsonElement? parameters, bool allow)
    {
        var approvalId = parameters is null ? null : GetString(parameters.Value, "approvalId");
        var sessionId = parameters is null ? null : GetString(parameters.Value, "sessionId");
        var scopeText = parameters is null ? "session" : GetString(parameters.Value, "scope") ?? "session";
        if (string.IsNullOrWhiteSpace(approvalId))
        {
            return ToolResult<object>.Failure(
                new ErrorInfo { Code = ErrorCodes.InvalidArgument, Message = "approvalId is required.", Retryable = false },
                ResultMeta.Create(requestId, started));
        }

        var scope = scopeText.ToLowerInvariant() switch
        {
            "once" => PermissionGrantScope.Once,
            "always" => PermissionGrantScope.Always,
            _ => PermissionGrantScope.Session
        };
        var session = string.IsNullOrWhiteSpace(sessionId)
            ? _security.Sessions.GetOrCreateDefault()
            : _security.Sessions.Get(sessionId) ?? _security.Sessions.GetOrCreateDefault();
        var ok = _security.Approvals.Resolve(approvalId, allow, scope, session, _security.Engine);
        return ToolResult<object>.Success(
            new { approvalId, allow, resolved = ok, scope = scope.ToString() },
            ResultMeta.Create(requestId, started),
            stateChanged: ok);
    }

    private object PermissionPending(string requestId, DateTimeOffset started)
    {
        var pending = _security.Approvals.ListPending();
        return ToolResult<object>.Success(pending, ResultMeta.Create(requestId, started));
    }

    private object AuditList(string requestId, DateTimeOffset started, JsonElement? parameters)
    {
        var take = 50;
        if (parameters is not null && parameters.Value.TryGetProperty("take", out var t) && t.TryGetInt32(out var n))
        {
            take = n;
        }

        var sessionId = parameters is null ? null : GetString(parameters.Value, "sessionId");
        return ToolResult<object>.Success(
            _security.Audit.List(take, sessionId),
            ResultMeta.Create(requestId, started));
    }

    private object EmergencyStop(string requestId, DateTimeOffset started)
    {
        _security.Emergency.Stop();
        var cancelledPlans = _plans.CancelAll();
        return ToolResult<object>.Success(
            new { emergencyStopped = true, cancelledPlans },
            ResultMeta.Create(requestId, started),
            stateChanged: true);
    }

    private object EmergencyClear(string requestId, DateTimeOffset started)
    {
        _security.Emergency.Clear();
        return ToolResult<object>.Success(
            new { emergencyStopped = false },
            ResultMeta.Create(requestId, started),
            stateChanged: true);
    }

    private static bool IsOk(object result)
    {
        var json = JsonSerializer.Serialize(result, JsonDefaults.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
    }

    private static string? TryGetErrorCode(object? result)
    {
        if (result is null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(result, JsonDefaults.Options);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("error", out var err) &&
            err.TryGetProperty("code", out var code) &&
            code.ValueKind == JsonValueKind.String)
        {
            return code.GetString();
        }

        return null;
    }

    private static UIFindQuery NormalizeFindQuery(JsonElement? parameters)
    {
        if (parameters is null)
        {
            return new UIFindQuery();
        }

        var raw = parameters.Value;
        if (raw.ValueKind != JsonValueKind.Object)
        {
            return new UIFindQuery();
        }

        // Accept both nested selector and flat CLI-style fields.
        if (raw.TryGetProperty("selector", out _))
        {
            return Deserialize<UIFindQuery>(parameters);
        }

        return new UIFindQuery
        {
            WindowId = GetString(raw, "windowId"),
            RootId = GetString(raw, "rootId"),
            Depth = GetInt(raw, "depth"),
            MaxResults = GetInt(raw, "maxResults"),
            Selector = new UIFindSelector
            {
                Name = GetString(raw, "name"),
                NameContains = GetString(raw, "nameContains"),
                AutomationId = GetString(raw, "automationId"),
                ClassName = GetString(raw, "className"),
                ControlType = GetString(raw, "type") ?? GetString(raw, "controlType"),
                FrameworkId = GetString(raw, "frameworkId"),
                Enabled = GetBool(raw, "enabled"),
                Offscreen = GetBool(raw, "offscreen")
            }
        };
    }

    private static string? GetString(JsonElement raw, string name) =>
        raw.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement raw, string name) =>
        raw.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? GetBool(JsonElement raw, string name) =>
        raw.TryGetProperty(name, out var value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? value.GetBoolean()
            : null;

    private static T Deserialize<T>(JsonElement? parameters)
    {
        if (parameters is null || parameters.Value.ValueKind == JsonValueKind.Null)
        {
            throw new ArgumentException("Parameters are required.");
        }

        return parameters.Value.Deserialize<T>(JsonDefaults.Options)
               ?? throw new ArgumentException("Unable to deserialize parameters.");
    }

    private PerformanceMeta BuildPerformance(
        string operation,
        DateTimeOffset started,
        int? elements = null,
        bool? cacheHit = null,
        string provider = "UIA") =>
        new()
        {
            Operation = operation,
            DurationMs = Elapsed(started),
            ElementsInspected = elements ?? _uia.LastElementsInspected,
            CacheHit = cacheHit ?? _uia.LastCacheHit,
            Provider = provider
        };

    private static long Elapsed(DateTimeOffset started) =>
        (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;

    private static ErrorInfo MapException(Exception ex) =>
        ex switch
        {
            TimeoutException => new ErrorInfo { Code = ErrorCodes.Timeout, Message = ex.Message, Retryable = true },
            InvalidOperationException when ex.Message == ErrorCodes.StaleTarget =>
                new ErrorInfo { Code = ErrorCodes.StaleTarget, Message = "Target is no longer valid.", Retryable = true },
            ArgumentException => new ErrorInfo { Code = ErrorCodes.InvalidArgument, Message = ex.Message, Retryable = false },
            _ => new ErrorInfo { Code = ErrorCodes.Internal, Message = ex.Message, Retryable = false }
        };

    public void Dispose() => _uia.Dispose();
}
