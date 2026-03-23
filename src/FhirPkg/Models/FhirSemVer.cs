// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace FhirPkg.Models;

/// <summary>
/// Represents a FHIR-aware semantic version with support for pre-release tags,
/// wildcard matching, and range evaluation following SemVer 2.0 rules with
/// FHIR-specific extensions for pre-release ordering.
/// </summary>
/// <remarks>
/// <para>
/// This class supports parsing, comparing, and matching FHIR package versions.
/// It follows the SemVer 2.0.0 specification with additional FHIR-specific rules
/// for pre-release tag classification and ordering.
/// </para>
/// <para>
/// FHIR pre-release ordering (highest to lowest):
/// Release &gt; Ballot &gt; Draft &gt; Snapshot &gt; CiBuild &gt; Other.
/// </para>
/// <para>
/// Supported version formats:
/// <list type="bullet">
///   <item><description>Standard: <c>"4.0.1"</c></description></item>
///   <item><description>Pre-release: <c>"6.0.0-ballot1"</c></description></item>
///   <item><description>Build metadata: <c>"1.2.3+20240115"</c> (ignored in comparison)</description></item>
///   <item><description>Wildcard patch: <c>"4.0.x"</c>, <c>"4.0.*"</c>, <c>"4.0.X"</c></description></item>
///   <item><description>Wildcard minor: <c>"4.x"</c>, <c>"4.*"</c>, <c>"4.X"</c></description></item>
///   <item><description>Wildcard all: <c>"*"</c></description></item>
///   <item><description>Two-segment (treated as wildcard): <c>"4.0"</c> → <c>"4.0.x"</c></description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class FhirSemVer : IComparable<FhirSemVer>, IEquatable<FhirSemVer>
{
    /// <summary>Specifies the level at which a version acts as a wildcard pattern.</summary>
    private enum WildcardLevel
    {
        /// <summary>Not a wildcard; all components are specified.</summary>
        None = 0,

        /// <summary>Patch is wildcarded (e.g., "4.0.x").</summary>
        Patch = 1,

        /// <summary>Minor and patch are wildcarded (e.g., "4.x").</summary>
        Minor = 2,

        /// <summary>All components are wildcarded (e.g., "*").</summary>
        All = 3,
    }

    private readonly WildcardLevel _wildcardLevel;

    // ────────────────────────────────────────────────────────────────────
    //  Properties
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Gets the major version component.</summary>
    public int Major { get; }

    /// <summary>Gets the minor version component.</summary>
    public int Minor { get; }

    /// <summary>Gets the patch version component.</summary>
    public int Patch { get; }

    /// <summary>
    /// Gets the pre-release tag (e.g., <c>"ballot1"</c>), or <c>null</c> for a release version.
    /// </summary>
    public string? PreRelease { get; }

    /// <summary>
    /// Gets the build metadata (e.g., <c>"20240115"</c>), or <c>null</c> if not specified.
    /// Build metadata is always ignored during comparison and equality checks.
    /// </summary>
    public string? BuildMetadata { get; }

    /// <summary>
    /// Gets a value indicating whether this version contains a wildcard component
    /// and therefore represents a matching pattern rather than a concrete version.
    /// </summary>
    public bool IsWildcard => _wildcardLevel != WildcardLevel.None;

    /// <summary>Gets a value indicating whether this is a pre-release version.</summary>
    public bool IsPreRelease => PreRelease is not null;

    /// <summary>
    /// Gets the classified FHIR pre-release type derived from the <see cref="PreRelease"/> tag.
    /// Returns <see cref="FhirPreReleaseType.Release"/> when <see cref="PreRelease"/> is <c>null</c>.
    /// </summary>
    public FhirPreReleaseType PreReleaseType { get; }

    // ────────────────────────────────────────────────────────────────────
    //  Constructor
    // ────────────────────────────────────────────────────────────────────

    private FhirSemVer(
        int major,
        int minor,
        int patch,
        string? preRelease,
        string? buildMetadata,
        WildcardLevel wildcardLevel)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
        BuildMetadata = buildMetadata;
        _wildcardLevel = wildcardLevel;
        PreReleaseType = ClassifyPreRelease(preRelease);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Parsing
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a version string into a <see cref="FhirSemVer"/> instance.
    /// </summary>
    /// <param name="versionString">
    /// A version string such as <c>"4.0.1"</c>, <c>"6.0.0-ballot1"</c>,
    /// <c>"4.0.x"</c>, or <c>"*"</c>.
    /// </param>
    /// <returns>A parsed <see cref="FhirSemVer"/> instance.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="versionString"/> is <c>null</c>, empty, or whitespace.
    /// </exception>
    /// <exception cref="FormatException">
    /// <paramref name="versionString"/> is not a valid version format.
    /// </exception>
    public static FhirSemVer Parse(string versionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionString);

        if (!TryParse(versionString, out var result))
            throw new FormatException($"Invalid version format: '{versionString}'.");

        return result;
    }

    /// <summary>
    /// Attempts to parse a version string into a <see cref="FhirSemVer"/> instance.
    /// </summary>
    /// <param name="versionString">The version string to parse, or <c>null</c>.</param>
    /// <param name="result">
    /// When this method returns <c>true</c>, contains the parsed version;
    /// otherwise, <c>null</c>.
    /// </param>
    /// <returns><c>true</c> if parsing succeeded; otherwise, <c>false</c>.</returns>
    public static bool TryParse(
        [NotNullWhen(true)] string? versionString,
        [NotNullWhen(true)] out FhirSemVer? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(versionString))
            return false;

        var input = versionString.AsSpan().Trim();

        // Pure wildcard: "*", "x", "X"
        if (input is "*" or "x" or "X")
        {
            result = new FhirSemVer(0, 0, 0, null, null, WildcardLevel.All);
            return true;
        }

        // Separate build metadata (everything after the first '+')
        string? buildMetadata = null;
        var plusIndex = input.IndexOf('+');
        if (plusIndex >= 0)
        {
            var bm = input[(plusIndex + 1)..];
            if (bm.Length == 0 || !IsValidIdentifier(bm))
                return false;
            buildMetadata = new string(bm);
            input = input[..plusIndex];
        }

        // Separate pre-release (everything after the first '-' in the remaining string)
        string? preRelease = null;
        var dashIndex = input.IndexOf('-');
        if (dashIndex >= 0)
        {
            var pr = input[(dashIndex + 1)..];
            if (pr.Length == 0 || !IsValidIdentifier(pr))
                return false;
            preRelease = new string(pr);
            input = input[..dashIndex];
        }

        // Parse the version core segments using span indexing (avoids Split allocation)
        var firstDot = input.IndexOf('.');
        if (firstDot < 0)
            return false;

        var afterFirst = input[(firstDot + 1)..];
        var secondDot = afterFirst.IndexOf('.');

        if (secondDot < 0)
        {
            // Two segments: "4.x", "4.*", "4.X", or "4.0"
            if (!TryParseSegment(input[..firstDot], out var major))
                return false;

            if (preRelease is not null || buildMetadata is not null)
                return false;

            if (IsWildcardSegment(afterFirst))
            {
                result = new FhirSemVer(major, 0, 0, null, null, WildcardLevel.Minor);
                return true;
            }

            if (TryParseSegment(afterFirst, out var minor))
            {
                result = new FhirSemVer(major, minor, 0, null, null, WildcardLevel.Patch);
                return true;
            }

            return false;
        }
        else
        {
            // Three segments
            var seg0 = input[..firstDot];
            var seg1 = afterFirst[..secondDot];
            var seg2 = afterFirst[(secondDot + 1)..];

            // Reject 4+ segments
            if (seg2.IndexOf('.') >= 0)
                return false;

            if (!TryParseSegment(seg0, out var major))
                return false;
            if (!TryParseSegment(seg1, out var minor))
                return false;

            if (IsWildcardSegment(seg2))
            {
                if (preRelease is not null || buildMetadata is not null)
                    return false;
                result = new FhirSemVer(major, minor, 0, null, null, WildcardLevel.Patch);
                return true;
            }

            if (TryParseSegment(seg2, out var patch))
            {
                result = new FhirSemVer(major, minor, patch, preRelease, buildMetadata, WildcardLevel.None);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Parses a single non-negative integer version segment, rejecting leading zeros
    /// per the SemVer 2.0 specification (e.g., <c>"01"</c> is invalid).
    /// </summary>
    private static bool TryParseSegment(ReadOnlySpan<char> segment, out int value)
    {
        value = 0;
        if (segment.Length == 0)
            return false;

        // SemVer 2.0: numeric identifiers MUST NOT include leading zeroes.
        if (segment.Length > 1 && segment[0] == '0')
            return false;

        return int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsWildcardSegment(ReadOnlySpan<char> segment) =>
        segment is "x" or "X" or "*";

    /// <summary>
    /// Validates that a pre-release or build metadata string contains only
    /// alphanumeric characters, hyphens, and dots (per SemVer 2.0).
    /// </summary>
    private static bool IsValidIdentifier(ReadOnlySpan<char> value)
    {
        foreach (var ch in value)
        {
            if (!char.IsLetterOrDigit(ch) && ch is not '.' and not '-')
                return false;
        }

        return true;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Pre-release Classification
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Classifies a pre-release tag into the corresponding <see cref="FhirPreReleaseType"/>.
    /// </summary>
    private static FhirPreReleaseType ClassifyPreRelease(string? preRelease)
    {
        if (preRelease is null)
            return FhirPreReleaseType.Release;

        if (preRelease.Contains("ballot", StringComparison.OrdinalIgnoreCase))
            return FhirPreReleaseType.Ballot;
        if (preRelease.Contains("draft", StringComparison.OrdinalIgnoreCase))
            return FhirPreReleaseType.Draft;
        if (preRelease.Contains("snapshot", StringComparison.OrdinalIgnoreCase))
            return FhirPreReleaseType.Snapshot;
        if (preRelease.Contains("cibuild", StringComparison.OrdinalIgnoreCase))
            return FhirPreReleaseType.CiBuild;

        return FhirPreReleaseType.Other;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Comparison
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compares this version to another following SemVer 2.0 rules with
    /// FHIR-specific pre-release ordering.
    /// </summary>
    /// <remarks>
    /// <para>Comparison rules:</para>
    /// <list type="number">
    ///   <item><description>Major, Minor, and Patch are compared numerically.</description></item>
    ///   <item><description>
    ///     A release version (no pre-release tag) is <b>greater</b> than any pre-release
    ///     of the same Major.Minor.Patch.
    ///   </description></item>
    ///   <item><description>
    ///     Pre-release ordering: Release &gt; Ballot &gt; Draft &gt; Snapshot &gt; CiBuild &gt; Other.
    ///   </description></item>
    ///   <item><description>
    ///     Within the same pre-release type, numeric suffixes are compared
    ///     (e.g., <c>"ballot2"</c> &gt; <c>"ballot1"</c>).
    ///   </description></item>
    ///   <item><description>Build metadata is ignored.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="other">The version to compare to, or <c>null</c>.</param>
    /// <returns>
    /// A negative value if this version precedes <paramref name="other"/>;
    /// zero if they are equal; a positive value if this version follows <paramref name="other"/>.
    /// A non-<c>null</c> instance is always greater than <c>null</c>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Either this instance or <paramref name="other"/> is a wildcard version.
    /// Wildcard versions represent matching patterns and cannot be ordered.
    /// Use <see cref="Satisfies(FhirSemVer)"/> for wildcard matching instead.
    /// </exception>
    public int CompareTo(FhirSemVer? other)
    {
        if (other is null)
            return 1;

        if (IsWildcard || other.IsWildcard)
        {
            throw new InvalidOperationException(
                "Wildcard versions cannot be compared directly. " +
                "Use Satisfies() for wildcard matching.");
        }

        // Compare numeric components
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;

        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;

        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;

        // Release (no pre-release) is greater than any pre-release
        if (!IsPreRelease && other.IsPreRelease) return 1;
        if (IsPreRelease && !other.IsPreRelease) return -1;
        if (!IsPreRelease) return 0; // Both are releases

        // Both are pre-release: compare by FHIR type.
        // Lower enum value = higher priority, so we reverse the comparison.
        result = other.PreReleaseType.CompareTo(PreReleaseType);
        if (result != 0) return result;

        // Same pre-release type: compare numeric suffix, then fall back to string
        return ComparePreReleaseSuffix(PreRelease!, other.PreRelease!);
    }

    /// <summary>
    /// Compares two pre-release strings by their trailing numeric suffix,
    /// falling back to ordinal string comparison when suffixes are equal.
    /// </summary>
    private static int ComparePreReleaseSuffix(string a, string b)
    {
        var suffixA = ExtractNumericSuffix(a);
        var suffixB = ExtractNumericSuffix(b);

        if (suffixA != suffixB)
            return suffixA.CompareTo(suffixB);

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the trailing numeric suffix from a pre-release tag.
    /// Returns <c>0</c> if no trailing digits are present or on overflow.
    /// </summary>
    private static int ExtractNumericSuffix(string preRelease)
    {
        var i = preRelease.Length - 1;
        while (i >= 0 && char.IsAsciiDigit(preRelease[i]))
            i--;

        // No trailing digits
        if (i == preRelease.Length - 1)
            return 0;

        if (int.TryParse(
                preRelease.AsSpan(i + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var suffix))
        {
            return suffix;
        }

        return 0;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Equality
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Determines whether this version is equal to another.
    /// Two versions are equal when their Major, Minor, Patch, PreRelease, and
    /// wildcard level all match. Build metadata is ignored.
    /// </summary>
    /// <param name="other">The version to compare with, or <c>null</c>.</param>
    /// <returns><c>true</c> if the versions are equal; otherwise, <c>false</c>.</returns>
    public bool Equals(FhirSemVer? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        return Major == other.Major
            && Minor == other.Minor
            && Patch == other.Patch
            && _wildcardLevel == other._wildcardLevel
            && string.Equals(PreRelease, other.PreRelease, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as FhirSemVer);

    /// <summary>
    /// Returns a hash code based on Major, Minor, Patch, PreRelease, and wildcard level.
    /// Build metadata is ignored.
    /// </summary>
    public override int GetHashCode() =>
        HashCode.Combine(Major, Minor, Patch, (int)_wildcardLevel, PreRelease?.ToLowerInvariant());

    // ────────────────────────────────────────────────────────────────────
    //  Operators
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Determines whether two versions are equal.</summary>
    public static bool operator ==(FhirSemVer? left, FhirSemVer? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Determines whether two versions are not equal.</summary>
    public static bool operator !=(FhirSemVer? left, FhirSemVer? right) =>
        !(left == right);

    /// <summary>Determines whether the left version is less than the right version.</summary>
    /// <exception cref="InvalidOperationException">Either operand is a wildcard.</exception>
    public static bool operator <(FhirSemVer? left, FhirSemVer? right)
    {
        if (left is null) return right is not null;
        return left.CompareTo(right) < 0;
    }

    /// <summary>Determines whether the left version is greater than the right version.</summary>
    /// <exception cref="InvalidOperationException">Either operand is a wildcard.</exception>
    public static bool operator >(FhirSemVer? left, FhirSemVer? right) =>
        right < left;

    /// <summary>Determines whether the left version is less than or equal to the right version.</summary>
    /// <exception cref="InvalidOperationException">Either operand is a wildcard.</exception>
    public static bool operator <=(FhirSemVer? left, FhirSemVer? right)
    {
        if (left is null) return true;
        return left.CompareTo(right) <= 0;
    }

    /// <summary>Determines whether the left version is greater than or equal to the right version.</summary>
    /// <exception cref="InvalidOperationException">Either operand is a wildcard.</exception>
    public static bool operator >=(FhirSemVer? left, FhirSemVer? right) =>
        right <= left;

    // ────────────────────────────────────────────────────────────────────
    //  Wildcard / Range Matching
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Determines whether this concrete version satisfies the given version specifier string.
    /// </summary>
    /// <param name="versionSpecifier">
    /// A version string or wildcard pattern such as <c>"4.0.1"</c>, <c>"4.0.x"</c>,
    /// <c>"4.x"</c>, or <c>"*"</c>.
    /// </param>
    /// <returns>
    /// <c>true</c> if this version satisfies the specifier; otherwise, <c>false</c>.
    /// Always returns <c>false</c> when this instance itself is a wildcard.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="versionSpecifier"/> is <c>null</c>, empty, or whitespace.
    /// </exception>
    /// <exception cref="FormatException">
    /// <paramref name="versionSpecifier"/> is not a valid version format.
    /// </exception>
    public bool Satisfies(string versionSpecifier)
    {
        var spec = Parse(versionSpecifier);
        return Satisfies(spec);
    }

    /// <summary>
    /// Determines whether this concrete version satisfies the given version specifier.
    /// </summary>
    /// <param name="other">
    /// A version or wildcard pattern to match against.
    /// If <paramref name="other"/> is a wildcard, wildcard matching rules apply.
    /// Otherwise, exact equality is used.
    /// </param>
    /// <returns>
    /// <c>true</c> if this version satisfies the specifier; otherwise, <c>false</c>.
    /// Always returns <c>false</c> when this instance itself is a wildcard, since
    /// a wildcard is a pattern, not a concrete version.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <c>null</c>.</exception>
    public bool Satisfies(FhirSemVer other)
    {
        ArgumentNullException.ThrowIfNull(other);

        // A wildcard is a pattern, not a concrete version — it cannot satisfy anything.
        if (IsWildcard)
            return false;

        if (other.IsWildcard)
        {
            return other._wildcardLevel switch
            {
                WildcardLevel.All => true,
                WildcardLevel.Minor => Major == other.Major,
                WildcardLevel.Patch => Major == other.Major && Minor == other.Minor,
                _ => false,
            };
        }

        return Equals(other);
    }

    /// <summary>
    /// Finds the maximum version from a collection that satisfies the given specifier.
    /// </summary>
    /// <param name="versions">The candidate versions to evaluate.</param>
    /// <param name="specifier">
    /// A version string or wildcard pattern (e.g., <c>"4.0.1"</c>, <c>"4.0.x"</c>, <c>"*"</c>).
    /// </param>
    /// <param name="includePreRelease">
    /// When <c>true</c>, pre-release versions are included in the candidates.
    /// When <c>false</c> (the default), pre-release versions are excluded
    /// <b>unless</b> the <paramref name="specifier"/> itself contains a pre-release tag.
    /// </param>
    /// <returns>
    /// The highest version that satisfies the specifier, or <c>null</c> if none match.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="versions"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="specifier"/> is <c>null</c> or empty.</exception>
    /// <exception cref="FormatException"><paramref name="specifier"/> is not a valid version format.</exception>
    public static FhirSemVer? MaxSatisfying(
        IEnumerable<FhirSemVer> versions,
        string specifier,
        bool includePreRelease = false)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentException.ThrowIfNullOrEmpty(specifier);

        var spec = Parse(specifier);
        var allowPreRelease = includePreRelease || spec.IsPreRelease;

        return versions
            .Where(v => !v.IsWildcard)
            .Where(v => v.Satisfies(spec))
            .Where(v => allowPreRelease || !v.IsPreRelease)
            .Max();
    }

    /// <summary>
    /// Returns all versions from a collection that satisfy a range expression.
    /// </summary>
    /// <remarks>
    /// <para>Supported range expressions:</para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Caret</b>: <c>"^3.0.1"</c> — matches ≥3.0.1 and &lt;4.0.0
    ///     (allows minor and patch bumps).
    ///   </description></item>
    ///   <item><description>
    ///     <b>Tilde</b>: <c>"~3.0.1"</c> — matches ≥3.0.1 and &lt;3.1.0
    ///     (allows patch bumps only).
    ///   </description></item>
    ///   <item><description>
    ///     <b>Pipe (OR)</b>: <c>"1.0.0|3.0.0"</c> — matches either version.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Hyphen</b>: <c>"1.0.0 - 2.0.0"</c> — matches ≥1.0.0 and ≤2.0.0.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Wildcard</b>: <c>"4.0.x"</c> — matches any patch version
    ///     with major=4, minor=0.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Exact</b>: <c>"4.0.1"</c> — matches exactly that version.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Pipe-separated sub-expressions can individually use any of the above syntaxes
    /// (e.g., <c>"^1.0.0|~2.3.0"</c>).
    /// </para>
    /// </remarks>
    /// <param name="versions">The candidate versions to evaluate.</param>
    /// <param name="rangeExpression">The range expression to evaluate.</param>
    /// <returns>All versions satisfying the range expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="versions"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="rangeExpression"/> is <c>null</c> or empty.</exception>
    /// <exception cref="FormatException">The range expression contains invalid version syntax.</exception>
    public static IEnumerable<FhirSemVer> SatisfyingRange(
        IEnumerable<FhirSemVer> versions,
        string rangeExpression)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentException.ThrowIfNullOrEmpty(rangeExpression);

        // Materialize once so multiple pipe-separated parts can iterate the same source.
        var versionList = versions as IReadOnlyList<FhirSemVer> ?? versions.ToList();
        var parts = rangeExpression.Split('|');

        if (parts.Length == 1)
            return EvaluateRangePart(versionList, parts[0].Trim());

        // Union results from all pipe-separated sub-expressions.
        var results = new HashSet<FhirSemVer>();
        foreach (var part in parts)
        {
            foreach (var v in EvaluateRangePart(versionList, part.Trim()))
                results.Add(v);
        }

        return results;
    }

    /// <summary>
    /// Evaluates a single (non-pipe) range expression part against a list of versions.
    /// </summary>
    private static IEnumerable<FhirSemVer> EvaluateRangePart(
        IReadOnlyList<FhirSemVer> versions,
        string part)
    {
        // Hyphen range: "1.0.0 - 2.0.0"
        var hyphenIndex = part.IndexOf(" - ", StringComparison.Ordinal);
        if (hyphenIndex >= 0)
        {
            var lower = ParseExactForRange(part[..hyphenIndex].Trim(), part);
            var upper = ParseExactForRange(part[(hyphenIndex + 3)..].Trim(), part);
            return versions.Where(v => !v.IsWildcard && v >= lower && v <= upper);
        }

        // Caret range: "^3.0.1"
        if (part.StartsWith('^'))
        {
            var baseVersion = ParseExactForRange(part[1..], part);
            var ceiling = new FhirSemVer(
                baseVersion.Major + 1, 0, 0, null, null, WildcardLevel.None);
            return versions.Where(v => !v.IsWildcard && v >= baseVersion && v < ceiling);
        }

        // Tilde range: "~3.0.1"
        if (part.StartsWith('~'))
        {
            var baseVersion = ParseExactForRange(part[1..], part);
            var ceiling = new FhirSemVer(
                baseVersion.Major, baseVersion.Minor + 1, 0, null, null, WildcardLevel.None);
            return versions.Where(v => !v.IsWildcard && v >= baseVersion && v < ceiling);
        }

        // Wildcard or exact match
        var specifier = Parse(part);
        return versions.Where(v => v.Satisfies(specifier));
    }

    /// <summary>
    /// Parses a version for use in a range expression, requiring it to be an exact
    /// (non-wildcard) version.
    /// </summary>
    private static FhirSemVer ParseExactForRange(string versionPart, string fullExpression)
    {
        var version = Parse(versionPart);
        if (version.IsWildcard)
        {
            throw new FormatException(
                $"Range expressions require exact versions, not wildcards: '{fullExpression}'.");
        }

        return version;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Formatting
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the version as a normalized string.
    /// </summary>
    /// <returns>
    /// The version string — for example, <c>"4.0.1"</c>, <c>"6.0.0-ballot1"</c>,
    /// <c>"4.0.x"</c>, or <c>"*"</c>.
    /// </returns>
    public override string ToString() => _wildcardLevel switch
    {
        WildcardLevel.All => "*",
        WildcardLevel.Minor => $"{Major}.x",
        WildcardLevel.Patch => $"{Major}.{Minor}.x",
        _ => FormatExactVersion(),
    };

    private string FormatExactVersion() => (PreRelease, BuildMetadata) switch
    {
        (not null, not null) => $"{Major}.{Minor}.{Patch}-{PreRelease}+{BuildMetadata}",
        (not null, null) => $"{Major}.{Minor}.{Patch}-{PreRelease}",
        (null, not null) => $"{Major}.{Minor}.{Patch}+{BuildMetadata}",
        _ => $"{Major}.{Minor}.{Patch}",
    };
}
