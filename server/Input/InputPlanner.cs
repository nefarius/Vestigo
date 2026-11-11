using Vestigo.Vision;

namespace Vestigo.Input;

/// <summary>Validates a client action list and resolves element ids and preview coordinates to physical pixels.</summary>
public static class InputPlanner
{
    public const int MaxScrollNotches = 50;

    public static OperationResult<InputBatch> Plan(
        IReadOnlyList<InputActionSpec>? actions,
        string? captureId,
        string? focusWindow,
        CaptureCache cache,
        InputOptions limits)
    {
        if (actions is null || actions.Count == 0)
        {
            return Fail("invalid_request", "actions must contain at least one action.");
        }

        if (actions.Count > limits.MaxActions)
        {
            return Fail("invalid_request", $"At most {limits.MaxActions} actions are allowed per call.");
        }

        CaptureRecord? capture = null;
        if (!string.IsNullOrWhiteSpace(captureId) && !cache.TryGet(captureId.Trim(), out capture))
        {
            return Fail("capture_expired", $"Capture '{captureId}' is unknown or expired. Run inspect_screen again.");
        }

        List<InputStep> steps = [];
        long totalWait = 0;
        long totalChars = 0;
        for (int index = 0; index < actions.Count; index++)
        {
            InputActionSpec action = actions[index];
            string label = $"actions[{index}]";
            string kind = (action.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (!InputKinds.All.Contains(kind))
            {
                return Fail("invalid_request", $"{label}.type must be one of {string.Join(", ", InputKinds.All)}.");
            }

            OperationResult<InputStep> step = PlanOne(kind, action, label, capture);
            if (!step.Success)
            {
                return Fail(step.ErrorCode!, step.Error!);
            }

            int hold = action.HoldMilliseconds ?? 0;
            int delay = action.DelayAfterMilliseconds ?? 0;
            if (hold is < 0 or > 5000 || delay is < 0 or > 5000)
            {
                return Fail("invalid_request", $"{label} holdMilliseconds and delayAfterMilliseconds must be 0 to 5000.");
            }

            if (hold > 0 && kind is not (InputKinds.Click or InputKinds.Key))
            {
                return Fail("invalid_request", $"{label}.holdMilliseconds applies to click and key.");
            }

            InputStep timed = step.Value! with { HoldMilliseconds = hold, DelayAfterMilliseconds = delay };
            step = OperationResult<InputStep>.Ok(timed);
            totalWait += timed.Milliseconds + (long)hold * timed.Count + delay;
            totalChars += step.Value.Text?.Length ?? 0;
            steps.Add(step.Value);
        }

        if (totalWait > limits.MaxWaitMilliseconds)
        {
            return Fail("invalid_request", $"Total wait time may not exceed {limits.MaxWaitMilliseconds} ms.");
        }

        if (totalChars > limits.MaxTypedCharacters)
        {
            return Fail("invalid_request", $"At most {limits.MaxTypedCharacters} characters may be typed per call.");
        }

        return OperationResult<InputBatch>.Ok(new InputBatch(steps, string.IsNullOrWhiteSpace(focusWindow) ? null : focusWindow.Trim()));
    }

    private static OperationResult<InputStep> PlanOne(string kind, InputActionSpec action, string label, CaptureRecord? capture)
    {
        switch (kind)
        {
            case InputKinds.Wait:
                if (action.Milliseconds is not (>= 0 and <= InputOptions.MaxMaxWaitMilliseconds))
                {
                    return Bad($"{label}.milliseconds must be between 0 and {InputOptions.MaxMaxWaitMilliseconds}.");
                }

                return Ok(new InputStep(kind, null, null, null, null, null, 1, 0, 0, null, null, action.Milliseconds.Value));

            case InputKinds.Type:
                if (string.IsNullOrEmpty(action.Text))
                {
                    return Bad($"{label}.text is required for type.");
                }

                return Ok(new InputStep(kind, null, null, null, null, null, 1, 0, 0, action.Text, null, 0));

            case InputKinds.Key:
                if (!KeyChordParser.TryParse(action.Keys, out IReadOnlyList<KeyStroke> keys, out string? keyError))
                {
                    return Bad($"{label}.keys: {keyError}");
                }

                return Ok(new InputStep(kind, null, null, null, null, null, 1, 0, 0, null, keys, 0));
        }

        OperationResult<(int X, int Y)?> point = ResolvePoint(action.ElementId, action.X, action.Y, capture, label, required: kind is InputKinds.Move or InputKinds.Drag);
        if (!point.Success)
        {
            return OperationResult<InputStep>.Fail(point.ErrorCode!, point.Error!);
        }

        (int X, int Y)? from = point.Value;
        string? button = null;
        if (kind is InputKinds.Click or InputKinds.Down or InputKinds.Up or InputKinds.Drag)
        {
            button = (action.Button ?? "left").Trim().ToLowerInvariant();
            if (button is not ("left" or "right" or "middle"))
            {
                return Bad($"{label}.button must be left, right or middle.");
            }
        }

        switch (kind)
        {
            case InputKinds.Move:
            case InputKinds.Down:
            case InputKinds.Up:
                return Ok(new InputStep(kind, from?.X, from?.Y, null, null, button, 1, 0, 0, null, null, 0));

            case InputKinds.Click:
                int count = action.Count ?? 1;
                if (count is < 1 or > 3)
                {
                    return Bad($"{label}.count must be between 1 and 3.");
                }

                return Ok(new InputStep(kind, from?.X, from?.Y, null, null, button, count, 0, 0, null, null, 0));

            case InputKinds.Scroll:
                int dy = action.DeltaY ?? 0;
                int dx = action.DeltaX ?? 0;
                if (dx == 0 && dy == 0 || Math.Abs(dx) > MaxScrollNotches || Math.Abs(dy) > MaxScrollNotches)
                {
                    return Bad($"{label}: scroll needs deltaX or deltaY, each within {MaxScrollNotches} notches.");
                }

                return Ok(new InputStep(kind, from?.X, from?.Y, null, null, null, 1, dx, dy, null, null, 0));

            default:
                OperationResult<(int X, int Y)?> destination = ResolvePoint(action.ToElementId, action.ToX, action.ToY, capture, label + " (destination)", required: true);
                if (!destination.Success)
                {
                    return OperationResult<InputStep>.Fail(destination.ErrorCode!, destination.Error!);
                }

                return Ok(new InputStep(kind, from!.Value.X, from.Value.Y, destination.Value!.Value.X, destination.Value.Value.Y, button, 1, 0, 0, null, null, 0));
        }
    }

    private static OperationResult<(int X, int Y)?> ResolvePoint(int? elementId, int? x, int? y, CaptureRecord? capture, string label, bool required)
    {
        if (elementId is { } id)
        {
            if (capture is null)
            {
                return OperationResult<(int X, int Y)?>.Fail("invalid_request", $"{label}: elementId needs captureId.");
            }

            (int X, int Y)? center = capture.ElementCenter(id);
            return center is null
                ? OperationResult<(int X, int Y)?>.Fail("element_not_found", $"{label}: capture '{capture.Id}' has no element {id}.")
                : OperationResult<(int X, int Y)?>.Ok(center);
        }

        if (x is null && y is null)
        {
            return required
                ? OperationResult<(int X, int Y)?>.Fail("invalid_request", $"{label}: needs elementId, or x and y.")
                : OperationResult<(int X, int Y)?>.Ok(null);
        }

        if (x is null || y is null)
        {
            return OperationResult<(int X, int Y)?>.Fail("invalid_request", $"{label}: x and y go together.");
        }

        if (capture is not null)
        {
            if (x < 0 || y < 0 || x > capture.ImageSize.Width || y > capture.ImageSize.Height)
            {
                return OperationResult<(int X, int Y)?>.Fail(
                    "invalid_request",
                    $"{label}: ({x},{y}) is outside the {capture.ImageSize.Width}x{capture.ImageSize.Height} image of capture '{capture.Id}'.");
            }

            return OperationResult<(int X, int Y)?>.Ok(capture.ToScreen(x.Value, y.Value));
        }

        return OperationResult<(int X, int Y)?>.Ok((x.Value, y.Value));
    }

    private static OperationResult<InputStep> Ok(InputStep step) => OperationResult<InputStep>.Ok(step);

    private static OperationResult<InputStep> Bad(string message) => OperationResult<InputStep>.Fail("invalid_request", message);

    private static OperationResult<InputBatch> Fail(string code, string message) => OperationResult<InputBatch>.Fail(code, message);
}
