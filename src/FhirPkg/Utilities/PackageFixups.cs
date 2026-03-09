// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;

namespace FhirPkg.Utilities;

/// <summary>
/// Applies known package version fixups and name mappings to correct
/// well-known errata in the FHIR package ecosystem.
/// </summary>
/// <remarks>
/// <para>
/// The FHIR package ecosystem contains several known issues where published
/// package versions have errors or inconsistencies. This class centralizes
/// the workarounds used by various FHIR tooling implementations.
/// </para>
/// <para>
/// Known fixups include:
/// <list type="bullet">
///   <item><description>hl7.fhir.r4.core@4.0.0 → 4.0.1 (errata in the 4.0.0 publication)</description></item>
///   <item><description>hl7.fhir.r4b.core@4.3.0-snapshot1 → 4.3.0 (pre-release alias)</description></item>
///   <item><description>hl7.fhir.uv.extensions → version-specific variants (R4/R5 mapping)</description></item>
///   <item><description>Stripping "-cibuild" suffix from pre-release versions</description></item>
/// </list>
/// </para>
/// </remarks>
public static class PackageFixups
{
    /// <summary>
    /// Map of known (name, version) pairs to their corrected versions.
    /// Keys are lowercase package names; values map from bad versions to good versions.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> s_versionFixups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hl7.fhir.r4.core"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["4.0.0"] = "4.0.1"
        },
        ["hl7.fhir.r4b.core"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["4.3.0-snapshot1"] = "4.3.0"
        }
    };

    /// <summary>
    /// Map of generic extension package names to FHIR-version-specific names.
    /// The key is the generic name; the value maps FHIR major version prefixes
    /// to the corresponding version-specific package name.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> s_nameFixups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hl7.fhir.uv.extensions"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["r4"] = "hl7.fhir.uv.extensions.r4",
            ["r4b"] = "hl7.fhir.uv.extensions.r4b",
            ["r5"] = "hl7.fhir.uv.extensions.r5"
        }
    };

    private const string CiBuildSuffix = "-cibuild";

    /// <summary>
    /// Applies all known fixups to a <see cref="PackageReference"/>.
    /// Returns the corrected reference, or the original if no fixups apply.
    /// </summary>
    /// <param name="reference">The package reference to check for known fixups.</param>
    /// <returns>
    /// A corrected <see cref="PackageReference"/> if any fixups were applied;
    /// otherwise, the original <paramref name="reference"/> unchanged.
    /// </returns>
    /// <remarks>
    /// Fixups are applied in the following order:
    /// <list type="number">
    ///   <item><description>Strip "-cibuild" suffix from the version string.</description></item>
    ///   <item><description>Apply known version corrections (e.g., 4.0.0 → 4.0.1).</description></item>
    ///   <item><description>Apply package name remapping for generic extension packages.</description></item>
    /// </list>
    /// </remarks>
    public static PackageReference Apply(PackageReference reference)
    {
        var name = reference.Name;
        var version = reference.Version;

        // Step 1: Strip "-cibuild" suffix from pre-release versions
        if (version is not null && version.EndsWith(CiBuildSuffix, StringComparison.OrdinalIgnoreCase))
        {
            version = version[..^CiBuildSuffix.Length];
        }

        // Step 2: Apply known version fixups
        if (version is not null
            && s_versionFixups.TryGetValue(name, out var versionMap)
            && versionMap.TryGetValue(version, out var correctedVersion))
        {
            version = correctedVersion;
        }

        // Step 3: Apply package name remapping (e.g., generic extensions → version-specific)
        if (s_nameFixups.TryGetValue(name, out var nameMap))
        {
            name = ApplyNameMapping(name, version, nameMap);
        }

        // Return a new reference only if something changed
        if (string.Equals(name, reference.Name, StringComparison.Ordinal)
            && string.Equals(version, reference.Version, StringComparison.Ordinal))
        {
            return reference;
        }

        return reference with { Name = name, Version = version };
    }

    /// <summary>
    /// Determines the correct version-specific package name by inspecting the version string
    /// to infer the FHIR release.
    /// </summary>
    private static string ApplyNameMapping(
        string originalName,
        string? version,
        Dictionary<string, string> nameMap)
    {
        if (version is null)
            return originalName;

        // Infer FHIR release from the major.minor version prefix
        var fhirRelease = InferFhirReleaseFromVersion(version);
        if (fhirRelease is not null && nameMap.TryGetValue(fhirRelease, out var mappedName))
            return mappedName;

        return originalName;
    }

    /// <summary>
    /// Attempts to infer the FHIR release identifier (r4, r4b, r5) from a version string.
    /// Uses the major version number as the primary discriminator.
    /// </summary>
    private static string? InferFhirReleaseFromVersion(string version)
    {
        // Try to extract the major version
        var dotIndex = version.IndexOf('.');
        var majorStr = dotIndex > 0 ? version[..dotIndex] : version;

        if (!int.TryParse(majorStr, out var major))
            return null;

        // Check for R4B (4.3.x range)
        if (major == 4)
        {
            var rest = dotIndex > 0 ? version[(dotIndex + 1)..] : string.Empty;
            var secondDot = rest.IndexOf('.');
            var minorStr = secondDot > 0 ? rest[..secondDot] : rest;

            // Remove any pre-release suffix for minor parsing
            var dashIndex = minorStr.IndexOf('-');
            if (dashIndex > 0)
                minorStr = minorStr[..dashIndex];

            if (int.TryParse(minorStr, out var minor) && minor >= 3)
                return "r4b";

            return "r4";
        }

        return major switch
        {
            1 or 2 or 3 => "r4", // legacy — best-effort mapping
            5 => "r5",
            _ => null
        };
    }
}
