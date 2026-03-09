// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;

namespace FhirPkg.Resolution;

/// <summary>
/// Resolves version specifiers (exact, wildcard, latest, range) to concrete <see cref="FhirSemVer"/> versions
/// by querying a package registry or filtering a set of locally available versions.
/// </summary>
public interface IVersionResolver
{
    /// <summary>
    /// Resolves a version specifier for a given package to a concrete <see cref="FhirSemVer"/>
    /// by querying the package registry for available versions.
    /// </summary>
    /// <param name="packageId">The package identifier (e.g. "hl7.fhir.us.core").</param>
    /// <param name="versionSpecifier">
    /// A version specifier such as "4.0.1" (exact), "4.0.x" (wildcard), "latest",
    /// "^4.0.0" (range), or "current" (CI build).
    /// </param>
    /// <param name="options">Optional settings controlling pre-release inclusion and FHIR release filtering.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The resolved <see cref="FhirSemVer"/>, or <c>null</c> if the version could not be resolved
    /// (e.g. CI builds, which are handled by a separate orchestrator, or if no matching version exists).
    /// </returns>
    Task<FhirSemVer?> ResolveVersionAsync(
        string packageId,
        string versionSpecifier,
        VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a version specifier against a pre-supplied collection of available versions.
    /// This synchronous overload is used when the caller already has the version list (e.g. from cache).
    /// </summary>
    /// <param name="versionSpecifier">
    /// A version specifier such as "4.0.1" (exact), "4.0.x" (wildcard), "latest",
    /// or "^4.0.0" (range).
    /// </param>
    /// <param name="availableVersions">The collection of candidate versions to match against.</param>
    /// <param name="options">Optional settings controlling pre-release inclusion and FHIR release filtering.</param>
    /// <returns>
    /// The resolved <see cref="FhirSemVer"/>, or <c>null</c> if no matching version was found.
    /// </returns>
    FhirSemVer? ResolveVersion(
        string versionSpecifier,
        IEnumerable<FhirSemVer> availableVersions,
        VersionResolveOptions? options = null);
}
