// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace FhirPkg.Models;

/// <summary>
/// Represents a FHIR-aware semantic version or standalone wildcard pattern.
/// </summary>
/// <remarks>
/// <para>
/// Concrete versions preserve two-part versus three-part precision. Wildcard
/// matching is component-aware: <c>*</c> can target any supported part,
/// <c>x</c>/<c>X</c> alias numeric minor and patch wildcards, and a trailing
/// <c>?</c> matches its current part and ignores all remaining parts.
/// </para>
/// <para>
/// FHIR pre-release ordering (highest to lowest):
/// Release &gt; Ballot &gt; Draft &gt; Snapshot &gt; CiBuild &gt; Other.
/// Build metadata is ignored for concrete precedence and equality.
/// </para>
/// </remarks>
public sealed class FhirSemVer : IComparable<FhirSemVer>, IEquatable<FhirSemVer>
{
    private enum PartKind
    {
        Missing = 0,
        Literal = 1,
        Wildcard = 2,
    }

    private enum RemainderBoundary
    {
        None = 0,
        Major = 1,
        Minor = 2,
        Patch = 3,
        PreRelease = 4,
        Build = 5,
    }

    private readonly record struct NumericPart(PartKind Kind, int Value)
    {
        public static NumericPart Missing => new(PartKind.Missing, 0);

        public static NumericPart Literal(int value) => new(PartKind.Literal, value);

        public static NumericPart Wildcard => new(PartKind.Wildcard, 0);
    }

    private readonly record struct LabelPart(PartKind Kind, string? Value)
    {
        public static LabelPart Missing => new(PartKind.Missing, null);

        public static LabelPart Literal(string value) => new(PartKind.Literal, value);

        public static LabelPart Wildcard => new(PartKind.Wildcard, "*");
    }

    private readonly NumericPart _majorPart;
    private readonly NumericPart _minorPart;
    private readonly NumericPart _patchPart;
    private readonly LabelPart _preReleasePart;
    private readonly LabelPart _buildPart;
    private readonly RemainderBoundary _remainderBoundary;
    private readonly bool _isAllWildcard;

    /// <summary>Gets the major version component, or zero when it is wildcarded.</summary>
    public int Major => _majorPart.Value;

    /// <summary>Gets the minor version component, or zero when it is wildcarded.</summary>
    public int Minor => _minorPart.Value;

    /// <summary>
    /// Gets the patch version component, or zero when it is missing or wildcarded.
    /// </summary>
    public int Patch => _patchPart.Value;

    /// <summary>
    /// Gets the pre-release tag, <c>*</c> for a pre-release wildcard, or
    /// <c>null</c> when the part is absent.
    /// </summary>
    public string? PreRelease => _preReleasePart.Value;

    /// <summary>
    /// Gets the build metadata, <c>*</c> for a build wildcard, or <c>null</c>
    /// when the part is absent.
    /// </summary>
    public string? BuildMetadata => _buildPart.Value;

    /// <summary>
    /// Gets a value indicating whether this instance is a matching pattern
    /// rather than a concrete version.
    /// </summary>
    public bool IsWildcard =>
        _isAllWildcard ||
        _remainderBoundary != RemainderBoundary.None ||
        _majorPart.Kind == PartKind.Wildcard ||
        _minorPart.Kind == PartKind.Wildcard ||
        _patchPart.Kind == PartKind.Wildcard ||
        _preReleasePart.Kind == PartKind.Wildcard ||
        _buildPart.Kind == PartKind.Wildcard;

    internal bool HasThreePartCore => _patchPart.Kind != PartKind.Missing;

    /// <summary>
    /// Gets a value indicating whether the version or pattern contains a
    /// required pre-release part.
    /// </summary>
    public bool IsPreRelease => _preReleasePart.Kind != PartKind.Missing;

    /// <summary>
    /// Gets the classified FHIR pre-release type derived from
    /// <see cref="PreRelease"/>.
    /// </summary>
    public FhirPreReleaseType PreReleaseType { get; }

    private FhirSemVer(
        NumericPart majorPart,
        NumericPart minorPart,
        NumericPart patchPart,
        LabelPart preReleasePart,
        LabelPart buildPart,
        RemainderBoundary remainderBoundary,
        bool isAllWildcard)
    {
        _majorPart = majorPart;
        _minorPart = minorPart;
        _patchPart = patchPart;
        _preReleasePart = preReleasePart;
        _buildPart = buildPart;
        _remainderBoundary = remainderBoundary;
        _isAllWildcard = isAllWildcard;
        PreReleaseType = ClassifyPreRelease(PreRelease);
    }

