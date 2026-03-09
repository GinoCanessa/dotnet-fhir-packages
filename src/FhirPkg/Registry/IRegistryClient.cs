// Copyright (c) Gino Canessa. Licensed under the MIT License. See LICENSE in the project root.

using FhirPkg.Models;

namespace FhirPkg.Registry;

/// <summary>
/// Defines the contract for interacting with a FHIR package registry.
/// </summary>
/// <remarks>
/// Each implementation targets a specific registry type (FHIR NPM, CI build, HL7 website,
/// or standard NPM) and handles the protocol-specific details of searching, resolving,
/// downloading, and publishing packages.
/// </remarks>
public interface IRegistryClient
{
    /// <summary>
    /// Gets the registry endpoint this client communicates with.
    /// </summary>
    RegistryEndpoint Endpoint { get; }

    /// <summary>
    /// Gets the package name types this client can resolve.
    /// </summary>
    IReadOnlyList<PackageNameType> SupportedNameTypes { get; }

    /// <summary>
    /// Gets the version specifier types this client can resolve.
    /// </summary>
    IReadOnlyList<VersionType> SupportedVersionTypes { get; }

    /// <summary>
    /// Searches the registry catalog for packages matching the specified criteria.
    /// </summary>
    /// <param name="criteria">The search criteria including name, canonical, and FHIR version filters.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of catalog entries matching the criteria, or an empty list if none found.</returns>
    Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        PackageSearchCriteria criteria,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the full listing of a package, including all published versions and metadata.
    /// </summary>
    /// <param name="packageId">The package identifier (e.g., <c>hl7.fhir.r4.core</c>).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The package listing, or <see langword="null"/> if the package does not exist.</returns>
    Task<PackageListing?> GetPackageListingAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a package directive to an exact version and download location.
    /// </summary>
    /// <param name="directive">The parsed package directive containing the package ID and version specifier.</param>
    /// <param name="options">Optional settings controlling pre-release inclusion and FHIR release filtering.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A resolved directive with exact version and tarball URI, or <see langword="null"/> if resolution fails.</returns>
    Task<ResolvedDirective?> ResolveAsync(
        PackageDirective directive,
        VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a resolved package as a tarball stream.
    /// </summary>
    /// <param name="resolved">The resolved directive containing the tarball URI.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// A download result containing the tarball stream, or <see langword="null"/> if the package is not found.
    /// The caller must dispose the result when finished.
    /// </returns>
    Task<PackageDownloadResult?> DownloadAsync(
        ResolvedDirective resolved,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a package tarball to the registry.
    /// </summary>
    /// <param name="reference">The package reference (name and version) to publish.</param>
    /// <param name="tarballStream">The stream containing the package tarball (<c>.tgz</c>).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A result indicating whether the publish operation succeeded.</returns>
    Task<PublishResult> PublishAsync(
        PackageReference reference,
        Stream tarballStream,
        CancellationToken cancellationToken = default);
}
