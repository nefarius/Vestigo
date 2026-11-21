using System.Security.Cryptography;
using System.Text;

using Vestigo.Configuration;

namespace Vestigo.SystemInfo;

/// <summary>
///     In-memory one-time consent requests. Tickets are stored as SHA-256 digests. A restart drops every request.
/// </summary>
public sealed class LogonConsentStore(TimeProvider time, VestigoOptions options)
{
    public const int MaxEntries = 16;

    public const int MaxFailedAttempts = 3;

    private readonly object _gate = new();

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public required string RequestId { get; init; }

        public required string TicketHash { get; init; }

        public required LogonConsentKind Kind { get; init; }

        public required string UserName { get; init; }

        public string? Domain { get; init; }

        public int? LogonCount { get; init; }

        public bool ForceAutoLogon { get; init; }

        public required DateTimeOffset ExpiresUtc { get; init; }

        public LogonRequestStatus Status { get; set; } = LogonRequestStatus.Pending;

        public string? ErrorCode { get; set; }

        public string? Error { get; set; }

        public int FailedAttempts { get; set; }

        public bool InFlight { get; set; }

        public bool Consumed { get; set; }

        public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    public OperationResult<LogonConsentLink> Issue(
        LogonConsentKind kind,
        string userName,
        string? domain,
        int? logonCount,
        bool forceAutoLogon,
        string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return OperationResult<LogonConsentLink>.Fail("consent_unavailable", "Consent links have no public base URL.");
        }

        TimeSpan lifetime = options.System.LogonConsentLifetime;
        DateTimeOffset expires = time.GetUtcNow() + lifetime;
        string token = Base64Url(RandomNumberGenerator.GetBytes(32));
        string hash = Hash(token);
        string id;
        lock (_gate)
        {
            PruneExpired();
            if (_entries.Count >= MaxEntries)
            {
                Entry? idle = _entries.Values
                    .Where(entry => entry.Status is not LogonRequestStatus.Pending
                                    and not LogonRequestStatus.Submitted
                                    && !entry.InFlight)
                    .MinBy(entry => entry.ExpiresUtc);
                if (idle is null)
                {
                    return OperationResult<LogonConsentLink>.Fail(
                        "consent_unavailable",
                        "Too many logon consent requests are pending. Cancel one or wait for it to expire.");
                }

                _entries.Remove(idle.RequestId);
            }

            do
            {
                id = "logon-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            }
            while (_entries.ContainsKey(id));

            _entries[id] = new Entry
            {
                RequestId = id,
                TicketHash = hash,
                Kind = kind,
                UserName = userName,
                Domain = domain,
                LogonCount = logonCount,
                ForceAutoLogon = forceAutoLogon,
                ExpiresUtc = expires
            };
        }

        string url = $"{baseUrl.TrimEnd('/')}/system/logon/consent/{id}?ticket={Uri.EscapeDataString(token)}";
        return OperationResult<LogonConsentLink>.Ok(new LogonConsentLink(
            id,
            url,
            expires,
            KindName(kind),
            userName,
            domain,
            LogonConsentLink.PresentToUser));
    }

    public LogonRequestView? Peek(string requestId)
    {
        lock (_gate)
        {
            PruneExpired();
            return _entries.TryGetValue(requestId, out Entry? entry) ? View(entry) : null;
        }
    }

    public async Task<LogonRequestView?> WaitAsync(string requestId, TimeSpan wait, CancellationToken cancellationToken)
    {
        TaskCompletionSource? signal;
        lock (_gate)
        {
            PruneExpired();
            if (!_entries.TryGetValue(requestId, out Entry? entry))
            {
                return null;
            }

            if (entry.Status is not LogonRequestStatus.Pending and not LogonRequestStatus.Submitted)
            {
                return View(entry);
            }

            signal = entry.Signal;
        }

        if (wait <= TimeSpan.Zero)
        {
            return Peek(requestId);
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(wait);
        try
        {
            await signal.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return Peek(requestId);
    }

    public bool TryOpen(string? id, string? ticket, out PendingConsent? pending, out string? errorCode, out string? error) =>
        TryOpen(id, ticket, claim: false, out pending, out errorCode, out error);

    public bool TryBeginDecision(string? id, string? ticket, out PendingConsent? pending, out string? errorCode, out string? error) =>
        TryOpen(id, ticket, claim: true, out pending, out errorCode, out error);

    private bool TryOpen(
        string? id,
        string? ticket,
        bool claim,
        out PendingConsent? pending,
        out string? errorCode,
        out string? error)
    {
        pending = null;
        errorCode = null;
        error = null;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(ticket))
        {
            errorCode = "missing_ticket";
            error = "Provide the consent ticket for this request.";
            return false;
        }

        string hash = Hash(ticket);
        lock (_gate)
        {
            PruneExpired();
            if (!_entries.TryGetValue(id, out Entry? entry))
            {
                errorCode = "unknown_request";
                error = "The consent request is unknown or has expired.";
                return false;
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(entry.TicketHash),
                    Encoding.UTF8.GetBytes(hash)))
            {
                errorCode = "mismatched_ticket";
                error = "The consent ticket does not match this request.";
                return false;
            }

            if (entry.Status is LogonRequestStatus.Pending && entry.ExpiresUtc <= time.GetUtcNow())
            {
                Mark(entry, LogonRequestStatus.Expired, "expired", "The consent request has expired.");
                errorCode = "expired";
                error = "The consent request has expired.";
                return false;
            }

            if (entry.Status is LogonRequestStatus.Expired)
            {
                errorCode = "expired";
                error = "The consent request has expired.";
                return false;
            }

            if (entry.Status is not LogonRequestStatus.Pending || (claim && entry.InFlight))
            {
                errorCode = "already_completed";
                error = "This consent request was already completed.";
                return false;
            }

            if (claim)
            {
                entry.InFlight = true;
            }

            pending = Snapshot(entry);
            return true;
        }
    }