    internal static FhirSemVer CreateExact(int major, int minor, int patch) =>
        new(
            NumericPart.Literal(major),
            NumericPart.Literal(minor),
            NumericPart.Literal(patch),
            LabelPart.Missing,
            LabelPart.Missing,
            RemainderBoundary.None,
            false);

    internal IReadOnlyList<FhirSemVer> GetCompatibilityBoundaryCandidates(
        bool allowPreRelease)
    {
        if (_isAllWildcard)
        {
            return
            [
                new FhirSemVer(
                    NumericPart.Literal(0),
                    NumericPart.Literal(0),
                    NumericPart.Missing,
                    allowPreRelease
                        ? LabelPart.Literal("-")
                        : LabelPart.Missing,
                    LabelPart.Missing,
                    RemainderBoundary.None,
                    false)
            ];
        }

        if (!allowPreRelease && IsPreRelease)
            return [];
        if (!IsWildcard)
            return [this];

        NumericPart majorPart = CreateBoundaryPart(_majorPart);
        NumericPart minorPart = CreateBoundaryPart(_minorPart);
        NumericPart patchPart = CreateBoundaryPart(_patchPart);
        LabelPart preReleasePart = CreateBoundaryPart(_preReleasePart);
        LabelPart buildPart = CreateBoundaryPart(_buildPart);

        if (_remainderBoundary is
                RemainderBoundary.Minor or RemainderBoundary.Patch &&
            preReleasePart.Kind == PartKind.Missing &&
            allowPreRelease)
        {
            preReleasePart = LabelPart.Literal("-");
        }

        FhirSemVer candidate = new(
            majorPart,
            minorPart,
            patchPart,
            preReleasePart,
            buildPart,
            RemainderBoundary.None,
            false);
        return candidate.Satisfies(this) ? [candidate] : [];
    }

    private static NumericPart CreateBoundaryPart(NumericPart part) =>
        part.Kind == PartKind.Wildcard
            ? NumericPart.Literal(0)
            : part;

    private static LabelPart CreateBoundaryPart(LabelPart part) =>
        part.Kind == PartKind.Wildcard
            // "-" is the lowest valid identifier under the existing ordering.
            ? LabelPart.Literal("-")
            : part;

    /// <summary>
    /// Parses a concrete two-part or three-part version, or one standalone
    /// wildcard pattern.
    /// </summary>
    /// <param name="versionString">The version expression to parse.</param>
    /// <returns>The parsed version or pattern.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="versionString"/> is null, empty, or whitespace.
    /// </exception>
    /// <exception cref="FormatException">
    /// <paramref name="versionString"/> is not a supported expression.
    /// </exception>
    public static FhirSemVer Parse(string versionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionString);

        if (!TryParse(versionString, out FhirSemVer? result))
            throw new FormatException($"Invalid version format: '{versionString}'.");

