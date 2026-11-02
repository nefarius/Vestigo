using Microsoft.Extensions.Diagnostics.HealthChecks;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Hosting;

/// <summary>
///     Reports spool writability and whether allowed roots are configured.
/// </summary>
public sealed class VestigoHealthCheck(VestigoOptions options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string spool = StateDirectories.EnsureSubdirectory(VestigoPaths.SpoolDirectory);
            string probe = Path.Combine(spool, ".health");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Capture spool is not writable.", ex));
        }

        if (options.UsesDefaultInputRoots || options.UsesDefaultOutputRoots)
        {
            try
            {
                string traces = StateDirectories.EnsureSubdirectory(VestigoPaths.TraceDirectory);
                string probe = Path.Combine(traces, ".health");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("Trace directory is not writable.", ex));
            }
        }

        return Task.FromResult(HealthCheckResult.Healthy("Vestigo is ready."));
    }
}
