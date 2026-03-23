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

    /// <summary>The registry endpoint that resolved this package.</summary>
    public RegistryEndpoint? SourceRegistry { get; init; }

    /// <summary>The date this version was published.</summary>
    public DateTime? PublicationDate { get; init; }
}
