// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;
using FhirPkg.Models;

namespace FhirPkg.Registry.CiBuild;

/// <summary>
/// Identifies a source repository that publishes a FHIR CI build, as an
/// organization (or user) and repository-name pair.
/// </summary>
/// <param name="Org">The GitHub organization or user that owns the repository.</param>
/// <param name="RepoName">The repository name.</param>
/// <remarks>
/// Equality and hashing are case-insensitive, matching GitHub's treatment of
/// organization and repository names.
/// </remarks>
internal readonly record struct CiBuildRepositoryIdentity(string Org, string RepoName)
{
    /// <inheritdoc />
    public bool Equals(CiBuildRepositoryIdentity other) =>
        string.Equals(Org, other.Org, StringComparison.OrdinalIgnoreCase)
        && string.Equals(RepoName, other.RepoName, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Org ?? string.Empty),
            StringComparer.OrdinalIgnoreCase.GetHashCode(RepoName ?? string.Empty));

    /// <inheritdoc />
    public override string ToString() => $"{Org}/{RepoName}";

    /// <summary>
    /// Renders this identity as a URL path fragment, escaping each segment exactly once.
    /// </summary>
    /// <returns>The escaped <c>{org}/{repo}</c> path fragment.</returns>
    public string ToUrlPath() =>
        $"{Uri.EscapeDataString(Org)}/{Uri.EscapeDataString(RepoName)}";
}

/// <summary>
/// Parses the assorted date shapes published by the FHIR CI build server into
/// a single comparable instant.
/// </summary>
/// <remarks>
/// Live <c>qas.json</c> data carries at least three shapes:
/// <c>2026-06-12T10:34:38-05:00</c> (ISO 8601), <c>Fri, 12 Jun, 2026 15:34:38 +0000</c>
/// (RFC 1123 with a non-standard comma after the month token), and
/// <c>20240617160736</c> (the compact form used by <c>package.manifest.json</c>).
/// Comparing these as strings, as the client previously did, orders them by
/// format rather than by time.
/// </remarks>
internal static class CiBuildDate
{
    private static readonly string[] s_exactFormats =
    [
        "yyyyMMddHHmmss",
        "yyyyMMddHHmm",
        "yyyyMMdd",
    ];

    private static readonly Regex s_monthCommaPattern =
        new(@"(?<=\d{1,2}\s\p{L}{3}),(?=\s\d{4})", RegexOptions.CultureInvariant);

    private static readonly Regex s_compactOffsetPattern =
        new(@"(?<=\s)(?<sign>[+-])(?<hours>\d{2})(?<minutes>\d{2})\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Attempts to parse a CI build date string into a <see cref="DateTimeOffset"/>.
    /// </summary>
    /// <param name="value">The raw date string; may be <see langword="null"/> or empty.</param>
    /// <param name="result">The parsed instant when parsing succeeds.</param>
    /// <returns><see langword="true"/> when the value was parsed; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out DateTimeOffset result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        string trimmed = value.Trim();

        if (DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out result))
        {
            return true;
        }

        if (DateTimeOffset.TryParseExact(
                trimmed,
                s_exactFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out result))
        {
            return true;
        }

        string normalized = Normalize(trimmed);

        return !string.Equals(normalized, trimmed, StringComparison.Ordinal)
            && DateTimeOffset.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out result);
    }

    /// <summary>
    /// Removes the non-standard comma after the month token and expands a trailing
    /// <c>±HHmm</c> offset to <c>±HH:mm</c>.
    /// </summary>
    private static string Normalize(string value)
    {
        string normalized = s_monthCommaPattern.Replace(value, string.Empty);

        return s_compactOffsetPattern.Replace(
            normalized,
            static match => $"{match.Groups["sign"].Value}{match.Groups["hours"].Value}:{match.Groups["minutes"].Value}");
    }
}

/// <summary>
/// A <see cref="CiBuildRecord"/> projected into the values CI build selection needs:
/// a parsed repository identity, the branch that produced the build, and a typed
/// build date.
/// </summary>
internal sealed record CiBuildCandidate
{
    /// <summary>The source <c>qas.json</c> record this candidate was projected from.</summary>
    public required CiBuildRecord Record { get; init; }

    /// <summary>The repository that published the build.</summary>
    public required CiBuildRepositoryIdentity Repository { get; init; }

    /// <summary>The branch that produced the build.</summary>
    public required string Branch { get; init; }

    /// <summary>
    /// The build date as a comparable instant, or <see langword="null"/> when the
    /// record's date could not be parsed.
    /// </summary>
    public DateTimeOffset? BuildDate { get; init; }

    /// <summary>
    /// Projects a <c>qas.json</c> record into a candidate.
    /// </summary>
    /// <param name="record">The record to project.</param>
    /// <returns>
    /// The candidate, or <see langword="null"/> when the record's <c>repo</c> field
    /// could not be parsed.
    /// </returns>
    public static CiBuildCandidate? TryCreate(CiBuildRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        (string Org, string RepoName, string Branch)? parsed = record.ParseRepo();
        if (parsed is null)
            return null;

        (string org, string repoName, string branch) = parsed.Value;

        return new CiBuildCandidate
        {
            Record = record,
            Repository = new CiBuildRepositoryIdentity(org, repoName),
            Branch = branch,
            BuildDate = CiBuildDate.TryParse(record.DateISO8601 ?? record.Date, out DateTimeOffset buildDate)
                ? buildDate
                : null,
        };
    }
}
