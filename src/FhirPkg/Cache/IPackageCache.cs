// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.Models;

namespace FhirPkg.Cache;

internal enum PackageCacheInstallEffect
{
    Unknown,
    Created,
    Replaced,
    Unchanged
}

internal sealed record PackageCacheInstallOutcome(
    PackageCacheInstallEffect Effect,
    string? PreviousManifestDate)
{
    internal static PackageCacheInstallOutcome Unknown { get; } =
        new(PackageCacheInstallEffect.Unknown, null);
}

/// <summary>
/// Interface for the local FHIR package cache (~/.fhir/packages).
/// Provides operations for installing, querying, and removing cached FHIR packages.
/// All implementations must be thread-safe for concurrent read access. Write operations
/// (install, remove, clear) should be serialized.
/// </summary>
public interface IPackageCache : IDisposable
{
    /// <summary>
    /// Gets the root directory of the package cache (e.g., ~/.fhir/packages).
    /// </summary>
    string CacheDirectory { get; }

    /// <summary>
    /// Checks if a specific package version is installed in the cache.
    /// A package is installed only when its cache directory, content directory,
    /// readable manifest, and manifest identity are valid.
    /// </summary>
    /// <param name="reference">The package identity to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the package is installed; <c>false</c> otherwise.</returns>
    Task<bool> IsInstalledAsync(PackageReference reference, CancellationToken ct = default);

    /// <summary>
    /// Gets the record for a cached package, or <c>null</c> if the package is not installed.
    /// Reads the manifest and optionally the index from the cached package directory.
    /// </summary>
    /// <param name="reference">The package identity to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="PackageRecord"/> describing the cached package, or <c>null</c>.</returns>
    Task<PackageRecord?> GetPackageAsync(PackageReference reference, CancellationToken ct = default);

    /// <summary>
    /// Lists all packages in the cache, optionally filtered by package ID prefix and/or version.
    /// </summary>
    /// <param name="packageIdFilter">Optional filter; only packages whose ID starts with this value are returned.</param>
    /// <param name="versionFilter">Optional exact version filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of matching package records.</returns>
    Task<IReadOnlyList<PackageRecord>> ListPackagesAsync(
        string? packageIdFilter = null,
        string? versionFilter = null,
        CancellationToken ct = default);

    /// <summary>
    /// Lists package summaries in the cache, optionally filtered by package ID
    /// prefix and/or version. Summary records deliberately omit resource indexes.
    /// </summary>
    /// <param name="packageIdFilter">Optional filter; only packages whose ID starts with this value are returned.</param>
    /// <param name="versionFilter">Optional exact version filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A read-only list of matching package records whose
    /// <see cref="PackageRecord.Index"/> values are <c>null</c>.
    /// </returns>
    /// <remarks>
    /// The default implementation preserves compatibility by cloning records
    /// returned from <see cref="ListPackagesAsync"/> without their indexes.
    /// It may therefore hydrate internally unless an implementation overrides
    /// this method. Call <see cref="ListPackagesAsync"/> when resource indexes
    /// are required.
    /// </remarks>
    async Task<IReadOnlyList<PackageRecord>> ListPackageSummariesAsync(
        string? packageIdFilter = null,
        string? versionFilter = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<PackageRecord> records = await ListPackagesAsync(
                packageIdFilter,
                versionFilter,
                ct)
            .ConfigureAwait(false);
        return records
            .Select(record => record with { Index = null })
            .ToArray();
    }

