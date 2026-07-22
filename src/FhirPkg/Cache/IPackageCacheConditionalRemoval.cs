// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;

namespace FhirPkg.Cache;

internal interface IPackageCacheConditionalRemoval
{
    Task<bool> RemoveIfUnchangedAsync(
        PackageRecord expected,
        CancellationToken cancellationToken = default);
}
