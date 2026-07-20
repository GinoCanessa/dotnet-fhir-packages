// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Registry;

namespace FhirPkg.Models;

/// <summary>
/// Stable categories for dependency-closure failures.
/// </summary>
public enum DependencyResolutionFailureCode
{
    /// <summary>A requested package version could not be resolved.</summary>
    PackageNotFound,

    /// <summary>Multiple active dependency edges selected incompatible versions.</summary>
    VersionConflict,

    /// <summary>An active dependency edge exceeded the configured root-relative depth.</summary>
    DepthLimitExceeded,

    /// <summary>The selected package version did not provide authoritative dependency metadata.</summary>
    MetadataUnavailable,

    /// <summary>Registry failures prevented an authoritative version-resolution result.</summary>
    RegistryUnavailable,

    /// <summary>The active graph repeated a prior state and could not reach a stable closure.</summary>
    UnstableResolution,
}

/// <summary>
/// Describes one structured failure in an active dependency closure.
/// </summary>
public sealed record DependencyResolutionFailure
{
    /// <summary>Gets the stable failure category.</summary>
    public required DependencyResolutionFailureCode Code { get; init; }

    /// <summary>Gets the affected package identifier.</summary>
    public required string PackageId { get; init; }

    /// <summary>Gets a safe human-readable explanation.</summary>
    public required string Message { get; init; }

    /// <summary>Gets the requested version specifier, when the failure belongs to one edge.</summary>
    public string? VersionSpecifier { get; init; }

    /// <summary>Gets the selected exact version, when dependency metadata could not be loaded.</summary>
    public string? SelectedVersion { get; init; }

    /// <summary>Gets the package identifier that declared the failing edge.</summary>
    public string? ParentPackageId { get; init; }

    /// <summary>Gets the exact parent version that declared the failing edge.</summary>
    public string? ParentVersion { get; init; }

    /// <summary>
    /// Gets the zero-based dependency depth, where direct root dependencies are depth zero.
    /// </summary>
    public int? Depth { get; init; }

    /// <summary>Gets the configured maximum dependency depth for a depth-limit failure.</summary>
    public int? MaxDepth { get; init; }

    /// <summary>Gets the distinct exact versions participating in a conflict.</summary>
    public IReadOnlyList<string> RequestedVersions { get; init; } = [];

    /// <summary>Gets sanitized registry-attempt failures, when available.</summary>
    public IReadOnlyList<RegistryAttemptFailure> RegistryFailures { get; init; } = [];
}
