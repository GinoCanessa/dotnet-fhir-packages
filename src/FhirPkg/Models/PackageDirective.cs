// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.RegularExpressions;

namespace FhirPkg.Models;

/// <summary>
/// A parsed and classified FHIR package directive. Captures the package name, version constraint,
/// their classifications, and any derived information such as expanded core package identifiers
/// or CI build branch names.
/// </summary>
public partial record PackageDirective
{
    /// <summary>The original, unparsed directive string.</summary>
    public required string RawDirective { get; init; }

    /// <summary>The extracted package identifier (e.g. "hl7.fhir.r4.core").</summary>
    public required string PackageId { get; init; }

    /// <summary>The version string from the directive, or <c>null</c> if none was specified.</summary>
    public string? RequestedVersion { get; init; }

    /// <summary>The NPM alias name, if the directive used the <c>alias@npm:name@version</c> syntax.</summary>
    public string? Alias { get; init; }

    /// <summary>The classification of the package name.</summary>
    public required PackageNameType NameType { get; init; }

    /// <summary>The classification of the version constraint.</summary>
    public required VersionType VersionType { get; init; }

    /// <summary>The resolved semantic version, if available after resolution.</summary>
    public FhirSemVer? ResolvedVersion { get; init; }

    /// <summary>
    /// For <see cref="PackageNameType.CorePartial"/> names, the list of expanded fully-qualified
    /// core package identifiers (e.g. ["hl7.fhir.r4.core", "hl7.fhir.r4.expansions", …]).
    /// </summary>
    public IReadOnlyList<string>? ExpandedPackageIds { get; init; }

    /// <summary>
    /// For <see cref="Models.VersionType.CiBuildBranch"/>, the branch name extracted from
    /// "current$branch".
    /// </summary>
    public string? CiBranch { get; init; }

    /// <summary>
    /// Converts this directive to a <see cref="PackageReference"/>.
    /// </summary>
    /// <returns>A <see cref="PackageReference"/> with the package identifier and requested version.</returns>
    public PackageReference ToReference() => new(PackageId, RequestedVersion);

    // ── Parsing ─────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a FHIR package directive string into a fully classified <see cref="PackageDirective"/>.
    /// </summary>
    /// <param name="directive">
    /// A package directive such as "hl7.fhir.r4.core#4.0.1", "hl7.fhir.us.core@6.1.0",
    /// "hl7.fhir.r4", or "v610@npm:hl7.fhir.us.core@6.1.0".
    /// </param>
    /// <returns>A parsed <see cref="PackageDirective"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="directive"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="directive"/> is empty or whitespace.</exception>
    public static PackageDirective Parse(string directive)
    {
        ArgumentNullException.ThrowIfNull(directive);

        var trimmed = directive.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Package directive must not be empty.", nameof(directive));

        var raw = trimmed;
        string? alias = null;
        ReadOnlySpan<char> input = trimmed.AsSpan();

        // Step 1: Strip NPM alias syntax "alias@npm:actual@version"
        var npmPrefixIndex = input.IndexOf("@npm:".AsSpan(), StringComparison.OrdinalIgnoreCase);
        if (npmPrefixIndex > 0)
        {
            alias = new string(input[..npmPrefixIndex]);
            input = input[(npmPrefixIndex + 5)..]; // skip "@npm:"
        }

        // Step 2: Split on '#' or '@' to get name + version
        string name;
        string? version;

        var hashIndex = input.IndexOf('#');
        if (hashIndex >= 0)
        {
            name = new string(input[..hashIndex]);
            var versionSpan = input[(hashIndex + 1)..];
            version = versionSpan.IsEmpty || versionSpan.IsWhiteSpace() ? null : new string(versionSpan);
        }
        else
        {
            // For '@', find the last '@' to avoid confusing with scope
            var atIndex = input.LastIndexOf('@');
            if (atIndex > 0)
            {
                name = new string(input[..atIndex]);
                var versionSpan = input[(atIndex + 1)..];
                version = versionSpan.IsEmpty || versionSpan.IsWhiteSpace() ? null : new string(versionSpan);
            }
            else
            {
                name = new string(input);
                version = null;
            }
        }

        // Step 3: Classify name type
        var nameType = ClassifyName(name);

        // Step 4: Classify version type
        var versionType = ClassifyVersion(version);

        // Step 5: Extract CI branch if CiBuildBranch
        string? ciBranch = null;
        if (versionType == VersionType.CiBuildBranch && version is not null)
        {
            ciBranch = version["current$".Length..];
        }

        // Step 6: For CorePartial, populate ExpandedPackageIds
        IReadOnlyList<string>? expandedIds = null;
        if (nameType == PackageNameType.CorePartial)
        {
            var release = FhirReleaseMapping.FromPackageName(name + ".core");
            if (release is not null)
            {
                expandedIds = FhirReleaseMapping.GetCorePackageNames(release.Value);
            }
        }

        return new PackageDirective
        {
            RawDirective = raw,
            PackageId = name,
            RequestedVersion = version,
            Alias = alias,
            NameType = nameType,
            VersionType = versionType,
            CiBranch = ciBranch,
            ExpandedPackageIds = expandedIds,
        };
    }

