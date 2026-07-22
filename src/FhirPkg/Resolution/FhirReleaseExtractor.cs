// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;

namespace FhirPkg.Resolution;

internal static class FhirReleaseExtractor
{
    internal static bool IsCompatible(
        string packageId,
        PackageVersionInfo versionInfo,
        FhirRelease preferredRelease)
    {
        ArgumentNullException.ThrowIfNull(packageId);
        ArgumentNullException.ThrowIfNull(versionInfo);

        IReadOnlyList<string>? explicitVersions = versionInfo.FhirVersions;
        if (explicitVersions is not { Count: > 0 }
            && versionInfo.FhirVersion is string singular)
        {
            explicitVersions = [singular];
        }

        bool hasExplicitMetadata =
            versionInfo.HasExplicitFhirVersionMetadata
            || versionInfo.FhirVersion is not null
            || versionInfo.FhirVersions is not null;
        if (hasExplicitMetadata)
        {
            return explicitVersions is { Count: > 0 }
                && explicitVersions.Any(value =>
                TryMap(value, out FhirRelease release)
                && release == preferredRelease);
        }

        FhirRelease? inferred =
            FhirReleaseMapping.FromPackageName(packageId)
            ?? FhirReleaseMapping.FromPackageName(versionInfo.Name);
        return inferred == preferredRelease;
    }

    internal static bool TryMap(string value, out FhirRelease release)
    {
        release = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim();
        FhirRelease? fromVersion = FhirReleaseMapping.FromVersionString(normalized);
        if (fromVersion is FhirRelease versionRelease)
        {
            release = versionRelease;
            return true;
        }

        string name = normalized.ToUpperInvariant();
        release = name switch
        {
            "DSTU2" => FhirRelease.DSTU2,
            "R2" => FhirRelease.DSTU2,
            "STU3" => FhirRelease.STU3,
            "R3" => FhirRelease.STU3,
            "R4" => FhirRelease.R4,
            "R4B" => FhirRelease.R4B,
            "R5" => FhirRelease.R5,
            "R6" => FhirRelease.R6,
            _ => default,
        };
        return name is "DSTU2" or "R2" or "STU3" or "R3"
            or "R4" or "R4B" or "R5" or "R6";
    }
}
