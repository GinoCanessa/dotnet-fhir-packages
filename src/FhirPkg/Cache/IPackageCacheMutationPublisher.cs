// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;

namespace FhirPkg.Cache;

internal interface IPackageCacheMutationPublisher
{
    IDisposable Subscribe(
        Action<PackageReference> packageInvalidated,
        Action cacheCleared);
}
