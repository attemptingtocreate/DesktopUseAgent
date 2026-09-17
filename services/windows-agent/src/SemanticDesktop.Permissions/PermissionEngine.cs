using System.Collections.Concurrent;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Security;

namespace SemanticDesktop.Permissions;

public sealed class PathRule
{
    public required string PathPrefix { get; init; }
    public bool AllowRead { get; init; } = true;
    public bool AllowWrite { get; init; }
    public bool Deny { get; init; }
}

public sealed class AppPermissionRule
{
    public required string ProcessName { get; init; }
    public PermissionDecisionKind Observe { get; init; } = PermissionDecisionKind.Allow;
    public PermissionDecisionKind Interact { get; init; } = PermissionDecisionKind.Allow;
}

public sealed class PermissionPolicy
{
    public Dictionary<string, PermissionDecisionKind> CapabilityDefaults { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [Capabilities.DesktopObserve] = PermissionDecisionKind.Allow,
        [Capabilities.WindowObserve] = PermissionDecisionKind.Allow,
        [Capabilities.WindowControl] = PermissionDecisionKind.Allow,
        [Capabilities.UiObserve] = PermissionDecisionKind.Allow,
        [Capabilities.UiInteract] = PermissionDecisionKind.Allow,
        [Capabilities.FilesystemRead] = PermissionDecisionKind.Allow,
        [Capabilities.FilesystemWrite] = PermissionDecisionKind.Ask,
        [Capabilities.FilesystemDelete] = PermissionDecisionKind.Ask,
        [Capabilities.ProcessObserve] = PermissionDecisionKind.Allow,
        [Capabilities.ProcessLaunch] = PermissionDecisionKind.Allow,
        [Capabilities.ProcessTerminate] = PermissionDecisionKind.Ask,
        [Capabilities.ShellExecute] = PermissionDecisionKind.Ask,
        [Capabilities.ClipboardRead] = PermissionDecisionKind.Allow,
        [Capabilities.ClipboardWrite] = PermissionDecisionKind.Ask,
        [Capabilities.SystemPower] = PermissionDecisionKind.Ask,
        [Capabilities.PlanExecute] = PermissionDecisionKind.Allow,
        [Capabilities.BrowserObserve] = PermissionDecisionKind.Allow,
        [Capabilities.BrowserInteract] = PermissionDecisionKind.Allow,
        [Capabilities.AdapterObserve] = PermissionDecisionKind.Allow,
        [Capabilities.AdapterInteract] = PermissionDecisionKind.Ask,
        [Capabilities.InputKeyboard] = PermissionDecisionKind.Ask,
        [Capabilities.InputMouse] = PermissionDecisionKind.Ask,
        [Capabilities.VisionCapture] = PermissionDecisionKind.Ask,
        [Capabilities.SystemAdmin] = PermissionDecisionKind.Deny
    };

    public int SchemaVersion { get; set; } = 1;
    public List<PathRule> PathRules { get; } = new();
    public List<AppPermissionRule> AppRules { get; } = new();

    public static PermissionPolicy CreateDefault()
    {
        var policy = new PermissionPolicy();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var temp = Path.GetTempPath();

        policy.PathRules.Add(new PathRule { PathPrefix = Path.GetFullPath(temp), AllowRead = true, AllowWrite = true });
        if (!string.IsNullOrWhiteSpace(docs))
        {
            policy.PathRules.Add(new PathRule { PathPrefix = Path.GetFullPath(docs), AllowRead = true, AllowWrite = true });
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            policy.PathRules.Add(new PathRule
            {
                PathPrefix = Path.GetFullPath(Path.Combine(userProfile, "Downloads")),
                AllowRead = true,
                AllowWrite = true
            });
        }

        policy.PathRules.Add(new PathRule { PathPrefix = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), Deny = true });
        policy.PathRules.Add(new PathRule
        {
            PathPrefix = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            AllowRead = true,
            AllowWrite = false
        });

