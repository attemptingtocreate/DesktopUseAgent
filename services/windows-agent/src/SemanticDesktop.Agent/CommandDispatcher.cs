using System.Text.Json;
using SemanticDesktop.Adapters;
using SemanticDesktop.Adapters.Blender;
using SemanticDesktop.Adapters.RobloxStudio;
using SemanticDesktop.Adapters.VisualStudio;
using SemanticDesktop.Adapters.VsCode;
using SemanticDesktop.Audit;
using SemanticDesktop.Browser;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Handles;
using SemanticDesktop.Core.Metrics;
using SemanticDesktop.Core.Models;
using SemanticDesktop.Core.Plans;
using SemanticDesktop.Core.Production;
using SemanticDesktop.Core.Results;
using SemanticDesktop.Core.Security;
using SemanticDesktop.Core.Serialization;
using SemanticDesktop.Core.Targets;
using SemanticDesktop.Core.Workflow;
using SemanticDesktop.Execution.Conditions;
using SemanticDesktop.Execution.Plans;
using SemanticDesktop.Files;
using SemanticDesktop.IPC;
using SemanticDesktop.Permissions;
using SemanticDesktop.UIA.Automation;
using SemanticDesktop.Win32.Input;
using SemanticDesktop.Win32.Monitors;
using SemanticDesktop.Win32.Processes;
using SemanticDesktop.Win32.Vision;
using SemanticDesktop.Win32.Windows;

namespace SemanticDesktop.Agent;

public sealed class CommandDispatcher : IDisposable
{
    private readonly HandleRegistry _handles = new();
    private readonly MonitorService _monitors;
    private readonly WindowService _windows;
    private readonly ProcessService _processes;
    private readonly UIAutomationService _uia;
    private readonly FileService _files = new();
    private readonly BrowserService _browser;
    private readonly PlanExecutor _plans;
    private readonly ConditionEvaluator _conditions;
    private readonly SecurityContext _security;
    private readonly AdapterRegistry _adapters;
    private readonly IRobloxBridge _robloxBridge;
    private readonly RobloxStudioAdapter _robloxAdapter;
    private readonly RobloxOpenPlaceCoordinator _robloxOpenPlace;
    private readonly IBlenderBridge _blenderBridge;
    private readonly BlenderAdapter _blenderAdapter;
    private readonly InputService _input = new();
    private readonly IVisionCaptureProvider _vision;
    private readonly DesktopGraphService _graph;
    private readonly SemanticCache _semanticCache = new();
    private readonly OperationTimingStore _operationTimings = new();
    private readonly DesktopEventHub _events = new();
    private readonly ProductionRuntime _prod;

