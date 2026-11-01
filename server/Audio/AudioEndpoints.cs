using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Vestigo.Audio;

/// <summary>Enumerates Core Audio endpoints and switches the default device through <see cref="IPolicyConfig" />.</summary>
public static class AudioEndpoints
{
    private static readonly Role[] AllRoles = [Role.Console, Role.Multimedia, Role.Communications];

    public static OperationResult<AudioEndpointListResult> List(AudioSelector selector)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<AudioEndpointListResult>.Fail("windows_required", "Audio enumeration requires Windows.");
        }

        if (!TryMapFlow(selector.Flow, out DataFlow flow, out string? error) ||
            !TryMapState(selector.State, out DeviceState state, out error))
        {
            return OperationResult<AudioEndpointListResult>.Fail("invalid_request", error!);
        }

        try
        {
            List<AudioEndpoint> endpoints = Enumerate(flow, state, selector.Id, selector.IncludeSessions, selector.MeterSampleMilliseconds);
            return OperationResult<AudioEndpointListResult>.Ok(new AudioEndpointListResult(endpoints, Process.GetCurrentProcess().SessionId));
        }
        catch (COMException ex)
        {
            return OperationResult<AudioEndpointListResult>.Fail("audio_failed", ex.Message);
        }
    }

    public static OperationResult<SetDefaultAudioResult> SetDefault(string id, string role)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<SetDefaultAudioResult>.Fail("windows_required", "Setting the default audio endpoint requires Windows.");
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return OperationResult<SetDefaultAudioResult>.Fail("invalid_request", "id is required.");
        }

        if (!AudioSelector.TryParseRole(role, out _, out IReadOnlyList<string> appliedRoles, out string? error))
        {
            return OperationResult<SetDefaultAudioResult>.Fail("invalid_request", error!);
        }

        try
        {
            using MMDeviceEnumerator enumerator = new();
            MMDevice? device = Find(enumerator, id.Trim());
            if (device is null)
            {
                return OperationResult<SetDefaultAudioResult>.Fail("not_found", $"No audio endpoint with id '{id}'.");
            }

            using (device)
            {
                if (device.State != DeviceState.Active)
                {
                    return OperationResult<SetDefaultAudioResult>.Fail(
                        "not_active",
                        $"Audio endpoint '{device.FriendlyName}' is {FormatState(device.State)} and cannot be the default.");
                }

                IPolicyConfig policy = (IPolicyConfig)new PolicyConfigClient();
                foreach (Role nativeRole in appliedRoles.Select(MapRole))
                {
                    int hr = policy.SetDefaultEndpoint(device.ID, nativeRole);
                    if (hr < 0)
                    {
                        Marshal.ThrowExceptionForHR(hr);
                    }
                }

                List<AudioEndpoint> endpoints = Enumerate(device.DataFlow, DeviceState.All, id: null, includeSessions: false, meterSampleMilliseconds: 0);
                return OperationResult<SetDefaultAudioResult>.Ok(new SetDefaultAudioResult(device.ID, appliedRoles, endpoints));
            }
        }
        catch (COMException ex)
        {
            return OperationResult<SetDefaultAudioResult>.Fail("set_default_failed", ex.Message);
        }
        catch (InvalidCastException ex)
        {
            return OperationResult<SetDefaultAudioResult>.Fail(
                "set_default_failed",
                "IPolicyConfig is unavailable on this Windows version: " + ex.Message);
        }
    }

    private static List<AudioEndpoint> Enumerate(
        DataFlow flow,
        DeviceState state,
        string? id,
        bool includeSessions,
        int meterSampleMilliseconds)
    {
        using MMDeviceEnumerator enumerator = new();
        Dictionary<string, List<string>> defaults = ReadDefaults(enumerator, flow);
        List<AudioEndpoint> endpoints = [];
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, state))
        {
            using (device)
            {
                if (!string.IsNullOrWhiteSpace(id) &&
                    !string.Equals(device.ID, id.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                endpoints.Add(Read(device, defaults, includeSessions, meterSampleMilliseconds));
            }
        }

        return endpoints
            .OrderBy(endpoint => endpoint.Flow, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(endpoint => endpoint.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static MMDevice? Find(MMDeviceEnumerator enumerator, string id)
    {
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All))
        {
            if (string.Equals(device.ID, id, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }

            device.Dispose();
        }

        return null;
    }

    private static AudioEndpoint Read(
        MMDevice device,
        Dictionary<string, List<string>> defaults,
        bool includeSessions,
        int meterSampleMilliseconds)
    {
        double? volumePercent = null;
        bool? muted = null;
        IReadOnlyList<double>? channelVolumes = null;
        double? peakPercent = null;
        List<AudioSession> sessions = [];
        bool sessionActive = false;

        TryReadVolume(device, out volumePercent, out muted, out channelVolumes);
        if (meterSampleMilliseconds > 0)
        {
            peakPercent = TryReadPeak(device, meterSampleMilliseconds);
        }

        if (includeSessions)
        {
            sessions = TryReadSessions(device, out sessionActive);
        }

        bool active = sessionActive || peakPercent is > 0;
        defaults.TryGetValue(device.ID, out List<string>? defaultFor);
        return new AudioEndpoint(
            device.ID,
            device.FriendlyName,
            TryReadProperty(device, PropertyKeys.PKEY_Device_DeviceDesc),
            TryReadProperty(device, PropertyKeys.PKEY_DeviceInterface_FriendlyName) ?? device.DeviceFriendlyName,
            FormatFlow(device.DataFlow),
            FormatState(device.State),
            TryReadFormFactor(device),
            defaultFor ?? [],
            volumePercent,
            muted,
            channelVolumes,
            peakPercent,
            active,
            sessions);
    }

    private static Dictionary<string, List<string>> ReadDefaults(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        Dictionary<string, List<string>> defaults = new(StringComparer.OrdinalIgnoreCase);
        DataFlow[] flows = flow == DataFlow.All
            ? [DataFlow.Render, DataFlow.Capture]
            : new[] { flow };
        foreach (DataFlow candidate in flows)
        {
            foreach (Role role in AllRoles)
            {
                if (!enumerator.HasDefaultAudioEndpoint(candidate, role))
                {
                    continue;
                }

                using MMDevice device = enumerator.GetDefaultAudioEndpoint(candidate, role);
                if (!defaults.TryGetValue(device.ID, out List<string>? roles))
                {
                    roles = [];
                    defaults[device.ID] = roles;
                }

                string name = FormatRole(role);
                if (!roles.Contains(name, StringComparer.Ordinal))
                {
                    roles.Add(name);
                }
            }
        }

        return defaults;
    }

    private static void TryReadVolume(MMDevice device, out double? volumePercent, out bool? muted, out IReadOnlyList<double>? channelVolumes)
    {
        volumePercent = null;
        muted = null;
        channelVolumes = null;
        try
        {
            AudioEndpointVolume volume = device.AudioEndpointVolume;
            volumePercent = Percent(volume.MasterVolumeLevelScalar);
            muted = volume.Mute;
            List<double> channels = [];
            for (int index = 0; index < volume.Channels.Count; index++)
            {
                channels.Add(Percent(volume.Channels[index].VolumeLevelScalar));
            }

            channelVolumes = channels;
        }
        catch (Exception)
        {
            // Disabled, unplugged, or not-present endpoints often fail Activate with COM or IO errors.
        }
    }

    private static double? TryReadPeak(MMDevice device, int meterSampleMilliseconds)
    {
        try
        {
            AudioMeterInformation meter = device.AudioMeterInformation;
            float peak = meter.MasterPeakValue;
            if (meterSampleMilliseconds <= 0)
            {
                return Percent(peak);
            }

            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < meterSampleMilliseconds)
            {
                int remaining = meterSampleMilliseconds - (int)clock.ElapsedMilliseconds;
                Thread.Sleep(Math.Clamp(remaining, 1, 50));
                peak = Math.Max(peak, meter.MasterPeakValue);
            }

            return Percent(peak);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<AudioSession> TryReadSessions(MMDevice device, out bool anyActive)
    {
        anyActive = false;
        try
        {
            SessionCollection sessions = device.AudioSessionManager.Sessions;
            List<AudioSession> listed = [];
            for (int index = 0; index < sessions.Count; index++)
            {
                AudioSessionControl session = sessions[index];
                AudioSessionState state = session.State;
                if (state == AudioSessionState.AudioSessionStateActive)
                {
                    anyActive = true;
                }

                int processId = unchecked((int)session.GetProcessID);
                listed.Add(new AudioSession(
                    processId,
                    TryProcessName(processId),
                    EmptyToNull(session.DisplayName),
                    FormatSessionState(state),
                    session.IsSystemSoundsSession));
            }

            return listed;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string? TryReadProperty(MMDevice device, PropertyKey key)
    {
        try
        {
            if (!device.Properties.Contains(key))
            {
                return null;
            }

            return EmptyToNull(Convert.ToString(device.Properties[key].Value, CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryReadFormFactor(MMDevice device)
    {
        try
        {
            if (!device.Properties.Contains(PropertyKeys.PKEY_AudioEndpoint_FormFactor))
            {
                return null;
            }

            object? value = device.Properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor].Value;
            uint formFactor = value switch
            {
                uint number => number,
                int number when number >= 0 => (uint)number,
                _ => uint.MaxValue
            };

            return formFactor switch
            {
                0 => "remoteNetworkDevice",
                1 => "speakers",
                2 => "lineLevel",
                3 => "headphones",
                4 => "microphone",
                5 => "headset",
                6 => "handset",
                7 => "unknownDigitalPassthrough",
                8 => "spdif",
                9 => "digitalAudioDisplayDevice",
                10 => "unknown",
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryProcessName(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryMapFlow(string flow, out DataFlow mapped, out string? error)
    {
        mapped = DataFlow.All;
        error = null;
        if (flow == AudioSelector.FlowRender)
        {
            mapped = DataFlow.Render;
            return true;
        }

        if (flow == AudioSelector.FlowCapture)
        {
            mapped = DataFlow.Capture;
            return true;
        }

        if (flow == AudioSelector.FlowAll)
        {
            return true;
        }

        error = "flow must be 'all', 'render' or 'capture'.";
        return false;
    }

    private static bool TryMapState(string state, out DeviceState mapped, out string? error)
    {
        mapped = DeviceState.Active;
        error = null;
        if (state == AudioSelector.StateActive)
        {
            return true;
        }

        if (state == AudioSelector.StateAll)
        {
            mapped = DeviceState.All;
            return true;
        }

        error = "state must be 'active' or 'all'.";
        return false;
    }

    private static Role MapRole(string role) =>
        role switch
        {
            "multimedia" => Role.Multimedia,
            "communications" => Role.Communications,
            _ => Role.Console
        };

    private static string FormatFlow(DataFlow flow) =>
        flow == DataFlow.Capture ? AudioSelector.FlowCapture : AudioSelector.FlowRender;

    private static string FormatState(DeviceState state) =>
        state switch
        {
            DeviceState.Disabled => "disabled",
            DeviceState.NotPresent => "notPresent",
            DeviceState.Unplugged => "unplugged",
            _ => "active"
        };

    private static string FormatRole(Role role) =>
        role switch
        {
            Role.Multimedia => "multimedia",
            Role.Communications => "communications",
            _ => "console"
        };

    private static string FormatSessionState(AudioSessionState state) =>
        state switch
        {
            AudioSessionState.AudioSessionStateActive => "active",
            AudioSessionState.AudioSessionStateExpired => "expired",
            _ => "inactive"
        };

    private static double Percent(float scalar) => Math.Round(Math.Clamp(scalar, 0f, 1f) * 100d, 1);

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