        policy.AppRules.Add(new AppPermissionRule
        {
            ProcessName = "KeePass",
            Observe = PermissionDecisionKind.Deny,
            Interact = PermissionDecisionKind.Deny
        });
        policy.SchemaVersion = 1;
        return policy;
    }

    public static PermissionPolicy Migrate(PermissionPolicy? incoming)
    {
        var fresh = CreateDefault();
        if (incoming is null)
        {
            return fresh;
        }

        foreach (var kv in incoming.CapabilityDefaults)
        {
            fresh.CapabilityDefaults[kv.Key] = kv.Value;
        }

        foreach (var key in fresh.CapabilityDefaults.Keys.ToArray())
        {
            if (!incoming.CapabilityDefaults.ContainsKey(key))
            {
                // keep fresh default for newly introduced capabilities
            }
        }

        fresh.PathRules.Clear();
        fresh.PathRules.AddRange(incoming.PathRules.Count > 0 ? incoming.PathRules : CreateDefault().PathRules);
        fresh.AppRules.Clear();
        fresh.AppRules.AddRange(incoming.AppRules.Count > 0 ? incoming.AppRules : CreateDefault().AppRules);
        fresh.SchemaVersion = 1;
        return fresh;
    }
}

public sealed class PermissionEngine
{
    private readonly PermissionPolicy _policy;
    private readonly ConcurrentDictionary<string, PermissionDecisionKind> _alwaysGrants = new(StringComparer.OrdinalIgnoreCase);

    public PermissionEngine(PermissionPolicy? policy = null)
    {
        _policy = PermissionPolicy.Migrate(policy);
    }

    public PermissionPolicy Policy => _policy;