    public CommandDispatcher(string? dataRoot = null)
    {
        _prod = ProductionRuntime.Create(dataRoot);
        _monitors = new MonitorService();
        _windows = new WindowService(_handles, _monitors);
        _vision = new GdiVisionCaptureProvider(_monitors);
        _processes = new ProcessService(_handles);
        _uia = new UIAutomationService(_handles, _windows);
        _browser = new BrowserService(_handles);
        _graph = new DesktopGraphService(_windows, _uia);
        _conditions = new ConditionEvaluator(new ConditionProbe(_windows, _uia), _files);
        _plans = new PlanExecutor(_conditions, RunActionAsJsonAsync);
        var robloxToken = new RobloxBridgeTokenStore(_prod.DataRoot).GetOrCreate();
        _robloxBridge = new RobloxStudioBridge(new RobloxBridgeOptions { Token = robloxToken });
        _robloxBridge.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _robloxAdapter = new RobloxStudioAdapter(_robloxBridge);
        _robloxOpenPlace = new RobloxOpenPlaceCoordinator(_windows);
        var blenderToken = new BlenderBridgeTokenStore(_prod.DataRoot).GetOrCreate();
        _blenderBridge = new BlenderStudioBridge(new BlenderBridgeOptions { Token = blenderToken });
        _blenderBridge.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _blenderAdapter = new BlenderAdapter(_blenderBridge);
        _adapters = new AdapterRegistry(new IApplicationAdapter[]
        {
            _blenderAdapter,
            new VsCodeAdapter(),
            new VisualStudioAdapter(),
            _robloxAdapter
        });
        _adapters.SetProcessResolver((processId, windowId) => _windows.TryResolveProcess(processId, windowId));
        _security = new SecurityContext
        {
            Engine = _prod.Engine,
            Sessions = new SessionManager(),
            Approvals = new ApprovalBroker(),
            Emergency = new EmergencyStopGate(),
            Audit = _prod.Audit,
            Windows = _windows
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

                RecordOperationTiming(request.Method, started, success: false);
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

            if (ReadSafety.IsCacheable(request.Method) &&
                GetBoolParam(request.Params, "forceRefresh") != true &&
                _semanticCache.TryGet(request.Method, request.Params, out var cachedJson))
            {
                var hit = CacheHitResult(cachedJson, request.Method, requestId, started);
                RecordOperationTiming(request.Method, started, success: true);
                _security.WriteAudit(
                    session,
                    request.Method,
                    gate.Evaluation?.Decision.ToString() ?? "Allow",
                    Elapsed(started),
                    true,
                    requestId,
                    gate.Evaluation?.Target,
                    null);
                return hit;
            }

            if (FaultInjector.ShouldFail(request.Method))
            {
                throw new InvalidOperationException("Injected fault.");
            }

            var result = await ExecuteAuthorizedAsync(request, requestId, started, session, cancellationToken)
                .ConfigureAwait(false);

            var success = result is not null && IsOk(result);
            RecordOperationTiming(request.Method, started, success);
            _prod.RecordTelemetry(success, request.Method);
            if (success)
            {
                if (ReadSafety.IsCacheable(request.Method))
                {
                    _semanticCache.Set(request.Method, request.Params, result!);
                }

                if (HasStateChanged(result) || ReadSafety.IsMutation(request.Method))
                {
                    _semanticCache.Invalidate();
                    _events.Publish(new DesktopEvent
                    {
                        Id = "evt_" + Guid.NewGuid().ToString("N")[..12],
                        Type = "state.changed",
                        Data = new Dictionary<string, object?> { ["method"] = request.Method }
                    });
                }
            }

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
            RecordOperationTiming(request.Method, started, success: false);
            _prod.RecordCrash(ex, recovered: true);
            _security.WriteAudit(
                session,
                request.Method,
                gate?.Evaluation?.Decision.ToString() ?? "Allow",
                Elapsed(started),
                false,
                requestId,
                gate?.Evaluation?.Target,
                MapException(ex).Code);

            var mapped = MapException(ex);
            return ToolResult<object>.Failure(
                new ErrorInfo
                {
                    Code = mapped.Code,
                    Message = mapped.Message,
                    Retryable = mapped.Retryable,
                    Details = new Dictionary<string, object?> { ["recovered"] = true }
                },
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
            CommandNames.MonitorList => MonitorListAsync(requestId, started),
            CommandNames.WindowList => await WindowListAsync(requestId, started, cancellationToken).ConfigureAwait(false),
            CommandNames.WindowGet => await WindowGetAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.WindowFocus => await WindowFocusAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.WindowMinimize => await WindowMinimizeAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.WindowMaximize => await WindowMaximizeAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.WindowRestore => await WindowRestoreAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.WindowMove => await WindowMoveAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.WindowResize => await WindowResizeAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
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
            CommandNames.DesktopDescribe => await DesktopDescribeAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.DesktopGetGraph => await DesktopGetGraphAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.DesktopBatch => await DesktopBatchAsync(requestId, started, request.Params, session, cancellationToken).ConfigureAwait(false),
            CommandNames.DesktopDiff => await DesktopDiffAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.EventsSubscribe => EventsSubscribe(requestId, started, request.Params),
            CommandNames.EventsPoll => await EventsPollAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.EventsUnsubscribe => EventsUnsubscribe(requestId, started, request.Params),
            CommandNames.FilesystemStat => await FilesystemStatAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.FilesystemInspect => await FilesystemInspectAsync(requestId, started, request.Params, session, cancellationToken).ConfigureAwait(false),
            CommandNames.WindowWaitFor => await WindowWaitForAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.UiWaitFor => await UiWaitForAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.PlanExecute => await PlanExecuteAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.PlanGet => PlanGet(requestId, started, request.Params),
            CommandNames.PlanCancel => PlanCancel(requestId, started, request.Params),
            CommandNames.SessionCreate => SessionCreate(requestId, started, request.Params),
            CommandNames.SessionGet => SessionGet(requestId, started, request.Params, session),
            CommandNames.SessionList => SessionList(requestId, started),
            CommandNames.PermissionApprove => PermissionResolve(requestId, started, request.Params, allow: true),
            CommandNames.PermissionDeny => PermissionResolve(requestId, started, request.Params, allow: false),
            CommandNames.PermissionPending => PermissionPending(requestId, started),
            CommandNames.PermissionPolicyGet => PermissionPolicyGet(requestId, started),
            CommandNames.PermissionPolicySet => PermissionPolicySet(requestId, started, request.Params),
            CommandNames.AuditList => AuditList(requestId, started, request.Params),
            CommandNames.SystemStatus => SystemStatus(requestId, started),
            CommandNames.SystemPerformance => SystemPerformance(requestId, started),
            CommandNames.SystemEmergencyStop => EmergencyStop(requestId, started),
            CommandNames.SystemEmergencyStopClear => EmergencyClear(requestId, started),
            CommandNames.AdapterList => await AdapterListAsync(requestId, started, cancellationToken).ConfigureAwait(false),
            CommandNames.AdapterCapabilities => await AdapterCapabilitiesAsync(requestId, started, request.Params, cancellationToken).ConfigureAwait(false),
            CommandNames.AdapterExecute
                or CommandNames.BlenderOpen or CommandNames.BlenderGetScene or CommandNames.BlenderGetObjects
                or CommandNames.BlenderSelectObject or CommandNames.BlenderExecutePython or CommandNames.BlenderExport or CommandNames.BlenderSave
                or CommandNames.BlenderBatch or CommandNames.BlenderRender or CommandNames.BlenderImportMesh
                or CommandNames.VsCodeOpenFile or CommandNames.VsCodeOpenFolder or CommandNames.VsCodeExecuteCommand or CommandNames.VsCodeGetWorkspace
                or CommandNames.VisualStudioGetSolution or CommandNames.VisualStudioBuild
                or CommandNames.VisualStudioOpenFile or CommandNames.VisualStudioOpenSolution
                or CommandNames.RobloxOpenPlace or CommandNames.RobloxPluginPing or CommandNames.RobloxGetHierarchy
                or CommandNames.RobloxGetSelection or CommandNames.RobloxSelect or CommandNames.RobloxSetProperty
                => await AdapterExecuteAsync(request, requestId, started, cancellationToken).ConfigureAwait(false),
            CommandNames.InputMouseMove or CommandNames.InputMouseClick or CommandNames.InputMouseDrag
                or CommandNames.InputScroll or CommandNames.InputKey or CommandNames.InputHotkey or CommandNames.InputType
                => InputDispatch(request.Method, requestId, started, request.Params),
            CommandNames.VisionCaptureScreen or CommandNames.VisionCaptureWindow or CommandNames.VisionCaptureRegion
                => VisionDispatch(request.Method, requestId, started, request.Params),
            CommandNames.SystemUpdateCheck => SystemUpdateCheck(requestId, started, request.Params),
            CommandNames.SystemUpdateApply => SystemUpdateApply(requestId, started, request.Params),
            CommandNames.SystemTelemetryGet => SystemTelemetryGet(requestId, started),
            CommandNames.SystemTelemetrySet => SystemTelemetrySet(requestId, started, request.Params),
            CommandNames.SystemSecurityReview => SystemSecurityReview(requestId, started),
            CommandNames.SystemIntegrity => SystemIntegrity(requestId, started, request.Params),
            CommandNames.BrowserList => Remeta(_browser.List(requestId), requestId, started),
            CommandNames.BrowserTabs => Remeta(await _browser.TabsAsync(GetStringParam(request.Params, "browserId"), cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserGetTab => Remeta(await _browser.GetTabAsync(RequireString(request.Params, "tabId"), cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserOpenTab => Remeta(await _browser.OpenTabAsync(GetStringParam(request.Params, "url"), cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserCloseTab => Remeta(await _browser.CloseTabAsync(RequireString(request.Params, "tabId"), cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserNavigate => Remeta(await _browser.NavigateAsync(
                RequireString(request.Params, "url"),
                GetStringParam(request.Params, "tabId"),
                GetIntParam(request.Params, "timeoutMs") ?? 30000,
                cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserBack => Remeta(await _browser.BackAsync(GetStringParam(request.Params, "tabId"), cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserForward => Remeta(await _browser.ForwardAsync(GetStringParam(request.Params, "tabId"), cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserReload => Remeta(await _browser.ReloadAsync(GetStringParam(request.Params, "tabId"), cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserQuery => Remeta(await _browser.QueryAsync(ParseBrowserSelector(request.Params), GetStringParam(request.Params, "tabId"), all: false, cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserQueryAll => Remeta(await _browser.QueryAsync(ParseBrowserSelector(request.Params), GetStringParam(request.Params, "tabId"), all: true, cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserClick => Remeta(await _browser.ClickAsync(
                GetStringParam(request.Params, "tabId"),
                GetStringParam(request.Params, "elementId"),
                TryParseBrowserSelector(request.Params),
                cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserFill => Remeta(await _browser.FillAsync(
                GetStringParam(request.Params, "value") ?? GetStringParam(request.Params, "text") ?? "",
                GetStringParam(request.Params, "tabId"),
                GetStringParam(request.Params, "elementId"),
                TryParseBrowserSelector(request.Params),
                cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserSelect => Remeta(await _browser.SelectAsync(
                GetStringParam(request.Params, "value") ?? "",
                GetStringParam(request.Params, "tabId"),
                GetStringParam(request.Params, "elementId"),
                TryParseBrowserSelector(request.Params),
                GetStringParam(request.Params, "label"),
                cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserFocus => Remeta(await _browser.FocusAsync(
                GetStringParam(request.Params, "tabId"),
                GetStringParam(request.Params, "elementId"),
                TryParseBrowserSelector(request.Params),
                cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserGetText => Remeta(await _browser.GetTextAsync(
                GetStringParam(request.Params, "tabId"),
                GetStringParam(request.Params, "elementId"),
                TryParseBrowserSelector(request.Params),
                cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserGetDom => Remeta(await _browser.GetDomAsync(
                GetStringParam(request.Params, "tabId"),
                GetIntParam(request.Params, "maxChars") ?? 50000,
                cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserGetAccessibilityTree => Remeta(await _browser.GetAccessibilityTreeAsync(
                GetStringParam(request.Params, "tabId"),
                GetIntParam(request.Params, "maxDepth") ?? 8,
                cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserWaitFor => Remeta(await _browser.WaitForAsync(
                ParseBrowserSelector(request.Params),
                GetStringParam(request.Params, "tabId"),
                GetIntParam(request.Params, "timeoutMs") ?? 15000,
                cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserWaitForNavigation => Remeta(await _browser.WaitForNavigationAsync(
                GetStringParam(request.Params, "tabId"),
                GetIntParam(request.Params, "timeoutMs") ?? 30000,
                cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserWaitForNetworkIdle => Remeta(await _browser.WaitForNetworkIdleAsync(
                GetStringParam(request.Params, "tabId"),
                GetIntParam(request.Params, "quietMs") ?? 500,
                GetIntParam(request.Params, "timeoutMs") ?? 30000,
                cancellationToken).ConfigureAwait(false), requestId, started),
            CommandNames.BrowserGetDownloads => Remeta(_browser.GetDownloads(), requestId, started),
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
                    Message = $"Unknown method '{request.Method}'. apiVersion={RuntimeCompat.ApiVersion} schemaVersion={RuntimeCompat.SchemaVersion}",
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

    private object MonitorListAsync(string requestId, DateTimeOffset started)
    {
        var monitors = _monitors.List(forceRefresh: true);
        return ToolResult<IReadOnlyList<MonitorInfo>>.Success(
            monitors,
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.MonitorList,
                DurationMs = Elapsed(started),
                ElementsInspected = monitors.Count,
                CacheHit = false,
                Provider = "Win32"
            });
    }

    private async Task<object> WindowGetAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var req = Deserialize<WindowIdRequest>(parameters);
        var window = await _windows.GetAsync(req.WindowId, ct).ConfigureAwait(false);
        if (window is null)
        {
            return WindowStaleFailure<WindowInfo>(CommandNames.WindowGet, requestId, started);
        }

        return ToolResult<WindowInfo>.Success(
            window,
            ResultMeta.Create(requestId, started),
            BuildPerformance(CommandNames.WindowGet, started, provider: "Win32"));
    }

    private async Task<object> WindowMinimizeAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var req = Deserialize<WindowIdRequest>(parameters);
        var window = await _windows.MinimizeAsync(req.WindowId, ct).ConfigureAwait(false);
        if (window is null)
        {
            return WindowStaleFailure<WindowInfo>(CommandNames.WindowMinimize, requestId, started);
        }

        return ToolResult<WindowInfo>.Success(
            window,
            ResultMeta.Create(requestId, started),
            BuildPerformance(CommandNames.WindowMinimize, started, provider: "Win32"),
            stateChanged: true);
    }

    private async Task<object> WindowMaximizeAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var req = Deserialize<WindowIdRequest>(parameters);
        var window = await _windows.MaximizeAsync(req.WindowId, ct).ConfigureAwait(false);
        if (window is null)
        {
            return WindowStaleFailure<WindowInfo>(CommandNames.WindowMaximize, requestId, started);
        }

        return ToolResult<WindowInfo>.Success(
            window,
            ResultMeta.Create(requestId, started),
            BuildPerformance(CommandNames.WindowMaximize, started, provider: "Win32"),
            stateChanged: true);
    }

    private async Task<object> WindowRestoreAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var req = Deserialize<WindowIdRequest>(parameters);
        var window = await _windows.RestoreAsync(req.WindowId, ct).ConfigureAwait(false);
        if (window is null)
        {
            return WindowStaleFailure<WindowInfo>(CommandNames.WindowRestore, requestId, started);
        }

        return ToolResult<WindowInfo>.Success(
            window,
            ResultMeta.Create(requestId, started),
            BuildPerformance(CommandNames.WindowRestore, started, provider: "Win32"),
            stateChanged: true);
    }

    private async Task<object> WindowMoveAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var req = Deserialize<WindowMoveRequest>(parameters);
        var result = await _windows.MoveAsync(req, ct).ConfigureAwait(false);
        if (result is null)
        {
            return WindowStaleFailure<WindowMutationResult>(CommandNames.WindowMove, requestId, started);
        }

        return ToolResult<WindowMutationResult>.Success(
            result,
            ResultMeta.Create(requestId, started),
            BuildPerformance(CommandNames.WindowMove, started, provider: "Win32"),
            stateChanged: true);
    }

    private async Task<object> WindowResizeAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var req = Deserialize<WindowResizeRequest>(parameters);
        var result = await _windows.ResizeAsync(req, ct).ConfigureAwait(false);
        if (result is null)
        {
            return WindowStaleFailure<WindowMutationResult>(CommandNames.WindowResize, requestId, started);
        }

        return ToolResult<WindowMutationResult>.Success(
            result,
            ResultMeta.Create(requestId, started),
            BuildPerformance(CommandNames.WindowResize, started, provider: "Win32"),
            stateChanged: true);
    }

    private ToolResult<T> WindowStaleFailure<T>(string operation, string requestId, DateTimeOffset started) =>
        ToolResult<T>.Failure(
            new ErrorInfo { Code = ErrorCodes.StaleTarget, Message = "Window handle is stale.", Retryable = true },
            ResultMeta.Create(requestId, started),
            BuildPerformance(operation, started, provider: "Win32"));

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
        var chrome = BrowserDiscovery.IsInstalled("chrome");
        var edge = BrowserDiscovery.IsInstalled("edge");
        return ToolResult<object>.Success(
            new
            {
                uia = true,
                browser = new { chrome, edge, firefox = false },
                shell = false,
                vision = true,
                input = true,
                plans = true,
                permissions = true,
                audit = true,
                adapters = _adapters.ListIds().ToArray(),
                capabilities = new[]
                {
                    "browser",
                    "adapters",
                    "input",
                    "vision",
                    "graph",
                    "workflow",
                    "hardening"
                },
                schemaVersion = RuntimeCompat.SchemaVersion,
                apiVersion = RuntimeCompat.ApiVersion,
                minCompatibleApi = RuntimeCompat.MinCompatibleApi,
                tools = new[]
                {
                    CommandNames.DesktopGetState,
                    CommandNames.DesktopGetCapabilities,
                    CommandNames.DesktopDescribe,
                    CommandNames.DesktopGetGraph,
                    CommandNames.DesktopBatch,
                    CommandNames.DesktopDiff,
                    CommandNames.EventsSubscribe,
                    CommandNames.EventsPoll,
                    CommandNames.EventsUnsubscribe,
                    CommandNames.MonitorList,
                    CommandNames.WindowList,
                    CommandNames.WindowGet,
                    CommandNames.WindowFocus,
                    CommandNames.WindowMinimize,
                    CommandNames.WindowMaximize,
                    CommandNames.WindowRestore,
                    CommandNames.WindowMove,
                    CommandNames.WindowResize,
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
                    CommandNames.FilesystemStat,
                    CommandNames.FilesystemInspect,
                    CommandNames.PlanExecute,
                    CommandNames.PlanCancel,
                    CommandNames.SystemEmergencyStop,
                    CommandNames.AdapterList,
                    CommandNames.AdapterCapabilities,
                    CommandNames.AdapterExecute,
                    CommandNames.BlenderOpen,
                    CommandNames.BlenderGetScene,
                    CommandNames.BlenderGetObjects,
                    CommandNames.BlenderSelectObject,
                    CommandNames.BlenderExecutePython,
                    CommandNames.BlenderExport,
                    CommandNames.BlenderSave,
                    CommandNames.BlenderBatch,
                    CommandNames.BlenderRender,
                    CommandNames.BlenderImportMesh,
                    CommandNames.VsCodeOpenFile,
                    CommandNames.VsCodeOpenFolder,
                    CommandNames.VsCodeExecuteCommand,
                    CommandNames.VsCodeGetWorkspace,
                    CommandNames.VisualStudioGetSolution,
                    CommandNames.VisualStudioBuild,
                    CommandNames.VisualStudioOpenFile,
                    CommandNames.VisualStudioOpenSolution,
                    CommandNames.RobloxOpenPlace,
                    CommandNames.RobloxPluginPing,
                    CommandNames.RobloxGetHierarchy,
                    CommandNames.RobloxGetSelection,
                    CommandNames.RobloxSelect,
                    CommandNames.RobloxSetProperty,
                    CommandNames.InputMouseMove,
                    CommandNames.InputMouseClick,
                    CommandNames.InputMouseDrag,
                    CommandNames.InputScroll,
                    CommandNames.InputKey,
                    CommandNames.InputHotkey,
                    CommandNames.InputType,
                    CommandNames.VisionCaptureScreen,
                    CommandNames.VisionCaptureWindow,
                    CommandNames.VisionCaptureRegion,
                    CommandNames.BrowserList,
                    CommandNames.BrowserTabs,
                    CommandNames.BrowserGetTab,
                    CommandNames.BrowserOpenTab,
                    CommandNames.BrowserCloseTab,
                    CommandNames.BrowserNavigate,
                    CommandNames.BrowserBack,
                    CommandNames.BrowserForward,
                    CommandNames.BrowserReload,
                    CommandNames.BrowserQuery,
                    CommandNames.BrowserQueryAll,
                    CommandNames.BrowserClick,
                    CommandNames.BrowserFill,
                    CommandNames.BrowserSelect,
                    CommandNames.BrowserFocus,
                    CommandNames.BrowserGetText,
                    CommandNames.BrowserGetDom,
                    CommandNames.BrowserGetAccessibilityTree,
                    CommandNames.BrowserWaitFor,
                    CommandNames.BrowserWaitForNavigation,
                    CommandNames.BrowserWaitForNetworkIdle,
                    CommandNames.BrowserGetDownloads,
                    CommandNames.SystemUpdateCheck,
                    CommandNames.SystemUpdateApply,
                    CommandNames.SystemTelemetryGet,
                    CommandNames.SystemTelemetrySet,
                    CommandNames.SystemSecurityReview,
                    CommandNames.SystemIntegrity,
                    CommandNames.SystemPerformance
                }
            },
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.DesktopGetCapabilities,
                DurationMs = Elapsed(started),
                ElementsInspected = 0,
                CacheHit = false,
                Provider = "Agent"
            });
    }

    private async Task<object> DesktopDescribeAsync(
        string requestId,
        DateTimeOffset started,
        JsonElement? parameters,
        CancellationToken ct)
    {
        var includeControls = GetBoolParam(parameters, "includeControls") ?? true;
        var payload = await _graph.DescribeAsync(includeControls, ct).ConfigureAwait(false);
        return ToolResult<object>.Success(
            payload,
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.DesktopDescribe,
                DurationMs = Elapsed(started),
                ElementsInspected = 0,
                CacheHit = false,
                Provider = "Agent"
            });
    }

    private async Task<object> DesktopGetGraphAsync(
        string requestId,
        DateTimeOffset started,
        JsonElement? parameters,
        CancellationToken ct)
    {
        var includeControls = GetBoolParam(parameters, "includeControls") ?? true;
        var forceRefresh = GetBoolParam(parameters, "forceRefresh") ?? false;
        var graph = await _graph.GetGraphAsync(forceRefresh, includeControls, ct).ConfigureAwait(false);
        return ToolResult<object>.Success(
            graph,
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.DesktopGetGraph,
                DurationMs = Elapsed(started),
                ElementsInspected = graph.Windows.Count + graph.ImportantControls.Count,
                CacheHit = false,
                Provider = "Agent"
            });
    }

    private async Task<object> DesktopBatchAsync(
        string requestId,
        DateTimeOffset started,
        JsonElement? parameters,
        AgentSession session,
        CancellationToken ct)
    {
        var calls = ParseBatchCalls(parameters);
        if (calls.Count == 0)
        {
            return ToolResult<object>.Failure(
                new ErrorInfo { Code = ErrorCodes.InvalidArgument, Message = "calls must be a non-empty array.", Retryable = false },
                ResultMeta.Create(requestId, started));
        }

        if (calls.Count > 32)
        {
            return ToolResult<object>.Failure(
                new ErrorInfo { Code = ErrorCodes.InvalidArgument, Message = "calls is capped at 32.", Retryable = false },
                ResultMeta.Create(requestId, started));
        }

        if (calls.Any(c => string.Equals(c.Method, CommandNames.DesktopBatch, StringComparison.OrdinalIgnoreCase)))
        {
            return ToolResult<object>.Failure(
                new ErrorInfo { Code = ErrorCodes.InvalidArgument, Message = "Nested desktop.batch is not allowed.", Retryable = false },
                ResultMeta.Create(requestId, started));
        }

        var originalCount = calls.Count;
        var fused = CommandFusion.FuseCalls(calls);
        var results = new List<object>();
        var parallelGroups = 0;
        var i = 0;
        while (i < fused.Count)
        {
            var group = new List<BatchCall> { fused[i] };
            if (ReadSafety.IsParallelSafe(fused[i].Method))
            {
                while (i + group.Count < fused.Count &&
                       ReadSafety.IsParallelSafe(fused[i + group.Count].Method))
                {
                    group.Add(fused[i + group.Count]);
                }
            }

            if (group.Count > 1)
            {
                parallelGroups++;
                var parts = await Task.WhenAll(group.Select(call => DispatchCallAsync(call, session, ct)))
                    .ConfigureAwait(false);
                results.AddRange(parts);
            }
            else
            {
                results.Add(await DispatchCallAsync(group[0], session, ct).ConfigureAwait(false));
            }

            i += group.Count;
        }

        return ToolResult<object>.Success(
            new
            {
                results,
                fused = Math.Max(0, originalCount - fused.Count),
                parallelGroups
            },
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.DesktopBatch,
                DurationMs = Elapsed(started),
                ElementsInspected = results.Count,
                CacheHit = false,
                Provider = "Agent"
            });
    }

    private Task<object> DispatchCallAsync(BatchCall call, AgentSession session, CancellationToken ct)
    {
        JsonElement? parameters = call.Params;
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            parameters = JsonSerializer.SerializeToElement(new { sessionId = session.SessionId });
        }
        else if (!parameters.Value.TryGetProperty("sessionId", out _))
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parameters.Value.GetRawText())
                       ?? new Dictionary<string, JsonElement>();
            dict["sessionId"] = JsonSerializer.SerializeToElement(session.SessionId);
            parameters = JsonSerializer.SerializeToElement(dict);
        }

        return DispatchAsync(new RpcRequest
        {
            Id = call.Id ?? Guid.NewGuid().ToString("N"),
            Method = call.Method,
            Params = parameters
        }, ct);
    }

    private static List<BatchCall> ParseBatchCalls(JsonElement? parameters)
    {
        var list = new List<BatchCall>();
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object ||
            !parameters.Value.TryGetProperty("calls", out var calls) ||
            calls.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        var index = 0;
        foreach (var item in calls.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var method = item.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(method))
            {
                continue;
            }

            JsonElement? callParams = null;
            if (item.TryGetProperty("params", out var p))
            {
                callParams = p.Clone();
            }

            var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : $"c{index}";
            list.Add(new BatchCall { Id = id, Method = method, Params = callParams });
            index++;
        }

        return list;
    }

    private async Task<object> DesktopDiffAsync(
        string requestId,
        DateTimeOffset started,
        JsonElement? parameters,
        CancellationToken ct)
    {
        var includeControls = GetBoolParam(parameters, "includeControls") ?? false;
        var current = await _graph.GetGraphAsync(forceRefresh: true, includeControls, ct).ConfigureAwait(false);
        var diff = _graph.Diff(current);
        PublishGraphEvents(diff);
        return ToolResult<object>.Success(
            diff,
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.DesktopDiff,
                DurationMs = Elapsed(started),
                ElementsInspected = current.Windows.Count,
                CacheHit = false,
                Provider = "Agent"
            });
    }

    private void PublishGraphEvents(DesktopGraphDiff diff)
    {
        if (diff.FocusChanged)
        {
            _events.Publish(new DesktopEvent
            {
                Id = "evt_" + Guid.NewGuid().ToString("N")[..12],
                Type = "focus.changed",
                Data = new Dictionary<string, object?>
                {
                    ["from"] = diff.PreviousFocus,
                    ["to"] = diff.CurrentFocus
                }
            });
        }

        foreach (var w in diff.AddedWindows.Take(12))
        {
            _events.Publish(new DesktopEvent
            {
                Id = "evt_" + Guid.NewGuid().ToString("N")[..12],
                Type = "window.opened",
                Source = new Dictionary<string, object?> { ["windowId"] = w.Id },
                Data = new Dictionary<string, object?> { ["title"] = w.Title, ["process"] = w.Process }
            });
        }

        foreach (var w in diff.RemovedWindows.Take(12))
        {
            _events.Publish(new DesktopEvent
            {
                Id = "evt_" + Guid.NewGuid().ToString("N")[..12],
                Type = "window.closed",
                Source = new Dictionary<string, object?> { ["windowId"] = w.Id },
                Data = new Dictionary<string, object?> { ["title"] = w.Title, ["process"] = w.Process }
            });
        }
    }

    private object EventsSubscribe(string requestId, DateTimeOffset started, JsonElement? parameters)
    {
        string[]? types = null;
        if (parameters is not null && parameters.Value.ValueKind == JsonValueKind.Object &&
            parameters.Value.TryGetProperty("types", out var t) && t.ValueKind == JsonValueKind.Array)
        {
            types = t.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToArray();
        }

        var id = _events.Subscribe(types);
        return ToolResult<object>.Success(
            new { subscriptionId = id },
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.EventsSubscribe,
                DurationMs = Elapsed(started),
                ElementsInspected = 0,
                CacheHit = false,
                Provider = "Agent"
            });
    }

    private async Task<object> EventsPollAsync(
        string requestId,
        DateTimeOffset started,
        JsonElement? parameters,
        CancellationToken ct)
    {
        var id = GetStringParam(parameters, "subscriptionId") ?? GetStringParam(parameters, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return ToolResult<object>.Failure(
                new ErrorInfo { Code = ErrorCodes.InvalidArgument, Message = "subscriptionId is required.", Retryable = false },
                ResultMeta.Create(requestId, started));
        }

        var max = GetIntParam(parameters, "max") ?? 50;
        var waitMs = GetIntParam(parameters, "timeoutMs") ?? 0;
        try
        {
            var events = await _events.PollAsync(id, max, waitMs, ct).ConfigureAwait(false);
            return ToolResult<object>.Success(
                new { events },
                ResultMeta.Create(requestId, started),
                new PerformanceMeta
                {
                    Operation = CommandNames.EventsPoll,
                    DurationMs = Elapsed(started),
                    ElementsInspected = events.Count,
                    CacheHit = false,
                    Provider = "Agent"
                });
        }
        catch (ArgumentException ex)
        {
            return ToolResult<object>.Failure(
                new ErrorInfo { Code = ErrorCodes.NotFound, Message = ex.Message, Retryable = false },
                ResultMeta.Create(requestId, started));
        }
    }

    private object EventsUnsubscribe(string requestId, DateTimeOffset started, JsonElement? parameters)
    {
        var id = GetStringParam(parameters, "subscriptionId") ?? GetStringParam(parameters, "id");
        var removed = !string.IsNullOrWhiteSpace(id) && _events.Unsubscribe(id);
        return ToolResult<object>.Success(
            new { subscriptionId = id, removed },
            ResultMeta.Create(requestId, started));
    }

    private async Task<object> FilesystemStatAsync(string requestId, DateTimeOffset started, JsonElement? parameters, CancellationToken ct)
    {
        var path = parameters is null ? null : GetString(parameters.Value, "path");
        var result = await _files.StatAsync(path ?? "", ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return ToolResult<object>.Failure(result.Error!, ResultMeta.Create(requestId, started), result.Performance);
        }

        return ToolResult<object>.Success(result.Data!, ResultMeta.Create(requestId, started), result.Performance);
    }

    private async Task<object> FilesystemInspectAsync(
        string requestId,
        DateTimeOffset started,
        JsonElement? parameters,
        AgentSession session,
        CancellationToken ct)
    {
        var path = GetStringParam(parameters, "path");
        var extra = GetStringListOptional(parameters, "paths");
        if (string.IsNullOrWhiteSpace(path) && extra.Count == 0)
        {
            return ToolResult<object>.Failure(
                new ErrorInfo { Code = ErrorCodes.InvalidArgument, Message = "path or paths is required.", Retryable = false },
                ResultMeta.Create(requestId, started));
        }
        foreach (var extraPath in extra)
        {
            var evaluation = _security.Engine.Evaluate(session, CommandNames.FilesystemInspect, extraPath);
            if (evaluation.Decision == PermissionDecisionKind.Deny)
            {
                return ToolResult<object>.Failure(
                    new ErrorInfo
                    {
                        Code = ErrorCodes.PathNotAllowed,
                        Message = evaluation.Reason ?? $"Path not allowed: {extraPath}",
                        Retryable = false
                    },
                    ResultMeta.Create(requestId, started));
            }
        }

        var result = await _files.InspectAsync(path, extra, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            return ToolResult<object>.Failure(result.Error!, ResultMeta.Create(requestId, started), result.Performance);
        }

        return ToolResult<object>.Success(result.Data!, ResultMeta.Create(requestId, started), result.Performance);
    }

    private static IReadOnlyList<string> GetStringListOptional(JsonElement? parameters, string name)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object ||
            !parameters.Value.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return el.EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .ToList();
    }

    private static object CacheHitResult(string json, string method, string requestId, DateTimeOffset started)
    {
        using var doc = JsonDocument.Parse(json);
        object? data = null;
        if (doc.RootElement.TryGetProperty("data", out var dataEl))
        {
            data = JsonSerializer.Deserialize<object>(dataEl.GetRawText(), JsonDefaults.Options);
        }

        return ToolResult<object>.Success(
            data!,
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = method,
                DurationMs = Elapsed(started),
                ElementsInspected = 0,
                CacheHit = true,
                Provider = "Cache"
            });
    }

    private static bool HasStateChanged(object? result)
    {
        if (result is null)
        {
            return false;
        }

        var json = JsonSerializer.Serialize(result, JsonDefaults.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("stateChanged", out var flag) && flag.ValueKind == JsonValueKind.True;
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
            TitleRegex = parameters is null ? null : GetString(parameters.Value, "titleRegex"),
            TimeoutMs = parameters is not null && parameters.Value.TryGetProperty("timeoutMs", out var t) && t.TryGetInt32(out var ms)
                ? ms
                : 15000
        };
        await _conditions.WaitAsync(condition, condition.TimeoutMs ?? 15000, ct).ConfigureAwait(false);
        var windows = await _windows.ListAsync(ct).ConfigureAwait(false);
        var match = windows.FirstOrDefault(w =>
            ConditionProbe.WindowMatches(w, condition.Process, condition.TitleContains, null, condition.TitleRegex));
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
        var timeoutSeconds = GetIntParam(parameters, "approvalTimeoutSeconds") ?? 30;
        var session = _security.Sessions.Create(clientId, autoApprove, TimeSpan.FromSeconds(timeoutSeconds));
        return ToolResult<object>.Success(
            new
            {
                session.SessionId,
                session.ClientId,
                session.AutoApproveAsk,
                approvalTimeoutSeconds = (int)session.ApprovalTimeout.TotalSeconds
            },
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

    private object SessionList(string requestId, DateTimeOffset started)
    {
        var sessions = _security.Sessions.List()
            .Select(s => new
            {
                s.SessionId,
                s.ClientId,
                s.AutoApproveAsk,
                s.CreatedAt,
                grants = s.SessionGrants.ToArray()
            })
            .ToArray();
        return ToolResult<object>.Success(sessions, ResultMeta.Create(requestId, started));
    }

    private object SystemStatus(string requestId, DateTimeOffset started)
    {
        return ToolResult<object>.Success(
            new
            {
                agent = true,
                emergencyStopped = _security.Emergency.IsStopped,
                pendingApprovals = _security.Approvals.ListPending().Count,
                sessionCount = _security.Sessions.List().Count,
                pipe = PipeNames.Default,
                schemaVersion = RuntimeCompat.SchemaVersion,
                apiVersion = RuntimeCompat.ApiVersion,
                productVersion = RuntimeCompat.ProductVersion,
                installedVersion = _prod.State.InstalledVersion ?? RuntimeCompat.ProductVersion,
                installRoot = ResolveInstallRoot(),
                installId = _prod.State.InstallId,
                lastCrash = _prod.LastCrash,
                telemetryEnabled = _prod.State.Telemetry.Enabled,
                uiaAvailable = _uia.Available
            },
            ResultMeta.Create(requestId, started));
    }

    private object SystemPerformance(string requestId, DateTimeOffset started)
    {
        var operations = _operationTimings.Snapshot()
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(
                kv => kv.Key,
                kv => new
                {
                    kv.Value.Count,
                    kv.Value.SuccessCount,
                    kv.Value.FailureCount,
                    p50Ms = kv.Value.P50Ms,
                    p95Ms = kv.Value.P95Ms,
                    lastMs = kv.Value.LastMs
                },
                StringComparer.Ordinal);

        return ToolResult<object>.Success(
            new
            {
                capturedAt = DateTimeOffset.UtcNow,
                operations
            },
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.SystemPerformance,
                DurationMs = Elapsed(started),
                ElementsInspected = 0,
                CacheHit = false,
                Provider = "Agent"
            });
    }

    private object SystemUpdateCheck(string requestId, DateTimeOffset started, JsonElement? parameters)
    {
        try
        {
            var current = CurrentManifest();
            var availablePath = GetStringParam(parameters, "manifestPath");
            if (string.IsNullOrWhiteSpace(availablePath))
            {
                return ToolResult<object>.Success(
                    new { current = current.Version, updateAvailable = false, compatible = true },
                    ResultMeta.Create(requestId, started));
            }

            var available = UpdateService.LoadManifest(availablePath);
            return ToolResult<object>.Success(
                UpdateService.Check(current, available),
                ResultMeta.Create(requestId, started));
        }
        catch (Exception ex)
        {
            return ToolResult<object>.Failure(MapException(ex), ResultMeta.Create(requestId, started));
        }
    }

    private object SystemUpdateApply(string requestId, DateTimeOffset started, JsonElement? parameters)
    {
        try
        {
            var staged = GetStringParam(parameters, "stagedDir")
                         ?? throw new ArgumentException("stagedDir is required.");
            var manifestPath = GetStringParam(parameters, "manifestPath")
                               ?? Path.Combine(staged, "install-manifest.json");
            var target = GetStringParam(parameters, "targetRoot")
                         ?? ResolveInstallRoot()
                         ?? throw new ArgumentException("targetRoot is required.");
            var allowDowngrade = GetBoolParam(parameters, "allowDowngrade") ?? false;
            var available = UpdateService.LoadManifest(manifestPath);
            var applied = UpdateService.Apply(staged, target, available, allowDowngrade);
            return ToolResult<object>.Success(applied, ResultMeta.Create(requestId, started), stateChanged: true);
        }
        catch (Exception ex)
        {
            return ToolResult<object>.Failure(MapException(ex), ResultMeta.Create(requestId, started));
        }
    }

    private object SystemTelemetryGet(string requestId, DateTimeOffset started) =>
        ToolResult<object>.Success(_prod.State.Telemetry, ResultMeta.Create(requestId, started));

    private object SystemTelemetrySet(string requestId, DateTimeOffset started, JsonElement? parameters)
    {
        _prod.State.Telemetry.Enabled = GetBoolParam(parameters, "enabled") ?? false;
        var level = GetStringParam(parameters, "level");
        if (!_prod.State.Telemetry.Enabled)
        {
            _prod.State.Telemetry.Level = "off";
        }
        else if (!string.IsNullOrWhiteSpace(level))
        {
            _prod.State.Telemetry.Level = level;
        }
        else if (string.Equals(_prod.State.Telemetry.Level, "off", StringComparison.OrdinalIgnoreCase))
        {
            _prod.State.Telemetry.Level = "local";
        }

        var days = GetIntParam(parameters, "logRetentionDays");
        if (days is > 0)
        {
            _prod.State.LogRetentionDays = days.Value;
        }

        _prod.SaveState();
        return ToolResult<object>.Success(_prod.State.Telemetry, ResultMeta.Create(requestId, started), stateChanged: true);
    }

    private object SystemSecurityReview(string requestId, DateTimeOffset started) =>
        ToolResult<object>.Success(_prod.SecurityReview(_uia.Available), ResultMeta.Create(requestId, started));

    private object SystemIntegrity(string requestId, DateTimeOffset started, JsonElement? parameters)
    {
        try
        {
            var root = GetStringParam(parameters, "root") ?? ResolveInstallRoot();
            var manifestPath = GetStringParam(parameters, "manifestPath")
                               ?? InstallRootResolver.ResolveManifestPath(root);
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                return ToolResult<object>.Success(
                    new { ok = true, signed = false, issues = Array.Empty<string>(), reason = "No install manifest in this runtime." },
                    ResultMeta.Create(requestId, started));
            }

            var manifest = UpdateService.LoadManifest(manifestPath);
            return ToolResult<object>.Success(UpdateService.VerifyLayout(root, manifest), ResultMeta.Create(requestId, started));
        }
        catch (Exception ex)
        {
            return ToolResult<object>.Failure(MapException(ex), ResultMeta.Create(requestId, started));
        }
    }

    private UpdateManifest CurrentManifest()
    {
        var installRoot = ResolveInstallRoot();
        var path = InstallRootResolver.ResolveManifestPath(installRoot);
        if (path is not null && File.Exists(path))
        {
            return UpdateService.LoadManifest(path);
        }

        return new UpdateManifest { Version = RuntimeCompat.ProductVersion, Files = Array.Empty<ManifestFile>() };
    }

    private string? ResolveInstallRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return InstallRootResolver.ResolveInstallRoot(
            Environment.GetEnvironmentVariable("DESKTOPUSEAGENT_INSTALL"),
            localAppData,
            AppContext.BaseDirectory);
    }

    private object PermissionPolicyGet(string requestId, DateTimeOffset started)
    {
        return ToolResult<object>.Success(
            _security.Engine.SnapshotPolicy(),
            ResultMeta.Create(requestId, started));
    }

    private object PermissionPolicySet(string requestId, DateTimeOffset started, JsonElement? parameters)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            return ToolResult<object>.Failure(
                new ErrorInfo { Code = ErrorCodes.InvalidArgument, Message = "params required.", Retryable = false },
                ResultMeta.Create(requestId, started));
        }

        var kind = GetString(parameters.Value, "kind")?.ToLowerInvariant();
        var ok = kind switch
        {
            "capability" => _security.Engine.SetCapabilityDefault(
                GetString(parameters.Value, "capability") ?? "",
                ParseDecision(GetString(parameters.Value, "decision"))),
            "app" => _security.Engine.UpsertAppRule(
                GetString(parameters.Value, "processName") ?? "",
                ParseDecision(GetString(parameters.Value, "observe")),
                ParseDecision(GetString(parameters.Value, "interact"))),
            "path" => _security.Engine.UpsertPathRule(
                GetString(parameters.Value, "pathPrefix") ?? "",
                GetBool(parameters.Value, "allowRead", true),
                GetBool(parameters.Value, "allowWrite", false),
                GetBool(parameters.Value, "deny", false)),
            _ => false
        };

        if (!ok)
        {
            return ToolResult<object>.Failure(
                new ErrorInfo
                {
                    Code = ErrorCodes.InvalidArgument,
                    Message = "Invalid permission.policy.set payload. Use kind=capability|app|path.",
                    Retryable = false
                },
                ResultMeta.Create(requestId, started));
        }

        _prod.SavePolicy();
        return ToolResult<object>.Success(
            _security.Engine.SnapshotPolicy(),
            ResultMeta.Create(requestId, started),
            stateChanged: true);
    }

    private static PermissionDecisionKind ParseDecision(string? text) =>
        (text ?? "deny").ToLowerInvariant() switch
        {
            "allow" => PermissionDecisionKind.Allow,
            "ask" => PermissionDecisionKind.Ask,
            _ => PermissionDecisionKind.Deny
        };

    private static bool GetBool(JsonElement parameters, string name, bool defaultValue)
    {
        if (!parameters.TryGetProperty(name, out var value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue
        };
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

    private async Task<object> AdapterListAsync(string requestId, DateTimeOffset started, CancellationToken ct)
    {
        var caps = await _adapters.ListCapabilitiesAsync(ct).ConfigureAwait(false);
        return ToolResult<object>.Success(caps, ResultMeta.Create(requestId, started));
    }

    private async Task<object> AdapterCapabilitiesAsync(
        string requestId,
        DateTimeOffset started,
        JsonElement? parameters,
        CancellationToken ct)
    {
        var adapterId = parameters is null ? null : GetString(parameters.Value, "adapterId");
        if (string.IsNullOrWhiteSpace(adapterId))
        {
            return await AdapterListAsync(requestId, started, ct).ConfigureAwait(false);
        }

        var adapter = _adapters.Get(adapterId);
        if (adapter is null)
        {
            return ToolResult<object>.Failure(
                new ErrorInfo { Code = ErrorCodes.AdapterNotFound, Message = $"Adapter '{adapterId}' not found.", Retryable = false },
                ResultMeta.Create(requestId, started));
        }

        var caps = await adapter.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        return ToolResult<object>.Success(caps, ResultMeta.Create(requestId, started));
    }

    private async Task<object> AdapterExecuteAsync(
        RpcRequest request,
        string requestId,
        DateTimeOffset started,
        CancellationToken ct)
    {
        var action = request.Method == CommandNames.AdapterExecute
            ? (request.Params is null ? null : GetString(request.Params.Value, "action"))
            : request.Method;
        if (string.IsNullOrWhiteSpace(action))
        {
            return ToolResult<object>.Failure(
                new ErrorInfo { Code = ErrorCodes.InvalidArgument, Message = "action is required.", Retryable = false },
                ResultMeta.Create(requestId, started));
        }

        var adapterId = request.Params is null ? null : GetString(request.Params.Value, "adapterId");
        var processId = request.Params is null ? null : GetString(request.Params.Value, "processId");
        var windowId = request.Params is null ? null : GetString(request.Params.Value, "windowId");
        var parameters = ToObjectDictionary(request.Params);

        var result = string.Equals(action, CommandNames.RobloxOpenPlace, StringComparison.OrdinalIgnoreCase)
            ? await _robloxOpenPlace.OpenPlaceAsync((IRobloxPlaceLauncher)_robloxAdapter, parameters, ct).ConfigureAwait(false)
            : await _adapters.ExecuteAsync(
                adapterId,
                new AdapterCommand
                {
                    Action = action,
                    Params = parameters,
                    ProcessId = processId,
                    WindowId = windowId
                },
                ct).ConfigureAwait(false);

        if (!result.Ok)
        {
            return ToolResult<object>.Failure(
                new ErrorInfo
                {
                    Code = result.ErrorCode ?? ErrorCodes.AdapterFailed,
                    Message = result.Message ?? "Adapter failed.",
                    Retryable = false
                },
                ResultMeta.Create(requestId, started),
                new PerformanceMeta
                {
                    Operation = action,
                    DurationMs = Elapsed(started),
                    ElementsInspected = 0,
                    CacheHit = false,
                    Provider = adapterId ?? action.Split('.')[0]
                });
        }

        return ToolResult<object>.Success(
            result.Data ?? new { ok = true },
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = action,
                DurationMs = Elapsed(started),
                ElementsInspected = 0,
                CacheHit = false,
                Provider = adapterId ?? action.Split('.')[0]
            },
            stateChanged: true);
    }

    private static Dictionary<string, object?> ToObjectDictionary(JsonElement? parameters)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            return dict;
        }

        foreach (var prop in parameters.Value.EnumerateObject())
        {
            if (prop.NameEquals("adapterId") || prop.NameEquals("action") || prop.NameEquals("processId") || prop.NameEquals("windowId") || prop.NameEquals("sessionId"))
            {
                continue;
            }

            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number when prop.Value.TryGetInt64(out var l) => l,
                JsonValueKind.Number => prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.Clone()
            };
        }

        return dict;
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

    private void RecordOperationTiming(string method, DateTimeOffset started, bool success)
    {
        if (string.Equals(method, CommandNames.SystemPerformance, StringComparison.Ordinal))
        {
            return;
        }

        _operationTimings.Record(method, Elapsed(started), success);
    }

    private static ErrorInfo MapException(Exception ex) =>
        ex switch
        {
            TimeoutException => new ErrorInfo { Code = ErrorCodes.Timeout, Message = ex.Message, Retryable = true },
            InvalidOperationException when ex.Message.StartsWith(ErrorCodes.IncompatibleSchema, StringComparison.Ordinal) =>
                new ErrorInfo { Code = ErrorCodes.IncompatibleSchema, Message = ex.Message, Retryable = false },
            InvalidOperationException when ex.Message.StartsWith(ErrorCodes.IntegrityFailed, StringComparison.Ordinal) =>
                new ErrorInfo { Code = ErrorCodes.IntegrityFailed, Message = ex.Message, Retryable = false },
            InvalidOperationException when ex.Message.StartsWith(ErrorCodes.UpdateFailed, StringComparison.Ordinal) =>
                new ErrorInfo { Code = ErrorCodes.UpdateFailed, Message = ex.Message, Retryable = false },
            InvalidOperationException when ex.Message == ErrorCodes.StaleTarget =>
                new ErrorInfo { Code = ErrorCodes.StaleTarget, Message = "Target is no longer valid.", Retryable = true },
            ArgumentException => new ErrorInfo { Code = ErrorCodes.InvalidArgument, Message = ex.Message, Retryable = false },
            _ => new ErrorInfo { Code = ErrorCodes.Internal, Message = ex.Message, Retryable = false }
        };

    private object InputDispatch(string method, string requestId, DateTimeOffset started, JsonElement? parameters)
    {
        var reason = GetStringParam(parameters, "fallbackReason")
                     ?? GetStringParam(parameters, "fallback_reason")
                     ?? InputService.DefaultFallbackReason;
        var dpi = GetDoubleParam(parameters, "dpiScale");

        object payload = method switch
        {
            CommandNames.InputMouseMove => _input.MouseMove(
                RequireInt(parameters, "x"),
                RequireInt(parameters, "y"),
                dpi,
                reason),
            CommandNames.InputMouseClick => _input.MouseClick(
                RequireInt(parameters, "x"),
                RequireInt(parameters, "y"),
                GetStringParam(parameters, "button") ?? "left",
                GetIntParam(parameters, "clickCount") ?? 1,
                dpi,
                reason),
            CommandNames.InputMouseDrag => _input.MouseDrag(
                RequireInt(parameters, "fromX"),
                RequireInt(parameters, "fromY"),
                RequireInt(parameters, "toX"),
                RequireInt(parameters, "toY"),
                GetStringParam(parameters, "button") ?? "left",
                dpi,
                reason),
            CommandNames.InputScroll => _input.Scroll(
                GetIntParam(parameters, "x"),
                GetIntParam(parameters, "y"),
                GetIntParam(parameters, "delta") ?? 120,
                GetStringParam(parameters, "axis") ?? "vertical",
                dpi,
                reason),
            CommandNames.InputKey => _input.Key(
                RequireString(parameters, "key"),
                GetStringParam(parameters, "action") ?? "press",
                reason),
            CommandNames.InputHotkey => _input.Hotkey(RequireStringList(parameters, "keys"), reason),
            CommandNames.InputType => _input.TypeText(
                GetStringParam(parameters, "text") ?? GetStringParam(parameters, "value") ?? "",
                GetIntParam(parameters, "delayMs") ?? 0,
                reason),
            _ => throw new ArgumentException($"Unsupported input method '{method}'.")
        };

        return ToolResult<object>.Success(
            payload,
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = method,
                DurationMs = Elapsed(started),
                ElementsInspected = 0,
                CacheHit = false,
                Provider = InputService.Provider,
                FallbackReason = reason
            },
            stateChanged: true);
    }

    private object VisionDispatch(string method, string requestId, DateTimeOffset started, JsonElement? parameters)
    {
        var reason = GetStringParam(parameters, "visionReason")
                     ?? GetStringParam(parameters, "vision_reason")
                     ?? GdiVisionCaptureProvider.DefaultVisionReason;

        VisionCaptureResult capture = method switch
        {
            CommandNames.VisionCaptureScreen => _vision.CaptureScreen(GetIntParam(parameters, "monitor"), reason),
            CommandNames.VisionCaptureWindow => CaptureWindow(RequireString(parameters, "windowId"), reason),
            CommandNames.VisionCaptureRegion => _vision.CaptureRegion(
                RequireInt(parameters, "x"),
                RequireInt(parameters, "y"),
                RequireInt(parameters, "width"),
                RequireInt(parameters, "height"),
                reason),
            _ => throw new ArgumentException($"Unsupported vision method '{method}'.")
        };

        return ToolResult<object>.Success(
            new
            {
                mimeType = capture.MimeType,
                pngBase64 = Convert.ToBase64String(capture.PngBytes),
                byteLength = capture.PngBytes.Length,
                meta = capture.Meta
            },
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = method,
                DurationMs = Elapsed(started),
                ElementsInspected = 0,
                CacheHit = false,
                Provider = capture.Meta.Provider,
                VisionReason = capture.Meta.VisionReason
            });
    }

    private VisionCaptureResult CaptureWindow(string windowId, string reason)
    {
        if (!_windows.TryGetHwnd(windowId, out var hwnd))
        {
            throw new ArgumentException($"{ErrorCodes.NotFound}: window '{windowId}' not found.");
        }

        return _vision.CaptureWindow(hwnd, windowId, reason);
    }

    public void Dispose()
    {
        _robloxBridge.Dispose();
        _blenderBridge.Dispose();
        _browser.Dispose();
        _uia.Dispose();
    }

    private static ToolResult<object> Remeta(ToolResult<object> result, string requestId, DateTimeOffset started)
    {
        if (!result.Ok)
        {
            return ToolResult<object>.Failure(result.Error!, ResultMeta.Create(requestId, started), result.Performance, result.Warnings);
        }

        return ToolResult<object>.Success(result.Data!, ResultMeta.Create(requestId, started), result.Performance, result.StateChanged, result.Warnings);
    }

    private static string RequireString(JsonElement? parameters, string name)
    {
        var value = GetStringParam(parameters, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.");
        }

        return value;
    }

    private static string? GetStringParam(JsonElement? parameters, string name)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetString(parameters.Value, name);
    }

    private static bool? GetBoolParam(JsonElement? parameters, string name)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!parameters.Value.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int? GetIntParam(JsonElement? parameters, string name)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetInt(parameters.Value, name);
    }

    private static int RequireInt(JsonElement? parameters, string name)
    {
        var value = GetIntParam(parameters, name);
        if (value is null)
        {
            throw new ArgumentException($"{name} is required.");
        }

        return value.Value;
    }

    private static double? GetDoubleParam(JsonElement? parameters, string name)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!parameters.Value.TryGetProperty(name, out var el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.String when double.TryParse(el.GetString(), out var d) => d,
            _ => null
        };
    }

    private static IReadOnlyList<string> RequireStringList(JsonElement? parameters, string name)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object ||
            !parameters.Value.TryGetProperty(name, out var el))
        {
            throw new ArgumentException($"{name} is required.");
        }

        if (el.ValueKind == JsonValueKind.Array)
        {
            var list = el.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Cast<string>()
                .ToList();
            if (list.Count == 0)
            {
                throw new ArgumentException($"{name} must not be empty.");
            }

            return list;
        }

        if (el.ValueKind == JsonValueKind.String)
        {
            var parts = (el.GetString() ?? "")
                .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                throw new ArgumentException($"{name} must not be empty.");
            }

            return parts;
        }

        throw new ArgumentException($"{name} must be an array or '+'-joined string.");
    }

    private static BrowserSelector ParseBrowserSelector(JsonElement? parameters)
    {
        return TryParseBrowserSelector(parameters) ?? new BrowserSelector();
    }

    private static BrowserSelector? TryParseBrowserSelector(JsonElement? parameters)
    {
        if (parameters is null || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var raw = parameters.Value;
        JsonElement selectorEl = raw;
        if (raw.TryGetProperty("selector", out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            selectorEl = nested;
        }

        var selector = new BrowserSelector
        {
            Css = GetString(selectorEl, "css"),
            Role = GetString(selectorEl, "role"),
            Name = GetString(selectorEl, "name"),
            Text = GetString(selectorEl, "text"),
            Placeholder = GetString(selectorEl, "placeholder"),
            Label = GetString(selectorEl, "label"),
            TestId = GetString(selectorEl, "testId") ?? GetString(selectorEl, "testid")
        };

        if (selector.Css is null && selector.Role is null && selector.Name is null && selector.Text is null
            && selector.Placeholder is null && selector.Label is null && selector.TestId is null)
        {
            return null;
        }

        return selector;
    }
}
