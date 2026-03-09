// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;

namespace FhirPkg.Resolution;

/// <summary>
/// Resolves the full transitive dependency closure for a FHIR package manifest
/// or restores a previously resolved closure from a lock file.
/// </summary>
public interface IDependencyResolver
{
    /// <summary>
    /// Computes the full transitive dependency closure for the given root manifest.
    /// Recursively resolves each dependency's own dependencies, handling version conflicts
    /// according to the specified strategy, and enforcing a maximum recursion depth.
    /// </summary>
    /// <param name="rootManifest">The root package manifest whose dependencies should be resolved.</param>
    /// <param name="options">
    /// Optional settings controlling conflict resolution strategy, max depth,
    /// pre-release inclusion, and FHIR release preferences.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="PackageClosure"/> containing all resolved packages and any
    /// packages that could not be resolved.
    /// </returns>
    Task<PackageClosure> ResolveAsync(
        PackageManifest rootManifest,
        DependencyResolveOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a package closure from a previously generated lock file.
    /// Verifies that each locked dependency is present in the cache, and if not,
    /// downloads and installs it from the registry.
    /// </summary>
    /// <param name="lockFile">The lock file containing exact version pinning for all dependencies.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="PackageClosure"/> reflecting the state of the restored dependencies,
    /// including any that could not be found in the registry.
    /// </returns>
    Task<PackageClosure> RestoreFromLockFileAsync(
        PackageLockFile lockFile,
        CancellationToken cancellationToken = default);
}
