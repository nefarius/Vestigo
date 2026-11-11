namespace Vestigo.Diagnostics;

/// <summary>Names only the documented Windows Firewall constants. Other values stay unnamed.</summary>
internal static class FirewallProjection
{
    public const int MaxTextChars = 512;

    private const int Domain = 1;
    private const int Private = 2;
    private const int Public = 4;

    public static string? Bound(string? value, ref bool truncated)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length <= MaxTextChars)
        {
            return value;
        }

        truncated = true;
        return value[..MaxTextChars];
    }

    public static IReadOnlyList<string> CurrentProfileNames(int mask, out int unknownBits)
    {
        List<string> names = [];
        if ((mask & Domain) != 0)
        {
            names.Add("domain");
        }

        if ((mask & Private) != 0)
        {
            names.Add("private");
        }

        if ((mask & Public) != 0)
        {
            names.Add("public");
        }

        unknownBits = mask & ~(Domain | Private | Public);
        return names;
    }

    public static string? ActionName(int action) => action switch
    {
        0 => "block",
        1 => "allow",
        _ => null
    };

    public static string? DirectionName(int direction) => direction switch
    {
        1 => "in",
        2 => "out",
        _ => null
    };

    public static string? ProtocolName(int protocol) => protocol switch
    {
        6 => "tcp",
        17 => "udp",
        256 => "any",
        _ => null
    };

    public static string? ModifyStateName(int state) => state switch
    {
        0 => "ok",
        1 => "gpOverride",
        2 => "inboundBlocked",
        _ => null
    };

    public static string? EdgeTraversalName(int value) => value switch
    {
        0 => "deny",
        1 => "allow",
        2 => "deferToApp",
        3 => "deferToUser",
        _ => null
    };
}
