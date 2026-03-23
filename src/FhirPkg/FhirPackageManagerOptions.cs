// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;
using FhirPkg.Registry;

namespace FhirPkg;

/// <summary>
/// Root configuration for the FHIR package manager.
/// Controls cache location, registry endpoints, HTTP behavior, checksum verification,
/// and in-memory caching parameters.
/// </summary>
public class FhirPackageManagerOptions
{
    /// <summary>
    /// Path to the local package cache directory.
    /// When <c>null</c>, the <c>PACKAGE_CACHE_FOLDER</c> environment variable is used
    /// if set; otherwise defaults to <c>~/.fhir/packages</c>.
    /// </summary>
    public string? CachePath { get; set; }

    /// <summary>
    /// Registry endpoints to query, in priority order.
    /// When empty, the default chain is constructed from <see cref="IncludeCiBuilds"/>
    /// and <see cref="IncludeHl7WebsiteFallback"/> settings.
    /// </summary>
    public List<RegistryEndpoint> Registries { get; init; } = [];

    /// <summary>
    /// Whether to include the FHIR CI build registry (<c>build.fhir.org</c>) in the
    /// default registry chain. Default: <c>true</c>.
    /// </summary>
    public bool IncludeCiBuilds { get; set; } = true;

    /// <summary>
    /// Whether to include the HL7 website (<c>hl7.org/fhir</c>) as a fallback source
    /// for core FHIR packages. Default: <c>true</c>.
    /// </summary>
    public bool IncludeHl7WebsiteFallback { get; set; } = true;

    /// <summary>
    /// HTTP timeout for individual registry requests. Default: 30 seconds.
    /// </summary>
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of HTTP redirects to follow per request. Default: 5.
    /// </summary>
    public int MaxRedirects { get; set; } = 5;

    /// <summary>
    /// Whether to verify SHA-1 checksums of downloaded tarballs against registry-provided hashes.
    /// Default: <c>true</c>.
    /// </summary>
    public bool VerifyChecksums { get; set; } = true;

    /// <summary>
    /// Maximum number of parallel registry queries during batch operations. Default: 3.
    /// </summary>
    public int MaxParallelRegistryQueries { get; set; } = 3;

    /// <summary>
    /// Maximum number of entries in the in-memory resource cache.
    /// Set to 0 to disable in-memory caching. Default: 200.
    /// </summary>
    public int ResourceCacheSize { get; set; } = 200;

    /// <summary>
    /// Controls how cached resources are returned from the in-memory cache.
    /// Default: <see cref="SafeMode.Off"/> (direct references, caller must not mutate).
    /// </summary>
    public SafeMode ResourceCacheSafeMode { get; set; } = SafeMode.Off;

    /// <summary>
    /// Known package version fixups that correct well-known errata in the FHIR package ecosystem.
    /// Keys are in the format <c>"name@version"</c>; values are the corrected version strings.
    /// </summary>
    public Dictionary<string, string> VersionFixups { get; init; } = new()
    {
        ["hl7.fhir.r4.core@4.0.0"] = "4.0.1"
    };
}

/// <summary>
/// Options controlling how individual packages are installed.
/// </summary>
public class InstallOptions
{
    /// <summary>
    /// Whether to recursively install transitive dependencies of the target package.
    /// Default: <c>false</c>.
    /// </summary>
    public bool IncludeDependencies { get; set; }

    /// <summary>
    /// Whether to overwrite the package if it is already present in the cache.
    /// Default: <c>false</c>.
    /// </summary>
    public bool OverwriteExisting { get; set; }

    /// <summary>
    /// FHIR release to prefer when resolving packages with version-specific variants.
    /// When <c>null</c>, any FHIR release is accepted.
    /// </summary>
    public FhirRelease? PreferredFhirRelease { get; set; }

    /// <summary>
    /// Whether to include pre-release versions when resolving version specifiers.
    /// Default: <c>true</c>.
    /// </summary>
    public bool AllowPreRelease { get; set; } = true;

    /// <summary>
    /// Optional progress callback for reporting download and installation status.
    /// </summary>
    public IProgress<PackageProgress>? Progress { get; set; }
}

/// <summary>
/// Options controlling how project dependency restoration is performed.
/// Extends <see cref="InstallOptions"/> with conflict resolution and lock file settings.
/// </summary>
public class RestoreOptions : InstallOptions
{
    /// <summary>
    /// Strategy for resolving version conflicts when multiple dependencies require
    /// different versions of the same package. Default: <see cref="ConflictResolutionStrategy.HighestWins"/>.
    /// </summary>
    public ConflictResolutionStrategy ConflictStrategy { get; set; } = ConflictResolutionStrategy.HighestWins;

    /// <summary>
    /// Whether to write or update the lock file (<c>fhirpkg.lock.json</c>) after successful restoration.
    /// Default: <c>true</c>.
    /// </summary>
    public bool WriteLockFile { get; set; } = true;

    /// <summary>
    /// Maximum recursion depth for transitive dependency resolution.
    /// Prevents infinite loops in circular dependency graphs. Default: 20.
    /// </summary>
    public int MaxDepth { get; set; } = 20;
}

/// <summary>
/// Options controlling how version specifiers are resolved to exact versions.
/// </summary>
public class VersionResolveOptions
{
    /// <summary>
    /// Whether to include pre-release versions when resolving. Default: <c>true</c>.
    /// </summary>
    public bool AllowPreRelease { get; set; } = true;

    /// <summary>
    /// FHIR release to restrict resolution to. When <c>null</c>, all releases are considered.
    /// </summary>
    public FhirRelease? FhirRelease { get; set; }
}
