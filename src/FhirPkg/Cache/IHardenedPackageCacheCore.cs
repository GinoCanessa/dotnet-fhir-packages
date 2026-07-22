// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Installation;
using FhirPkg.Models;

namespace FhirPkg.Cache;

internal interface IHardenedPackageCacheCore
{
    Task<PackageCacheInspection> InspectAsync(
        PackageReference reference,
        CancellationToken cancellationToken = default);

    Task<PackageIdentityValidationResult> DiscoverIdentityAsync(
        string manifestPath,
        string? directive,
        CancellationToken cancellationToken = default);
}
