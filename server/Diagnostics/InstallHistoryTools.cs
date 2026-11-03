using System.ComponentModel;
using System.Globalization;

using ModelContextProtocol.Server;

using Vestigo.Configuration;

namespace Vestigo.Diagnostics;

/// <summary>SetupAPI device-install history. The log path is fixed; callers cannot name a file.</summary>
[McpServerToolType]
public sealed class InstallHistoryTools(ISetupApiLogSource logs, VestigoOptions options)
{
    [McpServerTool(Name = "get_device_install_history")]
    [Description("Reads %windir%\\INF\\setupapi.dev.log and rotated setupapi.dev.*.log files for sections whose header token equals instanceId (case-insensitive, whole token, not a substring or hardware ID). Returns title, start and end, exit status, original numeric codes, INF and driver-version lines, and the log file name. complete is false when a section is truncated. includeRaw adds the section text, capped per section; otherwise raw lines are omitted. The read stops at Vestigo:Diagnostics:MaxInstallHistoryBytes and prefers newer bytes, setting truncated when older text was skipped. This does not rank drivers.")]
    public OperationResult<InstallHistory> GetDeviceInstallHistory(
        [Description("Device instance ID, matched exactly against the section-header token.")] string instanceId,
        [Description("ISO 8601 timestamp. Sections that end before this time are omitted.")] string? since = null,
        [Description("Maximum sections to return, newest kept, oldest first. 1 to 200.")] int limit = 20,
        [Description("Include the section text. Defaults to false.")] bool includeRaw = false)
    {
        if (!TryInstanceId(instanceId, out string id, out string? error))
        {
            return OperationResult<InstallHistory>.Fail("invalid_request", error!);
        }

        if (limit is < 1 or > 200)
        {
            return OperationResult<InstallHistory>.Fail("invalid_request", "limit must be between 1 and 200.");
        }

        if (!TrySince(since, out DateTimeOffset? start))
        {
            return OperationResult<InstallHistory>.Fail("invalid_time", "since must be an ISO 8601 timestamp.");
        }

        return logs.Read(id, start, limit, includeRaw, options.Diagnostics.MaxInstallHistoryBytes);
    }

    internal static bool TryInstanceId(string? instanceId, out string id, out string? error)
    {
        id = instanceId?.Trim() ?? "";
        if (id.Length is 0 or > 400 || id.IndexOfAny(['\r', '\n', '[', ']']) >= 0)
        {
            error = "instanceId must be a device instance ID.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TrySince(string? text, out DateTimeOffset? since)
    {
        since = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            return false;
        }

        since = parsed;
        return true;
    }
}
