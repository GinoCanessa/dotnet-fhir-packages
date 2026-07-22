// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;

namespace FhirPkg.Indexing;

internal interface IManagedPackageIndexer
{
    Task<PackageIndex> BuildIndexAsync(
        PackageRecord package,
        CancellationToken cancellationToken = default);

    void RegisterPersistedIndex(
        PackageReference reference,
        PackageIndex index);

    bool Unregister(PackageReference reference);

    void Clear();

    IReadOnlyList<ResourceInfo> FindManagedResources(
        ResourceSearchCriteria criteria);

    IReadOnlyList<ResourceInfo> FindManagedByResourceType(
        string resourceType,
        string? packageScope = null);
}