    // ── Classification ──────────────────────────────────────────────────

    [GeneratedRegex(@"^(hl7\.fhir\.|hl7\.cda\.|hl7\.v2\.|hl7\.other\.|ihe\.|who\.)", RegexOptions.IgnoreCase)]
    private static partial Regex Hl7ScopePattern();

    [GeneratedRegex(@"\.r(\d+b?)$", RegexOptions.IgnoreCase)]
    private static partial Regex FhirVersionSuffix();

    /// <summary>
    /// Classifies a package identifier into a <see cref="PackageNameType"/>.
    /// </summary>
    /// <param name="packageId">The package identifier (e.g. "hl7.fhir.r4.core", "hl7.fhir.us.core").</param>
    /// <returns>The classification of the package name.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="packageId"/> is <c>null</c>.</exception>
    public static PackageNameType ClassifyName(string packageId)
    {
        ArgumentNullException.ThrowIfNull(packageId);

        var lower = packageId.ToLowerInvariant();

        // Check for hl7.fhir.r* prefix (core packages)
        if (lower.StartsWith("hl7.fhir.r", StringComparison.Ordinal))
        {
            var segments = lower.Split('.');
            if (segments.Length >= 4)
            {
                var typeSuffix = segments[3];
                if (FhirReleaseMapping.KnownCoreTypes.Contains(typeSuffix))
                    return PackageNameType.CoreFull;
            }
            else if (segments.Length == 3)
            {
                return PackageNameType.CorePartial;
            }
        }

        // Check for HL7-scope pattern
        if (Hl7ScopePattern().IsMatch(lower))
        {
            return FhirVersionSuffix().IsMatch(lower)
                ? PackageNameType.GuideWithFhirSuffix
                : PackageNameType.GuideWithoutSuffix;
        }

        return PackageNameType.NonHl7Guide;
    }

    /// <summary>
    /// Classifies a version string into a <see cref="VersionType"/>.
    /// </summary>
    /// <param name="version">The version string (e.g. "4.0.1", "latest", "current", "4.0.x", "^4.0.0").</param>
    /// <returns>The classification of the version specifier.</returns>
    public static VersionType ClassifyVersion(string? version)
    {
        if (string.IsNullOrEmpty(version) ||
            version.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return VersionType.Latest;

        if (version.Equals("current", StringComparison.OrdinalIgnoreCase))
            return VersionType.CiBuild;

        if (version.StartsWith("current$", StringComparison.OrdinalIgnoreCase))
            return VersionType.CiBuildBranch;

        if (version.Equals("dev", StringComparison.OrdinalIgnoreCase))
            return VersionType.LocalBuild;

        // Wildcard: contains 'x', 'X', or '*' as a version segment
        var segments = version.Split('.');
        foreach (var seg in segments)
        {
            if (seg is "x" or "X" or "*")
                return VersionType.Wildcard;
        }

        // Range operators: ^, ~, or pipe
        if (version.StartsWith('^') || version.StartsWith('~') || version.Contains('|'))
            return VersionType.Range;

        return VersionType.Exact;
    }
}
