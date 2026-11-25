namespace Vestigo.Input;

/// <summary>
///     Sends an already validated <see cref="PhysicalKeyPlan" />. Modifier key-down belongs to
///     <c>sendStroke</c>; this type remembers what that call left down and releases only those keys.
/// </summary>
internal static class PhysicalKeySender
{
    private const ushort Shift = 0x10;

    private const ushort Control = 0x11;

    private const ushort Alt = 0x12;

    public static OperationResult<int> Send(
        PhysicalKeyPlan plan,
        Func<bool>? focusStillHeld,
        Func<PhysicalKeyStroke, OperationResult<int>> sendStroke,
        Func<IReadOnlyList<ushort>, OperationResult<bool>> releaseModifiers,
        Func<ushort, bool>? userHolds = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sendStroke);
        ArgumentNullException.ThrowIfNull(releaseModifiers);
        List<ushort> held = [];
        int submitted = 0;
        OperationResult<int> result = OperationResult<int>.Ok(0);
        bool succeeded = true;
        try
        {
            for (int index = 0; index < plan.Strokes.Count; index++)
            {
                if (focusStillHeld is not null && !focusStillHeld())
                {
                    result = OperationResult<int>.Fail("focus_lost", "The focus window is no longer in the foreground.");
                    succeeded = false;
                    break;
                }

                PhysicalKeyStroke stroke = plan.Strokes[index];
                OperationResult<PhysicalKeyStroke> adjusted = WithoutUserModifiers(stroke, held, userHolds);
                if (!adjusted.Success)
                {
                    result = OperationResult<int>.Fail(adjusted.ErrorCode!, adjusted.Error!);
                    succeeded = false;
                    break;
                }

                OperationResult<bool> released = ReleaseExcept(held, Modifiers(adjusted.Value!), releaseModifiers);
                if (!released.Success)
                {
                    result = OperationResult<int>.Fail(released.ErrorCode!, released.Error!);
                    succeeded = false;
                    break;
                }

                OperationResult<int> sent = sendStroke(adjusted.Value!);
                if (!sent.Success)
                {
                    result = OperationResult<int>.Fail(sent.ErrorCode!, sent.Error!);
                    succeeded = false;
                    break;
                }

                submitted += sent.Value;
                foreach (ushort vk in Modifiers(adjusted.Value!))
                {
                    if (!held.Contains(vk))
                    {
                        held.Add(vk);
                    }
                }
            }

            if (succeeded)
            {
                result = OperationResult<int>.Ok(submitted);
            }
        }
        finally
        {
            if (held.Count > 0)
            {
                ushort[] leftover = new ushort[held.Count];
                for (int index = 0; index < held.Count; index++)
                {
                    leftover[index] = held[held.Count - 1 - index];
                }

                OperationResult<bool> released = releaseModifiers(leftover);
                held.Clear();
                if (succeeded && !released.Success)
                {
                    result = OperationResult<int>.Fail(released.ErrorCode!, released.Error!);
                }
            }
        }

        return result;
    }

    internal static ushort[] Modifiers(PhysicalKeyStroke stroke)
    {
        List<ushort> modifiers = [];
        if (stroke.Shift)
        {
            modifiers.Add(Shift);
        }

        if (stroke.Control)
        {
            modifiers.Add(Control);
        }

        if (stroke.Alt)
        {
            modifiers.Add(Alt);
        }

        return [.. modifiers];
    }

    private static OperationResult<PhysicalKeyStroke> WithoutUserModifiers(
        PhysicalKeyStroke stroke,
        List<ushort> held,
        Func<ushort, bool>? userHolds)
    {
        if (userHolds is null)
        {
            return OperationResult<PhysicalKeyStroke>.Ok(stroke);
        }

        bool shiftHeld = userHolds(Shift) && !held.Contains(Shift);
        bool controlHeld = userHolds(Control) && !held.Contains(Control);
        bool altHeld = userHolds(Alt) && !held.Contains(Alt);
        if ((shiftHeld && !stroke.Shift) || (controlHeld && !stroke.Control) || (altHeld && !stroke.Alt))
        {
            return OperationResult<PhysicalKeyStroke>.Fail(
                "modifier_held",
                "A keyboard modifier is already held, so this character was not sent.");
        }

        return OperationResult<PhysicalKeyStroke>.Ok(stroke with
        {
            Shift = stroke.Shift && !shiftHeld,
            Control = stroke.Control && !controlHeld,
            Alt = stroke.Alt && !altHeld
        });
    }

    private static OperationResult<bool> ReleaseExcept(
        List<ushort> held,
        IReadOnlyList<ushort> needed,
        Func<IReadOnlyList<ushort>, OperationResult<bool>> releaseModifiers)
    {
        List<ushort> dropping = [];
        for (int index = held.Count - 1; index >= 0; index--)
        {
            if (!needed.Contains(held[index]))
            {
                dropping.Add(held[index]);
            }
        }

        if (dropping.Count == 0)
        {
            return OperationResult<bool>.Ok(true);
        }

        OperationResult<bool> released = releaseModifiers(dropping);
        if (!released.Success)
        {
            return released;
        }

        foreach (ushort vk in dropping)
        {
            held.Remove(vk);
        }

        return released;
    }
}
