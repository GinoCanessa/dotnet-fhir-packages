// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;

namespace FhirPkg;

/// <summary>
/// Manager capability for bounded URI, stream, and manifest-discovery installs.
/// </summary>
public interface IHardenedFhirPackageManager : IFhirPackageManager
{
    /// <summary>Installs URI content under an explicit expected identity.</summary>
    new Task<PackageRecord> InstallAsync(
        PackageReference expectedReference,
        Uri packageUri,
        PackageSourceInstallOptions? options,
        CancellationToken cancellationToken);

    /// <summary>Installs caller-owned stream content under an expected identity.</summary>
    new Task<PackageRecord> InstallAsync(
        PackageReference expectedReference,
        Stream packageStream,
        PackageSourceInstallOptions? options,
        CancellationToken cancellationToken);

    /// <summary>Imports URI content using its validated manifest identity.</summary>
    new Task<PackageRecord> ImportAsync(
        Uri packageUri,
        PackageSourceInstallOptions? options,
        CancellationToken cancellationToken);

    /// <summary>Imports caller-owned stream content using its validated manifest identity.</summary>
    new Task<PackageRecord> ImportAsync(
        Stream packageStream,
        PackageSourceInstallOptions? options,
        CancellationToken cancellationToken);
}
