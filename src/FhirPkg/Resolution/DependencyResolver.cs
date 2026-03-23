// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

using FhirPkg.Cache;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Utilities;

namespace FhirPkg.Resolution;

/// <summary>
/// Recursively resolves the full transitive dependency closure for a FHIR package manifest.
/// Handles circular dependency detection, version conflict resolution, depth limiting,
/// and known package fixups.
/// </summary>
/// <remarks>
/// <para>
/// The resolver builds a dependency graph by starting from the root manifest's direct dependencies
/// and recursively processing each resolved package's own dependencies. At each step:
/// </para>
/// <list type="number">
///   <item><description>Known package fixups (e.g. hl7.fhir.r4.core@4.0.0 → 4.0.1) are applied.</description></item>
///   <item><description>Version specifiers are resolved to exact versions via <see cref="IVersionResolver"/>.</description></item>
///   <item><description>Version conflicts are handled according to the configured <see cref="ConflictResolutionStrategy"/>.</description></item>
///   <item><description>Circular dependencies are detected and short-circuited.</description></item>
///   <item><description>Resolution depth is bounded by <see cref="DependencyResolveOptions.MaxDepth"/>.</description></item>
/// </list>
/// </remarks>
public class DependencyResolver : IDependencyResolver
{
    private readonly IRegistryClient _registryClient;
    private readonly IVersionResolver _versionResolver;
    private readonly IPackageCache _cache;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DependencyResolver"/> class.
    /// </summary>
    /// <param name="registryClient">
    /// The registry client used to query package metadata and transitive dependencies.
    /// May be a <see cref="RedundantRegistryClient"/> wrapping multiple registries.
    /// </param>
    /// <param name="versionResolver">Resolver for converting version specifiers to exact versions.</param>
    /// <param name="cache">The local package cache for reading manifests of already-cached packages.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public DependencyResolver(
        IRegistryClient registryClient,
        IVersionResolver versionResolver,
        IPackageCache cache,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registryClient);
        ArgumentNullException.ThrowIfNull(versionResolver);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        _registryClient = registryClient;
        _versionResolver = versionResolver;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PackageClosure> ResolveAsync(
        PackageManifest rootManifest,
        DependencyResolveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootManifest);

        options ??= new DependencyResolveOptions();

        var resolved = new Dictionary<string, PackageReference>(StringComparer.OrdinalIgnoreCase);
        var missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var dependencies = rootManifest.Dependencies ?? new Dictionary<string, string>();

        _logger.LogInformation(
            "Resolving dependencies for '{PackageName}@{PackageVersion}' ({DependencyCount} direct dependencies).",
            rootManifest.Name, rootManifest.Version, dependencies.Count);

        await ResolveRecursiveAsync(
            dependencies,
            resolved,
            missing,
            visited,
            options,
            currentDepth: 0,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Dependency resolution complete: {ResolvedCount} resolved, {MissingCount} missing.",
            resolved.Count, missing.Count);

        return new PackageClosure
        {
            Timestamp = DateTime.UtcNow,
            Resolved = resolved,
            Missing = missing,
        };
    }

    /// <inheritdoc />
    public async Task<PackageClosure> RestoreFromLockFileAsync(
        PackageLockFile lockFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lockFile);

        var resolved = new Dictionary<string, PackageReference>(StringComparer.OrdinalIgnoreCase);
        var missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "Restoring {Count} locked dependencies from lock file (updated {Updated:O}).",
            lockFile.Dependencies.Count, lockFile.Updated);

        foreach (var (packageId, version) in lockFile.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var reference = new PackageReference(packageId, version);

            // Check if the package is already in the local cache
            var isCached = await _cache.IsInstalledAsync(reference, cancellationToken).ConfigureAwait(false);
            if (isCached)
            {
                _logger.LogDebug("Package '{PackageId}@{Version}' found in cache.", packageId, version);
                resolved[packageId] = reference;
                continue;
            }

            // Not cached — try to resolve and mark for download by the orchestrator
            var resolvedVersion = await _versionResolver.ResolveVersionAsync(
                packageId, version, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (resolvedVersion is not null)
            {
                var resolvedRef = new PackageReference(packageId, resolvedVersion.ToString());
                resolved[packageId] = resolvedRef;

                _logger.LogDebug(
                    "Locked package '{PackageId}@{Version}' resolved to '{ResolvedVersion}' (not cached, needs download).",
                    packageId, version, resolvedVersion);
            }
            else
            {
                missing[packageId] = $"Locked version '{version}' could not be resolved from any registry.";
                _logger.LogWarning(
                    "Locked package '{PackageId}@{Version}' could not be resolved from any configured registry.",
                    packageId, version);
            }
        }

        // Also include any packages that were missing in the original lock file
        if (lockFile.Missing is not null)
        {
            foreach (var (packageId, versionConstraint) in lockFile.Missing)
            {
                if (!resolved.ContainsKey(packageId) && !missing.ContainsKey(packageId))
                {
                    missing[packageId] = $"Previously missing in lock file: '{versionConstraint}'";
                }
            }
        }

        _logger.LogInformation(
            "Lock file restore complete: {ResolvedCount} resolved, {MissingCount} missing.",
            resolved.Count, missing.Count);

        return new PackageClosure
        {
            Timestamp = DateTime.UtcNow,
            Resolved = resolved,
            Missing = missing,
        };
    }

    /// <summary>
    /// Recursively resolves a set of dependencies and their transitive dependencies.
    /// </summary>
    private async Task ResolveRecursiveAsync(
        IReadOnlyDictionary<string, string> dependencies,
        Dictionary<string, PackageReference> resolved,
        Dictionary<string, string> missing,
        HashSet<string> visited,
        DependencyResolveOptions options,
        int currentDepth,
        CancellationToken cancellationToken)
    {
        if (currentDepth > options.MaxDepth)
        {
            _logger.LogWarning(
                "Maximum dependency resolution depth ({MaxDepth}) exceeded. Stopping recursion.",
                options.MaxDepth);
            return;
        }

        foreach (var (rawPackageId, rawVersionSpec) in dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Apply known fixups (e.g. hl7.fhir.r4.core@4.0.0 → 4.0.1)
            var fixedRef = PackageFixups.Apply(new PackageReference(rawPackageId, rawVersionSpec));
            var packageId = fixedRef.Name;
            var versionSpec = fixedRef.Version ?? "latest";

            // Circular dependency detection
            var visitKey = $"{packageId}@{versionSpec}";
            if (!visited.Add(visitKey))
            {
                _logger.LogDebug("Skipping already-visited dependency '{VisitKey}'.", visitKey);
                continue;
            }

            // Resolve the version specifier
            var versionResolveOptions = new VersionResolveOptions
            {
                AllowPreRelease = options.AllowPreRelease,
                FhirRelease = options.PreferredFhirRelease,
            };

            var resolvedVersion = await _versionResolver.ResolveVersionAsync(
                packageId, versionSpec, versionResolveOptions, cancellationToken).ConfigureAwait(false);

            if (resolvedVersion is null)
            {
                // Could not resolve — record as missing
                if (!missing.ContainsKey(packageId))
                {
                    missing[packageId] = $"Could not resolve version '{versionSpec}'.";
                    _logger.LogWarning(
                        "Could not resolve '{PackageId}@{VersionSpec}' from any configured registry.",
                        packageId, versionSpec);
                }
                continue;
            }

            var resolvedRef = new PackageReference(packageId, resolvedVersion.ToString());

            // Handle version conflicts
            if (resolved.TryGetValue(packageId, out var existingRef))
            {
                var winner = ResolveConflict(existingRef, resolvedRef, options.ConflictStrategy);
                if (winner is null)
                {
                    // Error strategy — record conflict as missing
                    missing[packageId] =
                        $"Version conflict: '{existingRef.Version}' vs '{resolvedRef.Version}' " +
                        $"(strategy: {options.ConflictStrategy}).";
                    _logger.LogError(
                        "Version conflict for '{PackageId}': existing '{ExistingVersion}' vs new '{NewVersion}'.",
                        packageId, existingRef.Version, resolvedRef.Version);
                    continue;
                }

                if (string.Equals(winner.Value.Version, existingRef.Version, StringComparison.Ordinal))
                {
                    // Existing version wins — no need to recurse again
                    _logger.LogDebug(
                        "Version conflict for '{PackageId}': keeping '{WinnerVersion}' (strategy: {Strategy}).",
                        packageId, winner.Value.Version, options.ConflictStrategy);
                    continue;
                }

                _logger.LogDebug(
                    "Version conflict for '{PackageId}': upgrading to '{WinnerVersion}' (strategy: {Strategy}).",
                    packageId, winner.Value.Version, options.ConflictStrategy);
                resolved[packageId] = winner.Value;
            }
            else
            {
                resolved[packageId] = resolvedRef;
                _logger.LogDebug("Resolved '{PackageId}@{VersionSpec}' → '{ResolvedVersion}'.",
                    packageId, versionSpec, resolvedVersion);
            }

            // Recurse into this package's own dependencies
            var transitiveDeps = await GetTransitiveDependenciesAsync(
                resolvedRef, cancellationToken).ConfigureAwait(false);

            if (transitiveDeps is not null && transitiveDeps.Count > 0)
            {
                _logger.LogDebug(
                    "Recursing into {Count} transitive dependencies of '{PackageId}@{Version}'.",
                    transitiveDeps.Count, packageId, resolvedVersion);

                await ResolveRecursiveAsync(
                    transitiveDeps, resolved, missing, visited, options,
                    currentDepth + 1, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Resolves a version conflict between two references for the same package,
    /// according to the configured strategy.
    /// </summary>
    /// <returns>
    /// The winning <see cref="PackageReference"/>, or <c>null</c> if the strategy is
    /// <see cref="ConflictResolutionStrategy.Error"/>.
    /// </returns>
    private static PackageReference? ResolveConflict(
        PackageReference existing,
        PackageReference incoming,
        ConflictResolutionStrategy strategy)
    {
        return strategy switch
        {
            ConflictResolutionStrategy.FirstWins => existing,
            ConflictResolutionStrategy.HighestWins => PickHighestVersion(existing, incoming),
            ConflictResolutionStrategy.Error => null,
            _ => existing
        };
    }

    /// <summary>
    /// Compares two package references by their version strings and returns the one
    /// with the higher version.
    /// </summary>
    private static PackageReference PickHighestVersion(PackageReference a, PackageReference b)
    {
        var verA = TryParseSemVer(a.Version);
        var verB = TryParseSemVer(b.Version);

        if (verA is null) return b;
        if (verB is null) return a;

        var cmp = verA.CompareTo(verB);
        return cmp >= 0 ? a : b;
    }

    /// <summary>
    /// Gets the transitive dependencies for a resolved package by reading its manifest
    /// from the cache or, if not cached, from the registry listing.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> GetTransitiveDependenciesAsync(
        PackageReference reference,
        CancellationToken cancellationToken)
    {
        // First try reading from cache
        var manifest = await _cache.ReadManifestAsync(reference, cancellationToken).ConfigureAwait(false);
        if (manifest?.Dependencies is not null)
            return manifest.Dependencies;

        // Fall back to registry metadata
        var listing = await _registryClient.GetPackageListingAsync(reference.Name, cancellationToken)
            .ConfigureAwait(false);

        if (listing is not null &&
            reference.Version is not null &&
            listing.Versions.TryGetValue(reference.Version, out var versionInfo))
        {
            return versionInfo.Dependencies;
        }

        return null;
    }

    /// <summary>
    /// Tries to parse a version string into a <see cref="FhirSemVer"/>.
    /// Returns <c>null</c> on parse failure.
    /// </summary>
    private static FhirSemVer? TryParseSemVer(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        return FhirSemVer.TryParse(version, out var result) ? result : null;
    }
}
