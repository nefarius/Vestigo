using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Vestigo.Diagnostics;

/// <summary>Turns SetupAPI dev-log lines into instance-matched sections. Matching is an exact header token.</summary>
internal static class SetupApiSectionParser
{
    public const int RawByteCap = 65_536;

    private static readonly Regex HexCode = new(@"0x[0-9A-Fa-f]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<InstallSection> Parse(IEnumerable<string> lines, string sourceFile, string instanceId, bool includeRaw)
    {
        List<InstallSection> sections = [];
        SectionBuilder? current = null;
        foreach (string line in lines)
        {
            if (InstanceToken(line) is { } token)
            {
                if (current is not null)
                {
                    sections.Add(current.Build());
                }

                current = token.Equals(instanceId, StringComparison.OrdinalIgnoreCase)
                    ? new SectionBuilder(line.Trim(), sourceFile, includeRaw)
                    : null;
                continue;
            }

            current?.Add(line);
        }

        if (current is not null)
        {
            sections.Add(current.Build());
        }

        return sections;
    }

    /// <summary>The text after the last " - " inside a <c>&gt;&gt;&gt; [...]</c> header, or null when the line is not a header.</summary>
    internal static string? InstanceToken(string line)
    {
        if (!line.TrimStart().StartsWith(">>>", StringComparison.Ordinal))
        {
            return null;
        }

        int open = line.IndexOf('[');
        int close = line.LastIndexOf(']');
        if (open < 0 || close <= open)
        {
            return null;
        }

        string inside = line[(open + 1)..close].Trim();
        int split = inside.LastIndexOf(" - ", StringComparison.Ordinal);
        string token = (split < 0 ? inside : inside[(split + 3)..]).Trim();
        return token.Length == 0 ? null : token;
    }

    private sealed class SectionBuilder(string title, string sourceFile, bool includeRaw)
    {
        private readonly List<string> _codes = [];
        private readonly List<string> _inf = [];
        private readonly List<string> _versions = [];
        private readonly List<string> _raw = [];
        private int _rawBytes;
        private bool _rawTruncated;
        private bool _sawEnd;
        private bool _sawExit;
        private DateTimeOffset? _started;
        private DateTimeOffset? _ended;
        private string? _exit;

        public void Add(string line)
        {
            if (TryStamp(line, ">>>", "Section start", out DateTimeOffset started))
            {
                _started = started;
            }

            if (TryStamp(line, "<<<", "Section end", out DateTimeOffset ended))
            {
                _ended = ended;
                _sawEnd = true;
            }

            if (TryExit(line, out string? status))
            {
                _exit = status;
                _sawExit = true;
            }

            foreach (Match match in HexCode.Matches(line))
            {
                if (_codes.Contains(match.Value, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                _codes.Add(match.Value);
            }

            if (_inf.Count < 8 && line.Contains(".inf", StringComparison.OrdinalIgnoreCase))
            {
                _inf.Add(line.Trim());
            }

            if (_versions.Count < 8 &&
                (line.Contains("DriverVer", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("Driver Version", StringComparison.OrdinalIgnoreCase)))
            {
                _versions.Add(line.Trim());
            }

            if (!includeRaw || _rawTruncated)
            {
                return;
            }

            int bytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (_rawBytes + bytes > RawByteCap)
            {
                _rawTruncated = true;
                return;
            }

            _raw.Add(line);
            _rawBytes += bytes;
        }

        public InstallSection Build() => new(
            title,
            _started,
            _ended,
            _exit,
            _codes,
            _inf,
            _versions,
            sourceFile,
            _sawEnd && _sawExit,
            includeRaw ? _raw : null,
            includeRaw && _rawTruncated);

        private static bool TryStamp(string line, string marker, string label, out DateTimeOffset stamp)
        {
            stamp = default;
            string trimmed = line.Trim();
            if (!trimmed.StartsWith(marker, StringComparison.Ordinal))
            {
                return false;
            }

            int at = trimmed.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return false;
            }

            string tail = trimmed[(at + label.Length)..].Trim();
            if (!DateTime.TryParseExact(
                    tail,
                    ["yyyy/MM/dd HH:mm:ss.fff", "yyyy/MM/dd HH:mm:ss.ff", "yyyy/MM/dd HH:mm:ss.f", "yyyy/MM/dd HH:mm:ss"],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                    out DateTime parsed))
            {
                return false;
            }

            stamp = new DateTimeOffset(parsed);
            return true;
        }

        private static bool TryExit(string line, out string? status)
        {
            status = null;
            string trimmed = line.Trim();
            const string prefix = "<<<";
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            int label = trimmed.IndexOf("Exit status:", StringComparison.OrdinalIgnoreCase);
            if (label < 0)
            {
                return false;
            }

            string tail = trimmed[(label + "Exit status:".Length)..].Trim().TrimEnd(']').Trim();
            if (tail.Length == 0)
            {
                return false;
            }

            status = tail;
            return true;
        }
    }
}
