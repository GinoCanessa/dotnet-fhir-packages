// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Installation;

namespace FhirPkg;

/// <summary>
/// Primary interface for FHIR package management operations.
/// Coordinates registry queries, cache operations, dependency resolution,
/// and package installation across configured registries and the local cache.
/// </summary>
public interface IFhirPackageManager
{
    /// <summary>
    /// Installs a package by directive (e.g., "hl7.fhir.us.core#6.1.0").
    /// Resolves the directive, downloads if not cached, and extracts to the local cache.
    /// </summary>
    /// <param name="directive">Package directive (name#version or name@version).</param>
    /// <param name="options">Installation options controlling dependency resolution, overwrite behavior, and progress reporting.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached package record, or <c>null</c> if the package could not be resolved.</returns>
    Task<PackageRecord?> InstallAsync(
        string directive,
        InstallOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs multiple packages in parallel with concurrency control.
    /// Each directive is independently resolved, downloaded, and cached.
    /// </summary>
    /// <param name="directives">Collection of package directives to install.</param>
    /// <param name="options">Installation options applied to all packages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A read-only list of installation results, one per directive.</returns>
    Task<IReadOnlyList<PackageInstallResult>> InstallManyAsync(
        IEnumerable<string> directives,
        InstallOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores all dependencies declared in a project's package.json manifest.
    /// Performs recursive dependency resolution and produces a lock file.
    /// </summary>
    /// <param name="projectPath">Path to the project directory containing package.json.</param>
    /// <param name="options">Restore options controlling conflict strategy, lock file generation, and depth limits.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The complete package closure containing all resolved and missing dependencies.</returns>
    Task<PackageClosure> RestoreAsync(
        string projectPath,
        RestoreOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all packages currently in the local cache, optionally filtered by package ID prefix.
    /// </summary>
    /// <param name="filter">Optional filter; only packages whose ID starts with this value are returned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A read-only list of cached package records.</returns>
    Task<IReadOnlyList<PackageRecord>> ListCachedAsync(
        string? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a package from the local cache.
    /// </summary>
    /// <param name="directive">Package directive identifying the cached package to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the package was found and removed; <c>false</c> otherwise.</returns>
    Task<bool> RemoveAsync(
        string directive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all packages from the local cache and returns the number of packages removed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of packages that were removed.</returns>
    Task<int> CleanCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches configured package registries for packages matching the given criteria.
    /// </summary>
    /// <param name="criteria">Search criteria including name, canonical, FHIR version, etc.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A read-only list of catalog entries matching the search criteria.</returns>
    Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        PackageSearchCriteria criteria,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all available versions of a package from configured registries.
    /// </summary>
    /// <param name="packageId">The package identifier to look up (e.g., "hl7.fhir.us.core").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A merged <see cref="PackageListing"/> with all known versions, or <c>null</c>
    /// when every successful source reports absence. Check
    /// <see cref="PackageListing.IsComplete"/> before making global selections.
    /// </returns>
    /// <exception cref="RegistryOperationException">
    /// No registry produced a listing and at least one registry attempt failed.
    /// </exception>
    Task<PackageListing?> GetPackageListingAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a directive to an exact version and download location without downloading the package.
    /// </summary>
    /// <param name="directive">Package directive (name#version or name@version).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ResolvedDirective"/> with exact, source-coherent metadata, or
    /// <c>null</c> when all successful sources report absence.
    /// </returns>
    /// <exception cref="RegistryOperationException">
    /// Registry failures prevent an authoritative resolution.
    /// </exception>
    Task<ResolvedDirective?> ResolveAsync(
        string directive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a package tarball to the specified registry.
    /// </summary>
    /// <param name="tarballPath">Full file-system path to the .tgz package tarball.</param>
    /// <param name="registry">The target registry endpoint to publish to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="PublishResult"/> indicating the outcome of the publish operation.</returns>
    Task<PublishResult> PublishAsync(
        string tarballPath,
        RegistryEndpoint registry,
        CancellationToken cancellationToken = default);

    /// <summary>Installs URI content under an explicit expected identity.</summary>
    Task<PackageRecord> InstallAsync(
        PackageReference expectedReference,
        Uri packageUri,
        PackageSourceInstallOptions? options,
        CancellationToken cancellationToken)
    {
        if (this is IHardenedFhirPackageManager hardenedManager)
        {
            return hardenedManager.InstallAsync(
                expectedReference,
                packageUri,
                options,
                cancellationToken);
        }

        throw UnsupportedManagerCapability();
    }

    /// <summary>Installs caller-owned stream content under an expected identity.</summary>
    Task<PackageRecord> InstallAsync(
        PackageReference expectedReference,
        Stream packageStream,
        PackageSourceInstallOptions? options,
        CancellationToken cancellationToken)
    {
        if (this is IHardenedFhirPackageManager hardenedManager)
        {
            return hardenedManager.InstallAsync(
                expectedReference,
                packageStream,
                options,
                cancellationToken);
        }

        throw UnsupportedManagerCapability();
    }

    /// <summary>Imports URI content using its validated manifest identity.</summary>
    Task<PackageRecord> ImportAsync(
        Uri packageUri,
        PackageSourceInstallOptions? options,
        CancellationToken cancellationToken)
    {
        if (this is IHardenedFhirPackageManager hardenedManager)
        {
            return hardenedManager.ImportAsync(
                packageUri,
                options,
                cancellationToken);
        }

        throw UnsupportedManagerCapability();
    }

    /// <summary>Imports caller-owned stream content using its validated manifest identity.</summary>
    Task<PackageRecord> ImportAsync(
        Stream packageStream,
        PackageSourceInstallOptions? options,
        CancellationToken cancellationToken)
    {
        if (this is IHardenedFhirPackageManager hardenedManager)
        {
            return hardenedManager.ImportAsync(
                packageStream,
                options,
                cancellationToken);
        }

        throw UnsupportedManagerCapability();
    }

    private static PackageInstallException UnsupportedManagerCapability() =>
        new(
            PackageInstallErrorCode.UnsupportedManagerCapability,
            PackageInstallStage.PolicyValidation,
            "The package manager does not support hardened URI or stream installation.");
}
