// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;

namespace FhirPkg.Resolution;

internal sealed record PackageVersionSelection(
    string Key,
    FhirSemVer Version,
    PackageVersionInfo VersionInfo);

internal static class PackageVersionSelector
{
    internal static PackageVersionSelection? Select(
        PackageDirective directive,
        PackageListing listing,
        VersionResolveOptions? options)
    {
        ArgumentNullException.ThrowIfNull(directive);
        ArgumentNullException.ThrowIfNull(listing);

        List<PackageVersionSelection> eligible = listing.Versions
            .Select(entry => CreateCandidate(entry.Key, entry.Value))
            .Where(candidate => candidate is not null)
            .Cast<PackageVersionSelection>()
            .Where(candidate => IsEligible(listing.PackageId, candidate, options))
            .ToList();

        if (eligible.Count == 0)
        {
            return null;
        }

        return directive.VersionType switch
        {
            VersionType.Exact => SelectExact(eligible, directive.RequestedVersion!),
            VersionType.Latest => SelectLatest(eligible, listing),
            VersionType.Wildcard => SelectWildcard(eligible, directive.RequestedVersion!),
            VersionType.Range => SelectRange(eligible, directive.RequestedVersion!),
            _ => null,
        };
    }

    internal static PackageVersionSelection? Select(
        string packageId,
        string versionSpecifier,
        IEnumerable<FhirSemVer> availableVersions,
        VersionResolveOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionSpecifier);
        ArgumentNullException.ThrowIfNull(availableVersions);

        Dictionary<string, PackageVersionInfo> versions = new(StringComparer.Ordinal);
        foreach (FhirSemVer version in availableVersions)
        {
            string key = version.ToString();
            versions.TryAdd(
                key,
                new PackageVersionInfo
                {
                    Name = packageId,
                    Version = key,
                });
        }

        PackageListing listing = new()
        {
            PackageId = packageId,
            Versions = versions,
        };
        PackageDirective directive =
            PackageDirective.Parse($"{packageId}#{versionSpecifier}");
        return Select(directive, listing, options);
    }

    private static PackageVersionSelection? CreateCandidate(
        string key,
        PackageVersionInfo versionInfo) =>
        FhirSemVer.TryParse(key, out FhirSemVer? version)
        && !version.IsWildcard
            ? new PackageVersionSelection(key, version, versionInfo)
            : null;

    private static bool IsEligible(
        string packageId,
        PackageVersionSelection candidate,
        VersionResolveOptions? options)
    {
        if (options?.AllowPreRelease == false && candidate.Version.IsPreRelease)
        {
            return false;
        }

        return options?.FhirRelease is not FhirRelease preferredRelease
            || FhirReleaseExtractor.IsCompatible(
                packageId,
                candidate.VersionInfo,
                preferredRelease);
    }

    private static PackageVersionSelection? SelectExact(
        IEnumerable<PackageVersionSelection> candidates,
        string requestedVersion) =>
        candidates.FirstOrDefault(candidate =>
            candidate.Key.Equals(requestedVersion, StringComparison.OrdinalIgnoreCase));

    private static PackageVersionSelection SelectLatest(
        IReadOnlyCollection<PackageVersionSelection> candidates,
        PackageListing listing)
    {
        if (listing.DistTags is not null
            && listing.DistTags.TryGetValue("latest", out string? latestKey))
        {
            PackageVersionSelection? tagged = candidates.FirstOrDefault(candidate =>
                candidate.Key.Equals(latestKey, StringComparison.OrdinalIgnoreCase));
            if (tagged is not null)
            {
                return tagged;
            }
        }

        return candidates.MaxBy(candidate => candidate.Version)!;
    }

    private static PackageVersionSelection? SelectWildcard(
        IReadOnlyCollection<PackageVersionSelection> candidates,
        string specifier)
    {
        FhirSemVer? selected = FhirSemVer.MaxSatisfying(
            candidates.Select(candidate => candidate.Version),
            specifier,
            includePreRelease: true);
        return selected is null
            ? null
            : candidates.First(candidate => candidate.Version.Equals(selected));
    }

    private static PackageVersionSelection? SelectRange(
        IReadOnlyCollection<PackageVersionSelection> candidates,
        string rangeExpression)
    {
        HashSet<FhirSemVer> satisfying = FhirSemVer
            .SatisfyingRange(
                candidates.Select(candidate => candidate.Version),
                rangeExpression)
            .ToHashSet();
        return candidates
            .Where(candidate => satisfying.Contains(candidate.Version))
            .MaxBy(candidate => candidate.Version);
    }
}
