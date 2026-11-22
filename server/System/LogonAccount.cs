using System.Text;

namespace Vestigo.SystemInfo;

/// <summary>Validates local or domain account names used for AutoLogon and interactive logon.</summary>
public static class LogonAccount
{
    public const int MaxNameLength = 256;

    public static bool TryNormalize(
        string? userName,
        string? domain,
        out string normalizedUser,
        out string? normalizedDomain,
        out string error)
    {
        normalizedUser = "";
        normalizedDomain = null;
        error = "";
        if (string.IsNullOrWhiteSpace(userName))
        {
            error = "userName is required.";
            return false;
        }

        string user = userName.Trim();
        if (user.Length > MaxNameLength || ContainsControl(user))
        {
            error = "userName is invalid.";
            return false;
        }

        if (IsReserved(user))
        {
            error = "That account cannot be used for AutoLogon or interactive logon.";
            return false;
        }

        string? parsedDomain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();
        if (user.Contains('\\', StringComparison.Ordinal))
        {
            if (parsedDomain is not null)
            {
                error = "Pass domain in domain or as DOMAIN\\user, not both.";
                return false;
            }

            string[] parts = user.Split('\\', 2);
            parsedDomain = parts[0].Trim();
            user = parts[1].Trim();
            if (user.Length == 0 || parsedDomain.Length == 0 || user.Length > MaxNameLength || ContainsControl(user))
            {
                error = "userName is invalid.";
                return false;
            }

            if (IsReserved(user))
            {
                error = "That account cannot be used for AutoLogon or interactive logon.";
                return false;
            }
        }

        if (parsedDomain is not null && (parsedDomain.Length > MaxNameLength || ContainsControl(parsedDomain)))
        {
            error = "domain is invalid.";
            return false;
        }

        if (IsReserved(parsedDomain))
        {
            error = "That account cannot be used for AutoLogon or interactive logon.";
            return false;
        }

        normalizedUser = user;
        normalizedDomain = parsedDomain;
        return true;
    }

    public static string Display(string userName, string? domain) =>
        string.IsNullOrWhiteSpace(domain) ? userName : domain + "\\" + userName;

    public static bool TryNormalizeLogonCount(int? logonCount, out int? normalized, out string error)
    {
        normalized = logonCount;
        error = "";
        if (logonCount is null)
        {
            return true;
        }

        if (logonCount is < 1 or > 1_000_000)
        {
            error = "logonCount must be between 1 and 1000000.";
            return false;
        }

        return true;
    }

    private static bool ContainsControl(string value)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c) || c == '\0')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReserved(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
               || name.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
               || name.Equals("NT AUTHORITY", StringComparison.OrdinalIgnoreCase)
               || name.Equals("LocalService", StringComparison.OrdinalIgnoreCase)
               || name.Equals("NetworkService", StringComparison.OrdinalIgnoreCase);
    }
}
