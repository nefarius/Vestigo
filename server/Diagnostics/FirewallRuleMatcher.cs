namespace Vestigo.Diagnostics;

/// <summary>Pure firewall-rule matching used before the result cap.</summary>
internal static class FirewallRuleMatcher
{
    public static bool Matches(FirewallRule rule, FirewallRuleFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.EnabledOnly && rule.Enabled != true)
        {
            return false;
        }

        if (!StringEquals(filter.Direction, rule.DirectionName))
        {
            return false;
        }

        if (!StringEquals(filter.Action, rule.ActionName))
        {
            return false;
        }

        if (!MatchesProfile(filter.Profile, rule.Profiles))
        {
            return false;
        }

        if (!Contains(filter.NameContains, rule.Name, rule.Description, rule.Grouping))
        {
            return false;
        }

        if (!Contains(filter.ProgramContains, rule.ApplicationName, rule.ServiceName, rule.LocalAppPackageId))
        {
            return false;
        }

        return MatchesPort(filter.Port, rule.LocalPorts, rule.RemotePorts);
    }

    private static bool StringEquals(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        return string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string? needle, params string?[] haystacks)
    {
        if (string.IsNullOrWhiteSpace(needle))
        {
            return true;
        }

        foreach (string? haystack in haystacks)
        {
            if (haystack is not null && haystack.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesProfile(string? profile, int? mask)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return true;
        }

        int bit = profile.Trim().ToLowerInvariant() switch
        {
            "domain" => 1,
            "private" => 2,
            "public" => 4,
            _ => 0
        };
        return bit != 0 && mask is int value && (value & bit) != 0;
    }

    private static bool MatchesPort(string? port, string? local, string? remote)
    {
        if (string.IsNullOrWhiteSpace(port))
        {
            return true;
        }

        string token = port.Trim();
        return ContainsToken(local, token) || ContainsToken(remote, token);
    }

    private static bool ContainsToken(string? expression, string token)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        if (expression.Equals("*", StringComparison.Ordinal) ||
            expression.Equals("any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string part in expression.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals(token, StringComparison.OrdinalIgnoreCase) ||
                part.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