        return result;
    }

    /// <summary>
    /// Attempts to parse a concrete two-part or three-part version, or one
    /// standalone wildcard pattern.
    /// </summary>
    public static bool TryParse(
        [NotNullWhen(true)] string? versionString,
        [NotNullWhen(true)] out FhirSemVer? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(versionString))
            return false;

        string input = versionString.Trim();
        bool hasRemainderBoundary = input.EndsWith('?');
        if (hasRemainderBoundary)
        {
            input = input[..^1];
            if (input.Length == 0 || input.Contains('?'))
                return false;
        }
        else if (input.Contains('?'))
        {
            return false;
        }

        if (input == "*")
        {
            if (hasRemainderBoundary)
                return false;

            result = new FhirSemVer(
                NumericPart.Missing,
                NumericPart.Missing,
                NumericPart.Missing,
                LabelPart.Missing,
                LabelPart.Missing,
                RemainderBoundary.None,
                true);
            return true;
        }

        LabelPart buildPart = LabelPart.Missing;
        int plusIndex = input.IndexOf('+');
        if (plusIndex >= 0)
        {
            if (input.IndexOf('+', plusIndex + 1) >= 0)
                return false;

            string build = input[(plusIndex + 1)..];
            if (!TryParseLabelPart(build, out buildPart))
                return false;

            input = input[..plusIndex];
        }

        LabelPart preReleasePart = LabelPart.Missing;
        int dashIndex = input.IndexOf('-');
        if (dashIndex >= 0)
        {
            string preRelease = input[(dashIndex + 1)..];
            if (!TryParseLabelPart(preRelease, out preReleasePart))
                return false;

            input = input[..dashIndex];
        }

        string[] numericSegments = input.Split('.', StringSplitOptions.None);
        if (numericSegments.Length is < 2 or > 3)
            return false;

        if (!TryParseNumericPart(numericSegments[0], false, out NumericPart majorPart) ||
            !TryParseNumericPart(numericSegments[1], true, out NumericPart minorPart))
        {
            return false;
        }

        NumericPart patchPart = NumericPart.Missing;
        if (numericSegments.Length == 3 &&
            !TryParseNumericPart(numericSegments[2], true, out patchPart))
        {
            return false;
        }

        RemainderBoundary remainderBoundary = RemainderBoundary.None;
        if (hasRemainderBoundary)
        {
            remainderBoundary = buildPart.Kind != PartKind.Missing
                ? RemainderBoundary.Build
                : preReleasePart.Kind != PartKind.Missing
                    ? RemainderBoundary.PreRelease
                    : patchPart.Kind != PartKind.Missing
                        ? RemainderBoundary.Patch
                        : RemainderBoundary.Minor;
        }

        result = new FhirSemVer(
            majorPart,
            minorPart,
            patchPart,
            preReleasePart,
            buildPart,
            remainderBoundary,
            false);
        return true;
    }

    private static bool TryParseNumericPart(
        string segment,
        bool allowXAlias,
        out NumericPart part)
    {
        part = NumericPart.Missing;

        if (segment == "*" ||
            (allowXAlias &&
             (segment.Equals("x", StringComparison.OrdinalIgnoreCase))))
        {
            part = NumericPart.Wildcard;
            return true;
        }

        if (!TryParseSegment(segment.AsSpan(), out int value))
            return false;

        part = NumericPart.Literal(value);
        return true;
    }

    private static bool TryParseLabelPart(string value, out LabelPart part)
    {
        part = LabelPart.Missing;

        if (value == "*")
        {
            part = LabelPart.Wildcard;
            return true;
        }

        if (!IsValidIdentifier(value.AsSpan()))
            return false;

        part = LabelPart.Literal(value);
        return true;
    }

    /// <summary>
    /// Parses a non-negative numeric part and rejects leading zeroes.
    /// </summary>
    private static bool TryParseSegment(ReadOnlySpan<char> segment, out int value)
    {
        value = 0;
        if (segment.Length == 0)
            return false;

        if (segment.Length > 1 && segment[0] == '0')
            return false;

        return int.TryParse(
            segment,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static bool IsValidIdentifier(ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
            return false;

        foreach (char ch in value)
        {
            if (!char.IsLetterOrDigit(ch) && ch is not '.' and not '-')
                return false;
        }

        return true;
    }

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

    /// <summary>
    /// Compares concrete versions using numeric precision, numeric values, and
    /// FHIR-specific pre-release ordering. Patterns are not orderable.
    /// </summary>
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

        int result = CompareNumericParts(_majorPart, other._majorPart);
        if (result != 0)
            return result;

        result = CompareNumericParts(_minorPart, other._minorPart);
        if (result != 0)
            return result;

        result = CompareNumericParts(_patchPart, other._patchPart);
        if (result != 0)
            return result;

        if (!IsPreRelease && other.IsPreRelease)
            return 1;
        if (IsPreRelease && !other.IsPreRelease)
            return -1;
        if (!IsPreRelease)
            return 0;

        result = other.PreReleaseType.CompareTo(PreReleaseType);
        if (result != 0)
            return result;

        return ComparePreReleaseSuffix(PreRelease!, other.PreRelease!);
    }

    private static int CompareNumericParts(NumericPart left, NumericPart right)
    {
        if (left.Kind == PartKind.Wildcard || right.Kind == PartKind.Wildcard)
            throw new InvalidOperationException("Wildcard parts cannot be ordered.");

        if (left.Kind == right.Kind)
        {
            return left.Kind == PartKind.Literal
                ? left.Value.CompareTo(right.Value)
                : 0;
        }

        return left.Kind == PartKind.Missing ? -1 : 1;
    }

    private static int ComparePreReleaseSuffix(string a, string b)
    {
        int suffixA = ExtractNumericSuffix(a);
        int suffixB = ExtractNumericSuffix(b);

        if (suffixA != suffixB)
            return suffixA.CompareTo(suffixB);

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static int ExtractNumericSuffix(string preRelease)
    {
        int index = preRelease.Length - 1;
        while (index >= 0 && char.IsAsciiDigit(preRelease[index]))
            index--;

        if (index == preRelease.Length - 1)
            return 0;

        return int.TryParse(
            preRelease.AsSpan(index + 1),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int suffix)
            ? suffix
            : 0;
    }

    /// <summary>
    /// Determines whether two versions or patterns are equal. Concrete build
    /// metadata is ignored; pattern build state participates in equality.
    /// </summary>
    public bool Equals(FhirSemVer? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;

        if (IsWildcard || other.IsWildcard)
        {
            return IsWildcard == other.IsWildcard &&
                _isAllWildcard == other._isAllWildcard &&
                NumericPartsEqual(_majorPart, other._majorPart) &&
                NumericPartsEqual(_minorPart, other._minorPart) &&
                NumericPartsEqual(_patchPart, other._patchPart) &&
                LabelPartsEqual(_preReleasePart, other._preReleasePart) &&
                LabelPartsEqual(_buildPart, other._buildPart) &&
                _remainderBoundary == other._remainderBoundary;
        }

        return NumericPartsEqual(_majorPart, other._majorPart) &&
            NumericPartsEqual(_minorPart, other._minorPart) &&
            NumericPartsEqual(_patchPart, other._patchPart) &&
            LabelPartsEqual(_preReleasePart, other._preReleasePart);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as FhirSemVer);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        AddNumericPartHash(ref hash, _majorPart);
        AddNumericPartHash(ref hash, _minorPart);
        AddNumericPartHash(ref hash, _patchPart);
        AddLabelPartHash(ref hash, _preReleasePart);

        if (IsWildcard)
        {
            AddLabelPartHash(ref hash, _buildPart);
            hash.Add(_remainderBoundary);
            hash.Add(_isAllWildcard);
        }

        return hash.ToHashCode();
    }

    private static bool NumericPartsEqual(NumericPart left, NumericPart right) =>
        left.Kind == right.Kind &&
        (left.Kind != PartKind.Literal || left.Value == right.Value);

    private static bool LabelPartsEqual(LabelPart left, LabelPart right) =>
        left.Kind == right.Kind &&
        (left.Kind != PartKind.Literal ||
         string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase));

    private static void AddNumericPartHash(ref HashCode hash, NumericPart part)
    {
        hash.Add(part.Kind);
        if (part.Kind == PartKind.Literal)
            hash.Add(part.Value);
    }

    private static void AddLabelPartHash(ref HashCode hash, LabelPart part)
    {
        hash.Add(part.Kind);
        if (part.Kind == PartKind.Literal)
            hash.Add(part.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Determines whether two versions or patterns are equal.</summary>
    public static bool operator ==(FhirSemVer? left, FhirSemVer? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Determines whether two versions or patterns are not equal.</summary>
    public static bool operator !=(FhirSemVer? left, FhirSemVer? right) =>
        !(left == right);

    /// <summary>Determines whether the left concrete version precedes the right.</summary>
    public static bool operator <(FhirSemVer? left, FhirSemVer? right)
    {
        if (left is null)
            return right is not null;

        return left.CompareTo(right) < 0;
    }

    /// <summary>Determines whether the left concrete version follows the right.</summary>
    public static bool operator >(FhirSemVer? left, FhirSemVer? right) =>
        right < left;

    /// <summary>
    /// Determines whether the left concrete version precedes or equals the right.
    /// </summary>
    public static bool operator <=(FhirSemVer? left, FhirSemVer? right)
    {
        if (left is null)
            return true;

        return left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Determines whether the left concrete version follows or equals the right.
    /// </summary>
    public static bool operator >=(FhirSemVer? left, FhirSemVer? right) =>
        right <= left;

    /// <summary>
    /// Determines whether this concrete version satisfies one standalone exact
    /// or wildcard expression.
    /// </summary>
    public bool Satisfies(string versionSpecifier)
    {
        FhirSemVer specifier = Parse(versionSpecifier);
        return Satisfies(specifier);
    }

    /// <summary>
    /// Determines whether this concrete version satisfies the supplied exact
    /// version or wildcard pattern.
    /// </summary>
    public bool Satisfies(FhirSemVer other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (IsWildcard)
            return false;
        if (other._isAllWildcard)
            return true;

        if (!NumericPartMatches(other._majorPart, _majorPart))
            return false;
        if (other._remainderBoundary == RemainderBoundary.Major)
            return true;

        if (!NumericPartMatches(other._minorPart, _minorPart))
            return false;
        if (other._remainderBoundary == RemainderBoundary.Minor)
            return true;

        if (!NumericPartMatches(other._patchPart, _patchPart))
            return false;
        if (other._remainderBoundary == RemainderBoundary.Patch)
            return true;

        if (!LabelPartMatches(other._preReleasePart, _preReleasePart))
            return false;
        if (other._remainderBoundary == RemainderBoundary.PreRelease)
            return true;

        if (!LabelPartMatches(other._buildPart, _buildPart))
            return false;

        return other._remainderBoundary is
            RemainderBoundary.None or RemainderBoundary.Build;
    }

    private static bool NumericPartMatches(NumericPart pattern, NumericPart candidate) =>
        pattern.Kind switch
        {
            PartKind.Missing => candidate.Kind == PartKind.Missing,
            PartKind.Wildcard => candidate.Kind != PartKind.Missing,
            PartKind.Literal =>
                candidate.Kind == PartKind.Literal &&
                pattern.Value == candidate.Value,
            _ => false,
        };

    private static bool LabelPartMatches(LabelPart pattern, LabelPart candidate) =>
        pattern.Kind switch
        {
            PartKind.Missing => candidate.Kind == PartKind.Missing,
            PartKind.Wildcard => candidate.Kind != PartKind.Missing,
            PartKind.Literal =>
                candidate.Kind == PartKind.Literal &&
                string.Equals(
                    pattern.Value,
                    candidate.Value,
                    StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    /// <summary>
    /// Finds the highest concrete version that satisfies one standalone exact
    /// or wildcard expression.
    /// </summary>
    public static FhirSemVer? MaxSatisfying(
        IEnumerable<FhirSemVer> versions,
        string specifier,
        bool includePreRelease = false)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentException.ThrowIfNullOrEmpty(specifier);

        FhirSemVer spec = Parse(specifier);
        bool allowPreRelease =
            includePreRelease || spec._preReleasePart.Kind != PartKind.Missing;

        return versions
            .Where(version => !version.IsWildcard)
            .Where(version => version.Satisfies(spec))
            .Where(version => allowPreRelease || !version.IsPreRelease)
            .Max();
    }

    /// <summary>
    /// Returns all concrete versions from a collection that satisfy a range
    /// expression.
    /// </summary>
    public static IEnumerable<FhirSemVer> SatisfyingRange(
        IEnumerable<FhirSemVer> versions,
        string rangeExpression)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentException.ThrowIfNullOrEmpty(rangeExpression);

        FhirSemVerRange range = FhirSemVerRange.Parse(rangeExpression);
        IReadOnlyList<FhirSemVer> versionList =
            versions as IReadOnlyList<FhirSemVer> ?? versions.ToList();
        return versionList.Where(range.IsSatisfiedBy);
    }

    /// <summary>Returns the normalized version or pattern string.</summary>
    public override string ToString()
    {
        if (_isAllWildcard)
            return "*";

        StringBuilder builder = new();
        AppendNumericPart(builder, _majorPart, true);
        builder.Append('.');
        AppendNumericPart(builder, _minorPart, false);

        if (_patchPart.Kind != PartKind.Missing)
        {
            builder.Append('.');
            AppendNumericPart(builder, _patchPart, false);
        }

        if (_preReleasePart.Kind != PartKind.Missing)
        {
            builder.Append('-');
            AppendLabelPart(builder, _preReleasePart);
        }

        if (_buildPart.Kind != PartKind.Missing)
        {
            builder.Append('+');
            AppendLabelPart(builder, _buildPart);
        }

        if (_remainderBoundary != RemainderBoundary.None)
            builder.Append('?');

        return builder.ToString();
    }

    private static void AppendNumericPart(
        StringBuilder builder,
        NumericPart part,
        bool isMajor)
    {
        if (part.Kind == PartKind.Wildcard)
        {
            builder.Append(isMajor ? '*' : 'x');
            return;
        }

        builder.Append(part.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendLabelPart(StringBuilder builder, LabelPart part)
    {
        builder.Append(part.Kind == PartKind.Wildcard ? "*" : part.Value);
    }
}
