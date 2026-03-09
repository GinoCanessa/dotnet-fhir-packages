// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Indexing;

namespace FhirPkg.Models;

/// <summary>
/// Describes a package that has been installed (or retrieved from cache) on the local system.
/// </summary>
public record PackageRecord
{
    /// <summary>The package identity (name and version).</summary>
    public required PackageReference Reference { get; init; }

    /// <summary>Full path to the package root directory (containing the package/ subfolder).</summary>
    public required string DirectoryPath { get; init; }

    /// <summary>Full path to the package content directory (the package/ subfolder).</summary>
    public required string ContentPath { get; init; }

    /// <summary>The deserialized package.json manifest.</summary>
    public required PackageManifest Manifest { get; init; }

    /// <summary>The resource index, if available.</summary>
    public PackageIndex? Index { get; init; }

    /// <summary>When the package was installed into the cache.</summary>
    public DateTimeOffset? InstalledAt { get; init; }

    /// <summary>Expanded size in bytes on disk.</summary>
    public long? SizeBytes { get; init; }
}
