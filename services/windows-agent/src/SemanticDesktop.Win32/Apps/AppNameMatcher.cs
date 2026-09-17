namespace SemanticDesktop.Win32.Apps;

/// <summary>Unit-testable display-name matching (exact / contains / fuzzy token overlap).</summary>
public static class AppNameMatcher
{
    /// <summary>Higher is better. Returns -1 when there is no usable match.</summary>
    public static int Score(string query, string candidate)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
        {
            return -1;
        }

        var q = Normalize(query);
        var c = Normalize(candidate);
        if (q.Length == 0 || c.Length == 0)
        {
            return -1;
        }

        if (string.Equals(q, c, StringComparison.Ordinal))
        {
            return 1_000;
        }

        if (c.StartsWith(q, StringComparison.Ordinal))
        {
            return 800 + Math.Min(99, q.Length);
        }

        if (c.Contains(q, StringComparison.Ordinal))
        {
            return 600 + Math.Min(99, q.Length);
        }

        if (q.Contains(c, StringComparison.Ordinal) && c.Length >= 3)
        {
            return 400 + Math.Min(99, c.Length);
        }

        var qTokens = Tokenize(q);
        var cTokens = Tokenize(c);
        if (qTokens.Count == 0 || cTokens.Count == 0)
        {
            return -1;
        }

        var hits = qTokens.Count(qt =>
            cTokens.Any(ct =>
                ct.Equals(qt, StringComparison.Ordinal) ||
                ct.StartsWith(qt, StringComparison.Ordinal) ||
                (qt.Length >= 3 && ct.Contains(qt, StringComparison.Ordinal))));

        if (hits == 0)
        {
            return -1;
        }

        var ratio = (double)hits / qTokens.Count;
        if (ratio < 0.5 && hits < qTokens.Count)
        {
            return -1;
        }

        return (int)(200 + ratio * 100) + hits;
    }

    public static T? PickBest<T>(string query, IEnumerable<T> items, Func<T, string> nameSelector)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(nameSelector);

        T? best = default;
        var bestScore = -1;
        foreach (var item in items)
        {
            var name = nameSelector(item);
            var score = Score(query, name);
            if (score > bestScore)
            {
                bestScore = score;
                best = item;
            }
        }

        return bestScore >= 0 ? best : default;
    }

    internal static string Normalize(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant();
        if (trimmed.EndsWith(".exe", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^4];
        }

        return trimmed;
    }

    private static List<string> Tokenize(string normalized) =>
        normalized
            .Split(new[] { ' ', '-', '_', '.', '+', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0)
            .ToList();
}
