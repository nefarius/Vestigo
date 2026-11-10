namespace Vestigo.Power;

/// <summary>Maps power reads to tool results. A failed Windows call becomes null fields, or <c>power_api_failed</c> when the platform throws.</summary>
public sealed class PowerService(IPowerPlatform platform)
{
    public OperationResult<PowerStateResult> GetPowerState(DateTimeOffset? utcNow = null)
    {
        try
        {
            return OperationResult<PowerStateResult>.Ok(
                PowerMaps.State(platform.ReadState(), utcNow ?? DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return OperationResult<PowerStateResult>.Fail("power_api_failed", "Windows power status could not be read.");
        }
    }

    public OperationResult<SleepConfigurationResult> GetSleepConfiguration()
    {
        try
        {
            return OperationResult<SleepConfigurationResult>.Ok(PowerMaps.Sleep(platform.ReadSleep()));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return OperationResult<SleepConfigurationResult>.Fail("power_api_failed", "Windows sleep policy could not be read.");
        }
    }
}
