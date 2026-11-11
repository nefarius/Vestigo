namespace Vestigo.Diagnostics;

/// <summary>Builds a device-problem report from device, service, boot, event, and SetupAPI evidence.</summary>
public interface IDeviceProblemSource
{
    OperationResult<DeviceProblemReport> Explain(string instanceId);

    OperationResult<DriverLoadReport> ExplainLoad(string? instanceId, string? serviceName);
}
