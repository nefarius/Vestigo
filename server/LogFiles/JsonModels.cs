using System.Text.Json;

namespace Vestigo.LogFiles;

/// <summary>One value selected by a JSON query.</summary>
/// <param name="MatchedAt">Canonical path of this match.</param>
/// <param name="Type">JSON type: object, array, string, number, boolean, or null.</param>
/// <param name="Value">
///     The JSON value. Absent only when <paramref name="ValueOmitted" /> is true.
///     A JSON null is a <see cref="JsonValueKind.Null" /> element, so it stays present after the document is released.
/// </param>
/// <param name="SerializedBytes">UTF-8 size of the matched JSON text.</param>
/// <param name="ValueOmitted">True when the value exceeded the configured byte limit and was left out.</param>
public sealed record JsonMatch(
    string MatchedAt,
    string Type,
    JsonElement? Value,
    long SerializedBytes,
    bool ValueOmitted);

/// <summary>Result of <c>read_json_path</c>.</summary>
/// <param name="Path">Root path requested by the caller.</param>
/// <param name="DocumentBytes">UTF-8 size of the document.</param>
/// <param name="Sha256">SHA-256 of the document bytes, uppercase hexadecimal.</param>
/// <param name="RootType">JSON type of the document root.</param>
/// <param name="TopLevelKeys">Property names when the root is an object, capped by <c>MaxJsonMatches</c>.</param>
/// <param name="TopLevelKeysTruncated">True when <paramref name="TopLevelKeys" /> is shorter than the object.</param>
/// <param name="Matches">Values selected by the query, in document order.</param>
/// <param name="Truncated">True when more values matched than <c>MaxJsonMatches</c> allows.</param>
public sealed record JsonQueryResult(
    string Path,
    long DocumentBytes,
    string Sha256,
    string RootType,
    IReadOnlyList<string> TopLevelKeys,
    bool TopLevelKeysTruncated,
    IReadOnlyList<JsonMatch> Matches,
    bool Truncated);

/// <summary>One top-level property in a JSON summary.</summary>
/// <param name="Name">Property name.</param>
/// <param name="Type">JSON type of the property value.</param>
/// <param name="SerializedBytes">UTF-8 size of the property value.</param>
public sealed record JsonMemberSummary(string Name, string Type, long SerializedBytes);

/// <summary>Result of <c>get_json_summary</c>. Member details are returned only for a top-level object.</summary>
/// <param name="Path">Root path requested by the caller.</param>
/// <param name="DocumentBytes">UTF-8 size of the document.</param>
/// <param name="Sha256">SHA-256 of the document bytes, uppercase hexadecimal.</param>
/// <param name="RootType">JSON type of the document root.</param>
/// <param name="Length">Element count when the root is an array.</param>
/// <param name="MemberCount">Property count when the root is an object.</param>
/// <param name="Members">Top-level properties, capped by <c>MaxJsonMatches</c>.</param>
/// <param name="Truncated">True when <paramref name="Members" /> is shorter than the object.</param>
public sealed record JsonSummary(
    string Path,
    long DocumentBytes,
    string Sha256,
    string RootType,
    int? Length,
    int? MemberCount,
    IReadOnlyList<JsonMemberSummary> Members,
    bool Truncated);