    public static string MapActionToCapability(string action) =>
        action switch
        {
            CommandNames.MonitorList => Capabilities.WindowObserve,
            CommandNames.WindowList or CommandNames.WindowGet => Capabilities.WindowObserve,
            CommandNames.WindowFocus or CommandNames.WindowMinimize or CommandNames.WindowMaximize
                or CommandNames.WindowRestore or CommandNames.WindowMove or CommandNames.WindowResize
                => Capabilities.WindowControl,
            CommandNames.UiGetTree or CommandNames.UiFind or CommandNames.UiGetText => Capabilities.UiObserve,
            CommandNames.UiInvoke or CommandNames.UiSetValue => Capabilities.UiInteract,
            CommandNames.ProcessLaunch or CommandNames.AppLaunch => Capabilities.ProcessLaunch,
            CommandNames.ShellOpen or CommandNames.FilesystemOpen => Capabilities.ShellExecute,
            CommandNames.ProcessList => Capabilities.ProcessObserve,
            CommandNames.ClipboardRead => Capabilities.ClipboardRead,
            CommandNames.ClipboardWrite => Capabilities.ClipboardWrite,
            CommandNames.SystemPower => Capabilities.SystemPower,
            CommandNames.SearchFiles => Capabilities.FilesystemRead,
            CommandNames.FilesystemExists or CommandNames.FilesystemList or CommandNames.FilesystemReadText
                or CommandNames.FilesystemStat or CommandNames.FilesystemInspect
                => Capabilities.FilesystemRead,
            CommandNames.FilesystemWriteText or CommandNames.FilesystemCopy or CommandNames.FilesystemMove
                => Capabilities.FilesystemWrite,
            CommandNames.FilesystemDelete => Capabilities.FilesystemDelete,
            CommandNames.DesktopGetState or CommandNames.DesktopGetCapabilities
                or CommandNames.DesktopDescribe or CommandNames.DesktopGetGraph
                or CommandNames.DesktopBatch or CommandNames.DesktopDiff
                or CommandNames.EventsSubscribe or CommandNames.EventsPoll or CommandNames.EventsUnsubscribe
                or CommandNames.SystemUpdateCheck or CommandNames.SystemTelemetryGet
                or CommandNames.SystemSecurityReview or CommandNames.SystemIntegrity
                => Capabilities.DesktopObserve,
            CommandNames.WindowWaitFor => Capabilities.WindowObserve,
            CommandNames.UiWaitFor => Capabilities.UiObserve,
            CommandNames.PlanExecute or CommandNames.PlanGet or CommandNames.PlanCancel => Capabilities.PlanExecute,
            CommandNames.BrowserList or CommandNames.BrowserTabs or CommandNames.BrowserGetTab
                or CommandNames.BrowserQuery or CommandNames.BrowserQueryAll
                or CommandNames.BrowserGetText or CommandNames.BrowserGetDom or CommandNames.BrowserGetAccessibilityTree
                or CommandNames.BrowserWaitFor or CommandNames.BrowserWaitForNavigation or CommandNames.BrowserWaitForNetworkIdle
                or CommandNames.BrowserGetDownloads
                => Capabilities.BrowserObserve,
            CommandNames.BrowserOpenTab or CommandNames.BrowserCloseTab or CommandNames.BrowserNavigate
                or CommandNames.BrowserBack or CommandNames.BrowserForward or CommandNames.BrowserReload
                or CommandNames.BrowserClick or CommandNames.BrowserFill or CommandNames.BrowserSelect or CommandNames.BrowserFocus
                => Capabilities.BrowserInteract,
            CommandNames.SystemPing or CommandNames.SessionCreate or CommandNames.SessionGet or CommandNames.SessionList
                or CommandNames.PermissionApprove or CommandNames.PermissionDeny or CommandNames.PermissionPending
                or CommandNames.PermissionPolicyGet or CommandNames.PermissionPolicySet
                or CommandNames.AuditList or CommandNames.SystemStatus or CommandNames.SystemPerformance
                or CommandNames.SystemEmergencyStop or CommandNames.SystemEmergencyStopClear
                or CommandNames.SystemUpdateApply or CommandNames.SystemTelemetrySet
                => Capabilities.DesktopObserve,
            CommandNames.AdapterList or CommandNames.AdapterCapabilities
                or CommandNames.BlenderGetScene or CommandNames.BlenderGetObjects
                or CommandNames.VsCodeGetWorkspace or CommandNames.VisualStudioGetSolution
                or CommandNames.RobloxPluginPing or CommandNames.RobloxGetHierarchy or CommandNames.RobloxGetSelection
                or CommandNames.RobloxFindInstances or CommandNames.RobloxGetScriptSource
                => Capabilities.AdapterObserve,
            CommandNames.AdapterExecute
                or CommandNames.BlenderOpen or CommandNames.BlenderSelectObject or CommandNames.BlenderExecutePython
                or CommandNames.BlenderExport or CommandNames.BlenderSave
                or CommandNames.BlenderBatch or CommandNames.BlenderRender or CommandNames.BlenderImportMesh
                or CommandNames.BlenderCreateMesh or CommandNames.BlenderExportForRoblox
                or CommandNames.BlenderMeshExtrude or CommandNames.BlenderMeshInset or CommandNames.BlenderMeshBevel
                or CommandNames.BlenderMeshLoopCut or CommandNames.BlenderModifierBoolean or CommandNames.BlenderModifierMirror
                or CommandNames.BlenderModifierArray or CommandNames.BlenderMaterialSet or CommandNames.BlenderUvUnwrap
                or CommandNames.BlenderSelectGeometry
                or CommandNames.VsCodeOpenFile or CommandNames.VsCodeOpenFolder or CommandNames.VsCodeExecuteCommand
                or CommandNames.VisualStudioBuild or CommandNames.VisualStudioOpenFile or CommandNames.VisualStudioOpenSolution
                or             CommandNames.DiscordOpen or CommandNames.DiscordJoinVoice or CommandNames.DiscordQuickSwitch
                or CommandNames.OfficeOpen or CommandNames.OfficeMailCompose or CommandNames.OfficeCalendarWeek
                or CommandNames.RobloxOpenPlace or CommandNames.RobloxSelect or CommandNames.RobloxSetProperty
                or CommandNames.RobloxCreateInstance or CommandNames.RobloxDestroyInstance or CommandNames.RobloxCloneInstance
                or CommandNames.RobloxSetParent or CommandNames.RobloxSetScriptSource or CommandNames.RobloxBatch
                or CommandNames.RobloxPlaytestStart or CommandNames.RobloxPlaytestStop
                or CommandNames.RobloxTerrainFillBlock or CommandNames.RobloxTerrainFillBall or CommandNames.RobloxTerrainClear
                or CommandNames.RobloxInsertAsset or CommandNames.RobloxImportLocalModel
                or CommandNames.RobloxPublishPlace or CommandNames.RobloxExecuteLuau
                => Capabilities.AdapterInteract,
            CommandNames.InputMouseMove or CommandNames.InputMouseClick or CommandNames.InputMouseDrag or CommandNames.InputScroll
                => Capabilities.InputMouse,
            CommandNames.InputKey or CommandNames.InputHotkey or CommandNames.InputType
                or CommandNames.MediaTransport or CommandNames.MediaVolume
                => Capabilities.InputKeyboard,
            CommandNames.VisionCaptureScreen or CommandNames.VisionCaptureWindow or CommandNames.VisionCaptureRegion
                or CommandNames.VisionOcr
                => Capabilities.VisionCapture,
            _ => Capabilities.SystemAdmin
        };

