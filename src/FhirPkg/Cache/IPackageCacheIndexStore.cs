// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Indexing;
using FhirPkg.Models;

namespace FhirPkg.Cache;

internal interface IPackageCacheIndexStore
{
    Task<IReadOnlyList<PackageRecord>>
        ListPackagesForIndexingAsync(
            string? packageIdFilter = null,
            string? versionFilter = null,
            CancellationToken cancellationToken = default);

    Task<PackageIndex?> GetOrCreateIndexAsync(
        PackageReference reference,
        bool forceReindex,
        Func<PackageRecord, CancellationToken, Task<PackageIndex>> generator,
        CancellationToken cancellationToken = default,
        Action<PackageRecord, PackageIndex>? indexReady = null);
}
