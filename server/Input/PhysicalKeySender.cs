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
        Action<IReadOnlyList<ushort>> releaseModifiers)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sendStroke);
        ArgumentNullException.ThrowIfNull(releaseModifiers);
        List<ushort> held = [];
        int submitted = 0;
        try
        {
            for (int index = 0; index < plan.Strokes.Count; index++)
            {
                if (focusStillHeld is not null && !focusStillHeld())
                {
                    return OperationResult<int>.Fail("focus_lost", "The focus window is no longer in the foreground.");
                }

                PhysicalKeyStroke stroke = plan.Strokes[index];
                ReleaseExcept(held, Modifiers(stroke), releaseModifiers);
                OperationResult<int> sent = sendStroke(stroke);
                if (!sent.Success)
                {
                    return OperationResult<int>.Fail(sent.ErrorCode!, sent.Error!);
                }

                submitted += sent.Value;
                foreach (ushort vk in Modifiers(stroke))
                {
                    if (!held.Contains(vk))
                    {
                        held.Add(vk);
                    }
                }
            }

            return OperationResult<int>.Ok(submitted);
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

                held.Clear();
                releaseModifiers(leftover);
            }
        }
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

    private static void ReleaseExcept(List<ushort> held, IReadOnlyList<ushort> needed, Action<IReadOnlyList<ushort>> releaseModifiers)
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
            return;
        }

        foreach (ushort vk in dropping)
        {
            held.Remove(vk);
        }

        releaseModifiers(dropping);
    }
}