    public static RiskClass MapActionToRisk(string action) =>
        action switch
        {
            CommandNames.MonitorList or CommandNames.WindowList or CommandNames.WindowGet
                or CommandNames.UiGetTree or CommandNames.UiFind or CommandNames.UiGetText
                or CommandNames.FilesystemExists or CommandNames.FilesystemList or CommandNames.FilesystemReadText
                or CommandNames.FilesystemStat or CommandNames.FilesystemInspect
                or CommandNames.SearchFiles or CommandNames.ClipboardRead
                or CommandNames.SystemPing or CommandNames.AuditList or CommandNames.SystemStatus or CommandNames.SystemPerformance
                or CommandNames.SessionGet or CommandNames.SessionList or CommandNames.PermissionPending
                or CommandNames.PermissionPolicyGet
                or CommandNames.DesktopGetState or CommandNames.DesktopGetCapabilities
                or CommandNames.DesktopDescribe or CommandNames.DesktopGetGraph
                or CommandNames.DesktopBatch or CommandNames.DesktopDiff
                or CommandNames.EventsSubscribe or CommandNames.EventsPoll or CommandNames.EventsUnsubscribe
                or CommandNames.SystemUpdateCheck or CommandNames.SystemTelemetryGet
                or CommandNames.SystemSecurityReview or CommandNames.SystemIntegrity
                or CommandNames.WindowWaitFor or CommandNames.UiWaitFor or CommandNames.ProcessList
                or CommandNames.BrowserList or CommandNames.BrowserTabs or CommandNames.BrowserGetTab
                or CommandNames.BrowserQuery or CommandNames.BrowserQueryAll
                or CommandNames.BrowserGetText or CommandNames.BrowserGetDom or CommandNames.BrowserGetAccessibilityTree
                or CommandNames.BrowserWaitFor or CommandNames.BrowserWaitForNavigation or CommandNames.BrowserWaitForNetworkIdle
                or CommandNames.BrowserGetDownloads
                or CommandNames.AdapterList or CommandNames.AdapterCapabilities
                or CommandNames.BlenderGetScene or CommandNames.BlenderGetObjects
                or CommandNames.VsCodeGetWorkspace or CommandNames.VisualStudioGetSolution
                or CommandNames.RobloxPluginPing or CommandNames.RobloxGetHierarchy or CommandNames.RobloxGetSelection
                or CommandNames.RobloxFindInstances or CommandNames.RobloxGetScriptSource
                => RiskClass.Read,
            CommandNames.WindowFocus or CommandNames.WindowMinimize or CommandNames.WindowMaximize
                or CommandNames.WindowRestore or CommandNames.WindowMove or CommandNames.WindowResize
                or CommandNames.UiInvoke or CommandNames.UiSetValue or CommandNames.ProcessLaunch or CommandNames.AppLaunch
                or CommandNames.ShellOpen or CommandNames.FilesystemOpen or CommandNames.ClipboardWrite
                or CommandNames.PlanExecute or CommandNames.PlanCancel or CommandNames.FilesystemWriteText
                or CommandNames.FilesystemCopy or CommandNames.FilesystemMove
                or CommandNames.BrowserOpenTab or CommandNames.BrowserCloseTab or CommandNames.BrowserNavigate
                or CommandNames.BrowserBack or CommandNames.BrowserForward or CommandNames.BrowserReload
                or CommandNames.BrowserClick or CommandNames.BrowserFill or CommandNames.BrowserSelect or CommandNames.BrowserFocus
                or CommandNames.PermissionPolicySet
                or CommandNames.AdapterExecute
                or CommandNames.BlenderOpen or CommandNames.BlenderSelectObject or CommandNames.BlenderExecutePython
                or CommandNames.BlenderExport or CommandNames.BlenderSave
                or CommandNames.BlenderBatch or CommandNames.BlenderRender or CommandNames.BlenderImportMesh
                or CommandNames.BlenderCreateMesh or CommandNames.BlenderExportForRoblox
                or CommandNames.BlenderMeshExtrude or CommandNames.BlenderMeshInset or CommandNames.BlenderMeshBevel
                or CommandNames.BlenderMeshLoopCut or CommandNames.BlenderModifierBoolean or CommandNames.BlenderModifierMirror
                or CommandNames.BlenderModifierArray or CommandNames.BlenderMaterialSet or CommandNames.BlenderUvUnwrap
                or CommandNames.BlenderSelectGeometry
                or CommandNames.VsCodeOpenFile or CommandNames.VsCodeOpenFolder or CommandNames.VsCodeExecuteCommand
                or CommandNames.VisualStudioBuild or CommandNames.VisualStudioOpenFile or CommandNames.VisualStudioOpenSolution
                or             CommandNames.DiscordOpen or CommandNames.DiscordJoinVoice or CommandNames.DiscordQuickSwitch
                or CommandNames.OfficeOpen or CommandNames.OfficeMailCompose
                or CommandNames.RobloxOpenPlace or CommandNames.RobloxSelect or CommandNames.RobloxSetProperty
                or CommandNames.RobloxCreateInstance or CommandNames.RobloxDestroyInstance or CommandNames.RobloxCloneInstance
                or CommandNames.RobloxSetParent or CommandNames.RobloxSetScriptSource or CommandNames.RobloxBatch
                or CommandNames.RobloxPlaytestStart or CommandNames.RobloxPlaytestStop
                or CommandNames.RobloxTerrainFillBlock or CommandNames.RobloxTerrainFillBall or CommandNames.RobloxTerrainClear
                or CommandNames.RobloxInsertAsset or CommandNames.RobloxImportLocalModel
                or CommandNames.RobloxPublishPlace
                => RiskClass.LowRiskWrite,
            CommandNames.OfficeCalendarWeek => RiskClass.Read,
            CommandNames.FilesystemDelete => RiskClass.Destructive,
            CommandNames.SystemPower => RiskClass.Privileged,
            CommandNames.RobloxExecuteLuau
                => RiskClass.HighRiskWrite,
            CommandNames.InputMouseMove or CommandNames.InputScroll => RiskClass.LowRiskWrite,
            CommandNames.MediaTransport or CommandNames.MediaVolume => RiskClass.LowRiskWrite,
            CommandNames.InputMouseClick or CommandNames.InputMouseDrag
                or CommandNames.InputKey or CommandNames.InputHotkey or CommandNames.InputType
                => RiskClass.HighRiskWrite,
            CommandNames.VisionCaptureScreen or CommandNames.VisionCaptureWindow or CommandNames.VisionCaptureRegion
                or CommandNames.VisionOcr
                => RiskClass.Read,
            CommandNames.SystemEmergencyStop or CommandNames.SystemEmergencyStopClear
                or CommandNames.SystemUpdateApply or CommandNames.SystemTelemetrySet => RiskClass.Privileged,
            _ => RiskClass.HighRiskWrite
        };

