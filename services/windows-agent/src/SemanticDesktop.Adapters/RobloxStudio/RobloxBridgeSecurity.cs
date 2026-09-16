using System.Text.RegularExpressions;

namespace SemanticDesktop.Adapters.RobloxStudio;

internal static partial class RobloxBridgeSecurity
{
    public const int MaxSessionIdLength = 128;
    public const int MaxErrorMessageLength = 500;

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdPattern();

    public static bool TryNormalizeSessionId(string? sessionId, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            error = "sessionId_required";
            return false;
        }

        if (sessionId.Length > MaxSessionIdLength || !SessionIdPattern().IsMatch(sessionId))
        {
            error = "invalid_session_id";
            return false;
        }

        normalized = sessionId;
        return true;
    }

    public static string SanitizeErrorMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "request_failed";
        }

        var trimmed = message.Trim();
        return trimmed.Length <= MaxErrorMessageLength
            ? trimmed
            : trimmed[..MaxErrorMessageLength];
    }
}
