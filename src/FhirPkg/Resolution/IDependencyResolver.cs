// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;

namespace FhirPkg.Resolution;

/// <summary>
/// Resolves the live full transitive dependency closure for a FHIR package
/// manifest using the current registry and cache state.
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
    /// A <see cref="PackageClosure"/> containing the active resolved graph and
    /// structured failures for missing versions, conflicts, depth truncation,
    /// incomplete metadata, registry failures, or an unstable graph.
    /// </returns>
    Task<PackageClosure> ResolveAsync(
        PackageManifest rootManifest,
        DependencyResolveOptions? options = null,
        CancellationToken cancellationToken = default);
}
