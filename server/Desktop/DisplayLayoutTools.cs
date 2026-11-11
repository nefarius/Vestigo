using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;

namespace Vestigo.Desktop;

/// <summary>Snapshot, temporarily apply, observe, and restore a display layout. Virtual displays are out of scope.</summary>
[McpServerToolType]
public sealed class DisplayLayoutTools(DisplayLayoutTransactionManager transactions, VestigoOptions options)
{
    [McpServerTool(Name = "snapshot_display_layout", ReadOnly = true)]
    [Description(
        "Captures every CCD path, including inactive ones. topology is single, clone, extend, mixed, or inactive, from source identity. " +
        "Each path keeps raw path flags, source and target mode indexes, desktop-image size, signal size, and EDID identity when Windows says the ids are valid. " +
        "Restore still compares active paths only. Each path is identified by its monitor device path, connector instance, adapter LUID, and target id. "
        + "GDI device names such as \\\\.\\DISPLAY1 are included as operational names and are not stable across hotplug. "
        + "Coordinates are physical screen pixels. This does not change the displays. Interactive session only.")]
    public Task<OperationResult<DisplayLayoutSnapshot>> SnapshotDisplayLayout(
        [Description("Use the elevated token.")] bool elevated = false,
        CancellationToken cancellationToken = default) =>
        transactions.SnapshotAsync(elevated, cancellationToken);

    [McpServerTool(Name = "start_display_layout_transaction", Destructive = true)]
    [Description(
        "Validates and applies a layout for targets that are already active. The original topology is saved before the change. "
        + "persistent defaults to false and is the only way the change is also written to the display database. "
        + "Only one transaction can be open. Cancellation of this call attempts to restore the original layout. "
        + "A later client disconnect does not, because MCP is stateless; the lease restores it. "
        + "Service shutdown and the next service start also attempt restore. External display changes are reported and cannot be blocked. "
        + "This tool does not create or remove virtual displays. Requires Vestigo:Desktop:AllowDisplayLayoutChanges.")]
    public Task<OperationResult<DisplayLayoutTransaction>> StartDisplayLayoutTransaction(
        [Description("Active targets to reposition or change mode. Identity must match snapshot_display_layout.")] List<DisplayTargetChange> targets,
        [Description("Write the change to the display database. Leave false for a temporary test layout.")] bool persistent = false,
        [Description("Use the elevated token.")] bool elevated = false,
        CancellationToken cancellationToken = default)
    {
        if (!options.Desktop.AllowDisplayLayoutChanges)
        {
            return Task.FromResult(OperationResult<DisplayLayoutTransaction>.Fail(
                "display_layout_disabled",
                "Display layout changes are disabled. Set Vestigo:Desktop:AllowDisplayLayoutChanges to true."));
        }

        return transactions.StartAsync(targets, persistent, elevated, cancellationToken);
    }

    [McpServerTool(Name = "get_display_layout_transaction", ReadOnly = true)]
    [Description("Returns the open display transaction, including restore differences and whether restoration was verified.")]
    public OperationResult<DisplayLayoutTransaction> GetDisplayLayoutTransaction(
        [Description("Transaction id. Omit to return the open transaction.")] string? id = null) =>
        transactions.Get(id);

    [McpServerTool(Name = "observe_display_layout_transaction", ReadOnly = true)]
    [Description(
        "Samples the active topology until a target is active, matches the requested mode and position, and has the same geometry for stableSamples consecutive samples, or until timeoutSeconds. "
        + "There is no fixed pixel tolerance. A timeout returns the last differences with timedOut true and leaves the transaction open. "
        + "Omit the target fields to wait until the original topology is stable again.")]
    public Task<OperationResult<DisplayLayoutObservation>> ObserveDisplayLayoutTransaction(
        [Description("Transaction id.")] string id,
        [Description("Seconds to wait, from 1 to 120.")] int timeoutSeconds = 15,
        [Description("Consecutive matching samples required.")] int stableSamples = 2,
        [Description("Monitor device path from the snapshot.")] string? monitorDevicePath = null,
        [Description("Connector instance from the snapshot.")] uint? connectorInstance = null,
        [Description("Adapter LUID from the snapshot, HHHHHHHH:LLLLLLLL.")] string? adapterLuid = null,
        [Description("CCD target id from the snapshot.")] uint? targetId = null,
        [Description("Requested left, used when a target is selected.")] int? x = null,
        [Description("Requested top.")] int? y = null,
        [Description("Requested width.")] int? width = null,
        [Description("Requested height.")] int? height = null,
        [Description("Optional IDENTITY, ROTATE90, ROTATE180, or ROTATE270.")] string? rotation = null,
        CancellationToken cancellationToken = default)
    {
        DisplayTargetChange? target = monitorDevicePath is null && adapterLuid is null && targetId is null
            ? null
            : new DisplayTargetChange(
                monitorDevicePath,
                connectorInstance,
                adapterLuid,
                targetId,
                x ?? 0,
                y ?? 0,
                width ?? 0,
                height ?? 0,
                null,
                null,
                rotation);
        return transactions.ObserveAsync(id, new DisplayWait(TimeSpan.FromSeconds(timeoutSeconds), stableSamples, target), cancellationToken);
    }

    [McpServerTool(Name = "restore_display_layout_transaction", Destructive = true)]
    [Description(
        "Applies the snapshot taken before the transaction and reads the topology again. RestorationVerified is true only when the fingerprint matches. "
        + "A failed restore stays open and blocks another transaction. The restore is written to the display database so a temporary test layout cannot return at logoff. "
        + "Requires Vestigo:Desktop:AllowDisplayLayoutChanges.")]
    public Task<OperationResult<DisplayLayoutTransaction>> RestoreDisplayLayoutTransaction(
        [Description("Transaction id.")] string id,
        CancellationToken cancellationToken = default)
    {
        if (!options.Desktop.AllowDisplayLayoutChanges)
        {
            return Task.FromResult(OperationResult<DisplayLayoutTransaction>.Fail(
                "display_layout_disabled",
                "Display layout changes are disabled. Set Vestigo:Desktop:AllowDisplayLayoutChanges to true."));
        }

        return transactions.RestoreAsync(id, cancellationToken);
    }
}
