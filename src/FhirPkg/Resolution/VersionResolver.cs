// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

using FhirPkg.Models;
using FhirPkg.Registry;

namespace FhirPkg.Resolution;

/// <summary>
/// Resolves version specifiers (exact, wildcard, latest, range) to concrete <see cref="FhirSemVer"/> versions
/// by querying the FHIR package registry for available versions.
/// </summary>
/// <remarks>
/// <para>
/// This resolver handles the following version types:
/// <list type="bullet">
///   <item><description><see cref="VersionType.Exact"/>: Direct lookup in the registry's version map.</description></item>
///   <item><description><see cref="VersionType.Latest"/>: Uses the dist-tags "latest" from the package listing.</description></item>
///   <item><description><see cref="VersionType.Wildcard"/>: Parses all available versions and calls <see cref="FhirSemVer.MaxSatisfying"/>.</description></item>
///   <item><description><see cref="VersionType.Range"/>: Parses all available versions and calls <see cref="FhirSemVer.SatisfyingRange"/>.</description></item>
///   <item><description><see cref="VersionType.CiBuild"/> / <see cref="VersionType.CiBuildBranch"/>: Returns <c>null</c> (delegated to the CI build client by the orchestrator).</description></item>
/// </list>
/// </para>
/// <para>
/// Pre-release versions are included or excluded based on <see cref="VersionResolveOptions.AllowPreRelease"/>.
/// </para>
/// </remarks>
public class VersionResolver : IVersionResolver
{
    private readonly IRegistryClient _registryClient;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VersionResolver"/> class.
    /// </summary>
    /// <param name="registryClient">
    /// The registry client used to query available package versions.
    /// May be a <see cref="RedundantRegistryClient"/> wrapping multiple registries.
    /// </param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public VersionResolver(IRegistryClient registryClient, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registryClient);
        ArgumentNullException.ThrowIfNull(logger);

        _registryClient = registryClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FhirSemVer?> ResolveVersionAsync(
        string packageId,
        string versionSpecifier,
        VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageId);
        ArgumentNullException.ThrowIfNull(versionSpecifier);

        VersionType versionType = DirectiveParser.ClassifyVersion(versionSpecifier);

        // CI builds are handled externally by the orchestrator
        if (versionType is VersionType.CiBuild or VersionType.CiBuildBranch)
        {
            _logger.LogDebug(
                "Version specifier '{VersionSpecifier}' for '{PackageId}' is a CI build; delegating to CI build client.",
                versionSpecifier, packageId);
            return null;
        }

        // Local builds cannot be resolved from a registry
        if (versionType == VersionType.LocalBuild)
        {
            _logger.LogDebug(
                "Version specifier '{VersionSpecifier}' for '{PackageId}' is a local build; cannot resolve from registry.",
                versionSpecifier, packageId);
            return null;
        }

        PackageListing? listing = await _registryClient.GetPackageListingAsync(packageId, cancellationToken)
            .ConfigureAwait(false);

        if (listing is null)
        {
            _logger.LogWarning("Package '{PackageId}' was not found in any configured registry.", packageId);
            return null;
        }

