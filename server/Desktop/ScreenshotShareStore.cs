using System.Security.Cryptography;
using System.Text;

namespace Vestigo.Desktop;

/// <summary>A short-lived URL for one downscaled screenshot preview.</summary>
/// <param name="Url">Absolute URL. The ticket in the query string is the only credential.</param>
/// <param name="ExpiresUtc">When the link stops working.</param>
/// <param name="SingleUse">True when a completed download retires the link.</param>
/// <param name="Instruction">Text the MCP client should follow instead of assuming the image is visible.</param>
public sealed record ScreenshotShareLink(string Url, DateTimeOffset ExpiresUtc, bool SingleUse, string Instruction)
{
    /// <summary>Tells an agent to hand the URL to the user. The MCP image block is for the model.</summary>
    public const string PresentToUser =
        "Present this URL to the user so they can open and copy the screenshot. The MCP image block is for the model and may not appear in chat. Do not fetch the URL yourself; it works once. Do not say the image is already visible.";
}

/// <summary>Why a share URL could not be opened.</summary>
public enum ShareFailure
{
    /// <summary>The ticket opened the preview.</summary>
    None = 0,

    /// <summary>The request had no ticket.</summary>
    Missing,

    /// <summary>The ticket was never issued, or it was evicted.</summary>
    Unknown,

    /// <summary>The ticket is past its lifetime.</summary>
    Expired,

    /// <summary>The ticket belongs to a different screenshot id.</summary>
    Mismatched,

    /// <summary>The ticket is in use or was already consumed.</summary>
    Replayed
}

/// <summary>
///     In-memory store of downscaled screenshot previews. Tokens are stored as SHA-256 digests. Entries are bounded
///     by lifetime, count and total bytes, and a service restart drops them. The full-resolution capture is never kept.
/// </summary>
public sealed class ScreenshotShareStore
{
    public const int MaxEntries = 16;

    public const long MaxTotalBytes = 32L * 1024 * 1024;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time;

    private readonly int _maxEntries;

    private readonly long _maxTotalBytes;

    private readonly TimeSpan _lifetime;

    private readonly object _gate = new();

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private long _sequence;

    public ScreenshotShareStore(TimeProvider time, int maxEntries = MaxEntries, long maxTotalBytes = MaxTotalBytes, TimeSpan? lifetime = null)
    {
        if (maxEntries < 1 || maxTotalBytes < 1 || (lifetime is { } ttl && ttl <= TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "Screenshot share limits must be positive.");
        }

        _time = time ?? throw new ArgumentNullException(nameof(time));
        _maxEntries = maxEntries;
        _maxTotalBytes = maxTotalBytes;
        _lifetime = lifetime ?? Lifetime;
    }

    /// <summary>A reserved preview. <see cref="Png" /> stays valid after <see cref="ScreenshotShareStore.Consume" />.</summary>
    public sealed record Reservation(string Key, byte[] Png);

    private sealed class Entry
    {
        public required string Key { get; init; }

        public required string Id { get; init; }

        public required byte[] Png { get; set; }

        public required DateTimeOffset ExpiresUtc { get; init; }

        public required long Sequence { get; init; }

        public bool InUse { get; set; }

        public bool Consumed { get; set; }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public OperationResult<ScreenshotShareLink> Issue(byte[] png, string baseUrl)
    {
        if (png is not { Length: > 0 })
        {
            return OperationResult<ScreenshotShareLink>.Fail(
                "share_unavailable",
                "share needs a preview. Set maxEdge above 0.");
        }

        if (png.Length > _maxTotalBytes)
        {
            return OperationResult<ScreenshotShareLink>.Fail(
                "share_unavailable",
                "The preview is too large to share. Lower maxEdge.");
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return OperationResult<ScreenshotShareLink>.Fail(
                "share_unavailable",
                "Screenshot sharing has no public base URL.");
        }

        byte[] copy = png.ToArray();
        string token = Base64Url(RandomNumberGenerator.GetBytes(32));
        string key = Hash(token);
        DateTimeOffset expires = _time.GetUtcNow() + _lifetime;
        string id;
        lock (_gate)
        {
            if (!TryMakeRoom(copy.Length))
            {
                return OperationResult<ScreenshotShareLink>.Fail(
                    "share_unavailable",
                    "Too many screenshot shares are in use. Try again shortly.");
            }

            do
            {
                id = "shot-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            }
            while (_entries.Values.Any(entry => string.Equals(entry.Id, id, StringComparison.Ordinal)));

            _entries[key] = new Entry
            {
                Key = key,
                Id = id,
                Png = copy,
                ExpiresUtc = expires,
                Sequence = ++_sequence
            };
        }

        string url = $"{baseUrl.TrimEnd('/')}/desktop/shares/{id}?ticket={Uri.EscapeDataString(token)}";
        return OperationResult<ScreenshotShareLink>.Ok(new ScreenshotShareLink(url, expires, true, ScreenshotShareLink.PresentToUser));
    }

    /// <summary>Atomically marks a matching ticket as in use. Returns null for any failure.</summary>
    public Reservation? TryReserve(string? id, string? token, out ShareFailure failure)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(token))
        {
            failure = ShareFailure.Missing;
            return null;
        }

        string key = Hash(token);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                PruneExpired(now);
                failure = ShareFailure.Unknown;
                return null;
            }

            if (!string.Equals(entry.Id, id, StringComparison.Ordinal))
            {
                failure = ShareFailure.Mismatched;
                return null;
            }

            if (entry.ExpiresUtc <= now)
            {
                if (!entry.InUse)
                {
                    _entries.Remove(key);
                }

                failure = ShareFailure.Expired;
                return null;
            }

            if (entry.InUse || entry.Consumed)
            {
                failure = ShareFailure.Replayed;
                return null;
            }

            entry.InUse = true;
            failure = ShareFailure.None;
            return new Reservation(key, entry.Png);
        }
    }

    /// <summary>Retires a reserved ticket after the preview was written to the client.</summary>
    public void Consume(Reservation reservation)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(reservation.Key, out Entry? entry))
            {
                entry.InUse = false;
                entry.Consumed = true;
                entry.Png = [];
            }
        }
    }

    /// <summary>Returns a reserved ticket to the pool after a failed or aborted transfer.</summary>
    public void Release(Reservation reservation)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(reservation.Key, out Entry? entry))
            {
                entry.InUse = false;
            }
        }
    }

    private bool TryMakeRoom(int incomingBytes)
    {
        PruneExpired(_time.GetUtcNow());
        while (Over(incomingBytes))
        {
            Entry? tombstone = Oldest(entry => entry.Consumed && !entry.InUse);
            if (tombstone is null)
            {
                break;
            }

            _entries.Remove(tombstone.Key);
        }

        while (Over(incomingBytes))
        {
            Entry? idle = Oldest(entry => !entry.InUse);
            if (idle is null)
            {
                return false;
            }

            _entries.Remove(idle.Key);
        }

        return !Over(incomingBytes);
    }

    private bool Over(int incomingBytes) =>
        _entries.Count >= _maxEntries || _entries.Values.Sum(entry => (long)entry.Png.Length) + incomingBytes > _maxTotalBytes;

    private Entry? Oldest(Func<Entry, bool> predicate) =>
        _entries.Values.Where(predicate).MinBy(entry => entry.Sequence);

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (string key in _entries.Where(pair => !pair.Value.InUse && pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToList())
        {
            _entries.Remove(key);
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
