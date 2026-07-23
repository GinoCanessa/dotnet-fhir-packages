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

        IEnumerable<KeyValuePair<string, PackageVersionInfo>> candidateEntries =
            listing.VersionCandidates.Count > 0
                ? listing.VersionCandidates.Select(candidate =>
                    new KeyValuePair<string, PackageVersionInfo>(
                        candidate.Version,
                        candidate))
                : listing.Versions;
        List<PackageVersionSelection> eligible = candidateEntries
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

    internal static PackageVersionInfo? SelectExactSourceCandidate(
        string packageId,
        string version,
        IEnumerable<PackageVersionInfo> candidates,
        VersionResolveOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(candidates);

        List<PackageVersionInfo> eligible = candidates
            .Where(candidate => candidate.Version.Equals(
                version,
                StringComparison.Ordinal))
            .Where(candidate =>
            {
                PackageVersionSelection? selection = CreateCandidate(
                    candidate.Version,
                    candidate);
                return selection is not null
                    && IsEligible(packageId, selection, options);
            })
            .ToList();

        // Prefer an explicit dependency declaration, but move the whole source
        // candidate so artifact and dependency metadata remain coherent.
        return eligible.FirstOrDefault(candidate =>
                candidate.Dependencies is not null)
            ?? eligible.FirstOrDefault();
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
            candidate.Key.Equals(requestedVersion, StringComparison.Ordinal));

    private static PackageVersionSelection SelectLatest(
        IReadOnlyCollection<PackageVersionSelection> candidates,
        PackageListing listing)
    {
        PackageVersionSelection? highestSourceLatest = SelectHighest(
            candidates.Where(candidate => candidate.VersionInfo.IsSourceLatest));
        if (highestSourceLatest is not null)
        {
            return highestSourceLatest;
        }

        if (listing.DistTags is not null
            && listing.DistTags.TryGetValue("latest", out string? latestKey))
        {
            PackageVersionSelection? tagged = candidates.FirstOrDefault(candidate =>
                candidate.Key.Equals(latestKey, StringComparison.Ordinal));
            if (tagged is not null)
            {
                return tagged;
            }
        }

        return SelectHighest(candidates)!;
    }

    private static PackageVersionSelection? SelectWildcard(
        IReadOnlyCollection<PackageVersionSelection> candidates,
        string specifier)
    {
        FhirSemVer pattern = FhirSemVer.Parse(specifier);
        return SelectHighest(
            candidates.Where(candidate => candidate.Version.Satisfies(pattern)));
    }

    private static PackageVersionSelection? SelectRange(
        IReadOnlyCollection<PackageVersionSelection> candidates,
        string rangeExpression)
    {
        FhirSemVerRange range = FhirSemVerRange.Parse(rangeExpression);
        return SelectHighest(candidates.Where(candidate =>
            range.IsSatisfiedBy(candidate.Version)));
    }

    private static PackageVersionSelection? SelectHighest(
        IEnumerable<PackageVersionSelection> candidates)
    {
        PackageVersionSelection? selected = null;
        foreach (PackageVersionSelection candidate in candidates)
        {
            if (selected is null || candidate.Version > selected.Version)
                selected = candidate;
        }

        return selected;
    }
}
