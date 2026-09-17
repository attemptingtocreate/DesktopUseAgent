using System.Diagnostics;
using SemanticDesktop.Core.Errors;

namespace SemanticDesktop.Win32.Shell;

public static class ShellOpenService
{
    public static object Open(string target, string[]? args = null)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("target is required.", nameof(target));
        }

        var trimmed = target.Trim();
        var startInfo = new ProcessStartInfo
        {
            FileName = trimmed,
            UseShellExecute = true
        };
        if (args is { Length: > 0 })
        {
            startInfo.Arguments = string.Join(" ", args.Select(QuoteArg));
        }

        try
        {
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{ErrorCodes.ProcessFailed}: {ex.Message}", ex);
        }

        return new { opened = true, target = trimmed };
    }

    private static string QuoteArg(string arg) =>
        arg.Contains(' ') || arg.Contains('"')
            ? "\"" + arg.Replace("\"", "\\\"") + "\""
            : arg;
}
