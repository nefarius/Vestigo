using Windows.Win32.Devices.HumanInterfaceDevice;

namespace Vestigo.Devices;

/// <summary>Projects HID capability structs into the tool records. No report bytes are copied.</summary>
internal static class HidProjection
{
    internal const int MaxReturnedControls = 32;
    internal const int MaxReturnedCollections = 64;
    internal const int MaxAcceptedCount = 256;
    internal const int MaxTextChars = 126;

    /// <summary><c>HIDP_STATUS_SUCCESS</c>. The hidpi.h macro is not in the Win32 metadata.</summary>
    internal const int StatusSuccess = 0x00110000;

    /// <summary><c>HIDP_STATUS_USAGE_NOT_FOUND</c>. The hidpi.h macro is not in the Win32 metadata.</summary>
    internal const int StatusUsageNotFound = unchecked((int)0xC0110004);

    /// <summary><c>HIDP_STATUS_BUFFER_TOO_SMALL</c> (<c>HIDP_ERROR_CODES(0xC, 7)</c>). The hidpi.h macro is not in the Win32 metadata.</summary>
    internal const int StatusBufferTooSmall = unchecked((int)0xC0110007);

    internal static HidCapabilities Capabilities(in HIDP_CAPS caps) => new(
        caps.UsagePage,
        caps.Usage,
        caps.InputReportByteLength,
        caps.OutputReportByteLength,
        caps.FeatureReportByteLength,
        caps.NumberLinkCollectionNodes,
        caps.NumberInputButtonCaps,
        caps.NumberInputValueCaps,
        caps.NumberOutputButtonCaps,
        caps.NumberOutputValueCaps,
        caps.NumberFeatureButtonCaps,
        caps.NumberFeatureValueCaps);

    internal static HidControl Button(string reportKind, in HIDP_BUTTON_CAPS caps)
    {
        bool range = caps.IsRange;
        return new HidControl(
            reportKind,
            "button",
            caps.UsagePage,
            range ? null : caps.NotRange.Usage,
            range ? caps.Range.UsageMin : null,
            range ? caps.Range.UsageMax : null,
            caps.ReportID,
            caps.LinkCollection,
            caps.IsAbsolute,
            caps.ReportCount,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    internal static HidControl Value(string reportKind, in HIDP_VALUE_CAPS caps)
    {
        bool range = caps.IsRange;
        return new HidControl(
            reportKind,
            "value",
            caps.UsagePage,
            range ? null : caps.NotRange.Usage,
            range ? caps.Range.UsageMin : null,
            range ? caps.Range.UsageMax : null,
            caps.ReportID,
            caps.LinkCollection,
            caps.IsAbsolute,
            caps.ReportCount,
            caps.LogicalMin,
            caps.LogicalMax,
            caps.PhysicalMin,
            caps.PhysicalMax,
            caps.BitSize,
            unchecked((int)caps.Units),
            unchecked((int)caps.UnitsExp),
            caps.HasNull);
    }

    internal static HidCollection Collection(int index, in HIDP_LINK_COLLECTION_NODE node) => new(
        index,
        node.LinkUsagePage,
        node.LinkUsage,
        node.Parent,
        node.NumberOfChildren,
        node.NextSibling,
        node.FirstChild,
        node.CollectionType,
        CollectionTypeName(node.CollectionType),
        node.IsAlias);

    internal static string? CollectionTypeName(int type) => type switch
    {
        0 => "physical",
        1 => "application",
        2 => "logical",
        3 => "report",
        4 => "namedArray",
        5 => "usageSwitch",
        6 => "usageModifier",
        _ => null,
    };

    internal static string? BoundText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string trimmed = text.Trim();
        return trimmed.Length <= MaxTextChars ? trimmed : trimmed[..MaxTextChars];
    }
}
