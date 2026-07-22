// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json.Nodes;
using FhirPkg.Indexing;
using FhirPkg.Models;

namespace FhirPkg;

/// <summary>
/// Optional package-resource capability implemented by managers that support
/// durable package indexes and resource reads.
/// </summary>
public interface IFhirPackageResourceManager
{
    /// <summary>Loads or generates the resource index for one cached package.</summary>
    Task<PackageIndex?> IndexPackageAsync(
        PackageReference reference,
        IndexingOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Finds cached package resources matching the supplied criteria.</summary>
    Task<IReadOnlyList<ResourceInfo>> FindResourcesAsync(
        ResourceSearchCriteria criteria,
        CancellationToken cancellationToken = default);

    /// <summary>Finds the first cached resource with an exact canonical URL.</summary>
    Task<ResourceInfo?> FindByCanonicalUrlAsync(
        string canonicalUrl,
        string? packageScope = null,
        CancellationToken cancellationToken = default);

    /// <summary>Finds cached resources with the requested FHIR resource type.</summary>
    Task<IReadOnlyList<ResourceInfo>> FindByResourceTypeAsync(
        string resourceType,
        string? packageScope = null,
        CancellationToken cancellationToken = default);

    /// <summary>Reads and parses one indexed resource from the package cache.</summary>
    Task<JsonNode?> ReadResourceAsync(
        ResourceInfo resource,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Additive resource operations for <see cref="IFhirPackageManager"/>.
/// </summary>
public static class FhirPackageManagerResourceExtensions
{
    /// <inheritdoc cref="IFhirPackageResourceManager.IndexPackageAsync"/>
    public static Task<PackageIndex?> IndexPackageAsync(
        this IFhirPackageManager manager,
        PackageReference reference,
        IndexingOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RequireCapability(manager).IndexPackageAsync(
            reference,
            options,
            cancellationToken);

    /// <inheritdoc cref="IFhirPackageResourceManager.FindResourcesAsync"/>
    public static Task<IReadOnlyList<ResourceInfo>> FindResourcesAsync(
        this IFhirPackageManager manager,
        ResourceSearchCriteria criteria,
        CancellationToken cancellationToken = default) =>
        RequireCapability(manager).FindResourcesAsync(
            criteria,
            cancellationToken);

    /// <inheritdoc cref="IFhirPackageResourceManager.FindByCanonicalUrlAsync"/>
    public static Task<ResourceInfo?> FindByCanonicalUrlAsync(
        this IFhirPackageManager manager,
        string canonicalUrl,
        string? packageScope = null,
        CancellationToken cancellationToken = default) =>
        RequireCapability(manager).FindByCanonicalUrlAsync(
            canonicalUrl,
            packageScope,
            cancellationToken);

    /// <inheritdoc cref="IFhirPackageResourceManager.FindByResourceTypeAsync"/>
    public static Task<IReadOnlyList<ResourceInfo>> FindByResourceTypeAsync(
        this IFhirPackageManager manager,
        string resourceType,
        string? packageScope = null,
        CancellationToken cancellationToken = default) =>
        RequireCapability(manager).FindByResourceTypeAsync(
            resourceType,
            packageScope,
            cancellationToken);

    /// <inheritdoc cref="IFhirPackageResourceManager.ReadResourceAsync"/>
    public static Task<JsonNode?> ReadResourceAsync(
        this IFhirPackageManager manager,
        ResourceInfo resource,
        CancellationToken cancellationToken = default) =>
        RequireCapability(manager).ReadResourceAsync(
            resource,
            cancellationToken);

    private static IFhirPackageResourceManager RequireCapability(
        IFhirPackageManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return manager as IFhirPackageResourceManager
            ?? throw new NotSupportedException(
                "The package manager does not support indexed resource operations.");
    }
}
