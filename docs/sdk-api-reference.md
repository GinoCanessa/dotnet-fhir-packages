# SDK API Reference

This document covers every public type in the **FhirPkg** SDK. For a high-level
introduction and quick-start examples, see the [SDK Overview](sdk-overview.md).

---

## Table of Contents

- [Core Interface — IFhirPackageManager](#core-interface--ifhirpackagemanager)
  - [Resource Capability — IFhirPackageResourceManager](#resource-capability--ifhirpackageresourcemanager)
- [Hardened Manager Sources](#hardened-manager-sources)
- [Options](#options)
  - [FhirPackageManagerOptions](#fhirpackagemanageroptions)
  - [InstallOptions](#installoptions)
  - [PackageSourceInstallOptions](#packagesourceinstalloptions)
  - [RestoreOptions](#restoreoptions)
  - [VersionResolveOptions](#versionresolveoptions)
- [Models](#models)
  - [PackageReference](#packagereference)
  - [PackageDirective](#packagedirective)
  - [PackageRecord](#packagerecord)
  - [PackageManifest](#packagemanifest)
  - [PackageListing & PackageVersionInfo](#packagelisting--packageversioninfo)
  - [CatalogEntry](#catalogentry)
  - [ResolvedDirective](#resolveddirective)
  - [PackageInstallResult](#packageinstallresult)
  - [PackageClosure](#packageclosure)
  - [PackageLockFile](#packagelockfile)
  - [PackageSearchCriteria](#packagesearchcriteria)
  - [PackageProgress](#packageprogress)
  - [PackageIndex & ResourceIndexEntry](#packageindex--resourceindexentry)
  - [ResourceInfo & ResourceSearchCriteria](#resourceinfo--resourcesearchcriteria)
  - [PublishResult](#publishresult)
  - [FhirSemVer](#fhirsemver)
- [Enumerations](#enumerations)
- [Cache](#cache)
  - [IPackageCache](#ipackagecache)
  - [IHardenedPackageCache](#ihardenedpackagecache)
  - [DiskPackageCache](#diskpackagecache)
  - [MemoryResourceCache](#memoryresourcecache)
- [Registry](#registry)
  - [IRegistryClient](#iregistryclient)
  - [RegistryEndpoint](#registryendpoint)
  - [Built-in Registry Clients](#built-in-registry-clients)
- [Resolution](#resolution)
  - [IVersionResolver](#iversionresolver)
  - [IDependencyResolver](#idependencyresolver)
- [Indexing](#indexing)
  - [IPackageIndexer](#ipackageindexer)
- [Dependency Injection](#dependency-injection)
- [Utilities](#utilities)

---

## Core Interface — IFhirPackageManager

`IFhirPackageManager` is the primary entry point for all FHIR package operations.
The concrete implementation is `FhirPackageManager`.

```csharp
public interface IFhirPackageManager
{
    Task<PackageRecord?> InstallAsync(
        string directive,
        InstallOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PackageInstallResult>> InstallManyAsync(
        IEnumerable<string> directives,
        InstallOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<PackageClosure> RestoreAsync(
        string projectPath,
        RestoreOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PackageRecord>> ListCachedAsync(
        string? filter = null,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(
        string directive,
        CancellationToken cancellationToken = default);

    Task<int> CleanCacheAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        PackageSearchCriteria criteria,
        CancellationToken cancellationToken = default);

    Task<PackageListing?> GetPackageListingAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    Task<ResolvedDirective?> ResolveAsync(
        string directive,
        CancellationToken cancellationToken = default);

    Task<PublishResult> PublishAsync(
        string tarballPath,
        RegistryEndpoint registry,
        CancellationToken cancellationToken = default);

    Task<PackageRecord> InstallAsync(
        PackageReference expectedReference,
        Uri packageUri,
        PackageSourceInstallOptions? options,
        CancellationToken cancellationToken);

    Task<PackageRecord> InstallAsync(
        PackageReference expectedReference,
        Stream packageStream,
        PackageSourceInstallOptions? options,
        CancellationToken cancellationToken);

    Task<PackageRecord> ImportAsync(
        Uri packageUri,
        PackageSourceInstallOptions? options,
        CancellationToken cancellationToken);

    Task<PackageRecord> ImportAsync(
        Stream packageStream,
        PackageSourceInstallOptions? options,
        CancellationToken cancellationToken);
}
```

### Method Details

#### `InstallAsync`

Installs a single package by directive. Resolves the directive, downloads the
tarball if not already cached, verifies the checksum, and extracts to the local
cache. Returns the `PackageRecord` on success, or `null` if the package could not
be found.

```csharp
var record = await manager.InstallAsync("hl7.fhir.us.core#6.1.0");
```

#### `InstallManyAsync`

Installs multiple packages with concurrency control. Returns a result per
directive with its status (`Installed`, `AlreadyCached`, `NotFound`, `Failed`).

```csharp
var results = await manager.InstallManyAsync(
    ["hl7.fhir.r4.core#4.0.1", "hl7.fhir.us.core#6.1.0"],
    new InstallOptions { IncludeDependencies = true });
```

#### `RestoreAsync`

Reads a `package.json` manifest from `projectPath`, resolves the full transitive
dependency closure, installs all resolved packages, and optionally writes a lock
file. A current schema-v2 lock can bypass graph resolution only when its exact
root directives and complete resolution-policy identity match this request.

```csharp
var closure = await manager.RestoreAsync("./my-ig", new RestoreOptions
{
    LockFilePath = "./locks/fhirpkg.lock.json",
    ConflictStrategy = ConflictResolutionStrategy.HighestWins,
    WriteLockFile = true,
});
```

#### `ListCachedAsync`

Lists all packages in the local cache. The optional `filter` parameter matches
against package ID prefixes.

```csharp
var all = await manager.ListCachedAsync();
var r4Only = await manager.ListCachedAsync("hl7.fhir.r4");
```

#### `RemoveAsync`

Removes a specific package from the cache. Returns `true` if found and removed.

```csharp
bool removed = await manager.RemoveAsync("hl7.fhir.us.core#6.1.0");
```

#### `CleanCacheAsync`

Removes **all** packages from the cache. Returns the number of packages removed.

```csharp
int removed = await manager.CleanCacheAsync();
```

#### `SearchAsync`

Searches all configured registries for packages matching the given criteria.

```csharp
var results = await manager.SearchAsync(new PackageSearchCriteria
{
    Name = "hl7.fhir.us",
    FhirVersion = "R4",
});
```

#### `GetPackageListingAsync`

Retrieves the full version listing for a package, including all published
versions, dist-tags, and metadata.

```csharp
var listing = await manager.GetPackageListingAsync("hl7.fhir.us.core");
Console.WriteLine($"Latest: {listing?.LatestVersion}");
```

#### `ResolveAsync`

Resolves a directive to an exact version and download URL without actually
downloading the package.

```csharp
var resolved = await manager.ResolveAsync("hl7.fhir.us.core#latest");
Console.WriteLine($"{resolved?.Reference.Version} → {resolved?.TarballUri}");
```

#### `PublishAsync`

Publishes a `.tgz` tarball once to the exact supplied endpoint. Publication
does not reuse or fall back through the manager's redundant read chain:
`RegistryEndpoint.Type`, authorization, custom headers, and URL all come from
this call.

```csharp
var result = await manager.PublishAsync("./my-package.tgz", new RegistryEndpoint
{
    Url = "https://my-registry.example.com",
    Type = RegistryType.FhirNpm,
    AuthHeaderValue = "Bearer my-token",
});
```

`RegistryType.FhirNpm` sends the validated archive as the raw gzip request body.
`RegistryType.Npm` validates a standard `package/package.json` layout and exact
name/version, rejects private or invalid-semver packages, and sends a standard
NPM packument with `latest`, SHA-1, SHA-512 integrity, and a streamed base64
attachment. Publish input is bounded by `InstallLimits`, consumed from its
current position, and left open.

### Constructors (FhirPackageManager)

```csharp
// Default options — uses ~/.fhir/packages and all default registries
new FhirPackageManager();

// Custom options
new FhirPackageManager(options);
new FhirPackageManager(options, loggerFactory);

// Full DI constructor (used by AddFhirPackageManagement)
new FhirPackageManager(
    cache, registryClient, versionResolver,
    dependencyResolver, packageIndexer, options,
    logger, memoryCache);
```

`FhirPackageManager` implements `IDisposable` to release the internal `HttpClient`
when used outside of DI.

## Hardened Manager Sources

`FhirPackageManager` implements `IHardenedFhirPackageManager`. The four URI and
stream methods shown above are also binary-compatible default methods on
`IFhirPackageManager`: they dispatch to the capability when available and
otherwise throw `PackageInstallException` with
`UnsupportedManagerCapability` before reading the source.

- Expected-identity methods validate the manifest against
  `expectedReference`. Exact versions and the mutable `current`,
  `current$branch`, and local `dev` aliases are supported; selectors such as
  `latest`, wildcards, and ranges are rejected.
- Import methods discover the canonical identity only after bounded archive and
  manifest validation.
- Caller streams are consumed from their current position and remain open.
- URI sources must be absolute HTTP/HTTPS URIs. The configured `HttpClient`
  uses `ResponseHeadersRead`; `HttpTimeout` covers header acquisition and body
  staging across every redirect. Redirect limits are configured by
  `MaxRedirects`.
- Network allow-list, proxy, DNS, and credential policy are application
  responsibilities.
- All four modes use the same limits, checksum validation, archive validation,
  cache repair, transactional commit, dependency, progress, and cancellation
  pipeline as directive installation.

```csharp
await using FileStream source = File.OpenRead("package.tgz");
PackageRecord installed = await manager.InstallAsync(
    new PackageReference("example.package", "1.0.0"),
    source,
    new PackageSourceInstallOptions
    {
        ExpectedSha256 = "..."
    },
    cancellationToken);

PackageRecord discovered = await manager.ImportAsync(
    new Uri("https://packages.example.test/package.tgz"),
    options: null,
    cancellationToken);
```

## Resource Capability — IFhirPackageResourceManager

`FhirPackageManager` implements an additive resource capability. The same
operations are extension methods on `IFhirPackageManager`, preserving
compatibility for existing implementations; an implementation without the
capability throws `NotSupportedException`.

```csharp
public interface IFhirPackageResourceManager
{
    Task<PackageIndex?> IndexPackageAsync(
        PackageReference reference,
        IndexingOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ResourceInfo>> FindResourcesAsync(
        ResourceSearchCriteria criteria,
        CancellationToken cancellationToken = default);

    Task<ResourceInfo?> FindByCanonicalUrlAsync(
        string canonicalUrl,
        string? packageScope = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ResourceInfo>> FindByResourceTypeAsync(
        string resourceType,
        string? packageScope = null,
        CancellationToken cancellationToken = default);

    Task<JsonNode?> ReadResourceAsync(
        ResourceInfo resource,
        CancellationToken cancellationToken = default);
}
```

Search methods lazily load or generate indexes for relevant cached packages.
`IndexPackageAsync` returns `null` when the package is not cached and honors
`ForceReindex`. Generated indexes are persisted atomically before registration.
Explicit and lazy failures propagate; eager post-install failures only produce
a warning.

`ReadResourceAsync` requires the package identity and contained path from
`ResourceInfo`, rejects unsafe portable paths, returns `null` for a missing
file, and propagates malformed JSON. Parsed `JsonNode` values are cached by
canonical package identity and normalized path when `ResourceCacheSize` is
positive and the package cache provides generation-aware reads. Other custom
cache implementations are read on every call.

---

## Options

### FhirPackageManagerOptions

Top-level configuration for the SDK. See the
[SDK Overview](sdk-overview.md#configuration) for a summary table.

```csharp
public class FhirPackageManagerOptions
{
    public PackageInstallLimits InstallLimits { get; set; }
    public CorruptCacheBehavior CorruptCacheBehavior { get; set; }  // default: Repair
    public string? CachePath { get; set; }                          // default: null → PACKAGE_CACHE_FOLDER env var → ~/.fhir/packages
    public List<RegistryEndpoint> Registries { get; set; }          // default: []
    public bool IncludeCiBuilds { get; set; }                       // default: true
    public bool IncludeHl7WebsiteFallback { get; set; }             // default: true
    public TimeSpan HttpTimeout { get; set; }                       // default: 30s
    public int MaxRedirects { get; set; }                           // default: 5
    public bool VerifyChecksums { get; set; }                       // default: true
    public int MaxParallelRegistryQueries { get; set; }             // default: 3
    public int ResourceCacheSize { get; set; }                      // default: 200
    public SafeMode ResourceCacheSafeMode { get; set; }             // default: SafeMode.Off
    public Dictionary<string, string> VersionFixups { get; set; }   // defaults include R4 4.0.0→4.0.1 and R4B snapshot1→4.3.0
}
```

Options are validated and snapshotted when a manager is constructed. Mutating
the original options or its collections afterward does not reconfigure that
manager. See [Version Resolution Policy](versioning-policy.md).

### InstallOptions

Controls behavior of `InstallAsync` and `InstallManyAsync`.

```csharp
public class InstallOptions
{
    public PackageInstallLimits? InstallLimits { get; set; }
    public bool IncludeDependencies { get; set; }           // default: false
    public bool OverwriteExisting { get; set; }             // default: false
    public FhirRelease? PreferredFhirRelease { get; set; }  // default: null
    public bool AllowPreRelease { get; set; }               // default: true
    public IProgress<PackageProgress>? Progress { get; set; } // default: null
}
```

### PackageSourceInstallOptions

Extends `InstallOptions` for URI and stream sources.

```csharp
public class PackageSourceInstallOptions : InstallOptions
{
    public CorruptCacheBehavior? CorruptCacheBehavior { get; set; }
    public string? ExpectedSha256 { get; set; }
    public string? ExpectedSha1 { get; set; }
}
```

The nullable corruption policy overrides the manager-level repair/strict
setting for that URI or stream operation. Hardened custom caches receive the
resolved value through public `InstallCacheOptions` before source consumption.

`PackageInstallLimits` has finite defaults: 100 MiB compressed, 1 GiB expanded,
128 MiB per entry, 50,000 entries, normalized path length 1,024, and depth 32.
Per-call values may only tighten manager limits. Unspecified manager values can
be configured with `FHIRPKG_MAX_COMPRESSED_BYTES`,
`FHIRPKG_MAX_EXPANDED_BYTES`, `FHIRPKG_MAX_ENTRY_BYTES`,
`FHIRPKG_MAX_ARCHIVE_ENTRIES`, `FHIRPKG_MAX_ARCHIVE_PATH_LENGTH`, and
`FHIRPKG_MAX_ARCHIVE_DEPTH`.

### RestoreOptions

Extends `InstallOptions` with dependency-resolution settings.

```csharp
public class RestoreOptions : InstallOptions
{
    public string? LockFilePath { get; set; }                         // default: <project>/fhirpkg.lock.json
    public ConflictResolutionStrategy ConflictStrategy { get; set; } // default: HighestWins
    public bool WriteLockFile { get; set; }                          // default: true
    public int MaxDepth { get; set; }                                // default: 20
}
```

Relative `LockFilePath` values are resolved against the project directory;
absolute values are unchanged. `WriteLockFile = false` prevents replacement
but does not disable reading a current lock. `OverwriteExisting` controls cache
replacement only and does not invalidate an otherwise current lock. The
filename `.fhirpkg-restore.lock` is reserved for restore coordination.

### VersionResolveOptions

```csharp
public class VersionResolveOptions
{
    public bool AllowPreRelease { get; set; }               // default: true
    public FhirRelease? FhirRelease { get; set; }           // default: null
}
```

---

## Models

### PackageReference

An immutable identity for a FHIR package (name + optional version). This is the
fundamental type used throughout the SDK to refer to a package.

```csharp
public readonly record struct PackageReference
{
    public string Name { get; }
    public string? Version { get; }
    public string? Scope { get; }

    // Computed properties
    public string FhirDirective { get; }          // "name#version"
    public string NpmDirective { get; }           // "name@version"
    public string CacheDirectoryName { get; }     // "name#version"
    public bool HasVersion { get; }

    public static PackageReference Parse(string directive);
}
```

Both `#` (FHIR-style) and `@` (NPM-style) separators are accepted:

```csharp
var r1 = PackageReference.Parse("hl7.fhir.r4.core#4.0.1");
var r2 = PackageReference.Parse("hl7.fhir.r4.core@4.0.1");
// r1 == r2
```

### PackageDirective

A parsed, classified directive with rich metadata about the name and version type.

```csharp
public record PackageDirective
{
    public required string RawDirective { get; init; }
    public required string PackageId { get; init; }
    public string? RequestedVersion { get; init; }
    public string? Alias { get; init; }
    public required PackageNameType NameType { get; init; }
    public required VersionType VersionType { get; init; }
    public FhirSemVer? ResolvedVersion { get; init; }
    public IReadOnlyList<string>? ExpandedPackageIds { get; init; }
    public string? CiBranch { get; init; }

    public static PackageDirective Parse(string directive);
    public PackageReference ToReference();
}
```

### PackageRecord

Represents a cached package on disk.

```csharp
public record PackageRecord
{
    public required PackageReference Reference { get; init; }
    public required string DirectoryPath { get; init; }    // full path to package root
    public required string ContentPath { get; init; }      // path to package/ subfolder
    public required PackageManifest Manifest { get; init; }
    public PackageIndex? Index { get; init; }
    public DateTimeOffset? InstalledAt { get; init; }
    public long? SizeBytes { get; init; }
}
```

### PackageManifest

The deserialized `package.json` for a FHIR package. Combines standard NPM fields
with FHIR-specific extensions.

```csharp
public record PackageManifest
{
    // Required
    public required string Name { get; init; }
    public required string Version { get; init; }

    // NPM fields
    public string? Description { get; init; }
    public string? License { get; init; }
    public string? Author { get; init; }
    public string? Homepage { get; init; }
    public IReadOnlyDictionary<string, string>? Dependencies { get; init; }
    public IReadOnlyDictionary<string, string>? DevDependencies { get; init; }
    public IReadOnlyList<string>? Keywords { get; init; }
    public NpmDistribution? Distribution { get; init; }
    public IReadOnlyDictionary<string, string>? DistTags { get; init; }

    // FHIR-specific fields
    public string? Canonical { get; init; }
    public IReadOnlyList<string>? FhirVersions { get; init; }
    public string? Type { get; init; }
    public string? Date { get; init; }
    public string? Title { get; init; }
    public string? Jurisdiction { get; init; }

    // Computed
    public Version? SemVer { get; }
    public FhirRelease? InferredFhirRelease { get; }

    public static PackageManifest Deserialize(string json);
    public static PackageManifest Deserialize(Stream stream);
    public string Serialize();
}
```

### PackageListing & PackageVersionInfo

Returned by `GetPackageListingAsync`. Contains all published versions and
dist-tags for a package.

```csharp
public record PackageListing
{
    public required string PackageId { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, string>? DistTags { get; init; }
    public required IReadOnlyDictionary<string, PackageVersionInfo> Versions { get; init; }
    public RegistryEndpoint? SourceRegistry { get; init; }                  // JSON-ignored
    public bool IsComplete { get; init; }                                   // default: true; JSON-ignored
    public IReadOnlyList<RegistryAttemptFailure> QueryFailures { get; init; } // default: []; JSON-ignored
    public IReadOnlyList<PackageVersionInfo> VersionCandidates { get; init; } // source-specific; JSON-ignored
    public string? LatestVersion { get; }   // computed: dist-tags["latest"] or last key
}

public record PackageVersionInfo
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
    public string? FhirVersion { get; init; }
    public IReadOnlyList<string>? FhirVersions { get; init; }
    public string? Canonical { get; init; }
    public string? Kind { get; init; }
    public string? PublicationDate { get; init; }
    public int? ResourceCount { get; init; }
    public string? License { get; init; }
    public string? Url { get; init; }
    public NpmDistribution? Distribution { get; init; }
    public IReadOnlyDictionary<string, string>? Dependencies { get; init; }
    public RegistryEndpoint? SourceRegistry { get; init; } // JSON-ignored
    public bool IsSourceLatest { get; init; }               // JSON-ignored
}

public record NpmDistribution(string? ShaSum, string? TarballUrl)
{
    public string? Integrity { get; init; }
}
```

Registry JSON may provide `fhirVersion` as a scalar or array, or use the plural
`fhirVersions` field. `FhirVersion` exposes the first value for compatibility;
`FhirVersions` exposes the complete normalized set.

Composite listings preserve every source copy in `VersionCandidates`. The
representative `Versions` dictionary follows configured source priority, while
resolution selects one complete source candidate atomically. If
the highest-priority compatible candidate omits dependency metadata, the first
later compatible candidate that supplies it is selected as a whole, including
its tarball, checksum, integrity value, and provenance. If
`IsComplete` is false, exact hits may still be used, but exact misses and global
latest/wildcard/range selections throw `RegistryOperationException`.
Returned `SourceRegistry` values are credential-free provenance snapshots: they
retain only the registry origin and protocol type, never configured
authorization, custom headers, user information, paths, queries, or fragments.

### CatalogEntry

A search result from `SearchAsync`.

```csharp
public record CatalogEntry
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? FhirVersion { get; init; }
    public string? Version { get; init; }
    public string? Canonical { get; init; }
    public string? Kind { get; init; }
    public string? Date { get; init; }
    public string? Url { get; init; }
    public int? ResourceCount { get; init; }
}
```

### ResolvedDirective

The result of `ResolveAsync` — an exact version and download location.

```csharp
public record ResolvedDirective
{
    public required PackageReference Reference { get; init; }
    public required Uri TarballUri { get; init; }
    public string? ShaSum { get; init; }
    public string? Sha256Sum { get; init; }
    public string? Integrity { get; init; }
    public RegistryEndpoint? SourceRegistry { get; init; }
    public DateTime? PublicationDate { get; init; }
    public IReadOnlyDictionary<string, string>? Dependencies { get; init; }
    public IReadOnlyList<string>? FhirVersions { get; init; }
}
```

### PackageInstallResult

Returned per-directive from `InstallManyAsync`.

```csharp
public record PackageInstallResult
{
    public required string Directive { get; init; }
    public PackageRecord? Package { get; init; }
    public PackageInstallStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
    public PackageInstallErrorCode? ErrorCode { get; init; }
    public PackageInstallStage? ErrorStage { get; init; }
    public IReadOnlyList<PackageInstallResult> DependencyFailures { get; init; }
}
```

For dependency-stage failures, `Status` is `Failed`, `Package` is the root
package already committed to the cache, and `DependencyFailures` enumerates
failed active child directives.

```csharp
public sealed class DependencyInstallationException : PackageInstallException
{
    public PackageRecord RootPackage { get; }
    public IReadOnlyList<PackageInstallResult> DependencyFailures { get; }
    public IReadOnlyList<DependencyResolutionFailure> DependencyResolutionFailures { get; }
}
```

### PackageClosure

Returned from `RestoreAsync`. Represents the fully resolved dependency tree.

```csharp
public record PackageClosure
{
    public required DateTime Timestamp { get; init; }
    public required IReadOnlyDictionary<string, PackageReference> Resolved { get; init; }
    public required IReadOnlyDictionary<string, string> Missing { get; init; }
    public IReadOnlyList<DependencyResolutionFailure> Failures { get; init; }
    public IReadOnlyList<PackageReference> InstallOrder { get; init; }
    public IReadOnlyList<PackageReference> ReplayOrder { get; init; }
    public IReadOnlyList<PackageReference> BootstrapInstallOrder { get; init; }
    public bool InstallOrderIsComplete { get; init; }
    public bool IsComplete { get; }  // true when Missing and Failures are empty
}
```

`Resolved` always carries selected exact manifest identities. `InstallOrder`
may preserve a mutable CI alias as the cache/install reference.
`ReplayOrder` is the complete dependency-first plan, including cached nodes,
used to persist a lock that can replay mutable aliases without losing their
exact expected identities.
`BootstrapInstallOrder` identifies CI aliases whose exact identity or
authoritative dependency metadata is only available after installation; the
manager installs those aliases and re-resolves before installing the final
active order. An empty
`InstallOrder` is authoritative when `InstallOrderIsComplete` is `true`.

`Missing` is retained as a backward-compatible package-to-message projection.
Use `Failures` for stable failure categories and structured context.

```csharp
public sealed record DependencyResolutionFailure
{
    public required DependencyResolutionFailureCode Code { get; init; }
    public required string PackageId { get; init; }
    public required string Message { get; init; }
    public string? VersionSpecifier { get; init; }
    public string? SelectedVersion { get; init; }
    public string? ParentPackageId { get; init; }
    public string? ParentVersion { get; init; }
    public int? Depth { get; init; }
    public int? MaxDepth { get; init; }
    public IReadOnlyList<string> RequestedVersions { get; init; }
    public IReadOnlyList<RegistryAttemptFailure> RegistryFailures { get; init; }
}
```

### PackageLockFile

Schema-versioned input and output for deterministic restores.

```csharp
public record PackageLockFile
{
   public const int CurrentSchemaVersion = 2;

   public int SchemaVersion { get; init; }
   public required DateTime Updated { get; init; }
   public string? RootPackage { get; init; }
   public IReadOnlyList<string>? Roots { get; init; }
   public PackageLockPolicy? Policy { get; init; }
   public required IReadOnlyDictionary<string, string> Dependencies { get; init; }
   public IReadOnlyList<string>? InstallOrder { get; init; }
   public IReadOnlyDictionary<string, string>? Missing { get; init; }
   public IReadOnlyList<DependencyResolutionFailure> Failures { get; init; }

   public static PackageLockFile Load(string path);
   public static Task<PackageLockFile> LoadAsync(
       string path,
       CancellationToken cancellationToken = default);
   public void Save(string path);
   public Task SaveAsync(
       string path,
       CancellationToken cancellationToken = default);
}

public sealed record PackageLockPolicy
{
   public required ConflictResolutionStrategy ConflictStrategy { get; init; }
   public required bool AllowPreRelease { get; init; }
   public FhirRelease? PreferredFhirRelease { get; init; }
   public required int MaxDepth { get; init; }
   public required string VersionFixupHash { get; init; }
}
```

Schema-v1 files remain readable but are always stale because they cannot prove
root or policy identity. Unknown future schemas throw `NotSupportedException`.
Saving serializes fully before a durable same-directory atomic replacement;
cancellation or pre-commit failure preserves the prior bytes. Manager restore
also serializes writers for a requested lock path across processes and
revalidates the project manifest immediately before replacement.

### PackageSearchCriteria

Criteria for `SearchAsync`.

```csharp
public record PackageSearchCriteria
{
    public string? Name { get; init; }
    public string? Canonical { get; init; }
    public string? PackageCanonical { get; init; }
    public string? FhirVersion { get; init; }
    public string? Dependency { get; init; }
    public string? Sort { get; init; }
}
```

### PackageProgress

Progress reporting for long-running install operations.

```csharp
public record PackageProgress
{
    public required string PackageId { get; init; }
    public required PackageProgressPhase Phase { get; init; }
    public double? PercentComplete { get; init; }
    public long? BytesDownloaded { get; init; }
    public long? TotalBytes { get; init; }
}
```

### PackageIndex & ResourceIndexEntry

The `.index.json` file that catalogs all resources in a package.

```csharp
public record PackageIndex
{
    public int IndexVersion { get; init; }   // default: 2
    public DateTime? Date { get; init; }
    public required IReadOnlyList<ResourceIndexEntry> Files { get; init; }
}

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

    // StructureDefinition-specific
    public string? SdKind { get; init; }
    public string? SdDerivation { get; init; }
    public string? SdType { get; init; }
    public string? SdBaseDefinition { get; init; }
    public bool? SdAbstract { get; init; }
    public string? SdFlavor { get; init; }   // Profile, Extension, Logical, Type, Resource
    public bool? HasSnapshot { get; init; }
    public bool? HasExpansion { get; init; }
}
```

### ResourceInfo & ResourceSearchCriteria

Used by `IPackageIndexer` and `IFhirPackageResourceManager` for resource
lookup.

```csharp
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

public record ResourceSearchCriteria
{
    public string? Key { get; init; }
    public IReadOnlyList<string>? ResourceTypes { get; init; }
    public IReadOnlyList<string>? SdFlavors { get; init; }
    public string? PackageScope { get; init; }
    public int? Limit { get; init; }
}
```

### PublishResult

```csharp
public record PublishResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public HttpStatusCode StatusCode { get; init; }
}
```

### FhirSemVer

FHIR-aware semantic version with support for pre-release hierarchies, wildcards,
and ranges.

```csharp
public sealed class FhirSemVer : IComparable<FhirSemVer>, IEquatable<FhirSemVer>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? PreRelease { get; }
    public string? BuildMetadata { get; }
    public bool IsWildcard { get; }
    public bool IsPreRelease { get; }
    public FhirPreReleaseType PreReleaseType { get; }

    public static FhirSemVer? Parse(string version);

    // Returns the highest version matching this wildcard pattern
    public FhirSemVer? MaxSatisfying(IEnumerable<FhirSemVer> versions);

    // Returns the highest version satisfying this range
    public FhirSemVer? SatisfyingRange(IEnumerable<FhirSemVer> versions);
}
```

**Pre-release ordering:**

```
release (0) > ballot (1) > draft (2) > snapshot (3) > cibuild (4) > other (5)

Examples:
  1.0.0 > 1.0.0-ballot1 > 1.0.0-draft1 > 1.0.0-snapshot1 > 1.0.0-cibuild
```

**Supported version formats:**

| Format | Example | Type |
|--------|---------|------|
| Exact | `4.0.1`, `6.0.0-ballot1` | Exact |
| Wildcard | `4.0.x`, `4.*`, `*` | Wildcard |
| Range (caret) | `^4.0.0` | Range — compatible (≥4.0.0, <5.0.0) |
| Range (tilde) | `~4.0.0` | Range — approximately (≥4.0.0, <4.1.0) |
| Range (between) | `4.0.0 - 5.0.0` | Range — inclusive |
| Range (or) | `4.0.1 \| 5.0.0` | Range — either |

---

## Enumerations

### PackageNameType

Classifies a package name by its structure.

| Value | Example | Description |
|-------|---------|-------------|
| `CoreFull` | `hl7.fhir.r4.core` | Fully qualified core package |
| `CorePartial` | `hl7.fhir.r4` | Partial core name (must be expanded) |
| `GuideWithFhirSuffix` | `hl7.fhir.uv.extensions.r4` | IG with FHIR version suffix |
| `GuideWithoutSuffix` | `hl7.fhir.us.core` | IG without FHIR version suffix |
| `NonHl7Guide` | `us.nlm.vsac` | Third-party / community package |

### VersionType

Classifies how a version specifier should be resolved.

| Value | Example | Description |
|-------|---------|-------------|
| `Exact` | `4.0.1` | Exact semantic version |
| `Wildcard` | `4.0.x` | Pattern with wildcards |
| `Latest` | `latest`, *(empty)* | Latest published version |
| `Range` | `^4.0.0` | Semantic version range |
| `CiBuild` | `current` | Latest CI build (default branch) |
| `CiBuildBranch` | `current$R5` | CI build for a specific branch |
| `LocalBuild` | `dev` | Local development build |

### FhirRelease

| Value | FHIR Version | Core Package Prefix |
|-------|-------------|-------------------|
| `DSTU2` | 1.0.2 | `hl7.fhir.r2` |
| `STU3` | 3.0.2 | `hl7.fhir.r3` |
| `R4` | 4.0.1 | `hl7.fhir.r4` |
| `R4B` | 4.3.0 | `hl7.fhir.r4b` |
| `R5` | 5.0.0 | `hl7.fhir.r5` |
| `R6` | 6.0.0 | `hl7.fhir.r6` |

### FhirPreReleaseType

Ordered from highest to lowest precedence.

| Value | Precedence | Description |
|-------|-----------|-------------|
| `Release` | 0 (highest) | Stable release |
| `Ballot` | 1 | Ballot version |
| `Draft` | 2 | Draft version |
| `Snapshot` | 3 | Snapshot version |
| `CiBuild` | 4 | CI build |
| `Other` | 5 (lowest) | Unrecognized pre-release |

### PackageInstallStatus

| Value | Description |
|-------|-------------|
| `Installed` | Successfully downloaded and cached |
| `AlreadyCached` | Already present in cache (skipped) |
| `Failed` | Installation failed (see `ErrorMessage`) |
| `NotFound` | Package could not be found in any registry |

### RegistryType

| Value | Description |
|-------|-------------|
| `FhirNpm` | FHIR NPM registry (packages.fhir.org) |
| `FhirCiBuild` | FHIR CI build server (build.fhir.org) |
| `FhirHttp` | HL7 website (direct HTTP download) |
| `Npm` | Standard NPM registry |

### ConflictResolutionStrategy

Used during dependency resolution when the same package is required at different
versions.

| Value | Description |
|-------|-------------|
| `HighestWins` | Keep the highest version (default) |
| `FirstWins` | Keep the first version encountered |
| `Error` | Record a typed conflict failure and keep the first version for partial traversal |

### DependencyResolutionFailureCode

| Value | Description |
|-------|-------------|
| `PackageNotFound` | A requested version could not be resolved |
| `VersionConflict` | Active parent edges selected different exact versions under the `Error` strategy |
| `DepthLimitExceeded` | An active edge exceeded the root-relative maximum depth |
| `MetadataUnavailable` | Dependency metadata could not be proven complete |
| `RegistryUnavailable` | Registry failures prevented authoritative version resolution |
| `UnstableResolution` | A version-dependent graph repeated a prior state |
| `InvalidDirective` | A dependency edge contained an invalid package identity or version specifier |

### PackageProgressPhase

| Value | Description |
|-------|-------------|
| `Resolving` | Resolving the version |
| `Downloading` | Downloading the tarball |
| `Extracting` | Extracting to cache |
| `Indexing` | Indexing resources |
| `Complete` | Installation complete |
| `Failed` | Installation failed |

### SafeMode

Controls how `MemoryResourceCache` returns cached objects.

| Value | Description |
|-------|-------------|
| `Off` | Return direct references (fastest, caller must not mutate) |
| `Clone` | Return deep copies via JSON round-trip |
| `Freeze` | Return read-only copies |

### CorePackageType

| Value | Description |
|-------|-------------|
| `Core` | Core FHIR definitions |
| `Expansions` | ValueSet expansions |
| `Examples` | Example resources |
| `Search` | SearchParameter definitions |
| `CoreXml` | Core XML schemas (R5+) |
| `Elements` | Element definitions (R5+) |

---

## Cache

### IPackageCache

Interface for local package storage operations.

```csharp
public interface IPackageCache
{
    string CacheDirectory { get; }

    Task<bool> IsInstalledAsync(PackageReference reference, CancellationToken ct = default);
    Task<PackageRecord?> GetPackageAsync(PackageReference reference, CancellationToken ct = default);
    Task<IReadOnlyList<PackageRecord>> ListPackagesAsync(
        string? packageIdFilter = null, string? versionFilter = null, CancellationToken ct = default);
    Task<PackageRecord> InstallAsync(
        PackageReference reference, Stream tarballStream,
        InstallCacheOptions? options = null, CancellationToken ct = default);
    Task<bool> RemoveAsync(PackageReference reference, CancellationToken ct = default);
    Task<int> ClearAsync(CancellationToken ct = default);
    Task<PackageManifest?> ReadManifestAsync(PackageReference reference, CancellationToken ct = default);
    Task<PackageIndex?> GetIndexAsync(PackageReference reference, CancellationToken ct = default);
    Task<string?> GetFileContentAsync(
        PackageReference reference, string relativePath, CancellationToken ct = default);
    string? GetPackageContentPath(PackageReference reference);
    Task<CacheMetadata> GetMetadataAsync(CancellationToken ct = default);
    Task UpdateMetadataAsync(
        PackageReference reference, CacheMetadataEntry entry, CancellationToken ct = default);
}
```

`CacheMetadataEntry.ContentGeneration` exposes the opaque identifier of the
currently committed content generation. The cache owns this value:
`UpdateMetadataAsync` preserves the current generation even when passed an
older metadata snapshot.

#### InstallCacheOptions

```csharp
public class InstallCacheOptions
{
    public bool OverwriteExisting { get; set; }    // default: false
    public bool VerifyChecksum { get; set; }       // default: true
    public PackageInstallLimits? Limits { get; set; }
    public long? ReportedContentLength { get; set; }
    public string? ExpectedSha256Sum { get; set; }
    public string? ExpectedShaSum { get; set; }
    public DateTimeOffset? SourcePublicationDate { get; set; }
    public string? ArchiveSha256 { get; set; }
    public CorruptCacheBehavior CorruptCacheBehavior { get; set; }
    public bool SkipIfArchiveUnchanged { get; set; }
    public IProgress<PackageProgress>? Progress { get; set; }
}
```

### IHardenedPackageCache

`IHardenedPackageCache : IPackageCache` advertises bounded acquisition,
validated identities, transactional replacement, recovery, and SDK process
coordination. It adds path-neutral inspection and manifest-discovery import:

```csharp
Task<HardenedPackageCacheInspection> InspectAsync(
    PackageReference reference,
    CancellationToken cancellationToken = default);

Task<PackageRecord> ImportAsync(
    Stream tarballStream,
    InstallCacheOptions? options = null,
    CancellationToken cancellationToken = default);
```

The manager rejects an injected `IPackageCache` that does not implement this
capability with `UnsupportedCacheCapability` before source access.

### DiskPackageCache

The default `IPackageCache` implementation. Stores packages in the standard FHIR
directory layout. When no explicit path is provided, the `PACKAGE_CACHE_FOLDER`
environment variable is used if set; otherwise falls back to `~/.fhir/packages`:

```
~/.fhir/packages/
├── packages.ini
├── hl7.fhir.r4.core#4.0.1/
│   └── package/
│       ├── package.json
│       ├── .index.json
│       └── [FHIR resource .json files]
└── ...
```

```csharp
// Default: ~/.fhir/packages
var cache = new DiskPackageCache();

// Custom path
var cache = new DiskPackageCache("/my/cache");
```

Key behaviors:

- **Validated reads** — a hit requires a real package directory, real regular
  `package/package.json`, readable manifest, and matching exact/alias identity.
- **Corruption policy** — `Repair` stages and validates a replacement before
  quarantining corrupt content; `Strict` throws typed `CorruptCache` before
  replacement source access. Read signatures that cannot diagnose return
  false/null or omit corrupt entries.
- **Transactional installation/removal** — cache-local staging, journals,
  same-volume renames, atomic metadata replacement, rollback, and deterministic
  recovery protect valid and corrupt generations.
- **Process coordination** — keyed in-process semaphores and persistent OS lock
  files under `.fhirpkg/locks`; unrelated identities stage concurrently and a
  short global lock serializes promotion and metadata.
- **Hidden state** — `.fhirpkg/staging`, `.fhirpkg/transactions`,
  `.fhirpkg/backup`, `.fhirpkg/quarantine`, and `.fhirpkg/locks` are SDK-owned.
- **Raw path caveat** — SDK reads wait through replacement. A caller retaining
  a previously returned `ContentPath` and accessing it outside the SDK may
  briefly observe the target absent between replacement renames, never mixed
  generations.

### MemoryResourceCache

An optional in-memory LRU cache for frequently accessed FHIR resources.

```csharp
public class MemoryResourceCache
{
    public MemoryResourceCache(int maxEntries = 200, SafeMode safeMode = SafeMode.Off);

    public int Count { get; }

    public T? Get<T>(string key) where T : class;
    public void Set<T>(string key, T value) where T : class;
    public void Clear();
    public bool Remove(string key);
}
```

- **O(1)** lookup, insertion, and eviction.
- **Thread-safe** — uses `Lock`.
- Evicts the least recently used entry when full.

---

## Registry

### IRegistryClient

Interface for communicating with a package registry.

```csharp
public interface IRegistryClient
{
    RegistryEndpoint Endpoint { get; }
    IReadOnlyList<PackageNameType> SupportedNameTypes { get; }
    IReadOnlyList<VersionType> SupportedVersionTypes { get; }

    Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        PackageSearchCriteria criteria, CancellationToken cancellationToken = default);
    Task<PackageListing?> GetPackageListingAsync(
        string packageId, CancellationToken cancellationToken = default);
    Task<ResolvedDirective?> ResolveAsync(
        PackageDirective directive, VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default);
    Task<PackageDownloadResult?> DownloadAsync(
        ResolvedDirective resolved, CancellationToken cancellationToken = default);
    Task<PublishResult> PublishAsync(
        PackageReference reference, Stream tarballStream,
        CancellationToken cancellationToken = default);
}
```

Registry GET requests follow redirects manually so authorization and custom
headers are reevaluated for every destination. `HttpTimeout` is one total
deadline spanning headers, redirects, and response-body reads; expiry throws
`RegistryResponseTimeoutException`. Registry clients do not automatically
follow publish redirects. `PublishAsync` consumes or advances the supplied
stream but never owns or disposes it.

Composite reads return `null` only when every successful source authoritatively
reports absence. If failures prevent any positive result, they throw:

```csharp
public sealed class RegistryOperationException : HttpRequestException
{
    public string Operation { get; }
    public string PackageId { get; }
    public IReadOnlyList<RegistryAttemptFailure> Failures { get; }
}

public sealed class RegistryAttemptFailure
{
    public string EndpointOrigin { get; } // origin only; no path/query/user info
    public RegistryFailureCategory Category { get; }
    public string Message { get; }        // category-derived, sanitized
}
```

Raw inner exceptions, response bodies, endpoint credentials, and custom header
values are logged internally but are not retained in public aggregate state.
Nested `RedundantRegistryClient` instances are flattened in priority order so
`MaxParallelRegistryQueries` remains a single cap across the full client tree.

### RegistryEndpoint

Configuration for a single registry.

```csharp
public record RegistryEndpoint
{
    public required string Url { get; init; }
    public required RegistryType Type { get; init; }
    public string? AuthHeaderValue { get; init; }
    public IReadOnlyList<(string Name, string Value)>? CustomHeaders { get; init; }
    public IReadOnlyList<string> TrustedHeaderOrigins { get; init; }
    public string? UserAgent { get; init; }
}
```

Authorization and custom headers are sent only to the endpoint's exact origin
(scheme, IDN-normalized host, and effective port) or an origin explicitly
listed in `TrustedHeaderOrigins`. Paths, subdomains, and wildcards do not
broaden trust.

```csharp
RegistryEndpoint registry = new()
{
    Url = "https://registry.example.com/",
    Type = RegistryType.FhirNpm,
    AuthHeaderValue = "Bearer ...",
    TrustedHeaderOrigins =
    [
        "https://packages-cdn.example.com/"
    ]
};
```

Low-level registry constructors that accept an arbitrary `HttpClient` are
unverified transports. Authenticated or custom-header requests fail before
network access unless the client is created through
`RegistryClientFactory.CreateClientForEndpoint` with a
`RegistryHttpTransport.CreateRedirectControlled(...)` capability. The supplied
client's handler must have automatic redirects disabled. Registry transports
must not use `HttpClient.DefaultRequestHeaders`; configure all request headers
through `RegistryEndpoint`. POST and PUT helpers always require a
redirect-controlled transport, even when no credentials are configured.

**Well-known endpoints:**

| Static Property | URL | Type |
|----------------|-----|------|
| `RegistryEndpoint.FhirPrimary` | `https://packages.fhir.org/` | FhirNpm |
| `RegistryEndpoint.FhirSecondary` | `https://packages2.fhir.org/packages` | FhirNpm |
| `RegistryEndpoint.FhirCiBuild` | `https://build.fhir.org/` | FhirCiBuild |
| `RegistryEndpoint.Hl7Website` | `https://hl7.org/fhir/` | FhirHttp |
| `RegistryEndpoint.NpmPublic` | `https://registry.npmjs.org/` | Npm |

**Default chains:**

| Chain | Registries |
|-------|------------|
| `DefaultPublishedChain` | Primary → Secondary → HL7 Website |
| `DefaultFullChain` | Primary → Secondary → CI Build → HL7 Website |

### Built-in Registry Clients

| Client | Registry Type | Description |
|--------|--------------|-------------|
| `FhirNpmRegistryClient` | `FhirNpm` | FHIR registries; raw-gzip publication |
| `FhirCiBuildClient` | `FhirCiBuild` | CI builds from build.fhir.org |
| `Hl7WebsiteClient` | `FhirHttp` | HL7 website fallback (core packages only) |
| `NpmRegistryClient` | `Npm` | Standard NPM registries; packument publication |
| `RedundantRegistryClient` | *(composite)* | Chains multiple clients with automatic fallback |

---

## Resolution

### IVersionResolver

Resolves version specifiers (wildcards, ranges, `latest`) to exact versions by
querying registries.

```csharp
public interface IVersionResolver
{
    Task<FhirSemVer?> ResolveVersionAsync(
        string packageId, string versionSpecifier,
        VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default);

    FhirSemVer? ResolveVersion(
        string versionSpecifier,
        IEnumerable<FhirSemVer> availableVersions,
        VersionResolveOptions? options = null);
}
```

### IDependencyResolver

Resolves the full transitive dependency closure for a project manifest.

```csharp
public interface IDependencyResolver
{
    Task<PackageClosure> ResolveAsync(
        PackageManifest rootManifest,
        DependencyResolveOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<PackageClosure> RestoreFromLockFileAsync(
        PackageLockFile lockFile,
        CancellationToken cancellationToken = default);
}
```

**DependencyResolveOptions:**

```csharp
public class DependencyResolveOptions
{
    public ConflictResolutionStrategy ConflictStrategy { get; set; }  // default: HighestWins
    public int MaxDepth { get; set; }                                  // default: 20
    public bool AllowPreRelease { get; set; }                          // default: true
    public FhirRelease? PreferredFhirRelease { get; set; }             // default: null
}
```

Key behaviors:

- **Active graph replacement** — winner changes prune losing-only descendants
  and stale failures while preserving shared descendants.
- **Cycle handling** — shared DAG paths remain active; repeated whole-graph
  states produce a typed failure rather than looping.
- **Depth limiting** — direct dependencies are depth zero; truncation is a
  typed failure and negative limits are rejected.
- **Metadata provenance** — selected registry candidates are exhausted before
  cache fallback, and partial listings keep the closure incomplete.
- **Known version fixups** — automatically corrects known problematic versions
  (e.g., `hl7.fhir.r4.core@4.0.0` → `4.0.1`).

---

## Indexing

### IPackageIndexer

Indexes FHIR resources inside packages and provides search capabilities.

```csharp
public interface IPackageIndexer
{
    Task<PackageIndex> IndexPackageAsync(
        string packageContentPath,
        IndexingOptions? options = null,
        CancellationToken cancellationToken = default);

    IReadOnlyList<ResourceInfo> FindResources(ResourceSearchCriteria criteria);
    ResourceInfo? FindByCanonicalUrl(string canonicalUrl);
    IReadOnlyList<ResourceInfo> FindByResourceType(
        string resourceType, string? packageScope = null);
}
```

**IndexingOptions:**

```csharp
public class IndexingOptions
{
    public bool ForceReindex { get; set; }  // default: false
}
```

The low-level indexer reads existing `.index.json` files when available. If
absent (or `ForceReindex` is true), it scans all `.json` files in the package
and builds a new index. It does not own cache persistence.

For managed package workflows, prefer `IFhirPackageResourceManager`. It
validates and persists indexes through `DiskPackageCache`, registers them only
after durable replacement, performs lazy indexing before queries, and
invalidates package-specific state on cache mutation.

StructureDefinition resources are additionally classified by **flavor**:
`Profile`, `Extension`, `Logical`, `Type`, `Resource`.

---

## Dependency Injection

Register all SDK services with a single extension method:

```csharp
services.AddFhirPackageManagement(options =>
{
    options.CachePath = "/my/cache";
    options.IncludeCiBuilds = true;
    options.Registries.Add(new RegistryEndpoint
    {
        Url = "https://my-registry.example.com",
        Type = RegistryType.FhirNpm,
        AuthHeaderValue = "Bearer token"
    });
});
```

After registration, inject `IFhirPackageManager`,
`IFhirPackageResourceManager`, or any sub-component into your services. The two
manager interfaces resolve to the same singleton:

```csharp
public class MyService(IFhirPackageManager packages)
{
    public async Task DoWorkAsync()
    {
        var record = await packages.InstallAsync("hl7.fhir.us.core#6.1.0");
    }
}
```

---

## Utilities

### FhirReleaseMapping

Static helper for mapping between FHIR releases, version strings, and package
names.

```csharp
public static class FhirReleaseMapping
{
    public static string[] KnownCoreTypes { get; }  // ["core", "expansions", "examples", ...]

    public static FhirRelease? FromVersionString(string version);
    public static string? ToVersionString(FhirRelease release);
    public static string ToPackagePrefix(FhirRelease release);
    public static FhirRelease? FromPackageName(string packageName);
    public static IReadOnlyList<string> GetCorePackageNames(FhirRelease release);
}
```

### CheckSum

SHA-1 checksum computation and verification.

```csharp
public static class CheckSum
{
    public static string ComputeSha1(Stream stream);
    public static string ComputeSha1(byte[] data);
    public static bool Verify(Stream stream, string? expectedHash);
}
```

### DirectiveParser

Static helpers for parsing and classifying package directives.

```csharp
public static class DirectiveParser
{
    public static PackageDirective Parse(string directive);
    public static PackageNameType ClassifyName(string packageId);
    public static VersionType ClassifyVersion(string? version);
}
```