        return ResolveFromListing(versionSpecifier, versionType, listing, options);
    }

    /// <inheritdoc />
    public FhirSemVer? ResolveVersion(
        string versionSpecifier,
        IEnumerable<FhirSemVer> availableVersions,
        VersionResolveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(versionSpecifier);
        ArgumentNullException.ThrowIfNull(availableVersions);

        VersionType versionType = DirectiveParser.ClassifyVersion(versionSpecifier);
        IEnumerable<FhirSemVer> candidates = FilterPreRelease(availableVersions, options);

        return versionType switch
        {
            VersionType.Exact => ResolveExact(versionSpecifier, candidates),
            VersionType.Latest => ResolveLatestFromCandidates(candidates),
            VersionType.Wildcard => ResolveWildcard(versionSpecifier, candidates),
            VersionType.Range => ResolveRange(versionSpecifier, candidates),
            _ => null
        };
    }

    /// <summary>
    /// Resolves a version from a registry package listing.
    /// </summary>
    private FhirSemVer? ResolveFromListing(
        string versionSpecifier,
        VersionType versionType,
        PackageListing listing,
        VersionResolveOptions? options)
    {
        switch (versionType)
        {
            case VersionType.Exact:
                return ResolveExactFromListing(versionSpecifier, listing);

            case VersionType.Latest:
                return ResolveLatestFromListing(listing, options);

            case VersionType.Wildcard:
            {
                    IEnumerable<FhirSemVer> candidates = ParseAndFilterVersionKeys(listing, options);
                return ResolveWildcard(versionSpecifier, candidates);
            }

            case VersionType.Range:
            {
                    IEnumerable<FhirSemVer> candidates = ParseAndFilterVersionKeys(listing, options);
                return ResolveRange(versionSpecifier, candidates);
            }

            default:
                _logger.LogWarning("Unsupported version type '{VersionType}' for specifier '{VersionSpecifier}'.",
                    versionType, versionSpecifier);
                return null;
        }
    }

    /// <summary>
    /// Resolves an exact version by direct lookup in the listing's version map.
    /// </summary>
    private FhirSemVer? ResolveExactFromListing(string versionSpecifier, PackageListing listing)
    {
        if (listing.Versions.ContainsKey(versionSpecifier))
        {
            FhirSemVer? parsed = TryParseSemVer(versionSpecifier);
            if (parsed is not null)
            {
                _logger.LogDebug("Resolved exact version '{Version}' for '{PackageId}'.",
                    versionSpecifier, listing.PackageId);
            }
            return parsed;
        }

        // Try case-insensitive lookup
        string? match = listing.Versions.Keys
            .FirstOrDefault(k => k.Equals(versionSpecifier, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            _logger.LogDebug("Resolved exact version '{Version}' (case-insensitive) for '{PackageId}'.",
                match, listing.PackageId);
            return TryParseSemVer(match);
        }

        _logger.LogWarning("Exact version '{Version}' not found for package '{PackageId}'.",
            versionSpecifier, listing.PackageId);
        return null;
    }

    /// <summary>
    /// Resolves the "latest" tagged version from a package listing.
    /// </summary>
    private FhirSemVer? ResolveLatestFromListing(PackageListing listing, VersionResolveOptions? options)
    {
        string? latestTag = listing.LatestVersion;
        if (latestTag is null)
        {
            _logger.LogWarning("No 'latest' version found for package '{PackageId}'.", listing.PackageId);
            return null;
        }

        FhirSemVer? parsed = TryParseSemVer(latestTag);

        // If the latest tag is a pre-release and pre-releases are not allowed, fall back to
        // the highest non-pre-release version.
        if (parsed is not null && parsed.PreRelease is not null && options?.AllowPreRelease == false)
        {
            _logger.LogDebug(
                "Latest tag '{LatestTag}' is a pre-release but pre-releases are not allowed; falling back.",
                latestTag);
            IEnumerable<FhirSemVer> candidates = ParseAndFilterVersionKeys(listing, options);
            return ResolveLatestFromCandidates(candidates);
        }

        _logger.LogDebug("Resolved 'latest' to '{Version}' for '{PackageId}'.", latestTag, listing.PackageId);
        return parsed;
    }

    /// <summary>
    /// Resolves an exact version from an enumerable of candidate versions.
    /// </summary>
    private static FhirSemVer? ResolveExact(string versionSpecifier, IEnumerable<FhirSemVer> candidates)
    {
        FhirSemVer? target = TryParseSemVer(versionSpecifier);
        if (target is null) return null;

        return candidates.FirstOrDefault(v =>
            v.Major == target.Major &&
            v.Minor == target.Minor &&
            v.Patch == target.Patch &&
            string.Equals(v.PreRelease, target.PreRelease, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns the highest version from the candidate list.
    /// </summary>
    private static FhirSemVer? ResolveLatestFromCandidates(IEnumerable<FhirSemVer> candidates)
    {
        return candidates
            .OrderByDescending(v => v.Major)
            .ThenByDescending(v => v.Minor)
            .ThenByDescending(v => v.Patch)
            .ThenBy(v => v.PreRelease is null ? 0 : 1) // stable versions first
            .FirstOrDefault();
    }

    /// <summary>
    /// Resolves a wildcard pattern (e.g. "4.0.x", "4.*") to the highest matching version.
    /// </summary>
    private static FhirSemVer? ResolveWildcard(string pattern, IEnumerable<FhirSemVer> candidates)
    {
        return FhirSemVer.MaxSatisfying(candidates, pattern, includePreRelease: true);
    }

    /// <summary>
    /// Resolves a range expression (e.g. "^4.0.0", "~3.0.0") to the highest matching version.
    /// </summary>
    private static FhirSemVer? ResolveRange(string range, IEnumerable<FhirSemVer> candidates)
    {
        return FhirSemVer.SatisfyingRange(candidates, range)
            .OrderByDescending(v => v)
            .FirstOrDefault();
    }

    /// <summary>
    /// Parses all version keys from a listing into <see cref="FhirSemVer"/> and applies pre-release filtering.
    /// </summary>
    private IEnumerable<FhirSemVer> ParseAndFilterVersionKeys(PackageListing listing, VersionResolveOptions? options)
    {
        IEnumerable<FhirSemVer> parsed = listing.Versions.Keys
            .Select(TryParseSemVer)
            .Where(v => v is not null)
            .Cast<FhirSemVer>();

        return FilterPreRelease(parsed, options);
    }

    /// <summary>
    /// Filters out pre-release versions when the options disallow them.
    /// </summary>
    private static IEnumerable<FhirSemVer> FilterPreRelease(
        IEnumerable<FhirSemVer> versions,
        VersionResolveOptions? options)
    {
        if (options?.AllowPreRelease == false)
        {
            versions = versions.Where(v => v.PreRelease is null);
        }
        return versions;
    }

    /// <summary>
    /// Tries to parse a version string into a <see cref="FhirSemVer"/>.
    /// Returns <c>null</c> on parse failure instead of throwing.
    /// </summary>
    private static FhirSemVer? TryParseSemVer(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        return FhirSemVer.TryParse(version, out FhirSemVer? result) ? result : null;
    }
}
