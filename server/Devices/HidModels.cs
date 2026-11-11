namespace Vestigo.Devices;

/// <summary>HID interfaces of one device node. <paramref name="Truncated" /> means the scan or the per-device cap was hit.</summary>
public sealed record HidInspection(string InstanceId, IReadOnlyList<HidInterfaceInspection> Interfaces, bool Truncated);

/// <summary>
///     One HID interface. A failed open leaves capability fields null. This record never carries raw report-descriptor
///     bytes, input reports, or the process that holds an exclusive handle.
/// </summary>
public sealed record HidInterfaceInspection(
    string Path,
    bool Opened,
    string? OpenError,
    string? Access,
    HidIdentity? Identity,
    HidCapabilities? Capabilities,
    IReadOnlyList<HidControl> Buttons,
    bool ButtonsTruncated,
    IReadOnlyList<HidControl> Values,
    bool ValuesTruncated,
    IReadOnlyList<HidCollection> Collections,
    bool CollectionsTruncated,
    bool DetailsIncomplete);

/// <summary>Identity Windows returned for an open interface. Absent strings are null. Text is capped at 126 characters.</summary>
public sealed record HidIdentity(
    int? VendorId,
    int? ProductId,
    int? VersionNumber,
    string? Manufacturer,
    string? Product,
    string? SerialNumber);

/// <summary>Top-level usage and report lengths from <c>HidP_GetCaps</c>. Counts are what the preparsed data reported.</summary>
public sealed record HidCapabilities(
    int UsagePage,
    int Usage,
    int InputReportByteLength,
    int OutputReportByteLength,
    int FeatureReportByteLength,
    int LinkCollectionCount,
    int InputButtonCount,
    int InputValueCount,
    int OutputButtonCount,
    int OutputValueCount,
    int FeatureButtonCount,
    int FeatureValueCount);

/// <summary>
///     One button or value capability. A range sets <paramref name="UsageMin" /> and <paramref name="UsageMax" /> and
///     leaves <paramref name="Usage" /> null. Logical, physical, bit, unit, and null fields apply to values only.
/// </summary>
public sealed record HidControl(
    string ReportKind,
    string Kind,
    int UsagePage,
    int? Usage,
    int? UsageMin,
    int? UsageMax,
    int ReportId,
    int LinkCollection,
    bool Absolute,
    int? ReportCount,
    int? LogicalMin,
    int? LogicalMax,
    int? PhysicalMin,
    int? PhysicalMax,
    int? BitSize,
    int? Units,
    int? UnitExponent,
    bool? HasNull);

/// <summary>
///     One link-collection node. <paramref name="CollectionTypeName" /> is set only for the seven types in USB HID
///     1.11 section 6.2.2.6. <paramref name="Parent" /> and <paramref name="NextSibling" /> are the raw indexes.
/// </summary>
public sealed record HidCollection(
    int Index,
    int UsagePage,
    int Usage,
    int Parent,
    int ChildCount,
    int NextSibling,
    int FirstChild,
    int CollectionType,
    string? CollectionTypeName,
    bool Alias);

/// <summary>Top-level usages of present HID interfaces.</summary>
public sealed record HidUsageList(int? UsagePage, IReadOnlyList<HidUsageMatch> Devices, bool Truncated);

/// <summary>Top-level usage of one interface. Usage fields are null when the interface did not open or caps could not be read.</summary>
public sealed record HidUsageMatch(string InstanceId, string Path, bool Opened, string? OpenError, int? UsagePage, int? Usage);
