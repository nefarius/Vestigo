namespace Vestigo.Audio;

/// <summary>Settings for the Audio module, bound from <c>Vestigo:Audio</c>.</summary>
public sealed class AudioOptions
{
    public const string SectionName = "Audio";

    public const int DefaultMeterSampleMilliseconds = 200;

    public const int MaxMeterSampleMilliseconds = 2000;

    /// <summary>Enables <c>set_default_audio_endpoint</c>. Listing works regardless.</summary>
    public bool AllowSetDefault { get; set; }

    /// <summary>
    ///     How long to sample <c>IAudioMeterInformation</c> when listing an endpoint. Zero skips the peak meter.
    /// </summary>
    public int MeterSampleMilliseconds { get; set; } = DefaultMeterSampleMilliseconds;

    public void Validate()
    {
        if (MeterSampleMilliseconds is < 0 or > MaxMeterSampleMilliseconds)
        {
            throw new InvalidOperationException(
                $"Vestigo:Audio:MeterSampleMilliseconds must be between 0 and {MaxMeterSampleMilliseconds}.");
        }
    }
}
