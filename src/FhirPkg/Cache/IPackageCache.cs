// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Indexing;
using FhirPkg.Models;

namespace FhirPkg.Cache;

/// <summary>
/// Interface for the local FHIR package cache (~/.fhir/packages).
/// Provides operations for installing, querying, and removing cached FHIR packages.
/// All implementations must be thread-safe for concurrent read access. Write operations
/// (install, remove, clear) should be serialized.
/// </summary>
public interface IPackageCache
{
    /// <summary>
    /// Gets the root directory of the package cache (e.g., ~/.fhir/packages).
    /// </summary>
    string CacheDirectory { get; }

    /// <summary>
    /// Checks if a specific package version is installed in the cache.
    /// A package is considered installed if its content directory (package/) exists.
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
    /// Installs a package from a tarball stream into the cache.
    /// Performs atomic extraction via a temporary directory, normalizes the package structure,
    /// and moves the result to the final cache location.
    /// </summary>
    /// <param name="reference">The package identity (name and version) to install.</param>
    /// <param name="tarballStream">A readable stream containing the .tgz tarball.</param>
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
    /// Expected SHA-1 hash of the tarball. Used for integrity verification when
    /// <see cref="VerifyChecksum"/> is <c>true</c>.
    /// </summary>
    public string? ExpectedShaSum { get; set; }
}
