// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Registry;

namespace FhirPkg.Models;

/// <summary>
/// Result of resolving a directive to an exact version and download location.
/// </summary>
public record ResolvedDirective
{
    /// <summary>The resolved package identity (name + exact version).</summary>
    public required PackageReference Reference { get; init; }

    /// <summary>The URI to download the package tarball from.</summary>
    public required Uri TarballUri { get; init; }

    /// <summary>The SHA-1 hash of the tarball for integrity verification.</summary>
    public string? ShaSum { get; init; }

    /// <summary>
    /// The SHA-256 hash of the package tarball, if provided by the registry.
    /// When available, this is preferred over <see cref="ShaSum"/> for integrity verification.
    /// </summary>
    public string? Sha256Sum { get; init; }

    /// <summary>
    /// The Subresource Integrity value supplied by the selected registry source.
    /// </summary>
    public string? Integrity { get; init; }

    /// <summary>
    /// Credential-free registry-origin provenance for the source that resolved this package.
    /// </summary>
    public RegistryEndpoint? SourceRegistry { get; init; }

    internal IRegistryClient? SourceClient { get; init; }

    /// <summary>The date this version was published.</summary>
    public DateTime? PublicationDate { get; init; }

    /// <summary>Dependencies declared by the selected source candidate.</summary>
    public IReadOnlyDictionary<string, string>? Dependencies { get; init; }

    /// <summary>FHIR versions declared by the selected source candidate.</summary>
    public IReadOnlyList<string>? FhirVersions { get; init; }
}