    /// <summary>
    /// Installs a package from a tarball stream into the cache.
    /// Performs atomic extraction via a temporary directory, normalizes the package structure,
    /// and moves the result to the final cache location.
    /// </summary>
    /// <param name="reference">The package identity (name and version) to install.</param>
    /// <param name="tarballStream">
    /// A readable stream containing the .tgz tarball. The stream is consumed from
    /// its current position and is left open.
    /// </param>
    /// <param name="options">Optional installation options (overwrite, checksum verification).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="PackageRecord"/> for the newly installed package.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the package is already installed and overwrite is not enabled.</exception>
    Task<PackageRecord> InstallAsync(
        PackageReference reference,
        Stream tarballStream,
        InstallCacheOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a specific package from the cache.
    /// Deletes the package directory and updates the cache metadata.
    /// </summary>
    /// <param name="reference">The package identity to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the package was found and removed; <c>false</c> if it was not installed.</returns>
    Task<bool> RemoveAsync(PackageReference reference, CancellationToken ct = default);

    /// <summary>
    /// Removes all packages from the cache.
    /// Deletes every package directory and clears the cache metadata.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of packages that were removed.</returns>
    Task<int> ClearAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads the package manifest (package.json) from a cached package.
    /// </summary>
    /// <param name="reference">The package identity.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deserialized <see cref="PackageManifest"/>, or <c>null</c> if the package is not installed.</returns>
    Task<PackageManifest?> ReadManifestAsync(PackageReference reference, CancellationToken ct = default);

    /// <summary>
    /// Gets the resource index for a cached package.
    /// Tries to read .index.json from the package content directory.
    /// Returns <c>null</c> if no index file exists (the caller should generate one).
    /// </summary>
    /// <param name="reference">The package identity.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deserialized <see cref="PackageIndex"/>, or <c>null</c>.</returns>
    Task<PackageIndex?> GetIndexAsync(PackageReference reference, CancellationToken ct = default);

    /// <summary>
    /// Gets the text content of a file within a cached package.
    /// </summary>
    /// <param name="reference">The package identity.</param>
    /// <param name="relativePath">Path relative to the package/ content directory.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The file content as a string, or <c>null</c> if the file or package does not exist.</returns>
    Task<string?> GetFileContentAsync(PackageReference reference, string relativePath, CancellationToken ct = default);

    /// <summary>
    /// Gets the content folder path (package/) for a cached package.
    /// Returns <c>null</c> if the package is not installed.
    /// </summary>
    /// <param name="reference">The package identity.</param>
    /// <returns>The full path to the package content directory, or <c>null</c>.</returns>
    string? GetPackageContentPath(PackageReference reference);

    /// <summary>
    /// Reads or creates the cache metadata (packages.ini).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current <see cref="CacheMetadata"/>.</returns>
    Task<CacheMetadata> GetMetadataAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates the cache metadata for a specific package entry.
    /// Writes the updated packages.ini to disk.
    /// </summary>
    /// <param name="reference">The package identity whose metadata is being updated.</param>
    /// <param name="entry">The new metadata entry for the package.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateMetadataAsync(PackageReference reference, CacheMetadataEntry entry, CancellationToken ct = default);
}

/// <summary>
/// Options controlling how a package is installed into the cache.
/// </summary>
public class InstallCacheOptions
{
    /// <summary>
    /// If <c>true</c>, overwrites an existing cached package with the same identity.
    /// If <c>false</c> (default), throws if the package is already installed.
    /// </summary>
    public bool OverwriteExisting { get; set; }

    /// <summary>
    /// If <c>true</c> (default), verifies the SHA-1 checksum of the tarball against
    /// <see cref="ExpectedShaSum"/> when it is provided.
    /// </summary>
    public bool VerifyChecksum { get; set; } = true;

    /// <summary>
    /// Optional finite resource limits for this installation. Values may tighten,
    /// but not exceed, the limits configured for the cache.
    /// </summary>
    public PackageInstallLimits? Limits { get; set; }

    /// <summary>
    /// Content length reported by the source, when known. The actual byte count is
    /// always enforced independently.
    /// </summary>
    public long? ReportedContentLength { get; set; }

    /// <summary>
    /// Expected SHA-1 hash of the tarball. Used for integrity verification when
    /// <see cref="VerifyChecksum"/> is <c>true</c>.
    /// </summary>
    public string? ExpectedShaSum { get; set; }

    /// <summary>
    /// Expected SHA-256 hash of the tarball. When provided and <see cref="VerifyChecksum"/> is <c>true</c>,
    /// SHA-256 is preferred over SHA-1 for integrity verification.
    /// </summary>
    public string? ExpectedSha256Sum { get; set; }

    /// <summary>
    /// Source publication time associated with a mutable package alias, when known.
    /// </summary>
    public DateTimeOffset? SourcePublicationDate { get; set; }

    /// <summary>
    /// SHA-256 of the compressed archive used to install the package.
    /// </summary>
    public string? ArchiveSha256 { get; set; }

    internal PackageContentAcquisition? AcquiredContent { get; set; }

    internal PackageIdentityExpectation? IdentityExpectation { get; set; }

    internal PackageCacheInstallOutcome InstallOutcome { get; set; } =
        PackageCacheInstallOutcome.Unknown;

    /// <summary>
    /// Controls whether an invalid existing cache target is repaired or
    /// reported. Hardened cache implementations must honor this policy before
    /// consuming replacement content.
    /// </summary>
    public CorruptCacheBehavior CorruptCacheBehavior { get; set; } =
        CorruptCacheBehavior.Repair;

    /// <summary>
    /// When <c>true</c>, a mutable alias whose acquired archive SHA-256 matches
    /// the recorded archive may refresh metadata without replacing content.
    /// </summary>
    public bool SkipIfArchiveUnchanged { get; set; }

    /// <summary>Optional progress callback for hardened cache work.</summary>
    public IProgress<PackageProgress>? Progress { get; set; }
}
