using System.Security.Cryptography;
using System.Text;

using Vestigo.Configuration;

namespace Vestigo.Files;

/// <summary>An issued ticket and when it stops working.</summary>
public sealed record IssuedTicket(string Token, DateTimeOffset ExpiresUtc);

/// <summary>
///     In-memory store of expiring single-use transfer tickets. Each ticket is bound to one HTTP method, one
///     canonical full path, and optional upload flags. Only SHA-256 digests of tickets are kept.
/// </summary>
public sealed class FileTicketStore(VestigoOptions options, TimeProvider? time = null)
{
    public const int MaxOutstanding = 1024;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public required string Method { get; init; }

        public required string FullPath { get; init; }

        public required bool Overwrite { get; init; }

        public required bool DeferToReboot { get; init; }

        public required DateTimeOffset ExpiresUtc { get; init; }

        public required long Sequence { get; init; }

        public bool InUse { get; set; }
    }

    /// <summary>Flags bound to a reserved upload ticket.</summary>
    public sealed record Reservation(string Key, bool Overwrite, bool DeferToReboot);

    private long _sequence;

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

    public IssuedTicket Issue(string method, string fullPath, bool overwrite = false, bool deferToReboot = false)
    {
        string token = Base64Url(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset expires = now + options.Files.TicketLifetime;
        lock (_gate)
        {
            Prune(now);
            while (_entries.Count >= MaxOutstanding)
            {
                string oldest = _entries.MinBy(pair => pair.Value.Sequence).Key;
                _entries.Remove(oldest);
            }

            _entries[Hash(token)] = new Entry
            {
                Method = method.ToUpperInvariant(),
                FullPath = Normalize(fullPath),
                Overwrite = overwrite,
                DeferToReboot = deferToReboot,
                ExpiresUtc = expires,
                Sequence = ++_sequence
            };
        }

        return new IssuedTicket(token, expires);
    }

    /// <summary>Atomically marks a matching ticket as in use. Returns null for any failure.</summary>
    public Reservation? TryReserve(string? token, string method, string fullPath)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        string key = Hash(token);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                return null;
            }

            if (entry.ExpiresUtc <= now)
            {
                _entries.Remove(key);
                return null;
            }

            if (entry.InUse ||
                !string.Equals(entry.Method, method, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entry.FullPath, Normalize(fullPath), StringComparison.Ordinal))
            {
                return null;
            }

            entry.InUse = true;
            return new Reservation(key, entry.Overwrite, entry.DeferToReboot);
        }
    }

    /// <summary>Makes a reserved ticket permanently invalid.</summary>
    public void Consume(Reservation reservation)
    {
        lock (_gate)
        {
            _entries.Remove(reservation.Key);
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

    private void Prune(DateTimeOffset now)
    {
        foreach (string key in _entries.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToList())
        {
            _entries.Remove(key);
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