    public PermissionEvaluation Evaluate(
        AgentSession session,
        string action,
        string? path = null,
        string? application = null)
    {
        var capability = MapActionToCapability(action);
        var risk = MapActionToRisk(action);

        if (!string.IsNullOrWhiteSpace(application))
        {
            var appRule = _policy.AppRules.FirstOrDefault(r =>
                application.Contains(r.ProcessName, StringComparison.OrdinalIgnoreCase));
            if (appRule is not null)
            {
                var appDecision = capability is Capabilities.UiInteract or Capabilities.WindowControl
                    ? appRule.Interact
                    : appRule.Observe;
                if (appDecision != PermissionDecisionKind.Allow)
                {
                    return new PermissionEvaluation
                    {
                        Capability = capability,
                        Decision = appDecision,
                        Risk = risk,
                        Application = application,
                        Reason = $"Application policy for '{appRule.ProcessName}'."
                    };
                }
            }
        }

        if (capability is Capabilities.FilesystemRead or Capabilities.FilesystemWrite or Capabilities.FilesystemDelete)
        {
            var pathDecision = EvaluatePath(path, capability);
            if (pathDecision is not null)
            {
                return new PermissionEvaluation
                {
                    Capability = pathDecision.Capability,
                    Decision = pathDecision.Decision,
                    Risk = risk,
                    Target = pathDecision.Target,
                    Application = application,
                    Reason = pathDecision.Reason
                };
            }
        }

        if (_alwaysGrants.TryGetValue(capability, out var always))
        {
            return new PermissionEvaluation
            {
                Capability = capability,
                Decision = always,
                Risk = risk,
                Target = path,
                Application = application,
                Reason = "Always grant."
            };
        }

        if (session.SessionGrants.Contains(capability))
        {
            return new PermissionEvaluation
            {
                Capability = capability,
                Decision = PermissionDecisionKind.Allow,
                Risk = risk,
                Target = path,
                Application = application,
                Reason = "Session grant."
            };
        }

        if (session.CapabilityOverrides.TryGetValue(capability, out var over))
        {
            return new PermissionEvaluation
            {
                Capability = capability,
                Decision = over,
                Risk = risk,
                Target = path,
                Application = application,
                Reason = "Session override."
            };
        }

        var decision = _policy.CapabilityDefaults.TryGetValue(capability, out var d)
            ? d
            : PermissionDecisionKind.Deny;

        return new PermissionEvaluation
        {
            Capability = capability,
            Decision = decision,
            Risk = risk,
            Target = path,
            Application = application,
            Reason = "Capability default."
        };
    }

