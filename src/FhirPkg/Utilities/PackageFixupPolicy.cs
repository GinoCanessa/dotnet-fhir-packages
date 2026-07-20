// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Installation;
using FhirPkg.Models;

namespace FhirPkg.Utilities;

/// <summary>
/// Immutable package-version correction policy captured when a package manager is created.
/// </summary>
internal sealed class PackageFixupPolicy
{
    private const string CiBuildSuffix = "-cibuild";

    private readonly IReadOnlyDictionary<string, string> _versionFixups;

    internal static PackageFixupPolicy Default { get; } = Create(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hl7.fhir.r4.core@4.0.0"] = "4.0.1",
            ["hl7.fhir.r4b.core@4.3.0-snapshot1"] = "4.3.0",
        });

    private PackageFixupPolicy(IReadOnlyDictionary<string, string> versionFixups)
    {
        _versionFixups = versionFixups;
    }

    internal static PackageFixupPolicy Create(IReadOnlyDictionary<string, string> configuredFixups)
    {
        ArgumentNullException.ThrowIfNull(configuredFixups);

        Dictionary<string, string> parsed = new(StringComparer.Ordinal);
        foreach ((string directive, string targetVersion) in configuredFixups)
        {
            int separatorIndex = directive.LastIndexOf('@');
            if (separatorIndex <= 0 || separatorIndex == directive.Length - 1)
            {
                throw InvalidPolicy(
                    $"Version fixup key '{directive}' must use the form '<package>@<version>'.");
            }

            string packageName = directive[..separatorIndex].Trim();
            string sourceVersion = CanonicalizeVersion(
                directive[(separatorIndex + 1)..].Trim());
            string target = CanonicalizeVersion(
                targetVersion?.Trim() ?? string.Empty);

            if (packageName.Length == 0
                || !IsConcreteVersion(sourceVersion)
                || !IsConcreteVersion(target))
            {
                throw InvalidPolicy(
                    $"Version fixup '{directive}' must contain a package name and concrete source and target versions.");
            }

            string key = CreateKey(packageName, sourceVersion);
            if (!parsed.TryAdd(key, target))
            {
                throw InvalidPolicy(
                    $"Version fixup '{directive}' duplicates an existing package and source version.");
            }
        }

        return new PackageFixupPolicy(parsed);
    }

    internal string ApplyVersion(string packageName, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        string canonicalVersion = CanonicalizeVersion(version);
        return _versionFixups.TryGetValue(
            CreateKey(packageName, canonicalVersion),
            out string? corrected)
            ? CanonicalizeVersion(corrected)
            : canonicalVersion;
    }

    private static bool IsConcreteVersion(string version) =>
        FhirSemVer.TryParse(version, out FhirSemVer? parsed)
        && !parsed.IsWildcard;

    private static string CreateKey(string packageName, string version) =>
        $"{packageName.ToLowerInvariant()}\0{version}";

    private static string CanonicalizeVersion(string version) =>
        version.EndsWith(CiBuildSuffix, StringComparison.OrdinalIgnoreCase)
            ? version[..^CiBuildSuffix.Length]
            : version;

    private static PackageInstallException InvalidPolicy(string message) =>
        new(
            PackageInstallErrorCode.InvalidPolicy,
            PackageInstallStage.PolicyValidation,
            message);
}
