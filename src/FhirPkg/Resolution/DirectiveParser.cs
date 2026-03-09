// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.RegularExpressions;

using FhirPkg.Models;

namespace FhirPkg.Resolution;

/// <summary>
/// Static utility for parsing and classifying FHIR package directive strings.
/// Supplements <see cref="PackageDirective.Parse"/> with lower-level classification
/// and extraction helpers used by resolution components.
/// </summary>
public static class DirectiveParser
{
    // FHIR version suffix pattern: ".r2", ".r3", ".r4", ".r4b", ".r5", ".r6"
    private static readonly Regex s_fhirVersionSuffix = new(
        @"\.r(\d+b?)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // CI branch pattern: "current$branchname"
    private static readonly Regex s_ciBranchPattern = new(
        @"^current\$(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // HL7-scope prefix: hl7.fhir.*, hl7.cda.*, hl7.v2.*, ihe.*, who.*
    private static readonly Regex s_hl7ScopePattern = new(
        @"^(hl7\.fhir\.|hl7\.cda\.|hl7\.v2\.|hl7\.other\.|ihe\.|who\.)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses a raw directive string into a fully classified <see cref="PackageDirective"/>.
    /// This is a convenience wrapper around <see cref="PackageDirective.Parse"/>.
    /// </summary>
    /// <param name="directive">
    /// A package directive such as "hl7.fhir.r4.core#4.0.1", "hl7.fhir.us.core@6.1.0",
    /// or "v610@npm:hl7.fhir.us.core@6.1.0".
    /// </param>
    /// <returns>A parsed and classified <see cref="PackageDirective"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="directive"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="directive"/> is empty or whitespace.</exception>
    public static PackageDirective Parse(string directive) => PackageDirective.Parse(directive);

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
        if (s_hl7ScopePattern.IsMatch(lower))
        {
            return s_fhirVersionSuffix.IsMatch(lower)
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

    /// <summary>
    /// Splits a raw directive string into its component parts: package ID, version, and optional alias.
    /// Handles FHIR (<c>#</c>), NPM (<c>@</c>), and alias (<c>alias@npm:name@version</c>) syntaxes.
    /// </summary>
    /// <param name="directive">The raw directive string to parse.</param>
    /// <returns>
    /// A tuple of (PackageId, Version, Alias) where Version and Alias may be <c>null</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="directive"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="directive"/> is empty or whitespace.</exception>
    public static (string PackageId, string? Version, string? Alias) SplitDirective(string directive)
    {
        ArgumentNullException.ThrowIfNull(directive);

        var trimmed = directive.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Directive must not be empty.", nameof(directive));

        string? alias = null;
        var input = trimmed;

        // Handle NPM alias syntax: "alias@npm:actual@version"
        var npmPrefixIndex = input.IndexOf("@npm:", StringComparison.OrdinalIgnoreCase);
        if (npmPrefixIndex > 0)
        {
            alias = input[..npmPrefixIndex];
            input = input[(npmPrefixIndex + 5)..]; // skip "@npm:"
        }

        // Try FHIR separator '#' first
        var hashIndex = input.IndexOf('#');
        if (hashIndex >= 0)
        {
            var name = input[..hashIndex];
            var ver = input[(hashIndex + 1)..];
            return (name, NullIfEmpty(ver), alias);
        }

        // Try NPM separator '@' (last occurrence to avoid scope confusion)
        var atIndex = input.LastIndexOf('@');
        if (atIndex > 0)
        {
            var name = input[..atIndex];
            var ver = input[(atIndex + 1)..];
            return (name, NullIfEmpty(ver), alias);
        }

        // No separator — just a package name
        return (input, null, alias);
    }

    /// <summary>
    /// Extracts the CI branch name from a "current$branch" version specifier.
    /// </summary>
    /// <param name="version">A version string that may be in the "current$branch" format.</param>
    /// <returns>
    /// The branch name (e.g. "R5" from "current$R5"), or <c>null</c> if the input
    /// is not a CI branch specifier.
    /// </returns>
    public static string? ExtractCiBranch(string? version)
    {
        if (version is null)
            return null;

        var match = s_ciBranchPattern.Match(version);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Determines whether the given segment is a known FHIR core package type suffix
    /// (e.g. "core", "expansions", "examples", "search", "corexml", "elements").
    /// </summary>
    /// <param name="segment">The name segment to test.</param>
    /// <returns><c>true</c> if the segment is a known core type; otherwise, <c>false</c>.</returns>
    public static bool IsKnownCoreType(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return FhirReleaseMapping.KnownCoreTypes.Contains(segment.ToLowerInvariant());
    }

    /// <summary>
    /// Expands a partial core package name (e.g. "hl7.fhir.r4") into the full list
    /// of known core package names for that FHIR release.
    /// </summary>
    /// <param name="partialName">
    /// A three-segment core package prefix such as "hl7.fhir.r4" or "hl7.fhir.r5".
    /// </param>
    /// <returns>
    /// A read-only list of fully-qualified core package names
    /// (e.g. ["hl7.fhir.r4.core", "hl7.fhir.r4.expansions", …]),
    /// or an empty list if the partial name does not correspond to a known FHIR release.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="partialName"/> is <c>null</c>.</exception>
    public static IReadOnlyList<string> ExpandPartialCoreName(string partialName)
    {
        ArgumentNullException.ThrowIfNull(partialName);

        // Append ".core" temporarily to leverage the existing mapping logic
        var release = FhirReleaseMapping.FromPackageName(partialName + ".core");
        if (release is null)
            return [];

        return FhirReleaseMapping.GetCorePackageNames(release.Value);
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