    public void Grant(AgentSession session, string capability, PermissionGrantScope scope)
    {
        switch (scope)
        {
            case PermissionGrantScope.Once:
                break;
            case PermissionGrantScope.Session:
                session.SessionGrants.Add(capability);
                break;
            case PermissionGrantScope.Always:
                _alwaysGrants[capability] = PermissionDecisionKind.Allow;
                break;
        }
    }

    public object SnapshotPolicy() => new
    {
        capabilities = _policy.CapabilityDefaults
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new { capability = kv.Key, decision = kv.Value.ToString() })
            .ToArray(),
        schemaVersion = _policy.SchemaVersion,
        pathRules = _policy.PathRules
            .Select(r => new
            {
                pathPrefix = r.PathPrefix,
                allowRead = r.AllowRead,
                allowWrite = r.AllowWrite,
                deny = r.Deny
            })
            .ToArray(),
        appRules = _policy.AppRules
            .Select(r => new
            {
                processName = r.ProcessName,
                observe = r.Observe.ToString(),
                interact = r.Interact.ToString()
            })
            .ToArray()
    };

    public bool SetCapabilityDefault(string capability, PermissionDecisionKind decision)
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            return false;
        }

        _policy.CapabilityDefaults[capability.Trim()] = decision;
        return true;
    }

    public bool UpsertAppRule(string processName, PermissionDecisionKind observe, PermissionDecisionKind interact)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var existing = _policy.AppRules.FirstOrDefault(r =>
            string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _policy.AppRules.Remove(existing);
        }

        _policy.AppRules.Add(new AppPermissionRule
        {
            ProcessName = processName.Trim(),
            Observe = observe,
            Interact = interact
        });
        return true;
    }

    public bool UpsertPathRule(string pathPrefix, bool allowRead, bool allowWrite, bool deny)
    {
        if (string.IsNullOrWhiteSpace(pathPrefix))
        {
            return false;
        }

        var full = Path.GetFullPath(pathPrefix);
        var existing = _policy.PathRules.FirstOrDefault(r =>
            string.Equals(r.PathPrefix, full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _policy.PathRules.Remove(existing);
        }

        _policy.PathRules.Add(new PathRule
        {
            PathPrefix = full,
            AllowRead = allowRead,
            AllowWrite = allowWrite,
            Deny = deny
        });
        return true;
    }

    private PermissionEvaluation? EvaluatePath(string? path, string capability)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new PermissionEvaluation
            {
                Capability = capability,
                Decision = PermissionDecisionKind.Deny,
                Risk = RiskClass.HighRiskWrite,
                Reason = "Path required."
            };
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return new PermissionEvaluation
            {
                Capability = capability,
                Decision = PermissionDecisionKind.Deny,
                Risk = RiskClass.HighRiskWrite,
                Target = path,
                Reason = "Invalid path."
            };
        }

        // Longest matching prefix wins.
        var match = _policy.PathRules
            .Select(r => (rule: r, prefix: Path.GetFullPath(r.PathPrefix)))
            .Where(x => full.StartsWith(x.prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.prefix.Length)
            .Select(x => x.rule)
            .FirstOrDefault();

        if (match is null)
        {
            // Unscoped paths: read allow, write ask.
            return new PermissionEvaluation
            {
                Capability = capability,
                Decision = capability == Capabilities.FilesystemRead
                    ? PermissionDecisionKind.Allow
                    : PermissionDecisionKind.Ask,
                Risk = RiskClass.LowRiskWrite,
                Target = full,
                Reason = "No explicit path rule."
            };
        }

        if (match.Deny)
        {
            return new PermissionEvaluation
            {
                Capability = capability,
                Decision = PermissionDecisionKind.Deny,
                Risk = RiskClass.Destructive,
                Target = full,
                Reason = "Path denied by policy."
            };
        }

        if (capability == Capabilities.FilesystemRead)
        {
            return new PermissionEvaluation
            {
                Capability = capability,
                Decision = match.AllowRead ? PermissionDecisionKind.Allow : PermissionDecisionKind.Deny,
                Risk = RiskClass.Read,
                Target = full,
                Reason = "Path read rule."
            };
        }

        return new PermissionEvaluation
        {
            Capability = capability,
            Decision = match.AllowWrite ? PermissionDecisionKind.Allow : PermissionDecisionKind.Deny,
            Risk = RiskClass.LowRiskWrite,
            Target = full,
            Reason = "Path write rule."
        };
    }
}
