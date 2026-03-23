// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Frozen;

namespace FhirPkg.Models;

/// <summary>
/// Provides bidirectional mapping between <see cref="FhirRelease"/> enum values,
/// FHIR version strings, and core package name prefixes.
/// </summary>
public static class FhirReleaseMapping
{
    /// <summary>
    /// The known core package type suffixes (e.g. "core", "expansions").
    /// </summary>
    public static readonly string[] KnownCoreTypes = ["core", "expansions", "examples", "search", "corexml", "elements"];

    private static readonly FrozenDictionary<FhirRelease, string> ReleaseToVersion =
        new Dictionary<FhirRelease, string>
        {
            [FhirRelease.DSTU2] = "1.0.2",
            [FhirRelease.STU3] = "3.0.2",
            [FhirRelease.R4] = "4.0.1",
            [FhirRelease.R4B] = "4.3.0",
            [FhirRelease.R5] = "5.0.0",
            [FhirRelease.R6] = "6.0.0",
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<FhirRelease, string> ReleaseToPrefix =
        new Dictionary<FhirRelease, string>
        {
            [FhirRelease.DSTU2] = "hl7.fhir.r2",
            [FhirRelease.STU3] = "hl7.fhir.r3",
            [FhirRelease.R4] = "hl7.fhir.r4",
            [FhirRelease.R4B] = "hl7.fhir.r4b",
            [FhirRelease.R5] = "hl7.fhir.r5",
            [FhirRelease.R6] = "hl7.fhir.r6",
        }.ToFrozenDictionary();

    // Map from major.minor prefix to release (covers "4.0" → R4, "4.3" → R4B, etc.)
    private static readonly FrozenDictionary<string, FhirRelease> MajorMinorToRelease =
        new Dictionary<string, FhirRelease>
        {
            ["1.0"] = FhirRelease.DSTU2,
            ["3.0"] = FhirRelease.STU3,
            ["4.0"] = FhirRelease.R4,
            ["4.3"] = FhirRelease.R4B,
            ["5.0"] = FhirRelease.R5,
            ["6.0"] = FhirRelease.R6,
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, FhirRelease> PrefixToRelease =
        ReleaseToPrefix.ToFrozenDictionary(kvp => kvp.Value, kvp => kvp.Key);

    /// <summary>
    /// Maps a FHIR version string (e.g. "4.0.1" or "4.0.x") to the corresponding <see cref="FhirRelease"/>.
    /// Matching is based on the major.minor portion of the version string.
    /// </summary>
    /// <param name="version">A FHIR version string such as "4.0.1" or "4.0.x".</param>
    /// <returns>The corresponding <see cref="FhirRelease"/>, or <c>null</c> if no match is found.</returns>
    public static FhirRelease? FromVersionString(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        // Extract major.minor from the version string
        int dotIndex = version.IndexOf('.');
        if (dotIndex < 0) return null;

        int secondDotIndex = version.IndexOf('.', dotIndex + 1);
        string majorMinor = secondDotIndex >= 0
            ? version[..secondDotIndex]
            : version;

        return MajorMinorToRelease.GetValueOrDefault(majorMinor);
    }

    /// <summary>
    /// Maps a <see cref="FhirRelease"/> to its canonical version string (e.g. R4 → "4.0.1").
    /// </summary>
    /// <param name="release">The FHIR release to look up.</param>
    /// <returns>The canonical version string, or <c>null</c> if the release is not recognized.</returns>
    public static string? ToVersionString(FhirRelease release) =>
        ReleaseToVersion.GetValueOrDefault(release);

    /// <summary>
    /// Maps a <see cref="FhirRelease"/> to its core package name prefix (e.g. R4 → "hl7.fhir.r4").
    /// </summary>
    /// <param name="release">The FHIR release to look up.</param>
    /// <returns>The package name prefix.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the release value is not recognized.</exception>
    public static string ToPackagePrefix(FhirRelease release) =>
        ReleaseToPrefix.TryGetValue(release, out string? prefix)
            ? prefix
            : throw new ArgumentOutOfRangeException(nameof(release), release, "Unknown FHIR release.");

    /// <summary>
    /// Extracts the <see cref="FhirRelease"/> from a core FHIR package name such as "hl7.fhir.r4.core".
    /// </summary>
    /// <param name="packageName">A package name (e.g. "hl7.fhir.r4.core" or "hl7.fhir.r4b.expansions").</param>
    /// <returns>The corresponding <see cref="FhirRelease"/>, or <c>null</c> if no match is found.</returns>
    public static FhirRelease? FromPackageName(string packageName)
    {
        ArgumentNullException.ThrowIfNull(packageName);

        if (!packageName.StartsWith("hl7.fhir.r", StringComparison.OrdinalIgnoreCase))
            return null;

        // Extract the prefix (first 3 segments) using span-based indexing to avoid allocations
        ReadOnlySpan<char> span = packageName.AsSpan();
        int firstDot = span.IndexOf('.');
        if (firstDot < 0) return null;

        int secondDot = span[(firstDot + 1)..].IndexOf('.');
        if (secondDot < 0) return null;
        secondDot += firstDot + 1;

        int thirdDot = span[(secondDot + 1)..].IndexOf('.');
        ReadOnlySpan<char> prefixSpan;
        if (thirdDot >= 0)
        {
            thirdDot += secondDot + 1;
            prefixSpan = span[..thirdDot];
        }
        else
        {
            prefixSpan = span;
        }

        // Build the lowercase prefix for lookup — use stackalloc for small strings
        Span<char> lowerBuf = stackalloc char[prefixSpan.Length];
        prefixSpan.ToLowerInvariant(lowerBuf);
        string prefix = new string(lowerBuf);

        return PrefixToRelease.GetValueOrDefault(prefix);
    }

    /// <summary>
    /// Returns the list of well-known core package names for a given FHIR release
    /// (e.g. R4 → ["hl7.fhir.r4.core", "hl7.fhir.r4.expansions", …]).
    /// </summary>
    /// <param name="release">The FHIR release.</param>
    /// <returns>A read-only list of core package names.</returns>
    public static IReadOnlyList<string> GetCorePackageNames(FhirRelease release)
    {
        string prefix = ToPackagePrefix(release);
        return KnownCoreTypes.Select(type => $"{prefix}.{type}").ToList().AsReadOnly();
    }
}
