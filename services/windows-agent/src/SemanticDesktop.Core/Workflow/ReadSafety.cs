using SemanticDesktop.Core.Commands;

namespace SemanticDesktop.Core.Workflow;

public static class ReadSafety
{
    public static bool IsSafeRead(string? method) =>
        method switch
        {
            CommandNames.WindowList or CommandNames.UiGetTree or CommandNames.UiFind or CommandNames.UiGetText
                or CommandNames.FilesystemExists or CommandNames.FilesystemList or CommandNames.FilesystemReadText
                or CommandNames.FilesystemStat or CommandNames.FilesystemInspect
                or CommandNames.DesktopGetState or CommandNames.DesktopGetCapabilities
                or CommandNames.DesktopDescribe or CommandNames.DesktopGetGraph or CommandNames.DesktopDiff
                or CommandNames.ProcessList or CommandNames.WindowWaitFor or CommandNames.UiWaitFor
                or CommandNames.BrowserList or CommandNames.BrowserTabs or CommandNames.BrowserGetTab
                or CommandNames.BrowserQuery or CommandNames.BrowserQueryAll
                or CommandNames.BrowserGetText or CommandNames.BrowserGetDom or CommandNames.BrowserGetAccessibilityTree
                or CommandNames.BrowserWaitFor or CommandNames.BrowserWaitForNavigation or CommandNames.BrowserWaitForNetworkIdle
                or CommandNames.BrowserGetDownloads
                or CommandNames.AdapterList or CommandNames.AdapterCapabilities
                or CommandNames.BlenderGetScene or CommandNames.BlenderGetObjects
                or CommandNames.VsCodeGetWorkspace or CommandNames.VisualStudioGetSolution
                or CommandNames.SystemPing or CommandNames.SystemStatus or CommandNames.AuditList
                or CommandNames.SessionGet or CommandNames.SessionList
                or CommandNames.EventsPoll
                => true,
            _ => false
        };

    public static bool IsCacheable(string? method) =>
        method switch
        {
            CommandNames.WindowList or CommandNames.ProcessList
                or CommandNames.DesktopGetState or CommandNames.DesktopGetCapabilities
                or CommandNames.DesktopDescribe or CommandNames.DesktopGetGraph
                or CommandNames.FilesystemList or CommandNames.FilesystemExists
                or CommandNames.FilesystemStat or CommandNames.FilesystemInspect
                => true,
            _ => false
        };

    public static bool IsParallelSafe(string? method) =>
        method is CommandNames.FilesystemExists
            or CommandNames.FilesystemList
            or CommandNames.FilesystemReadText
            or CommandNames.FilesystemStat
            or CommandNames.FilesystemInspect;

    public static bool IsMutation(string? method) =>
        method is not null && !IsSafeRead(method)
        && method is not CommandNames.DesktopBatch
        && method is not CommandNames.EventsSubscribe
        && method is not CommandNames.EventsUnsubscribe
        && method is not CommandNames.PlanGet;
}
