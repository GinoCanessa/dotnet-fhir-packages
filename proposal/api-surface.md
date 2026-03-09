# API Surface Design

This document defines the complete public API for the FHIR Package Management library. It covers all interfaces, classes, records, enums, and configuration types.

## Table of Contents

- [Namespace Structure](#namespace-structure)
- [Core Interfaces](#core-interfaces)
- [Data Models](#data-models)
- [Enumerations](#enumerations)
- [Registry Clients](#registry-clients)
- [Cache Layer](#cache-layer)
- [Dependency Resolution](#dependency-resolution)
- [Version Resolution](#version-resolution)
- [Directive Parsing](#directive-parsing)
- [Resource Indexing](#resource-indexing)
- [Configuration](#configuration)
- [Logging & Events](#logging--events)
- [Usage Examples](#usage-examples)

---

## Namespace Structure

```
FhirPkg
├── FhirPkg                  # Core orchestrator, options, DI extensions
├── FhirPkg.Models           # Data models: manifests, directives, records
├── FhirPkg.Registry         # Registry client interfaces and implementations
├── FhirPkg.Cache            # Cache interfaces and implementations
├── FhirPkg.Resolution       # Dependency and version resolution
├── FhirPkg.Indexing         # Resource indexing and discovery
└── FhirPkg.Cli             # CLI tool (separate project)
```

---

## Core Interfaces

### `IFhirPackageManager`

The primary entry point for all package management operations.

```csharp
namespace FhirPkg;

/// <summary>
/// Primary interface for FHIR package management operations.
/// Coordinates registry queries, cache operations, and dependency resolution.
/// </summary>
public interface IFhirPackageManager
{
    /// <summary>
    /// Installs a package by directive (e.g., "hl7.fhir.us.core#6.1.0").
    /// Resolves the directive, downloads if not cached, and extracts to the local cache.
    /// </summary>
    /// <param name="directive">Package directive (name#version or name@version).</param>
    /// <param name="options">Installation options (dependencies, overwrite, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached package record, or null if the package could not be resolved.</returns>
    Task<PackageRecord?> InstallAsync(
        string directive,
        InstallOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs multiple packages in parallel.
    /// </summary>
    Task<IReadOnlyList<PackageInstallResult>> InstallManyAsync(
        IEnumerable<string> directives,
        InstallOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores all dependencies declared in a project's package.json manifest.
    /// Performs recursive dependency resolution and produces a lock file.
    /// </summary>
    /// <param name="projectPath">Path to the project directory containing package.json.</param>
    /// <param name="options">Restore options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The complete package closure (all resolved + missing dependencies).</returns>
    Task<PackageClosure> RestoreAsync(
        string projectPath,
        RestoreOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all packages currently in the local cache.
    /// </summary>
    /// <param name="filter">Optional filter by package ID prefix.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<PackageRecord>> ListCachedAsync(
        string? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a package from the local cache.
    /// </summary>
    /// <param name="directive">Package directive identifying the cached package.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the package was found and removed.</returns>
    Task<bool> RemoveAsync(
        string directive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all packages from the local cache.
    /// </summary>
    Task<int> CleanCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches package registries for packages matching the given criteria.
    /// </summary>
    Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        PackageSearchCriteria criteria,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all available versions of a package from configured registries.
    /// </summary>
    Task<PackageListing?> GetPackageListingAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a directive to an exact version without downloading.
    /// </summary>
    Task<ResolvedDirective?> ResolveAsync(
        string directive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a package tarball to a registry.
    /// </summary>
    Task<PublishResult> PublishAsync(
        string tarballPath,
        RegistryEndpoint registry,
        CancellationToken cancellationToken = default);
}
```

---

### `IRegistryClient`

Interface for querying and downloading from package registries.

```csharp
namespace FhirPkg.Registry;

/// <summary>
/// Interface for communicating with a FHIR package registry.
/// </summary>
public interface IRegistryClient
{
    /// <summary>The registry endpoint this client connects to.</summary>
    RegistryEndpoint Endpoint { get; }

    /// <summary>The types of package names this registry supports resolving.</summary>
    IReadOnlyList<PackageNameType> SupportedNameTypes { get; }

    /// <summary>The types of version specifiers this registry supports.</summary>
    IReadOnlyList<VersionType> SupportedVersionTypes { get; }

    /// <summary>
    /// Searches the registry catalog for packages matching the criteria.
    /// </summary>
    Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        PackageSearchCriteria criteria,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the full package listing (all versions) for a package.
    /// </summary>
    Task<PackageListing?> GetPackageListingAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a parsed directive to a specific version and download URI.
    /// </summary>
    Task<ResolvedDirective?> ResolveAsync(
        PackageDirective directive,
        VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a package tarball as a stream.
    /// </summary>
    Task<PackageDownloadResult?> DownloadAsync(
        ResolvedDirective resolved,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a package to the registry.
    /// </summary>
    Task<PublishResult> PublishAsync(
        PackageReference reference,
        Stream tarballStream,
        CancellationToken cancellationToken = default);
}
```

---

### `IPackageCache`

Interface for local package storage and retrieval.

```csharp
namespace FhirPkg.Cache;

/// <summary>
/// Interface for the local FHIR package cache (~/.fhir/packages).
/// </summary>
public interface IPackageCache
{
    /// <summary>Root directory of the cache.</summary>
    string CacheDirectory { get; }

    /// <summary>Checks if a specific package version is installed in the cache.</summary>
    Task<bool> IsInstalledAsync(
        PackageReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the record for a cached package, or null if not installed.</summary>
    Task<PackageRecord?> GetPackageAsync(
        PackageReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>Lists all packages in the cache, optionally filtered.</summary>
    Task<IReadOnlyList<PackageRecord>> ListPackagesAsync(
        string? packageIdFilter = null,
        string? versionFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs a package from a tarball stream into the cache.
    /// Performs atomic extraction (temp dir → move).
    /// </summary>
    Task<PackageRecord> InstallAsync(
        PackageReference reference,
        Stream tarballStream,
        InstallCacheOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a package from the cache.</summary>
    Task<bool> RemoveAsync(
        PackageReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>Removes all packages from the cache.</summary>
    Task<int> ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the package manifest (package.json) from a cached package.</summary>
    Task<PackageManifest?> ReadManifestAsync(
        PackageReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the resource index for a cached package.</summary>
    Task<PackageIndex?> GetIndexAsync(
        PackageReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a file content from within a cached package.</summary>
    Task<string?> GetFileContentAsync(
        PackageReference reference,
        string relativePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the content folder path for a cached package.
    /// Returns null if not installed.
    /// </summary>
    string? GetPackageContentPath(PackageReference reference);

    /// <summary>Reads or creates the cache metadata (packages.ini).</summary>
    Task<CacheMetadata> GetMetadataAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Updates the cache metadata after an install or remove.</summary>
    Task UpdateMetadataAsync(
        PackageReference reference,
        CacheMetadataEntry entry,
        CancellationToken cancellationToken = default);
}
```

---

### `IDependencyResolver`

Interface for resolving transitive dependency trees.

```csharp
namespace FhirPkg.Resolution;

/// <summary>
/// Resolves the complete transitive dependency tree for a package or project.
/// </summary>
public interface IDependencyResolver
{
    /// <summary>
    /// Resolves all dependencies declared in a package manifest recursively.
    /// Returns a closure containing all resolved and missing packages.
    /// </summary>
    Task<PackageClosure> ResolveAsync(
        PackageManifest rootManifest,
        DependencyResolveOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves dependencies from an existing lock file, only fetching
    /// packages that are not already cached.
    /// </summary>
    Task<PackageClosure> RestoreFromLockFileAsync(
        PackageLockFile lockFile,
        CancellationToken cancellationToken = default);
}
```

---

### `IVersionResolver`

Interface for resolving version specifiers to exact versions.

```csharp
namespace FhirPkg.Resolution;

/// <summary>
/// Resolves version specifiers (wildcards, ranges, tags) to exact versions.
/// </summary>
public interface IVersionResolver
{
    /// <summary>
    /// Resolves a version specifier against available versions from registries.
    /// </summary>
    Task<FhirSemVer?> ResolveVersionAsync(
        string packageId,
        string versionSpecifier,
        VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a version specifier against a known list of available versions.
    /// </summary>
    FhirSemVer? ResolveVersion(
        string versionSpecifier,
        IEnumerable<FhirSemVer> availableVersions,
        VersionResolveOptions? options = null);
}
```

---

### `IPackageIndexer`

Interface for indexing resources within packages.

```csharp
namespace FhirPkg.Indexing;

/// <summary>
/// Indexes FHIR resources within packages for efficient discovery.
/// </summary>
public interface IPackageIndexer
{
    /// <summary>
    /// Builds or reads the resource index for a cached package.
    /// If .index.json exists, reads it. Otherwise, scans and indexes all resources.
    /// </summary>
    Task<PackageIndex> IndexPackageAsync(
        string packageContentPath,
        IndexingOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds resources across all indexed packages.
    /// </summary>
    IReadOnlyList<ResourceInfo> FindResources(
        ResourceSearchCriteria criteria);

    /// <summary>
    /// Finds a single resource by canonical URL across all indexed packages.
    /// </summary>
    ResourceInfo? FindByCanonicalUrl(string canonicalUrl);

    /// <summary>
    /// Finds resources by resource type across all indexed packages.
    /// </summary>
    IReadOnlyList<ResourceInfo> FindByResourceType(
        string resourceType,
        string? packageScope = null);
}
```

---

## Data Models

### Package Identity

```csharp
namespace FhirPkg.Models;

/// <summary>
/// Immutable reference to a specific package version.
/// </summary>
/// <param name="Name">Package name (e.g., "hl7.fhir.us.core").</param>
/// <param name="Version">Exact version string (e.g., "6.1.0").</param>
/// <param name="Scope">Optional NPM scope (e.g., "@hl7").</param>
public readonly record struct PackageReference(
    string Name,
    string? Version = null,
    string? Scope = null)
{
    /// <summary>Directive in FHIR format: "name#version".</summary>
    public string FhirDirective => Version is null ? Name : $"{Name}#{Version}";

    /// <summary>Directive in NPM format: "name@version".</summary>
    public string NpmDirective => Version is null ? Name : $"{Name}@{Version}";

    /// <summary>Cache directory name: "name#version".</summary>
    public string CacheDirectoryName => FhirDirective;

    /// <summary>Whether this reference includes a version.</summary>
    public bool HasVersion => Version is not null;

    /// <summary>Parses a directive string (accepts both # and @ separators).</summary>
    public static PackageReference Parse(string directive);

    /// <summary>Implicit conversion from a "name#version" or "name@version" string.</summary>
    public static implicit operator PackageReference(string directive);

    /// <summary>Implicit conversion from a KeyValuePair (dependency entry).</summary>
    public static implicit operator PackageReference(KeyValuePair<string, string> dependency);
}

/// <summary>
/// A parsed package directive with classified name type and version type.
/// </summary>
public record PackageDirective
{
    /// <summary>Original directive string as provided by the user.</summary>
    public required string RawDirective { get; init; }

    /// <summary>Parsed package identifier (without version).</summary>
    public required string PackageId { get; init; }

    /// <summary>Requested version string (may be wildcard, tag, etc.).</summary>
    public string? RequestedVersion { get; init; }

    /// <summary>NPM alias if the directive uses alias syntax.</summary>
    public string? Alias { get; init; }

    /// <summary>Classification of the package name.</summary>
    public required PackageNameType NameType { get; init; }

    /// <summary>Classification of the version specifier.</summary>
    public required VersionType VersionType { get; init; }

    /// <summary>The resolved exact version (populated after resolution).</summary>
    public FhirSemVer? ResolvedVersion { get; init; }

    /// <summary>For partial core names, the expanded full package names.</summary>
    public IReadOnlyList<string>? ExpandedPackageIds { get; init; }

    /// <summary>For CI builds, the branch name (from "current$branch").</summary>
    public string? CiBranch { get; init; }

    /// <summary>Creates a PackageReference from this directive's resolved version.</summary>
    public PackageReference ToReference();

    /// <summary>Parses a raw directive string into a classified PackageDirective.</summary>
    public static PackageDirective Parse(string directive);
}
```

---

### Package Manifest

```csharp
namespace FhirPkg.Models;

/// <summary>
/// Represents the package.json manifest from a FHIR package.
/// Combines NPM-standard fields with FHIR-specific extensions.
/// </summary>
public record PackageManifest
{
    // --- Required fields ---
    public required string Name { get; init; }
    public required string Version { get; init; }

    // --- NPM-standard fields ---
    public string? Description { get; init; }
    public string? License { get; init; }
    public string? Author { get; init; }
    public string? Homepage { get; init; }
    public IReadOnlyDictionary<string, string>? Dependencies { get; init; }
    public IReadOnlyDictionary<string, string>? DevDependencies { get; init; }
    public IReadOnlyList<string>? Keywords { get; init; }
    public NpmRepository? Repository { get; init; }
    public NpmDistribution? Distribution { get; init; }
    public IReadOnlyDictionary<string, string>? DistTags { get; init; }

    // --- FHIR-specific fields ---
    public string? Canonical { get; init; }
    public IReadOnlyList<string>? FhirVersions { get; init; }
    public string? Type { get; init; }
    public string? Date { get; init; }
    public string? Title { get; init; }
    public string? Jurisdiction { get; init; }

    /// <summary>Parsed semantic version.</summary>
    public FhirSemVer SemVer => FhirSemVer.Parse(Version);

    /// <summary>Convenience: gets the FHIR release sequence from fhirVersions or dependencies.</summary>
    public FhirRelease? InferredFhirRelease { get; }
}

/// <summary>NPM distribution metadata.</summary>
public record NpmDistribution(string? ShaSum, string? TarballUrl);

/// <summary>NPM repository metadata.</summary>
public record NpmRepository(string? Type, string? Url, string? Directory);
```

---

### Version Model

```csharp
namespace FhirPkg.Models;

/// <summary>
/// FHIR-aware semantic version with support for FHIR pre-release tag hierarchy.
/// Handles parsing, comparison, wildcard matching, and range evaluation.
/// </summary>
public sealed class FhirSemVer : IComparable<FhirSemVer>, IEquatable<FhirSemVer>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? PreRelease { get; }
    public string? BuildMetadata { get; }

    /// <summary>Whether this version contains wildcard segments.</summary>
    public bool IsWildcard { get; }

    /// <summary>Whether this is a pre-release version.</summary>
    public bool IsPreRelease => PreRelease is not null;

    /// <summary>The FHIR pre-release category for ordering.</summary>
    public FhirPreReleaseType PreReleaseType { get; }

    /// <summary>Parses a version string. Supports exact, wildcard, and pre-release formats.</summary>
    public static FhirSemVer Parse(string version);

    /// <summary>Tries to parse a version string. Returns false if invalid.</summary>
    public static bool TryParse(string version, out FhirSemVer? result);

    /// <summary>
    /// Tests whether this version satisfies a version specifier.
    /// Supports exact match, wildcards, and ranges.
    /// </summary>
    public bool Satisfies(string versionSpecifier);

    /// <summary>
    /// Tests whether this version satisfies another version (including wildcard matching).
    /// </summary>
    public bool Satisfies(FhirSemVer other);

    /// <summary>
    /// Finds the maximum version from a collection that satisfies the given specifier.
    /// Equivalent to npm's semver.maxSatisfying().
    /// </summary>
    public static FhirSemVer? MaxSatisfying(
        IEnumerable<FhirSemVer> versions,
        string specifier,
        bool includePreRelease = false);

    /// <summary>
    /// Evaluates a SemVer range expression (^, ~, -, |).
    /// </summary>
    public static IEnumerable<FhirSemVer> SatisfyingRange(
        IEnumerable<FhirSemVer> versions,
        string rangeExpression);

    // IComparable<FhirSemVer>, IEquatable<FhirSemVer>, operators
    public int CompareTo(FhirSemVer? other);
    public bool Equals(FhirSemVer? other);
    public static bool operator >(FhirSemVer left, FhirSemVer right);
    public static bool operator <(FhirSemVer left, FhirSemVer right);
    public static bool operator >=(FhirSemVer left, FhirSemVer right);
    public static bool operator <=(FhirSemVer left, FhirSemVer right);

    public override string ToString();
}
```

---

### Package Resolution Results

```csharp
namespace FhirPkg.Models;

/// <summary>
/// Result of resolving a directive to an exact version and download location.
/// </summary>
public record ResolvedDirective
{
    public required PackageReference Reference { get; init; }
    public required Uri TarballUri { get; init; }
    public string? ShaSum { get; init; }
    public RegistryEndpoint? SourceRegistry { get; init; }
    public DateTime? PublicationDate { get; init; }
}

/// <summary>
/// Record of a package installed in the local cache.
/// </summary>
public record PackageRecord
{
    public required PackageReference Reference { get; init; }
    public required string DirectoryPath { get; init; }
    public required string ContentPath { get; init; }
    public required PackageManifest Manifest { get; init; }
    public PackageIndex? Index { get; init; }
    public DateTime? InstalledAt { get; init; }
    public long? SizeBytes { get; init; }
}

/// <summary>
/// Result of installing a single package (part of a batch operation).
/// </summary>
public record PackageInstallResult
{
    public required string Directive { get; init; }
    public PackageRecord? Package { get; init; }
    public PackageInstallStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Complete resolved dependency tree.
/// </summary>
public record PackageClosure
{
    public required DateTime Timestamp { get; init; }
    public required IReadOnlyDictionary<string, PackageReference> Resolved { get; init; }
    public required IReadOnlyDictionary<string, string> Missing { get; init; }
    public bool IsComplete => Missing.Count == 0;
}

/// <summary>
/// Lock file structure (fhirpkg.lock.json).
/// </summary>
public record PackageLockFile
{
    public required DateTime Updated { get; init; }
    public required IReadOnlyDictionary<string, string> Dependencies { get; init; }
    public IReadOnlyDictionary<string, string>? Missing { get; init; }

    public static PackageLockFile Load(string path);
    public void Save(string path);
}

/// <summary>
/// A full package listing from a registry (all versions).
/// </summary>
public record PackageListing
{
    public required string PackageId { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, string>? DistTags { get; init; }
    public required IReadOnlyDictionary<string, PackageVersionInfo> Versions { get; init; }

    /// <summary>Gets the version tagged as "latest".</summary>
    public string? LatestVersion => DistTags?.GetValueOrDefault("latest");
}

/// <summary>
/// Version-level metadata from a registry listing.
/// </summary>
public record PackageVersionInfo
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
    public string? FhirVersion { get; init; }
    public NpmDistribution? Distribution { get; init; }
    public string? Canonical { get; init; }
    public string? Kind { get; init; }
    public DateTime? PublicationDate { get; init; }
    public int? ResourceCount { get; init; }
    public string? License { get; init; }
    public string? Url { get; init; }
    public IReadOnlyDictionary<string, string>? Dependencies { get; init; }
}

/// <summary>
/// Entry from a registry catalog search.
/// </summary>
public record CatalogEntry
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? FhirVersion { get; init; }
    public string? Version { get; init; }
    public string? Canonical { get; init; }
    public string? Kind { get; init; }
    public DateTime? Date { get; init; }
    public string? Url { get; init; }
    public int? ResourceCount { get; init; }
}

/// <summary>
/// Result of a publish operation.
/// </summary>
public record PublishResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public HttpStatusCode StatusCode { get; init; }
}
```

---

### Package Download Result

```csharp
namespace FhirPkg.Models;

/// <summary>
/// Result of downloading a package tarball.
/// </summary>
public record PackageDownloadResult : IAsyncDisposable
{
    public required Stream Content { get; init; }
    public required string ContentType { get; init; }
    public long? ContentLength { get; init; }
    public string? ShaSum { get; init; }

    public ValueTask DisposeAsync();
}
```

---

### Resource Indexing Models

```csharp
namespace FhirPkg.Indexing;

/// <summary>
/// Index of all resources in a package.
/// </summary>
public record PackageIndex
{
    public int IndexVersion { get; init; } = 2;
    public DateTime? Date { get; init; }
    public required IReadOnlyList<ResourceIndexEntry> Files { get; init; }
}

/// <summary>
/// A single resource entry in a package index.
/// </summary>
public record ResourceIndexEntry
{
    public required string Filename { get; init; }
    public required string ResourceType { get; init; }
    public string? Id { get; init; }
    public string? Url { get; init; }
    public string? Version { get; init; }
    public string? Name { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }

    // StructureDefinition-specific fields
    public string? SdKind { get; init; }
    public string? SdDerivation { get; init; }
    public string? SdType { get; init; }
    public string? SdBaseDefinition { get; init; }
    public bool? SdAbstract { get; init; }
    public string? SdFlavor { get; init; }
    public bool? HasSnapshot { get; init; }
    public bool? HasExpansion { get; init; }
}

/// <summary>
/// Aggregated resource information for querying across packages.
/// </summary>
public record ResourceInfo
{
    public required string ResourceType { get; init; }
    public string? Id { get; init; }
    public string? Url { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? PackageName { get; init; }
    public string? PackageVersion { get; init; }
    public string? FilePath { get; init; }
    public string? SdFlavor { get; init; }
}

/// <summary>
/// Criteria for searching resources across packages.
/// </summary>
public record ResourceSearchCriteria
{
    /// <summary>Resource type or canonical URL to search for.</summary>
    public string? Key { get; init; }

    /// <summary>Filter by specific resource types.</summary>
    public IReadOnlyList<string>? ResourceTypes { get; init; }

    /// <summary>
    /// Filter by StructureDefinition flavor
    /// ("Resource", "Type", "Profile", "Extension", "Logical").
    /// </summary>
    public IReadOnlyList<string>? SdFlavors { get; init; }

    /// <summary>Restrict search to a specific package ("name|version").</summary>
    public string? PackageScope { get; init; }

    /// <summary>Maximum number of results.</summary>
    public int? Limit { get; init; }
}
```

---

### Cache Metadata

```csharp
namespace FhirPkg.Cache;

/// <summary>
/// Represents the packages.ini cache metadata file.
/// </summary>
public record CacheMetadata
{
    public int CacheVersion { get; init; } = 3;
    public IReadOnlyDictionary<string, CacheMetadataEntry> Packages { get; init; }
        = new Dictionary<string, CacheMetadataEntry>();
}

/// <summary>
/// Metadata for a single cached package entry.
/// </summary>
public record CacheMetadataEntry
{
    public required DateTime DownloadDateTime { get; init; }
    public long? SizeBytes { get; init; }
}
```

---

### CI Build Models

```csharp
namespace FhirPkg.Models;

/// <summary>
/// A record from the build.fhir.org QA index (qas.json).
/// </summary>
public record CiBuildRecord
{
    public string? Url { get; init; }
    public string? Name { get; init; }
    public string? Title { get; init; }
    public required string PackageId { get; init; }
    public string? IgVersion { get; init; }
    public required string Date { get; init; }
    public string? DateISO8601 { get; init; }
    public required string Repo { get; init; }
    public string? FhirVersion { get; init; }
    public int? Errors { get; init; }
    public int? Warnings { get; init; }

    /// <summary>Extracts the GitHub org/repo/branch from the Repo field.</summary>
    public (string Org, string RepoName, string Branch) ParseRepo();
}

/// <summary>
/// A CI build package manifest (from package.manifest.json).
/// </summary>
public record CiBuildManifest
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Date { get; init; }
    public IReadOnlyList<string>? FhirVersions { get; init; }
    public string? Jurisdiction { get; init; }
}
```

---

## Enumerations

```csharp
namespace FhirPkg.Models;

/// <summary>Classification of a package name.</summary>
public enum PackageNameType
{
    /// <summary>e.g., hl7.fhir.r4.core</summary>
    CoreFull,
    /// <summary>e.g., hl7.fhir.r4 (must be expanded before resolution)</summary>
    CorePartial,
    /// <summary>e.g., hl7.fhir.uv.extensions.r4</summary>
    GuideWithFhirSuffix,
    /// <summary>e.g., hl7.fhir.us.core</summary>
    GuideWithoutSuffix,
    /// <summary>e.g., us.nlm.vsac (non-HL7)</summary>
    NonHl7Guide
}

/// <summary>Classification of a version specifier.</summary>
public enum VersionType
{
    /// <summary>Fully qualified version: "4.0.1", "6.0.0-ballot1".</summary>
    Exact,
    /// <summary>Version with wildcards: "4.0.x", "4.*".</summary>
    Wildcard,
    /// <summary>The "latest" tag or empty/null version.</summary>
    Latest,
    /// <summary>A SemVer range expression: "^3.0.1", "~3.0.1".</summary>
    Range,
    /// <summary>The "current" tag (CI build, default branch).</summary>
    CiBuild,
    /// <summary>The "current$branch" tag (CI build, specific branch).</summary>
    CiBuildBranch,
    /// <summary>The "dev" tag (local build).</summary>
    LocalBuild
}

/// <summary>FHIR pre-release tag types, ordered from highest to lowest priority.</summary>
public enum FhirPreReleaseType
{
    Release = 0,
    Ballot = 1,
    Draft = 2,
    Snapshot = 3,
    CiBuild = 4,
    Other = 5
}

/// <summary>FHIR release sequences.</summary>
public enum FhirRelease
{
    DSTU2,
    STU3,
    R4,
    R4B,
    R5,
    R6
}

/// <summary>Known FHIR core package types.</summary>
public enum CorePackageType
{
    Core,
    Expansions,
    Examples,
    Search,
    CoreXml,
    Elements
}

/// <summary>Result status for a package install operation.</summary>
public enum PackageInstallStatus
{
    Installed,
    AlreadyCached,
    Failed,
    NotFound
}

/// <summary>Types of package registries.</summary>
public enum RegistryType
{
    /// <summary>FHIR NPM registry (packages.fhir.org, packages2.fhir.org).</summary>
    FhirNpm,
    /// <summary>CI build server (build.fhir.org).</summary>
    FhirCiBuild,
    /// <summary>Direct HTTP download (hl7.org/fhir).</summary>
    FhirHttp,
    /// <summary>Standard NPM registry (registry.npmjs.org).</summary>
    Npm
}
```

---

## Registry Clients

### Implementations

```csharp
namespace FhirPkg.Registry;

/// <summary>
/// Client for FHIR NPM registries (packages.fhir.org, packages2.fhir.org).
/// Supports catalog search, version listing, download, and publish.
/// </summary>
public class FhirNpmRegistryClient : IRegistryClient { }

/// <summary>
/// Client for CI builds from build.fhir.org.
/// Resolves IG and Core CI packages using qas.json and fixed URL patterns.
/// </summary>
public class FhirCiBuildClient : IRegistryClient { }

/// <summary>
/// Client for the HL7 website (hl7.org/fhir) as a fallback for core packages.
/// </summary>
public class Hl7WebsiteClient : IRegistryClient { }

/// <summary>
/// Client for standard NPM registries (registry.npmjs.org or custom).
/// </summary>
public class NpmRegistryClient : IRegistryClient { }

/// <summary>
/// Wraps multiple registry clients and tries them in order.
/// Falls back to the next client on failure.
/// </summary>
public class RedundantRegistryClient : IRegistryClient
{
    public RedundantRegistryClient(IEnumerable<IRegistryClient> clients);
    public RedundantRegistryClient(params IRegistryClient[] clients);
}
```

---

### Registry Endpoint Configuration

```csharp
namespace FhirPkg.Registry;

/// <summary>
/// Configuration for a package registry endpoint.
/// </summary>
public record RegistryEndpoint
{
    /// <summary>Base URL of the registry.</summary>
    public required string Url { get; init; }

    /// <summary>Type of registry.</summary>
    public required RegistryType Type { get; init; }

    /// <summary>Authentication header value (e.g., "Bearer token123").</summary>
    public string? AuthHeaderValue { get; init; }

    /// <summary>Custom HTTP headers to include in all requests.</summary>
    public IReadOnlyList<(string Name, string Value)>? CustomHeaders { get; init; }

    /// <summary>Custom User-Agent string.</summary>
    public string? UserAgent { get; init; }

    // --- Well-known endpoints ---
    public static RegistryEndpoint FhirPrimary { get; }
        // = new() { Url = "https://packages.fhir.org/", Type = RegistryType.FhirNpm };

    public static RegistryEndpoint FhirSecondary { get; }
        // = new() { Url = "https://packages2.fhir.org/packages", Type = RegistryType.FhirNpm };

    public static RegistryEndpoint FhirCiBuild { get; }
        // = new() { Url = "https://build.fhir.org/", Type = RegistryType.FhirCiBuild };

    public static RegistryEndpoint Hl7Website { get; }
        // = new() { Url = "https://hl7.org/fhir/", Type = RegistryType.FhirHttp };

    public static RegistryEndpoint NpmPublic { get; }
        // = new() { Url = "https://registry.npmjs.org/", Type = RegistryType.Npm };

    /// <summary>Default registry chain for published packages.</summary>
    public static IReadOnlyList<RegistryEndpoint> DefaultPublishedChain { get; }
        // = [FhirPrimary, FhirSecondary, Hl7Website];

    /// <summary>Default full registry chain including CI builds.</summary>
    public static IReadOnlyList<RegistryEndpoint> DefaultFullChain { get; }
        // = [FhirPrimary, FhirSecondary, FhirCiBuild, Hl7Website];
}
```

---

## Cache Layer

### Implementations

```csharp
namespace FhirPkg.Cache;

/// <summary>
/// Disk-based package cache at ~/.fhir/packages.
/// Thread-safe with atomic installation via temp directory.
/// </summary>
public class DiskPackageCache : IPackageCache
{
    /// <summary>Creates a cache using the default or specified directory.</summary>
    public DiskPackageCache(string? cacheDirectory = null);

    // ... implements all IPackageCache members
}

/// <summary>
/// Optional in-memory LRU cache layered on top of a disk cache.
/// Caches parsed resource JSON for frequently-accessed resources.
/// </summary>
public class MemoryResourceCache
{
    public MemoryResourceCache(int maxEntries = 200, SafeMode safeMode = SafeMode.Off);

    public T? Get<T>(string key) where T : class;
    public void Set<T>(string key, T value) where T : class;
    public void Clear();

    /// <summary>Current number of entries in the cache.</summary>
    public int Count { get; }
}

/// <summary>Controls how resources are returned from the in-memory cache.</summary>
public enum SafeMode
{
    /// <summary>Returns cached reference directly. Caller must not mutate.</summary>
    Off,
    /// <summary>Deep clones resources from cache. Safest but slowest.</summary>
    Clone,
    /// <summary>Freezes resources to prevent mutation. Middle ground.</summary>
    Freeze
}
```

---

## Configuration

### Options Pattern Types

```csharp
namespace FhirPkg;

/// <summary>
/// Root configuration for the FHIR package manager.
/// </summary>
public class FhirPackageManagerOptions
{
    /// <summary>Path to the local package cache. Default: ~/.fhir/packages.</summary>
    public string? CachePath { get; set; }

    /// <summary>
    /// Registry endpoints to query, in priority order.
    /// Default: packages.fhir.org → packages2.fhir.org → build.fhir.org → hl7.org/fhir.
    /// </summary>
    public List<RegistryEndpoint> Registries { get; set; } = new();

    /// <summary>Whether to include CI build registries. Default: true.</summary>
    public bool IncludeCiBuilds { get; set; } = true;

    /// <summary>Whether to include the HL7 website fallback. Default: true.</summary>
    public bool IncludeHl7WebsiteFallback { get; set; } = true;

    /// <summary>HTTP timeout for registry requests. Default: 30 seconds.</summary>
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum number of HTTP redirects to follow. Default: 5.</summary>
    public int MaxRedirects { get; set; } = 5;

    /// <summary>Whether to verify SHA checksums on download. Default: true.</summary>
    public bool VerifyChecksums { get; set; } = true;

    /// <summary>Maximum parallel registry queries. Default: 3.</summary>
    public int MaxParallelRegistryQueries { get; set; } = 3;

    /// <summary>In-memory resource cache size. 0 to disable. Default: 200.</summary>
    public int ResourceCacheSize { get; set; } = 200;

    /// <summary>Safe mode for in-memory cache. Default: Off.</summary>
    public SafeMode ResourceCacheSafeMode { get; set; } = SafeMode.Off;

    /// <summary>Known package version fixups (e.g., 4.0.0 → 4.0.1 for hl7.fhir.r4.core).</summary>
    public Dictionary<string, string> VersionFixups { get; set; } = new()
    {
        ["hl7.fhir.r4.core@4.0.0"] = "4.0.1"
    };
}

/// <summary>Options for package installation.</summary>
public class InstallOptions
{
    /// <summary>Whether to also install transitive dependencies. Default: false.</summary>
    public bool IncludeDependencies { get; set; } = false;

    /// <summary>Overwrite the package even if already cached. Default: false.</summary>
    public bool OverwriteExisting { get; set; } = false;

    /// <summary>FHIR release to prefer when resolving. Default: null (any).</summary>
    public FhirRelease? PreferredFhirRelease { get; set; }

    /// <summary>Whether to allow pre-release versions. Default: true.</summary>
    public bool AllowPreRelease { get; set; } = true;

    /// <summary>Progress callback for download operations.</summary>
    public IProgress<PackageProgress>? Progress { get; set; }
}

/// <summary>Options for dependency restoration.</summary>
public class RestoreOptions : InstallOptions
{
    /// <summary>Strategy for resolving version conflicts. Default: HighestWins.</summary>
    public ConflictResolutionStrategy ConflictStrategy { get; set; }
        = ConflictResolutionStrategy.HighestWins;

    /// <summary>Whether to write/update the lock file. Default: true.</summary>
    public bool WriteLockFile { get; set; } = true;

    /// <summary>Maximum recursion depth for dependencies. Default: 20.</summary>
    public int MaxDepth { get; set; } = 20;
}

/// <summary>Options for version resolution.</summary>
public class VersionResolveOptions
{
    /// <summary>Whether to include pre-release versions. Default: true.</summary>
    public bool AllowPreRelease { get; set; } = true;

    /// <summary>FHIR release to filter by.</summary>
    public FhirRelease? FhirRelease { get; set; }
}

/// <summary>Version conflict resolution strategies.</summary>
public enum ConflictResolutionStrategy
{
    /// <summary>Use the highest version among conflicts (Firely behavior).</summary>
    HighestWins,
    /// <summary>Keep the first version encountered (depth-first).</summary>
    FirstWins,
    /// <summary>Report an error on conflicts.</summary>
    Error
}

/// <summary>Search criteria for catalog queries.</summary>
public record PackageSearchCriteria
{
    public string? Name { get; init; }
    public string? Canonical { get; init; }
    public string? PackageCanonical { get; init; }
    public string? FhirVersion { get; init; }
    public string? Dependency { get; init; }
    public string? Sort { get; init; }
}

/// <summary>Progress information for package operations.</summary>
public record PackageProgress
{
    public required string PackageId { get; init; }
    public required PackageProgressPhase Phase { get; init; }
    public double? PercentComplete { get; init; }
    public long? BytesDownloaded { get; init; }
    public long? TotalBytes { get; init; }
}

/// <summary>Phases of a package operation.</summary>
public enum PackageProgressPhase
{
    Resolving,
    Downloading,
    Extracting,
    Indexing,
    Complete,
    Failed
}
```

---

## Dependency Injection Extensions

```csharp
namespace FhirPkg;

/// <summary>
/// Extension methods for registering FHIR package services with Microsoft.Extensions.DI.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the FHIR package management services.
    /// </summary>
    public static IServiceCollection AddFhirPackageManagement(
        this IServiceCollection services,
        Action<FhirPackageManagerOptions>? configure = null);
}
```

**Usage:**

```csharp
var services = new ServiceCollection();
services.AddFhirPackageManagement(options =>
{
    options.CachePath = "./my-cache";
    options.Registries.Add(new RegistryEndpoint
    {
        Url = "https://my-private-registry.example.com/",
        Type = RegistryType.FhirNpm,
        AuthHeaderValue = "Bearer my-token"
    });
});

var provider = services.BuildServiceProvider();
var manager = provider.GetRequiredService<IFhirPackageManager>();
```

---

## Standalone Usage (No DI)

```csharp
// Quick usage without dependency injection
var manager = new FhirPackageManager();

// Install a single package
var record = await manager.InstallAsync("hl7.fhir.us.core#6.1.0");

// Install with dependencies
var record = await manager.InstallAsync("hl7.fhir.us.core#6.1.0", new InstallOptions
{
    IncludeDependencies = true,
    PreferredFhirRelease = FhirRelease.R4
});

// Search for packages
var results = await manager.SearchAsync(new PackageSearchCriteria
{
    Name = "hl7.fhir.us",
    FhirVersion = "R4"
});

// Resolve a CI build
var resolved = await manager.ResolveAsync("hl7.fhir.us.core#current");
Console.WriteLine($"CI build version: {resolved?.Reference.Version}");

// Restore a project's dependencies
var closure = await manager.RestoreAsync("./my-ig");
foreach (var (name, reference) in closure.Resolved)
    Console.WriteLine($"  {name} → {reference.Version}");
foreach (var (name, version) in closure.Missing)
    Console.WriteLine($"  MISSING: {name}@{version}");

// List and clean cache
var cached = await manager.ListCachedAsync();
var removed = await manager.RemoveAsync("hl7.fhir.us.core#6.1.0");
```

---

## Usage Examples

### Custom Registry with Authentication

```csharp
var manager = new FhirPackageManager(new FhirPackageManagerOptions
{
    Registries =
    [
        new RegistryEndpoint
        {
            Url = "https://my-private-registry.example.com/",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer ghp_xxxxxxxxxxxxxxxxxxxx",
            CustomHeaders = [("X-Organization", "my-org")]
        },
        RegistryEndpoint.FhirPrimary,
        RegistryEndpoint.FhirSecondary,
    ]
});

var record = await manager.InstallAsync("my-org.fhir.us.custom-ig#1.0.0");
```

### Progress Reporting

```csharp
var progress = new Progress<PackageProgress>(p =>
{
    Console.Write($"\r{p.PackageId}: {p.Phase} {p.PercentComplete:P0}");
});

await manager.InstallAsync("hl7.fhir.r4.core#4.0.1", new InstallOptions
{
    Progress = progress
});
```

### Virtual Packages

```csharp
// Register local files as a virtual package for validation/testing
var virtualPkg = new VirtualPackage(
    name: "my-ig",
    version: "0.1.0-dev",
    resourcePaths: ["./input/profiles", "./input/extensions"]);

var record = await manager.InstallVirtualAsync(virtualPkg);
```
