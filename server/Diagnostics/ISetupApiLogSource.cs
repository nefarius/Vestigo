namespace Vestigo.Diagnostics;

/// <summary>Reads the fixed SetupAPI dev logs. Callers never supply a path.</summary>
public interface ISetupApiLogSource
{
    OperationResult<InstallHistory> Read(string instanceId, DateTimeOffset? since, int limit, bool includeRaw, long maxBytes);
}
