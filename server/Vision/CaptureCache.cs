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
    DateTimeOffset createdAt)
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

    public CaptureRecord Add(DesktopRect bounds, DesktopSize imageSize, IReadOnlyList<ScreenElement> elements, byte[] workingPng)
    {
        string id = "cap-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        CaptureRecord record = new(id, bounds, imageSize, elements, workingPng, _clock.GetUtcNow());
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
