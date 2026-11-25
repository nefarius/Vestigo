using Vestigo.Desktop;

namespace Vestigo.Vision;

/// <summary>
///     A capture the client can refer to by id: the unannotated working image, how it maps onto the screen, and the
///     elements found on it. Boxes in <see cref="Elements" /> are physical virtual-screen pixels.
/// </summary>
public sealed class CaptureRecord(
    string id,
    DesktopRect bounds,
    DesktopSize imageSize,
    IReadOnlyList<ScreenElement> elements,
    byte[] workingPng,
    DateTimeOffset createdAt,
    string target = "desktop",
    string? windowHandle = null,
    DesktopRect? windowBounds = null,
    DesktopRect? effectiveCrop = null)
{
    public string Id { get; } = id;

    /// <summary>Captured area in virtual-screen physical pixels.</summary>
    public DesktopRect Bounds { get; } = bounds;

    /// <summary>Size of the image the client saw, in pixels.</summary>
    public DesktopSize ImageSize { get; } = imageSize;

    public IReadOnlyList<ScreenElement> Elements { get; } = elements;

    /// <summary>The working image without annotations, PNG.</summary>
    public byte[] WorkingPng { get; } = workingPng;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    /// <summary>Capture target, such as <c>desktop</c> or <c>window</c>.</summary>
    public string Target { get; } = target;

    /// <summary>Window handle when the capture was a window. Null for other targets.</summary>
    public string? WindowHandle { get; } = windowHandle;

    /// <summary>Window frame in virtual-screen physical pixels. Null when the capture was not cropped.</summary>
    public DesktopRect? WindowBounds { get; } = windowBounds;

    /// <summary>Window-relative crop after clipping. Null when the capture was the full target.</summary>
    public DesktopRect? EffectiveCrop { get; } = effectiveCrop;

    /// <summary>Maps a point in image pixels to virtual-screen physical pixels, clamped to the captured area.</summary>
    public (int X, int Y) ToScreen(double imageX, double imageY)
    {
        double sx = (double)Bounds.Width / ImageSize.Width;
        double sy = (double)Bounds.Height / ImageSize.Height;
        int x = Bounds.X + (int)Math.Round(imageX * sx);
        int y = Bounds.Y + (int)Math.Round(imageY * sy);
        return (
            Math.Clamp(x, Bounds.X, Bounds.X + Bounds.Width - 1),
            Math.Clamp(y, Bounds.Y, Bounds.Y + Bounds.Height - 1));
    }

    /// <summary>Maps a physical rectangle to image pixels of this capture.</summary>
    public DesktopRect ToImage(DesktopRect screen)
    {
        double sx = (double)ImageSize.Width / Bounds.Width;
        double sy = (double)ImageSize.Height / Bounds.Height;
        int left = (int)Math.Round((screen.X - Bounds.X) * sx);
        int top = (int)Math.Round((screen.Y - Bounds.Y) * sy);
        int width = Math.Max(1, (int)Math.Round(screen.Width * sx));
        int height = Math.Max(1, (int)Math.Round(screen.Height * sy));
        return new DesktopRect(left, top, width, height);
    }

    /// <summary>The center of an element in physical pixels, or null when the id is unknown.</summary>
    public (int X, int Y)? ElementCenter(int elementId)
    {
        ScreenElement? element = Elements.FirstOrDefault(item => item.Id == elementId);
        if (element is null)
        {
            return null;
        }

        return (element.Box.X + element.Box.Width / 2, element.Box.Y + element.Box.Height / 2);
    }
}

/// <summary>Bounded, expiring store of recent captures so later calls can click by element id or diff.</summary>
public sealed class CaptureCache(TimeProvider? clock = null, int capacity = CaptureCache.DefaultCapacity, TimeSpan? ttl = null)
{
    public const int DefaultCapacity = 8;

    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private readonly TimeSpan _ttl = ttl ?? DefaultTtl;

    private readonly object _gate = new();

    private readonly List<CaptureRecord> _records = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                Evict();
                return _records.Count;
            }
        }
    }

    public CaptureRecord Add(
        DesktopRect bounds,
        DesktopSize imageSize,
        IReadOnlyList<ScreenElement> elements,
        byte[] workingPng,
        string target = "desktop",
        string? windowHandle = null,
        DesktopRect? windowBounds = null,
        DesktopRect? effectiveCrop = null)
    {
        string id = "cap-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        CaptureRecord record = new(
            id,
            bounds,
            imageSize,
            elements,
            workingPng,
            _clock.GetUtcNow(),
            target,
            windowHandle,
            windowBounds,
            effectiveCrop);
        lock (_gate)
        {
            Evict();
            _records.Add(record);
            while (_records.Count > capacity)
            {
                _records.RemoveAt(0);
            }
        }

        return record;
    }

    public bool TryGet(string? id, out CaptureRecord record)
    {
        lock (_gate)
        {
            Evict();
            CaptureRecord? found = _records.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            record = found!;
            return found is not null;
        }
    }

    private void Evict()
    {
        DateTimeOffset cutoff = _clock.GetUtcNow() - _ttl;
        _records.RemoveAll(item => item.CreatedAt < cutoff);
    }
}

/// <summary>Decides whether two inspections are the same picture for <c>diffAgainst</c>.</summary>
internal static class CaptureCompatibility
{
    /// <summary>Null when the earlier capture can be diffed against the new one. Otherwise the <c>capture_mismatch</c> message.</summary>
    public static string? DescribeMismatch(CaptureRecord previous, DesktopSize imageSize, ScreenCaptureResult current)
    {
        if (!string.Equals(previous.Target, current.Target, StringComparison.OrdinalIgnoreCase))
        {
            return $"diffAgainst needs the same target. The earlier capture was '{previous.Target}' and this one is '{current.Target}'.";
        }

        if (previous.ImageSize != imageSize)
        {
            return "diffAgainst preview scale/maxEdge does not match. The earlier image is "
                + $"{previous.ImageSize.Width}x{previous.ImageSize.Height} and this one is {imageSize.Width}x{imageSize.Height}.";
        }

        bool previousCrop = previous.EffectiveCrop is not null;
        bool currentCrop = current.EffectiveCrop is not null;
        if (previousCrop != currentCrop)
        {
            return "diffAgainst crop geometry does not match. One capture is a window crop and the other is the full window.";
        }

        if (previous.EffectiveCrop is { } earlier && current.EffectiveCrop is { } crop)
        {
            if (earlier != crop
                || earlier.Width != previous.Bounds.Width
                || earlier.Height != previous.Bounds.Height
                || crop.Width != current.Bounds.Width
                || crop.Height != current.Bounds.Height)
            {
                return "diffAgainst crop geometry does not match the earlier capture.";
            }

            return null;
        }

        if (previous.Bounds != current.Bounds)
        {
            return "diffAgainst needs the same target, area and maxEdge as the earlier capture.";
        }

        return null;
    }
}
