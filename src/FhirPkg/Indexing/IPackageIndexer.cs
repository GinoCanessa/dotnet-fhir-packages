// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace FhirPkg.Indexing;

/// <summary>
/// Indexes FHIR package content directories and provides search capabilities across
/// all indexed packages.
/// </summary>
public interface IPackageIndexer
{
    /// <summary>
    /// Indexes all FHIR resources in the specified package content directory.
    /// If an existing <c>.index.json</c> is present (and <see cref="IndexingOptions.ForceReindex"/>
    /// is <c>false</c>), it is deserialized and returned directly.
    /// Otherwise, all <c>.json</c> files in the directory are scanned and a new index is built.
    /// </summary>
    /// <param name="packageContentPath">
    /// Full path to the package content directory (the <c>package/</c> folder inside the cache entry).
    /// </param>
    /// <param name="options">Optional settings controlling re-indexing behavior.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="PackageIndex"/> containing entries for every resource in the package.</returns>
    /// <exception cref="DirectoryNotFoundException">
    /// Thrown when <paramref name="packageContentPath"/> does not exist.
    /// </exception>
    Task<PackageIndex> IndexPackageAsync(
        string packageContentPath,
        IndexingOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches across all previously indexed packages for resources matching the given criteria.
    /// </summary>
    /// <param name="criteria">The search criteria to apply.</param>
    /// <returns>A read-only list of matching <see cref="ResourceInfo"/> entries.</returns>
    IReadOnlyList<ResourceInfo> FindResources(ResourceSearchCriteria criteria);

    /// <summary>
    /// Finds a single resource by its canonical URL across all indexed packages.
    /// Returns the first match, or <c>null</c> if no resource has the given URL.
    /// </summary>
    /// <param name="canonicalUrl">The canonical URL to search for (exact match).</param>
    /// <returns>A <see cref="ResourceInfo"/> for the matching resource, or <c>null</c>.</returns>
    ResourceInfo? FindByCanonicalUrl(string canonicalUrl);

    /// <summary>
    /// Finds all resources of a given FHIR resource type across all indexed packages,
    /// optionally scoped to a specific package.
    /// </summary>
    /// <param name="resourceType">The FHIR resource type to filter by (e.g. "StructureDefinition").</param>
    /// <param name="packageScope">
    /// Optional package scope (format: "name#version" or just "name").
    /// When <c>null</c>, all indexed packages are searched.
    /// </param>
    /// <returns>A read-only list of matching <see cref="ResourceInfo"/> entries.</returns>
    IReadOnlyList<ResourceInfo> FindByResourceType(string resourceType, string? packageScope = null);
}

/// <summary>
/// Options controlling package indexing behavior.
/// </summary>
public class IndexingOptions
{
    /// <summary>
    /// When <c>true</c>, regenerates the index even if a valid <c>.index.json</c> already exists
    /// in the package directory. Default: <c>false</c>.
    /// </summary>
    public bool ForceReindex { get; set; }
}
