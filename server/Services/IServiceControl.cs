namespace Vestigo.Services;

/// <summary>
///     Thin seam over the Service Control Manager. Implementations throw <see cref="ServiceControlException" />
///     for SCM failures; timeouts, validation, and protected-name checks live in <c>ServiceTools</c>.
/// </summary>
public interface IServiceControl
{
    /// <summary>Lists services of the given kind. Detail fields that need a per-service query may be null.</summary>
    IReadOnlyList<ServiceInfo> List(ServiceKind kind);

    /// <summary>Returns full details, or null when no such service exists.</summary>
    ServiceInfo? Get(string name);

    /// <summary>Requests a start and returns without waiting.</summary>
    void Start(string name, IReadOnlyList<string> arguments);

    /// <summary>Requests a stop and returns without waiting.</summary>
    void Stop(string name);

    /// <summary>Names of services that depend on <paramref name="name" /> and are not stopped, dependents first.</summary>
    IReadOnlyList<string> ActiveDependents(string name);

    void SetStartType(string name, ServiceStartType startType);
}
