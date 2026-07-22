// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;

namespace FhirPkg.Cache;

/// <summary>
/// Advertises a cache implementation that enforces bounded acquisition,
/// validated identities, transactional replacement, and process coordination.
/// </summary>
public interface IHardenedPackageCache : IPackageCache
{
    /// <summary>Inspects one canonical cache identity using hardened validation.</summary>
    Task<HardenedPackageCacheInspection> InspectAsync(
        PackageReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports package content whose identity is discovered from its validated manifest.
    /// </summary>
    Task<PackageRecord> ImportAsync(
        Stream tarballStream,
        InstallCacheOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Stable cache-entry states exposed by the hardened cache capability.</summary>
public enum HardenedPackageCacheState
{
    /// <summary>No cache target exists for the identity.</summary>
    Missing = 0,

    /// <summary>The cache target is valid and matches its identity.</summary>
    Valid = 1,

    /// <summary>The cache target exists but is invalid.</summary>
    Corrupt = 2
}

/// <summary>Public, path-neutral result of hardened cache inspection.</summary>
public sealed record HardenedPackageCacheInspection
{
    /// <summary>The validated state of the requested cache identity.</summary>
    public required HardenedPackageCacheState State { get; init; }

    /// <summary>Whether normal installation may transactionally repair corruption.</summary>
    public bool IsRepairable { get; init; } = true;

    /// <summary>A non-sensitive reason when <see cref="State"/> is corrupt.</summary>
    public string? CorruptionReason { get; init; }
}
