namespace Vestigo.Power;

/// <summary>
///     Documented power-setting GUIDs from the Windows SDK <c>winnt.h</c> <c>DEFINE_GUID</c> macros.
///     The pinned CsWin32 metadata does not emit those macros, so the values are copied here with their SDK names.
/// </summary>
internal static class PowerSettingGuids
{
    /// <summary><c>GUID_VIDEO_SUBGROUP</c>.</summary>
    public static readonly Guid Video = new(0x7516B95F, 0xF776, 0x4464, 0x8C, 0x53, 0x06, 0x16, 0x7F, 0x40, 0xCC, 0x99);

    /// <summary><c>GUID_VIDEO_POWERDOWN_TIMEOUT</c>.</summary>
    public static readonly Guid MonitorTimeout = new(0x3C0BC021, 0xC8A8, 0x4E07, 0xA9, 0x73, 0x6B, 0x14, 0xCB, 0xCB, 0x2B, 0x7E);

    /// <summary><c>GUID_SLEEP_SUBGROUP</c>.</summary>
    public static readonly Guid Sleep = new(0x238C9FA8, 0x0AAD, 0x41ED, 0x83, 0xF4, 0x97, 0xBE, 0x24, 0x2C, 0x8F, 0x20);

    /// <summary><c>GUID_STANDBY_TIMEOUT</c>.</summary>
    public static readonly Guid StandbyTimeout = new(0x29F6C1DB, 0x86DA, 0x48C5, 0x9F, 0xDB, 0xF2, 0xB6, 0x7B, 0x1F, 0x44, 0xDA);

    /// <summary><c>GUID_HIBERNATE_TIMEOUT</c>.</summary>
    public static readonly Guid HibernateTimeout = new(0x9D7815A6, 0x7EE4, 0x497E, 0x88, 0x88, 0x51, 0x5A, 0x05, 0xF0, 0x23, 0x64);

    /// <summary><c>GUID_LIDCLOSE_ACTION</c>.</summary>
    public static readonly Guid LidClose = new(0x5CA83367, 0x6E45, 0x459F, 0xA2, 0x7B, 0x47, 0x6B, 0x1D, 0x01, 0xC9, 0x36);

    /// <summary><c>GUID_ALLOW_RTC_WAKE</c>.</summary>
    public static readonly Guid WakeTimers = new(0xBD3B718A, 0x0680, 0x4D9D, 0x8A, 0xB2, 0xE1, 0xD2, 0xB4, 0xAC, 0x80, 0x6D);

    /// <summary><c>GUID_HIBERNATE_FASTS4_POLICY</c>, shown by Windows as Allow hybrid sleep.</summary>
    public static readonly Guid HybridSleep = new(0x94AC6D29, 0x73CE, 0x41A6, 0x80, 0x9F, 0x63, 0x63, 0xBA, 0x21, 0xB4, 0x7E);
}
