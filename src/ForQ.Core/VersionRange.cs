namespace ForQ.Core;

internal static class VersionRange
{
    public static bool Matches(string version, string range)
    {
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(range) || range.Trim() == "*")
            return true;

        var sets = range.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (sets.Length == 0)
            return true;

        var normalized = Normalize(version);
        return sets.Any(set => MatchesSet(normalized, set));
    }

    private static bool MatchesSet(string version, string set)
    {
        set = set.Trim();
        if (set.Length == 0)
            return true;

        if (set[0] is '[' or '(')
            return MatchesMaven(version, set);

        if (set.Contains(' '))
            return set.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .All(predicate => MatchesPredicate(version, predicate));

        return MatchesPredicate(version, set);
    }

    private static bool MatchesMaven(string version, string set)
    {
        if (set.Length < 3 || set[0] is not ('[' or '(') || set[^1] is not (']' or ')'))
            return true;

        var body = set[1..^1];
        var comma = body.IndexOf(',');
        if (comma < 0)
            return Compare(version, NormalizeBound(body)) == 0;

        var lower = NormalizeBound(body[..comma]);
        var upper = NormalizeBound(body[(comma + 1)..]);

        if (lower.Length > 0)
        {
            var comparison = Compare(version, lower);
            if (set[0] == '[' ? comparison < 0 : comparison <= 0)
                return false;
        }

        if (upper.Length > 0)
        {
            var comparison = Compare(version, upper);
            if (set[^1] == ']' ? comparison > 0 : comparison >= 0)
                return false;
        }

        return true;
    }

    private static bool MatchesPredicate(string version, string predicate)
    {
        predicate = predicate.Trim();
        if (predicate.Length == 0 || predicate is "*" or "x" or "X")
            return true;

        if (predicate.StartsWith(">=", StringComparison.Ordinal))
            return Compare(version, NormalizeBound(predicate[2..])) >= 0;

        if (predicate.StartsWith("<=", StringComparison.Ordinal))
            return Compare(version, NormalizeBound(predicate[2..])) <= 0;

        if (predicate.StartsWith("!=", StringComparison.Ordinal))
            return Compare(version, NormalizeBound(predicate[2..])) != 0;

        if (predicate.StartsWith("==", StringComparison.Ordinal))
            return Compare(version, NormalizeBound(predicate[2..])) == 0;

        if (predicate.StartsWith('>'))
            return Compare(version, NormalizeBound(predicate[1..])) > 0;

        if (predicate.StartsWith('<'))
            return Compare(version, NormalizeBound(predicate[1..])) < 0;

        if (predicate.StartsWith('='))
            return Compare(version, NormalizeBound(predicate[1..])) == 0;

        if (predicate.StartsWith('^'))
            return MatchesCaret(version, NormalizeBound(predicate[1..]));

        if (predicate.StartsWith('~'))
            return MatchesTilde(version, NormalizeBound(predicate[1..]));

        if (predicate.Contains('x') || predicate.Contains('X') || predicate.Contains('*'))
            return MatchesWildcard(version, predicate);

        return Compare(version, NormalizeBound(predicate)) == 0;
    }

    private static bool MatchesCaret(string version, string bound)
    {
        if (Compare(version, bound) < 0)
            return false;

        var parts = Numbers(bound);
        var major = parts.Length > 0 ? parts[0] : 0;
        var minor = parts.Length > 1 ? parts[1] : 0;
        var patch = parts.Length > 2 ? parts[2] : 0;

        var upper = major > 0
            ? $"{major + 1}.0.0"
            : minor > 0
                ? $"0.{minor + 1}.0"
                : $"0.0.{patch + 1}";

        return Compare(version, upper) < 0;
    }

    private static bool MatchesTilde(string version, string bound)
    {
        if (Compare(version, bound) < 0)
            return false;

        var parts = Numbers(bound);
        var upper = parts.Length <= 1
            ? $"{(parts.Length == 1 ? parts[0] : 0) + 1}.0.0"
            : $"{parts[0]}.{parts[1] + 1}.0";

        return Compare(version, upper) < 0;
    }

    private static bool MatchesWildcard(string version, string pattern)
    {
        var versionParts = CoreParts(version);
        var patternParts = CoreParts(pattern);

        for (var i = 0; i < patternParts.Length; i++)
        {
            var part = patternParts[i];
            if (part is "x" or "X" or "*" || part.Contains('x') || part.Contains('X') || part.Contains('*'))
                return true;

            var current = i < versionParts.Length ? versionParts[i] : "0";
            var left = ParseNumber(part);
            var right = ParseNumber(current);

            if (left.HasValue && right.HasValue)
            {
                if (left.Value != right.Value)
                    return false;
            }
            else if (!string.Equals(part, current, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static int Compare(string left, string right)
    {
        var leftVersion = Normalize(left);
        var rightVersion = Normalize(right);
        var leftParts = Numbers(leftVersion);
        var rightParts = Numbers(rightVersion);
        var count = Math.Max(leftParts.Length, rightParts.Length);

        for (var i = 0; i < count; i++)
        {
            var leftPart = i < leftParts.Length ? leftParts[i] : 0;
            var rightPart = i < rightParts.Length ? rightParts[i] : 0;
            if (leftPart != rightPart)
                return leftPart.CompareTo(rightPart);
        }

        var leftPre = PreRelease(leftVersion);
        var rightPre = PreRelease(rightVersion);

        if (leftPre.Length == 0 && rightPre.Length == 0)
            return 0;
        if (leftPre.Length == 0)
            return 1;
        if (rightPre.Length == 0)
            return -1;

        var leftIdentifiers = leftPre.Split('.');
        var rightIdentifiers = rightPre.Split('.');
        var identifiers = Math.Max(leftIdentifiers.Length, rightIdentifiers.Length);

        for (var i = 0; i < identifiers; i++)
        {
            if (i >= leftIdentifiers.Length)
                return -1;
            if (i >= rightIdentifiers.Length)
                return 1;

            var leftNumber = ParseNumber(leftIdentifiers[i]);
            var rightNumber = ParseNumber(rightIdentifiers[i]);

            if (leftNumber.HasValue && rightNumber.HasValue)
            {
                if (leftNumber.Value != rightNumber.Value)
                    return leftNumber.Value.CompareTo(rightNumber.Value);
            }
            else if (leftNumber.HasValue)
            {
                return -1;
            }
            else if (rightNumber.HasValue)
            {
                return 1;
            }
            else
            {
                var comparison = string.Compare(leftIdentifiers[i], rightIdentifiers[i], StringComparison.OrdinalIgnoreCase);
                if (comparison != 0)
                    return comparison;
            }
        }

        return 0;
    }

    private static string Normalize(string version)
    {
        version = version.Trim();
        if (version.StartsWith('v') && version.Length > 1 && char.IsDigit(version[1]))
            version = version[1..];

        var build = version.IndexOf('+');
        return build >= 0 ? version[..build] : version;
    }

    private static string NormalizeBound(string version)
    {
        var normalized = Normalize(version);
        return normalized.EndsWith('-') ? normalized[..^1] : normalized;
    }

    private static string PreRelease(string version)
    {
        var dash = version.IndexOf('-');
        return dash >= 0 ? version[(dash + 1)..] : "";
    }

    private static int[] Numbers(string version)
    {
        var core = CoreParts(version);
        return core.Select(part => ParseNumber(part) ?? 0).ToArray();
    }

    private static string[] CoreParts(string version)
    {
        var dash = version.IndexOf('-');
        var core = dash >= 0 ? version[..dash] : version;
        return core.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int? ParseNumber(string value)
    {
        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var number) ? number : null;
    }
}
