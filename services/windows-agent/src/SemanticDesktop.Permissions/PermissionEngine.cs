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
        [Capabilities.PlanExecute] = PermissionDecisionKind.Allow,
        [Capabilities.SystemAdmin] = PermissionDecisionKind.Deny
    };

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
        return policy;
    }
}

public sealed class PermissionEngine
{
    private readonly PermissionPolicy _policy;
    private readonly ConcurrentDictionary<string, PermissionDecisionKind> _alwaysGrants = new(StringComparer.OrdinalIgnoreCase);

    public PermissionEngine(PermissionPolicy? policy = null)
    {
        _policy = policy ?? PermissionPolicy.CreateDefault();
    }

    public PermissionPolicy Policy => _policy;

    public static string MapActionToCapability(string action) =>
        action switch
        {
            CommandNames.WindowList => Capabilities.WindowObserve,
            CommandNames.WindowFocus => Capabilities.WindowControl,
            CommandNames.UiGetTree or CommandNames.UiFind or CommandNames.UiGetText => Capabilities.UiObserve,
            CommandNames.UiInvoke or CommandNames.UiSetValue => Capabilities.UiInteract,
            CommandNames.ProcessLaunch => Capabilities.ProcessLaunch,
            CommandNames.ProcessList => Capabilities.ProcessObserve,
            CommandNames.FilesystemExists or CommandNames.FilesystemList or CommandNames.FilesystemReadText
                => Capabilities.FilesystemRead,
            CommandNames.FilesystemWriteText => Capabilities.FilesystemWrite,
            CommandNames.DesktopGetState or CommandNames.DesktopGetCapabilities => Capabilities.DesktopObserve,
            CommandNames.WindowWaitFor => Capabilities.WindowObserve,
            CommandNames.UiWaitFor => Capabilities.UiObserve,
            CommandNames.PlanExecute or CommandNames.PlanGet or CommandNames.PlanCancel => Capabilities.PlanExecute,
            CommandNames.SystemPing or CommandNames.SessionCreate or CommandNames.SessionGet
                or CommandNames.PermissionApprove or CommandNames.PermissionDeny or CommandNames.PermissionPending
                or CommandNames.AuditList or CommandNames.SystemEmergencyStop or CommandNames.SystemEmergencyStopClear
                => Capabilities.DesktopObserve,
            _ => Capabilities.SystemAdmin
        };

    public static RiskClass MapActionToRisk(string action) =>
        action switch
        {
            CommandNames.WindowList or CommandNames.UiGetTree or CommandNames.UiFind or CommandNames.UiGetText
                or CommandNames.FilesystemExists or CommandNames.FilesystemList or CommandNames.FilesystemReadText
                or CommandNames.SystemPing or CommandNames.AuditList
                or CommandNames.SessionGet or CommandNames.PermissionPending
                or CommandNames.DesktopGetState or CommandNames.DesktopGetCapabilities
                or CommandNames.WindowWaitFor or CommandNames.UiWaitFor or CommandNames.ProcessList
                => RiskClass.Read,
            CommandNames.WindowFocus or CommandNames.UiInvoke or CommandNames.UiSetValue or CommandNames.ProcessLaunch
                or CommandNames.PlanExecute or CommandNames.PlanCancel or CommandNames.FilesystemWriteText
                => RiskClass.LowRiskWrite,
            CommandNames.SystemEmergencyStop or CommandNames.SystemEmergencyStopClear => RiskClass.Privileged,
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