    public void ReleaseDecision(string requestId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(requestId, out Entry? entry) &&
                entry.Status is LogonRequestStatus.Pending)
            {
                entry.InFlight = false;
            }
        }
    }

    public bool RecordFailure(string requestId, out bool locked)
    {
        locked = false;
        lock (_gate)
        {
            if (!_entries.TryGetValue(requestId, out Entry? entry) || entry.Status is not LogonRequestStatus.Pending)
            {
                return false;
            }

            entry.FailedAttempts++;
            if (entry.FailedAttempts >= MaxFailedAttempts)
            {
                Mark(entry, LogonRequestStatus.Failed, "too_many_attempts", "Too many incorrect passwords.");
                locked = true;
            }

            return true;
        }
    }

    public void Complete(string requestId, LogonRequestStatus status, string? errorCode, string? error)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(requestId, out Entry? entry) &&
                entry.Status is LogonRequestStatus.Pending or LogonRequestStatus.Submitted)
            {
                Mark(entry, status, errorCode, error);
            }
        }
    }

    public bool Cancel(string requestId) => Cancel(requestId, out _);

    public bool Cancel(string requestId, out LogonRequestView? view)
    {
        view = null;
        lock (_gate)
        {
            if (!_entries.TryGetValue(requestId, out Entry? entry) ||
                entry.Status is not LogonRequestStatus.Pending ||
                entry.InFlight)
            {
                return false;
            }

            Mark(entry, LogonRequestStatus.Cancelled, "cancelled", "The consent request was cancelled.");
            view = View(entry);
            return true;
        }
    }

    public sealed record PendingConsent(
        string RequestId,
        LogonConsentKind Kind,
        string UserName,
        string? Domain,
        int? LogonCount,
        bool ForceAutoLogon,
        DateTimeOffset ExpiresUtc,
        int RemainingAttempts);

    private void PruneExpired()
    {
        DateTimeOffset now = time.GetUtcNow();
        foreach (Entry entry in _entries.Values
                     .Where(item => item.Status == LogonRequestStatus.Pending && !item.InFlight && item.ExpiresUtc <= now)
                     .ToList())
        {
            Mark(entry, LogonRequestStatus.Expired, "expired", "The consent request has expired.");
        }
    }

    private static PendingConsent Snapshot(Entry entry) =>
        new(
            entry.RequestId,
            entry.Kind,
            entry.UserName,
            entry.Domain,
            entry.LogonCount,
            entry.ForceAutoLogon,
            entry.ExpiresUtc,
            MaxFailedAttempts - entry.FailedAttempts);

    private static void Mark(Entry entry, LogonRequestStatus status, string? errorCode, string? error)
    {
        entry.Status = status;
        entry.ErrorCode = errorCode;
        entry.Error = error;
        entry.InFlight = false;
        entry.Consumed = status is not LogonRequestStatus.Pending;
        if (status is not LogonRequestStatus.Submitted)
        {
            entry.Signal.TrySetResult();
        }
    }

    private static LogonRequestView View(Entry entry) =>
        new(
            entry.RequestId,
            KindName(entry.Kind),
            StatusName(entry.Status),
            entry.UserName,
            entry.Domain,
            entry.LogonCount,
            entry.ForceAutoLogon,
            entry.ExpiresUtc,
            entry.ErrorCode,
            entry.Error,
            Notes(entry));

    private static IReadOnlyList<string> Notes(Entry entry)
    {
        List<string> notes = [];
        if (entry.Status == LogonRequestStatus.Pending)
        {
            notes.Add("Waiting for the user to open the consent URL and enter the Windows password.");
        }

        notes.Add("The password is never returned.");
        return notes;
    }

    internal static string KindName(LogonConsentKind kind) => kind switch
    {
        LogonConsentKind.AutoLogonEnable => "autoLogon",
        LogonConsentKind.InteractiveLogon => "interactiveLogon",
        LogonConsentKind.UnlockWorkstation => "unlockWorkstation",
        _ => "unknown"
    };

    internal static string StatusName(LogonRequestStatus status) => status switch
    {
        LogonRequestStatus.Pending => "pending",
        LogonRequestStatus.Approved => "approved",
        LogonRequestStatus.Rejected => "rejected",
        LogonRequestStatus.Expired => "expired",
        LogonRequestStatus.Failed => "failed",
        LogonRequestStatus.Cancelled => "cancelled",
        LogonRequestStatus.Submitted => "submitted",
        _ => "unknown"
    };

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
