using System.Text;
using System.Text.Json;

using Vestigo.Desktop;

namespace Vestigo.Processes;

public static partial class SessionHelper
{
    public const string SnapshotDisplayCommand = "snapshot-display-layout";

    public const string ApplyDisplayCommand = "apply-display-layout";

    public const string PlaceWindowCommand = "place-window";

    public const string RestoreWindowCommand = "restore-window";

    public static List<string> BuildSnapshotDisplayArguments() => [Flag, SnapshotDisplayCommand];

    public static bool TryParseSnapshotDisplay(string[] args, out string? error)
    {
        error = args.Length == 2 && args[0] == Flag && args[1] == SnapshotDisplayCommand
            ? null
            : $"Usage: {Flag} {SnapshotDisplayCommand}";
        return error is null;
    }

    public static List<string> BuildApplyDisplayArguments(DisplayLayoutRequest request) =>
    [
        Flag,
        ApplyDisplayCommand,
        "--request",
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json)))
    ];

    public static bool TryParseApplyDisplay(string[] args, out DisplayLayoutRequest request, out string? error)
    {
        request = new DisplayLayoutRequest([], false, true);
        error = null;
        if (args.Length != 4 || args[0] != Flag || args[1] != ApplyDisplayCommand || args[2] != "--request")
        {
            error = $"Usage: {Flag} {ApplyDisplayCommand} --request BASE64_JSON";
            return false;
        }

        try
        {
            DisplayLayoutRequest? parsed = JsonSerializer.Deserialize<DisplayLayoutRequest>(Convert.FromBase64String(args[3]), Json);
            if (parsed is null || parsed.Targets.Count == 0)
            {
                error = "The display layout request is empty.";
                return false;
            }

            request = parsed;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            error = "The display layout request is not valid base64 JSON.";
            return false;
        }
    }

    public static List<string> BuildPlaceWindowArguments(WindowPlacementRequest request) =>
    [
        Flag,
        PlaceWindowCommand,
        "--request",
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json)))
    ];

    public static bool TryParsePlaceWindow(string[] args, out WindowPlacementRequest request, out string? error)
    {
        request = new WindowPlacementRequest(string.Empty, null, null, 0, 0, 1, 1, DesktopCoordinates.PhysicalScreenPixels, false);
        error = null;
        if (args.Length != 4 || args[0] != Flag || args[1] != PlaceWindowCommand || args[2] != "--request")
        {
            error = $"Usage: {Flag} {PlaceWindowCommand} --request BASE64_JSON";
            return false;
        }

        try
        {
            WindowPlacementRequest? parsed = JsonSerializer.Deserialize<WindowPlacementRequest>(Convert.FromBase64String(args[3]), Json);
            if (parsed is null || !WindowInspection.TryParseHandle(parsed.Handle, out _))
            {
                error = "The placement request needs a window handle.";
                return false;
            }

            request = parsed;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            error = "The placement request is not valid base64 JSON.";
            return false;
        }
    }

    public static List<string> BuildRestoreWindowArguments(SavedWindowPlacement placement) =>
    [
        Flag,
        RestoreWindowCommand,
        "--placement",
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(placement, Json)))
    ];

    public static bool TryParseRestoreWindow(string[] args, out SavedWindowPlacement placement, out string? error)
    {
        placement = new SavedWindowPlacement(string.Empty, 0, null, false, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0);
        error = null;
        if (args.Length != 4 || args[0] != Flag || args[1] != RestoreWindowCommand || args[2] != "--placement")
        {
            error = $"Usage: {Flag} {RestoreWindowCommand} --placement BASE64_JSON";
            return false;
        }

        try
        {
            SavedWindowPlacement? parsed = JsonSerializer.Deserialize<SavedWindowPlacement>(Convert.FromBase64String(args[3]), Json);
            if (parsed is null || !WindowInspection.TryParseHandle(parsed.Handle, out _))
            {
                error = "The saved placement is invalid.";
                return false;
            }

            placement = parsed;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            error = "The saved placement is not valid base64 JSON.";
            return false;
        }
    }

    private static int RunSnapshotDisplay(string[] args)
    {
        if (!TryParseSnapshotDisplay(args, out string? error))
        {
            return Reject(error!);
        }

        OperationResult<DisplayLayoutSnapshot> result = WindowsDisplayConfig.Capture();
        if (!result.Success)
        {
            return Reject(result.Error!, result.ErrorCode!);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(result.Value, Json));
        return 0;
    }

    private static int RunApplyDisplay(string[] args)
    {
        if (!TryParseApplyDisplay(args, out DisplayLayoutRequest request, out string? error))
        {
            return Reject(error!);
        }

        OperationResult<DisplayLayoutApplyResult> result = WindowsDisplayConfig.Apply(request);
        if (!result.Success)
        {
            return Reject(result.Error!, result.ErrorCode!);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(result.Value, Json));
        return 0;
    }

    private static int RunPlaceWindow(string[] args)
    {
        if (!TryParsePlaceWindow(args, out WindowPlacementRequest request, out string? error))
        {
            return Reject(error!);
        }

        OperationResult<WindowPlacementResult> result = new WindowPlacer(new WindowsWindowPlacementSource(), new WindowPlacementStore(TimeProvider.System))
            .Place(request);
        if (!result.Success)
        {
            return Reject(result.Error!, result.ErrorCode!);
        }

        Console.Out.WriteLine(JsonSerializer.Serialize(result.Value, Json));
        return 0;
    }

    private static int RunRestoreWindow(string[] args)
    {
        if (!TryParseRestoreWindow(args, out SavedWindowPlacement placement, out string? error))
        {
            return Reject(error!);
        }

        WindowsWindowPlacementSource source = new();
        string? before = source.Foreground();
        OperationResult<PlacementMove> applied = source.ApplyPlacement(placement);
        if (!applied.Success)
        {
            return Reject(applied.Error!, applied.ErrorCode!);
        }

        OperationResult<PlacementSample> measured = source.Measure(
            placement.Handle,
            placement.ProcessId,
            placement.StartTimeReadable ? placement.ProcessStartTime : null);
        OperationResult<SavedWindowPlacement> reread = source.ReadPlacement(
            placement.Handle,
            placement.ProcessId,
            placement.StartTimeReadable ? placement.ProcessStartTime : null);
        DesktopRect requested = new(placement.NormalX, placement.NormalY, placement.NormalWidth, placement.NormalHeight);
        bool achieved = applied.Value!.ApiSucceeded && reread.Success && WindowPlacer.SameRestoredPlacement(placement, reread.Value!);
        string? after = source.Foreground();
        WindowPlacementResult result = new(
            achieved,
            DesktopCoordinates.PhysicalScreenPixels,
            requested,
            measured.Success ? measured.Value!.Client : null,
            measured.Success ? measured.Value!.Raw : null,
            string.Equals(before, after, StringComparison.OrdinalIgnoreCase),
            before,
            after,
            [new WindowPlacementAttempt(1, requested, applied.Value.ApiSucceeded, applied.Value.NativeError, applied.Value.Error, measured.Success ? measured.Value!.Raw : null)],
            null,
            achieved ? null : "placement_not_achieved",
            achieved ? null : "Restoring the saved placement did not reproduce its normal rectangle.");
        Console.Out.WriteLine(JsonSerializer.Serialize(result, Json));
        return 0;
    }
}
