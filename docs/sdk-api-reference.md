# SDK API Reference

This document is the public API reference for the **FhirPkg** SDK, covering its
interfaces, options, models, enumerations, cache, registry, resolution,
indexing, utilities, and dependency-injection types. For a high-level
introduction and quick-start examples, see the [SDK Overview](sdk-overview.md).

---

## Table of Contents

- [Core Interface — IFhirPackageManager](#core-interface--ifhirpackagemanager)
  - [Resource Capability — IFhirPackageResourceManager](#resource-capability--ifhirpackageresourcemanager)
- [Hardened Manager Sources](#hardened-manager-sources)
  - [Archive Layout and Runtime Preservation](#archive-layout-and-runtime-preservation)
- [Options](#options)
  - [FhirPackageManagerOptions](#fhirpackagemanageroptions)
  - [FileSystemRetryOptions](#filesystemretryoptions)
  - [InstallOptions](#installoptions)
  - [PackageSourceInstallOptions](#packagesourceinstalloptions)
  - [PackageInstallLimits](#packageinstalllimits)
  - [RestoreOptions](#restoreoptions)
  - [VersionResolveOptions](#versionresolveoptions)
- [Models](#models)
  - [PackageReference](#packagereference)
  - [PackageDirective](#packagedirective)
  - [PackageRecord](#packagerecord)
  - [PackageManifest](#packagemanifest)
  - [PackageListing & PackageVersionInfo](#packagelisting--packageversioninfo)
  - [Bounded Metadata Inventory and Local Evidence](#bounded-metadata-inventory-and-local-evidence)
  - [CatalogEntry](#catalogentry)
  - [CiBuildRecord & CiBuildManifest](#cibuildrecord--cibuildmanifest)
  - [ResolvedDirective](#resolveddirective)
  - [PackageInstallResult](#packageinstallresult)
  - [PackageClosure](#packageclosure)
  - [PackageInstallationIdentity](#packageinstallationidentity)
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
  - [RegistryClientFactory](#registryclientfactory)
  - [Built-in Registry Clients](#built-in-registry-clients)
  - [RegistryClientBase](#registryclientbase)
- [Resolution](#resolution)
  - [IVersionResolver](#iversionresolver)
  - [IDependencyResolver](#idependencyresolver)
- [Indexing](#indexing)
  - [IPackageIndexer](#ipackageindexer)
- [Dependency Injection](#dependency-injection)
- [Utilities](#utilities)
- [Public Surface Coverage](#public-surface-coverage)

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

    Task<IReadOnlyList<PackageRecord>> ListCachedSummariesAsync(
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
PackageRecord? record =
    await manager.InstallAsync("hl7.fhir.us.core#6.1.0");
```

#### `InstallManyAsync`

Installs multiple packages with concurrency control. Returns a result per
directive with its compatibility status (`Installed`, `AlreadyCached`,
`NotFound`, `Failed`). Successful `current` and `current$branch` results can
also expose a nullable `PackageInstallDisposition` that distinguishes first
install, replacement, already-current, and same-content refresh outcomes.

```csharp
IReadOnlyList<PackageInstallResult> results =
    await manager.InstallManyAsync(
        ["hl7.fhir.r4.core#4.0.1", "hl7.fhir.us.core#6.1.0"],
        new InstallOptions { IncludeDependencies = true });
```

#### `RestoreAsync`

Reads a `package.json` manifest from `projectPath`, resolves the full transitive
dependency closure from current registry/cache state, and installs every
reachable exact package identity. Distinct versions of one package and their
separate transitive subgraphs coexist. The conflict strategy selects the
preferred name-keyed projection but never removes required exact versions.

```csharp
PackageClosure closure = await manager.RestoreAsync(
    "./my-ig",
    new RestoreOptions
    {
        ConflictStrategy = ConflictResolutionStrategy.HighestWins,
    });
```

#### `ListCachedAsync`

Lists all packages in the local cache. The optional `filter` parameter matches
against package ID prefixes.

```csharp
var all = await manager.ListCachedAsync();
var r4Only = await manager.ListCachedAsync("hl7.fhir.r4");
```

#### `ListCachedSummariesAsync`

Lists package-level cache metadata without requiring resource indexes.
Every returned `PackageRecord` has `Index == null`; use `ListCachedAsync`
when the caller needs to consume `PackageRecord.Index`.

```csharp
IReadOnlyList<PackageRecord> summaries =
    await manager.ListCachedSummariesAsync("hl7.fhir");
```

The default interface implementation clones records from `ListCachedAsync`,
which keeps third-party implementations source-compatible but may still
hydrate indexes internally. Implementations should override the summary
operation to provide lightweight enumeration.

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

### Archive Layout and Runtime Preservation

`FhirPackageManagerOptions.Unmodified` and `InstallCacheOptions.Unmodified`
are optional booleans with default `false`. The manager snapshots its option
at construction. It covers registry/directive, URI, stream, and local archive
installations, imports, `InstallManyAsync`, dependencies, `RestoreAsync`, and
eager, lazy, and explicitly forced index operations. There is no per-call
manager override. The cache option controls one direct cache install/import
call; neither setting creates persistent package or generation protection.
The CLI equivalents are `--unmodified` and the `unmodified` configuration key.

| Archive / mode | Newly committed layout | `ContentPath` | Active resource index |
|----------------|------------------------|---------------|-----------------------|
| Standard / ordinary | Original archive, including safe root files and siblings of `package/` | `DirectoryPath/package` | Reuse valid; write missing, unusable, or forced index |
| Standard / unmodified | Same distributed layout | `DirectoryPath/package` | Reuse valid; otherwise build in memory |
| Root-layout / ordinary | Entire archive wrapped once beneath `package/` | `DirectoryPath/package` | Ordinary index reuse/write |
| Root-layout / unmodified | Original archive at the identity root | `DirectoryPath` | Reuse valid root index; otherwise build in memory |

Archive authority is the exact regular-file slot `package/package.json` or
root `package.json`. Two regular authoritative candidates are rejected, even
if their bytes agree. A selected manifest must parse and validate; a failure
does not fall back to the other slot or an arbitrary nested manifest. Safe
ancillary manifests, root files, `openapi/`, `other/`, and unknown sibling trees
are opaque payload, not alternate authorities or relocation candidates.
Default root wrapping includes **all** payload, including incidental entries
named `package`, so relationships within the archive remain intact.

Use `DirectoryPath` to access known standard sibling paths. Content-relative
file and resource APIs still reject `..` and other unsafe paths; the accepted
sibling layout does not allow reads to escape `ContentPath`. Package identity
must come from `PackageRecord.Reference`, not from the parent of `ContentPath`.

Unmodified mode is not a read-only cache: acquisition, extraction, staging,
identity-directory promotion, cache-root metadata and journals, recovery, and
explicit replacement/removal/clean remain allowed. A distributed `packages.ini`
or `.fhirpkg` inside a package target is payload, not cache-root control state.
An ordinary manager in a later process may maintain the active index in the
same preserved root layout. External tools are not constrained by this option.

A cache hit returns the existing generation; it does not reconstruct original
archive paths or index bytes. Explicitly reinstall from the source using SDK
`OverwriteExisting` (on installation options) or CLI `install --overwrite`,
not `install --force`, to request a new layout under the chosen mode.
Older SDKs and external tooling may not understand raw-root entries and may
classify them as corrupt. Use a **dedicated cache** when interoperability with
those consumers is a concern.

Extraction writes exact safe filename spelling while retaining canonical
case/Unicode collision checks. Native filesystems may themselves normalize
visible Unicode names; exact byte-for-byte filename spelling cannot be promised
on such hosts. Fidelity tests compare actual names/bytes to an independent
direct-file round trip and report that limitation, rather than normalizing both
sides of an assertion.

Unmodified manager construction initially requires the built-in coordinated
`DiskPackageCache` and read-only managed `PackageIndexer` path. An unsupported
injected cache raises `PackageInstallException` with
`UnsupportedCacheCapability` / `PolicyValidation`; an unsupported indexer raises
an explicit `NotSupportedException`. Both fail before source consumption or
potentially mutating callbacks. Ordinary custom collaborators remain supported;
this adds no public capability hierarchy or required interface members.

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
`ForceReindex`. Ordinary managers persist generated indexes atomically before
registration. Unmodified managers validate and register them in memory under
the same identity/generation coordination, with no temporary payload files or
index writes. A valid publisher index is reused without rewriting unless forced;
unmodified forced reindexing changes only memory. Missing or unusable on-disk
indexes remain untouched in that mode. A distributed `.index.json` directory is
never recursively deleted to make room for a file; ordinary persistence fails
while unmodified indexing may still succeed in memory.
Explicit and lazy failures propagate; eager post-install failures only produce
a warning.

`ReadResourceAsync` requires the package identity and contained path from
`ResourceInfo`, rejects unsafe portable paths, returns `null` for a missing
file, and propagates malformed JSON. Parsed `JsonNode` values are cached by
canonical package identity and normalized path when `ResourceCacheSize` is
positive and the package cache provides generation-aware reads. Other custom
cache implementations are read on every call.

### FhirPackageManagerResourceExtensions

The extension methods that expose the capability on `IFhirPackageManager`, so
existing implementations keep compatibility. Each one resolves the manager to
`IFhirPackageResourceManager` and forwards unchanged, so the behavior above
applies without an interface change.

```csharp
public static class FhirPackageManagerResourceExtensions
{
    public static Task<PackageIndex?> IndexPackageAsync(
        this IFhirPackageManager manager,
        PackageReference reference,
        IndexingOptions? options = null,
        CancellationToken cancellationToken = default);

    public static Task<IReadOnlyList<ResourceInfo>> FindResourcesAsync(
        this IFhirPackageManager manager,
        ResourceSearchCriteria criteria,
        CancellationToken cancellationToken = default);

    public static Task<ResourceInfo?> FindByCanonicalUrlAsync(
        this IFhirPackageManager manager,
        string canonicalUrl,
        string? packageScope = null,
        CancellationToken cancellationToken = default);

    public static Task<IReadOnlyList<ResourceInfo>> FindByResourceTypeAsync(
        this IFhirPackageManager manager,
        string resourceType,
        string? packageScope = null,
        CancellationToken cancellationToken = default);

    public static Task<JsonNode?> ReadResourceAsync(
        this IFhirPackageManager manager,
        ResourceInfo resource,
        CancellationToken cancellationToken = default);
}
```

A `null` manager throws `ArgumentNullException`; a manager that does not
implement the capability throws `NotSupportedException`.

---

## Options

### FhirPackageManagerOptions

Top-level configuration for the SDK. See the
[SDK Overview](sdk-overview.md#configuration) for a summary table.

```csharp
public class FhirPackageManagerOptions
{
    public FileSystemRetryOptions FileSystemRetry { get; set; }    // default: new unspecified options
    public PackageInstallLimits InstallLimits { get; set; }
    public CorruptCacheBehavior CorruptCacheBehavior { get; set; }  // default: Repair
    public bool Unmodified { get; set; }                           // default: false; runtime payload/index policy
    public string? CachePath { get; set; }                          // default: null → PACKAGE_CACHE_FOLDER env var → ~/.fhir/packages
    public List<RegistryEndpoint> Registries { get; init; }         // default: []
    public bool IncludeCiBuilds { get; set; }                       // default: true
    public string? GitHubToken { get; set; }                        // default: null → unauthenticated GitHub lookups
    public bool IncludeHl7WebsiteFallback { get; set; }             // default: true
    public TimeSpan HttpTimeout { get; set; }                       // default: 30s
    public int MaxRedirects { get; set; }                           // default: 5
    public bool VerifyChecksums { get; set; }                       // default: true
    public int MaxParallelRegistryQueries { get; set; }             // default: 3
    public int ResourceCacheSize { get; set; }                      // default: 200
    public SafeMode ResourceCacheSafeMode { get; set; }             // default: SafeMode.Off
    public Dictionary<string, string> VersionFixups { get; init; }  // defaults include R4 4.0.0→4.0.1 and R4B snapshot1→4.3.0
}
```

Options are validated and snapshotted when a manager is constructed. Mutating
the original options or its collections afterward does not reconfigure that
manager. See [Version Resolution Policy](versioning-policy.md).

`GitHubToken` is used **only** for the `api.github.com` repository lookups behind
canonical CI build repository selection, and is never sent to a package registry.
`null` — the default — means those lookups are unauthenticated and carry no
`Authorization` header, which is subject to GitHub's anonymous rate limit but is
sufficient in practice because the lookup is skipped entirely for packages that a
single repository publishes or that the prefix table names.

A token requires a redirect-controlled transport, so the credential cannot follow
a redirect to another origin. The standalone `new FhirPackageManager(options)`
constructor and the DI registration both build one, so both support it. The
`RegistryClientFactory.BuildRegistryClient(options, HttpClient, …)` convenience
overload can only build an unverified transport and therefore throws
`InvalidOperationException` when `GitHubToken` is set; use the
`RegistryHttpTransport` overload instead.

### FileSystemRetryOptions

`FhirPackageManagerOptions.FileSystemRetry` configures bounded retries for
safely replayable managed/native filesystem work in the manager-owned graph.
It defaults to a new **unspecified** options object, not a fully assigned policy.

```csharp
public sealed class FileSystemRetryOptions
{
    public const int DefaultRetryCount = 5;
    public const int DefaultMinimumDelayMilliseconds = 10;
    public const int DefaultMaximumJitterMilliseconds = 10;

    public const string RetryCountEnvironmentVariable = "FHIRPKG_FS_RETRY_COUNT";
    public const string MinimumDelayMillisecondsEnvironmentVariable = "FHIRPKG_FS_MIN_DELAY_MS";
    public const string MaximumJitterMillisecondsEnvironmentVariable = "FHIRPKG_FS_JITTER_MAX_MS";

    public int RetryCount { get; set; }
    public int MinimumDelayMilliseconds { get; set; }
    public int MaximumJitterMilliseconds { get; set; }
}
```

| Property | Default | Environment override |
|---|---:|---|
| `RetryCount` | 5 | `FHIRPKG_FS_RETRY_COUNT` |
| `MinimumDelayMilliseconds` | 10 | `FHIRPKG_FS_MIN_DELAY_MS` |
| `MaximumJitterMilliseconds` | 10 | `FHIRPKG_FS_JITTER_MAX_MS` |

Resolve each value independently: **explicitly assigned property > environment
> default**. Getters report defaults while nullable backing values distinguish
unassigned from assigned properties. Assigning a default or zero is still
explicit; an explicit value masks only its own lower-priority environment
value, not invalid values for other settings.

All effective values must be nonnegative `Int32` integers. Environment text
uses invariant unsigned decimal syntax: empty, negative, malformed, and
overflowing values fail validation. Minimum delay plus maximum jitter must be
at most `Int32.MaxValue`; there is no smaller cap. Attempt totals and delay
arithmetic use wider types, so `Int32.MaxValue` retries means up to
2,147,483,648 executions. A null policy or invalid effective value throws
`PackageInstallException` with `InvalidPolicy` / `PolicyValidation` before
affected filesystem work.

`RetryCount` counts retries **after** the initial execution. Zero gives one
execution; defaults permit six executions with five waits. Before each
eligible retry, sample a fresh uniform integer from zero through
`MaximumJitterMilliseconds` **inclusive** and wait
`MinimumDelayMilliseconds + sample`. No wait precedes the initial execution;
success and exhaustion have no following wait. Default scheduled waits total
50–100 ms at exhaustion, excluding OS-call execution time.

```csharp
using FhirPackageManager manager = new(new FhirPackageManagerOptions
{
    FileSystemRetry = new FileSystemRetryOptions
    {
        RetryCount = 3,
        MinimumDelayMilliseconds = 10,
        MaximumJitterMilliseconds = 10,
    },
});
```

#### Lifetime and Injected Collaborators

Direct managers, DI graphs, and standalone components resolve immutable
effective settings at construction. Later options/environment mutations do not
reconfigure them. Fully populated option copies retain the **original
explicitness separately**; copying does not turn environment-only settings
into explicit manager configuration. Deferred file-backed HTTP content retains
its originating context until serialization.

Assigning **at least one manager retry property**, even its default or zero,
requires matching effective values on injected built-ins. An untouched/empty
options object, or environment-only configuration, does not enable that check.
Validation follows actual held collaborators of built-in dependency/version
resolvers and flattened composite registry leaves, with one shared
reference-identity visited set. Matching top-level arguments cannot hide a
different-policy cache or registry inside a resolver. Mismatches fail with
`InvalidPolicy` / `PolicyValidation` before newly initiated manager-owned I/O.
Already-constructed injected objects may have performed their own I/O.
Third-party implementations are opaque and are neither inspected nor mutated.

Standalone configured constructors and endpoint overloads are shown under
[DiskPackageCache](#diskpackagecache), [Indexing](#ipackageindexer),
[RegistryEndpoint](#registryendpoint), [Built-in Registry Clients](#built-in-registry-clients),
and [RegistryClientBase](#registryclientbase). The composite uses a named
factory to preserve existing `params`/null/default source bindings. Existing
interfaces and old overloads remain unchanged.

Static filesystem helpers resolve once per public invocation when a context is
needed. Internal graph calls reuse the existing context; already-protected
inputs are not rewrapped. Memory-only manifest/INI parsing and checksum
byte-array/memory-stream operations do not resolve retry environment or start
I/O. An empty expected checksum skips verification without preparing a stream.
The existing manifest/checksum/INI/temp/archive/key helpers have no new public
retry-options overload; internal context overloads are not public signatures.

#### Safety and Compatibility

Eligibility is based on filesystem origin and safe replay, **not a
transient-error allowlist**. A block retains its handle, offset, and write bytes;
compound mutations retain completed phases and pending durability barriers.
Open, subsequent blocks, and cleanup have separate budgets, without nested
executors or whole-parser/HTTP replay. Async domain companions await retry
waits, including metadata, inspection/traversal, normalization, acquisition,
lease release, and HTTP file preparation. Synchronous constructors/public
compatibility boundaries remain synchronous.

Ordinary readable/seekable `FileStream` inputs are borrowed from their current
position and left open. Read/write files remain compatible, but establishing
coherent read state may flush caller-owned buffered writes and is deliberately
one-shot on failure. After preparation, replay requires a stable handle and
quiescent caller use. Derived/opaque/nonseekable streams retain one-shot
consumption; the SDK does not unwrap them. Owned writes require demonstrated
write exclusion. Direct INI overwrite preserves `FileShare.Read`: Windows
deny-write sharing establishes replay proof, whereas Unix shared flock does
not, so failed mutations with that sharing remain unsafe one-shot. Exclusive
durable writers retain retries. Close and ambiguous mutation are also
deliberate one-shot safety boundaries, not missing supported-platform coverage.

Expected absence/kind rejection, malformed content, checksum/identity/limit
validation, cancellation, and observer callbacks are not fault retries.
Disappearance of a required manifest after inventory selection is still a
required-read fault with existing typed wrapping. Exhausted manifest I/O keeps
existing Corrupt/Repair/Strict behavior; optional derivative-index fallback
also remains. Final errors preserve original exception/native context, and
existing reason/message-only results retain final operation/path/phase/attempt
diagnostics. Terminal local failures cannot trigger another registry request,
rewind-and-publish attempt, or renewed budget; ordinary network fallback is
unchanged. Publication result fields/equality/serialization do not change.

Normal cache-lock coordination remains unbounded and separate (10–250 ms
backoff); contention consumes no fault retries and cannot replenish them.
Finite retries provide **no OS-call or whole-operation timeout**, including
deliberately uncancellable commit/recovery sections. See the
[call-site inventory and actual platform evidence](filesystem-retry-coverage.md)
for flush/close, native capability fallback, cleanup, and exclusion details.

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

`InstallLimits` may only tighten the manager's construction policy. For direct
cache calls the corresponding property is `InstallCacheOptions.Limits`;
source hashes map to `ExpectedSha256Sum` and `ExpectedShaSum` there, not to
new option names. See [PackageInstallLimits](#packageinstalllimits).

### PackageInstallLimits

Finite acquisition/extraction policy for managers, direct caches, and explicit
individual endpoint construction:

```csharp
public sealed class PackageInstallLimits
{
    public long MaxCompressedBytes { get; set; }
    public long MaxExpandedBytes { get; set; }
    public long MaxEntryBytes { get; set; }
    public int MaxArchiveEntries { get; set; }
    public int MaxArchivePathLength { get; set; }
    public int MaxArchiveDepth { get; set; }

    public static PackageInstallLimits FromEnvironment();
    public void Validate();
}
```

| Field | Finite default | Environment fallback | Native measurement |
|-------|---------------:|----------------------|--------------------|
| `MaxCompressedBytes` | 104,857,600 (100 MiB) | `FHIRPKG_MAX_COMPRESSED_BYTES` | Original gzip bytes, reported and actually acquired |
| `MaxExpandedBytes` | 1,073,741,824 (1 GiB) | `FHIRPKG_MAX_EXPANDED_BYTES` | Sum of regular-file payload bytes, including manifest and ancillary files, not tar headers/padding |
| `MaxEntryBytes` | 134,217,728 (128 MiB) | `FHIRPKG_MAX_ENTRY_BYTES` | One regular-file payload |
| `MaxArchiveEntries` | 50,000 | `FHIRPKG_MAX_ARCHIVE_ENTRIES` | Yielded tar entries, including explicit directories, not implied parents |
| `MaxArchivePathLength` | 1,024 | `FHIRPKG_MAX_ARCHIVE_PATH_LENGTH` | Both normalized canonical and exact-spelling path lengths, UTF-16 units including separators and `package/` |
| `MaxArchiveDepth` | 32 | `FHIRPKG_MAX_ARCHIVE_DEPTH` | Normalized segments including filename and `package`; trailing directory separator adds none |

Hidden PAX/GNU metadata has **separate** preflight entry-count and byte budgets;
its count is not added to the extractor's yielded-entry counter. Extraction
also retains portable component caps of 255 UTF-16 units and 255 UTF-8 bytes
against exact spelling. Raising a policy does not bypass path, archive,
identity, integrity, or transactional safety checks or host filesystem limits.

Construction resolves each field independently: **explicitly assigned value >
environment > default**. Getters show defaults for unassigned fields; assigning
even a default is explicit. Overridden malformed environment values are not
parsed, but malformed unresolved fields still fail. Environment values must be
positive invariant decimal integers fitting the field's `Int64`/`Int32` type.
All effective limits must be positive, entry bytes must not exceed expanded
bytes, and compressed+expanded and entry-bytes×entry-count accounting must fit
`Int64`. Null construction policy and invalid values throw
`PackageInstallException` with `InvalidPolicy` / `PolicyValidation`.
`Validate()` validates the object's values; `FromEnvironment()` resolves a
fresh environment/default policy.

Valid higher finite construction ceilings are allowed. Construction snapshots
both installation and [ordinary retry options](#filesystemretryoptions);
later caller-object or environment mutations do not reconfigure an instance.
The new cache route validates before cache-root discovery/creation. The new
endpoint routes validate ordinary endpoint/transport/logger arguments first,
then installation and retry policies before client construction or HTTP.

Per-call `InstallOptions.InstallLimits` and `InstallCacheOptions.Limits` inherit
unassigned fields from that instance, do not reread environment, allow equality
or tightening only, and validate the resulting combination. An upward override
fails with `InvalidPolicy` / `PolicyValidation` before source consumption,
even when it is below some other instance's ceiling.

This complete example explicitly assigns **all six** finite fields and normal
retry settings, without process-global production configuration:

```csharp
PackageInstallLimits limits = new()
{
    MaxCompressedBytes = 200L * 1024 * 1024,
    MaxExpandedBytes = 2L * 1024 * 1024 * 1024,
    MaxEntryBytes = 256L * 1024 * 1024,
    MaxArchiveEntries = 100_000,
    MaxArchivePathLength = 2_048,
    MaxArchiveDepth = 64,
};
FileSystemRetryOptions retry = new()
{
    RetryCount = 5,
    MinimumDelayMilliseconds = 10,
    MaximumJitterMilliseconds = 10,
};
using DiskPackageCache cache = new(
    cacheDirectory: "./isolated-cache",
    logger: null,
    timeProvider: null,
    fileSystemRetry: retry,
    installLimits: limits);

await using FileStream source = File.OpenRead("package.tgz");
PackageRecord imported = await cache.ImportAsync(
    source,
    new InstallCacheOptions
    {
        Unmodified = true,
        VerifyChecksum = true,
        Limits = new PackageInstallLimits { MaxArchiveDepth = 48 },
    },
    cancellationToken);
```

The partial per-call object above tightens only depth; it does not replace the
other five construction values.

### RestoreOptions

Extends `InstallOptions` with dependency-resolution settings.

```csharp
public class RestoreOptions : InstallOptions
{
    public ConflictResolutionStrategy ConflictStrategy { get; set; } // default: HighestWins
    public int MaxDepth { get; set; }                                // default: 20
}
```

Every restore resolves the live graph. `ConflictStrategy` chooses the preferred
entry exposed through `PackageClosure.Resolved`; it does not remove alternate
exact versions from `ResolvedPackages` or installation. `OverwriteExisting`
continues to control replacement of package-cache entries.

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
    public required string DirectoryPath { get; init; }    // full path to cache identity root, including siblings
    public required string ContentPath { get; init; }      // selected content directory; may equal DirectoryPath
    public required PackageManifest Manifest { get; init; }
    public PackageIndex? Index { get; init; }               // null when omitted by summary listing
    public DateTimeOffset? InstalledAt { get; init; }
    public long? SizeBytes { get; init; }                   // whole archive's expanded bytes, not a current disk recount
}
```

`Index == null` can mean that no valid persisted index exists or that a
summary API intentionally omitted it. Use the hydrated listing APIs when the
distinction matters. Manager operations may return a transient index in
unmodified mode; cache-only reads do not expose that manager's in-memory state.

`ContentPath` normally selects `DirectoryPath/package`, but selects the identity
root itself for a preserved root-layout entry. `DirectoryPath` includes standard
ancillary siblings; content-relative read APIs remain contained in `ContentPath`.
`SizeBytes` includes all expanded archive files, including siblings and a
publisher-supplied index. Later generated/replaced resource indexes do not
recompute it.

### PackageManifest

The deserialized `package.json` for a FHIR package. Combines standard NPM fields
with FHIR-specific extensions.

`Author` remains `string?` and has a property-scoped JSON converter: strings are
preserved verbatim, missing/JSON-null authors remain null, and objects project to
`name <email> (url)` (for example,
`Example Author <author@example.test> (https://example.test/)`). The optional
`name`, `email`, and `url` members accept only strings or null. Missing, null, and
exactly empty components are omitted; nonempty components are not trimmed and
are joined in that order with single separator spaces, with no extra leading or
trailing separator spaces. An object with no usable components projects to null.
Unknown members, including nested metadata, are ignored. Recognized member names
follow serializer casing options (the model helpers are case-insensitive;
default `JsonSerializer` options are case-sensitive). The last recognized
occurrence wins, including null/empty clearing, but every recognized occurrence
is validated. Arrays, numbers, and booleans as authors, or malformed recognized
member values, throw `JsonException`. This projection is lossy: explicit model
serialization emits a JSON string, not the original object; `Serialize()` omits
null authors while default external serialization emits normal JSON null.
Imported manifest bytes remain untouched independently of this typed projection.
Older SDK readers still reject object authors in those preserved cache files.

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
    public NpmRepository? Repository { get; init; }
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

`Deserialize(Stream)` protects ordinary file reads below JSON parsing, from
the current position, without taking ownership or replaying malformed JSON.
String/memory parsing remains pure. See
[filesystem retry safety](#safety-and-compatibility) for borrowed-file preparation.

`Repository` uses the `NpmRepository` record:

```csharp
public record NpmRepository(string? Type, string? Url, string? Directory);
```

`Repository` remains `NpmRepository?`, with a property-local converter only on
`PackageManifest.Repository`. A JSON string, including shorthand
`"github:example/repo"`, full URLs, empty strings, or whitespace, becomes
`new NpmRepository(null, input, null)` verbatim. There is no trimming, shorthand
expansion, inferred type, URL validation, or fetch. Object `type`, `url`, and
`directory` members accept strings/null and retain exactly empty strings;
unknown members are ignored. Casing follows serializer options, as for author.
Every recognized occurrence is validated before last-occurrence assignment,
including null/empty clearing. Numbers, booleans, arrays, and malformed
recognized members throw `JsonException`; absent/null remain null.
Explicit model writing delegates to ordinary `NpmRepository` serialization,
using JSON names `type`, `url`, `directory` and the supplied options; it emits
an object, not the original string.

`Date` remains `string?`. Strings stay verbatim, including ordinary noncompact
strings, empty strings, and strings with leading zeroes. A numeric JSON token
must lexically contain **exactly eight or fourteen ASCII digits**; for example
`20240203` and `20240203123456` become those strings. No calendar, time-zone,
or numeric normalization is performed (`99999999` is therefore accepted).
Signs, fractions, exponents, other digit lengths, booleans, arrays, and objects
are rejected. Invalid JSON such as leading-zero numbers is also rejected.
Absent/null remain null. Writing emits a JSON string, with normal null handling:
manifest `Serialize()` omits nulls; default external serialization emits null.
Top-level recognized repository/date duplicates also validate every occurrence,
even if a later valid/null value would replace an invalid earlier value.

These converters do not change public metadata types or R0 author behavior
above, register global converters, or rewrite/repack imported archives.
Native validating import/install still reads the **complete** manifest before
layout preparation, including in unmodified mode. Distributed manifest,
resources, opaque data, safe ancillary bytes, and explicit empty directories
are preserved according to the existing layout contract; model serialization
is projection, not a byte-fidelity API. Malformed repository/date JSON retains
native `InvalidArchive` / `ArchiveValidation` wrapping with inner
`JsonException` (normally the containing property's path).

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
    public string? LatestVersion { get; }   // computed: dist-tags["latest"] or highest semantic version
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
    public JsonElement Unlisted { get; init; }             // "unlisted"; raw source value
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

Listing date is **not** manifest date conversion: strings are retained
(including empty/whitespace); numeric dates and other non-string kinds are
ignored as null. Both FHIR fields accept nonblank scalar strings or string
array entries; blank/non-string entries and malformed field kinds are ignored.
Accepted strings are not trimmed, are combined singular first, then plural,
and deduplicated case-insensitively. An explicit field with no usable strings
still records explicit metadata internally; writing retains that presence as
an empty `fhirVersions` array rather than inventing a declaration.

`Unlisted` is raw source-specific JSON, **not a boolean eligibility flag**:
`JsonValueKind.Undefined` means absent, `Null` means explicit null, and strings
retain exact decoded contents (`""`, `"true"`, `"false"`, and whitespace are
distinct). Boolean, number, object, and array values are retained without
interpretation. The reader clones the element before disposing its parser
document. Programmatic `init` assignment does not clone: keep the owning
`JsonDocument` alive or assign `document.RootElement.Clone()` yourself.
The custom writer omits only `Undefined` and writes every other kind via
`WriteTo`, including explicit null even with `WhenWritingNull` or
`WhenWritingDefault` options.

Listing lookup deliberately differs from manifest enumeration: exact spelling
takes precedence, repeated exact spelling takes the last match, and without
an exact spelling the existing case-insensitive enumeration takes the first
match, independently of serializer casing options. No extra listing fetch or
caller duplicate parser is needed.

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

The raw marker stays paired with each complete source record's artifact,
hashes/integrity, dependencies, FHIR declarations, latest flag, and provenance
in both normal and representative-only merge branches. It does not change SDK
tags, prerelease/FHIR matching, wildcard resolution, or explicit known-version
acquisition. **Later Candle consumer obligation:** listed-only selection
excludes nonempty strings (including `"false"` and whitespace); absent, null,
and exactly empty strings are not excluded by that string rule. No SDK
listed-only default or prohibition on explicit acquisition was added.
Non-string eligibility interpretation requires independent supplier/consumer
evidence; the SDK does not guess.

Whole-listing JSON supports representative `Versions` records, not replay of
all-source state: `VersionCandidates`, `SourceRegistry`, `IsComplete`,
`QueryFailures`, and per-record provenance/latest fields are JSON-ignored.
Serializing a representative or an individual candidate separately retains
its raw marker and existing metadata, but not provenance. Deserializing the
whole-listing JSON cannot reconstruct the ignored candidates.

### Bounded Metadata Inventory and Local Evidence

This inventory covers **18 manifest JSON input properties total: 16 optional
plus required `name`/`version`**, every listed group and the nine recognized
nested members, unknown-byte controls, and relevant listing forms.
`SemVer`/`InferredFhirRelease` are computed, not additional input fields.
Baseline here means the retained model/read contract, not an independently
executed old-supplier comparison. Historical observations are inherited;
all candidate outcomes below link to the actual Windows runs in
[the execution record](#candidate-execution-record).

Fixture/test keys resolve to these checked-in, independently authored sources:

- **M** — [PackageManifestTests](../test/FhirPkg.Tests/Models/PackageManifestTests.cs),
  `Deserialize_OptionalFieldInventory_PreservesBaselineContracts`, data
  `OptionalFieldInventory`, literal recipe `CreateInventoryManifest`.
- **R/D/A** — the same file's `RepositoryForms`, `CompactDateForms`, and
  retained `AuthorForms`/`ObjectAuthorManifestJson`/`AuthorDuplicateMembers`.
  **F** is its retained `FullManifestJson`/`MinimalManifestJson` and inference
  examples; these are repository examples, not live-registry observations.
- **C** — [DiskPackageCacheTests](../test/FhirPkg.Tests/Cache/DiskPackageCacheTests.cs),
  literal manifest/archive recipes `CreatePreservationManifest`,
  `CreatePreservationTarball`, `RepositoryDateAuthorMatrix`,
  `RepositoryDateInstallControls`, `MalformedRepositoryOrDate`, and
  `MetadataIdentityAndArchiveFailures`.
- **L** — [PackageListingTests](../test/FhirPkg.Tests/Models/PackageListingTests.cs),
  independently authored `VersionJson`, `UnlistedForms`, `UnlistedLookupCases`,
  `UnlistedWriterCases`, and `ListingOptionalForms`.
- **S** — [PackageRegistryResolutionTests](../test/FhirPkg.Tests/Registry/PackageRegistryResolutionTests.cs),
  offline `SourceListingJson`/`PolicyListingJson` and `MarkerForms`, supplementing
  retained `FhirNpmClient_UsesSharedVersionPolicy`/`NpmClient_UsesSharedVersionPolicy`.
- **H** — [HardenedInstallationIntegrationTests](../test/FhirPkg.IntegrationTests/HardenedInstallationIntegrationTests.cs),
  generated `CreateBoundaryArchive`, `CreateMetadataBudgetArchive`,
  `CreateNativeFailureArchive`, and `CreatePreservationArchive`.

#### Per-case fixture/read matrix

M identifies each executed case exactly as **`row/control/reader`**.
Every row below has `absent`, `null`, `known-valid`, and
`malformed-shape:<literal>` controls for its listed rejected literals.
Nested row `parent.member` is independently inserted as
`"parent":{"member":<literal>}`; absent removes the parent. All recipes add
`"name":"test.package","version":"1.0.0"`. Each case executes on `String`,
`Stream`, and `DefaultSerializer` reader routes. The first two are
case-insensitive model helpers; the third is default case-sensitive external
`JsonSerializer`. Thus **210 row/control specimens × 3 readers = 630
inventory data cases per TFM**, not 630 cold archive imports.

Additional exact control IDs: dictionaries have `null-dictionary-value` and
`empty-dictionary`; lists have `null-list-element`; unknown has `ignored:42`,
`ignored:true`, `ignored:[]`, `ignored:"raw"`. In the table, **scalar bad**
means the literal set `42`, `true`, `{}`, `[]`; **dictionary bad** means
`42`, `true`, `[]`, `"x"`, `{"bad":42}`, `{"bad":true}`, `{"bad":{}}`,
`{"bad":[]}`; **list bad** means `42`, `true`, `{}`, `"x"`, `[42]`, `[true]`,
`[{}]`, `[[]]`. These finite sets are controls, not a promise about unseen
legacy shapes. “Pass” means the asserted accepted/rejected/ignored outcome
occurred, not that malformed data was accepted.

| Input field/member | Retained baseline model/read contract | Independent local fixture ID / known-valid literal and provenance | Candidate disposition and executed evidence | Original supplier / Firely / live corpus |
|--------------------|---------------------------------------|------------------------------------------------------------------|---------------------------------------------|-----------------------------------------|
| `name` (required) | Required presence; ordinary string reading is separate from native identity validation | F `MinimalManifestJson`; C `MetadataIdentityAndArchiveFailures`: `missing-name`, `empty-name`, `null-name`, `unsafe-name`, `mismatched-name` | Pass: missing rejected as JSON/native `InvalidArchive`; empty/null/unsafe and expected mismatch rejected as `InvalidPackageIdentity` at identity validation. [U11/U10](#candidate-execution-record) | **unverified** |
| `version` (required) | Required presence; concrete identity/selectors validated natively, not inferred from optional metadata | F `MinimalManifestJson`; C `missing-version`, `blank-version`, `range-version`, `mismatched-version`; M companion `Deserialize_RepositoryOrDateMalformedJsonAndMissingRequiredFields_Throws` | Pass: missing rejected as JSON/native `InvalidArchive`; blank/range/mismatch rejected at identity validation. [U11/U10](#candidate-execution-record) | **unverified** |
| `description` | `string?`, absent/null/string accepted; no shape relaxation | M `description/*/*`, `"description"`; F `"FHIR R4 Core definitions"` | Pass: valid/absent/null accepted, scalar bad rejected. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `license` | `string?`, absent/null/string | M `license/*/*`, `"CC0-1.0"`; F same | Pass: accepted baseline forms; scalar bad rejected. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `homepage` | `string?`, absent/null/string; no URL validation | M `homepage/*/*`, `"https://example.test/"`; F HL7 homepage | Pass: accepted baseline forms; scalar bad rejected. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `canonical` | `string?`, absent/null/string | M `canonical/*/*`, `"https://example.test/fhir"`; F `"http://hl7.org/fhir"` | Pass: accepted baseline forms; scalar bad rejected. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `type` | `string?`, absent/null/string | M `type/*/*`, `"ig"`; F `"fhir.core"` | Pass: accepted baseline forms; scalar bad rejected. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `title` | `string?`, absent/null/string | M `title/*/*`, `"Example IG"`; F `"FHIR R4"` | Pass: accepted baseline forms; scalar bad rejected. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `jurisdiction` | `string?`, absent/null/string | M `jurisdiction/*/*`, `"US"`; newly authored local row, not an F field | Pass: accepted baseline forms; scalar bad rejected. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `author` | R0 `string?`: string/object/null; lossy ordered projection, unchanged | M `author/*/*`, `{"name":"Example","email":"e@test","url":"raw"}`; A retained matrices | Pass: object → `"Example <e@test> (raw)"`; string/null preserved; `42`, `true`, `[]`, `[null]` rejected; unknown members ignored. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `author.name` | String/null; missing/null/empty omitted, whitespace not trimmed | M `author.name/*/*`, `"Example"`; A casing/duplicate/empty controls | Pass: → `"Example"`; scalar bad rejected, every recognized occurrence validated before last wins/clearing. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `author.email` | Same R0 member contract | M `author.email/*/*`, `"e@test"`; A | Pass: → `"<e@test>"`; scalar bad rejected; retained casing/duplicates/null/empty. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `author.url` | Same R0 member contract; no URL interpretation | M `author.url/*/*`, `"raw"`; A | Pass: → `"(raw)"`; scalar bad rejected; retained casing/duplicates/null/empty. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `repository` | Public `NpmRepository?`; baseline object/null, now bounded string projection | M `repository/*/*`, `{"type":"git","url":"raw","directory":"src"}`; R shorthand/URL/empty/Unicode/opaque members; C three-form matrix | Pass: object/null retained, all supplied strings project verbatim to Url; `42`, `true`, `[]`, `[null]` rejected; unknown members ignored. [T11/T10, U11/U10, H11/H10](#candidate-execution-record) | **unverified** |
| `repository.type` | String/null component; converter retains empty and option casing | M `repository.type/*/*`, `"git"`; R every-occurrence controls | Pass: string/null/empty, last clearing retained; scalar bad rejected even before later valid/null. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `repository.url` | String/null component; no validation/expansion | M `repository.url/*/*`, `"raw"`; R full URL/shorthand/whitespace | Pass: exact strings retained; scalar bad rejected; casing/duplicate controls pass. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `repository.directory` | String/null component; no trimming | M `repository.directory/*/*`, `"src"`; R empty/whitespace | Pass: exact strings/null/empty retained; scalar bad rejected; every-occurrence/clearing controls pass. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `date` | Public `string?`; baseline arbitrary strings/null, now exactly 8/14 ASCII-digit numeric tokens | M `date/*/*`, `20240203123456`; D lexical/date-shape controls; C `object8`, `shorthand8`, `baseline`, `absent`, `null`, `empty` | Pass: numeric → lexical string; all supplied strings verbatim; scalar bad and D signed/fraction/exponent/other lengths rejected. Listing dates are separate below. [T11/T10, U11/U10, H11/H10](#candidate-execution-record) | **unverified** |
| `dependencies` | `IReadOnlyDictionary<string,string>?`; serializer accepts null **values** despite C# annotation | M `dependencies/*/*`, `{"dependency.package":"2.0.0"}`, `{"nullable":null}`, `{}`; F dependency/inference examples | Pass: absent/null/string map/null value/empty map accepted; dictionary bad rejected. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `devDependencies` | Same ordinary dictionary contract, no new permissive converter | M `devDependencies/*/*`, `{"dev.package":"2.0.0"}`, null-value/empty controls | Pass: accepted baseline forms including null values; dictionary bad rejected. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `dist-tags` | Same ordinary dictionary contract | M `dist-tags/*/*`, `{"latest":"1.0.0"}`, null-value/empty controls; S retained tags | Pass: accepted baseline forms including null values; dictionary bad rejected. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `keywords` | `IReadOnlyList<string>?`; null elements accepted by baseline reader | M `keywords/*/*`, `["fhir","ig"]`, `["fhir",null]`; F keywords | Pass: absent/null/string list/null element accepted; list bad rejected. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `fhirVersions` | Same list reading; inference uses first element, then dependency-name fallback | M `fhirVersions/*/*`, `["4.0.1"]`, `["4.0.1",null]`; F inference tests; `Deserialize_NullCollectionValues_PreservesBaselineInferenceContracts` | Pass: null elements accepted, list bad rejected; `[null]` still throws `ArgumentNullException("version")` **on inference**, not deserialization; dependency-only null value still infers R5 by key. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `dist` | `NpmDistribution?`, ordinary object/null; no converter change | M `dist/*/*`, `{"shasum":"sha1","tarball":"https://example.test/p.tgz","integrity":"sha256-value"}`; L/S distribution examples | Pass: object/absent/null accepted; `42`, `true`, `[]`, `"x"` rejected. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `dist.shasum` | `string?` SHA-1 metadata, ordinary serializer string/null | M `dist.shasum/*/*`, `"sha1"`; L/S source hash fixtures | Pass: baseline forms accepted; scalar bad rejected; paired source hash retained. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `dist.tarball` | `string?` artifact URL, ordinary serializer | M `dist.tarball/*/*`, `"https://example.test/p.tgz"`; L/S source artifacts | Pass: baseline forms accepted; scalar bad rejected; source pairing unchanged. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| `dist.integrity` | `string?` integrity metadata, ordinary serializer | M `dist.integrity/*/*`, `"sha256-value"`; L/S distinct-source integrity | Pass: baseline forms accepted; scalar bad rejected; no cross-source splicing. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| Unknown manifest properties | Ignored by model; native archive bytes/tree remain authoritative | M `unknown/*/*`, `{"opaque":[1,true,null,{"nested":"unchanged"}]}` and four `ignored:*`; C unknown manifest/root bytes/empty directory; H `unknown-manifest` | Pass: supplied shapes ignored by model; original manifest/resource/ancillary bytes and explicit directories verified immediately and after reopen. Not a model round-trip promise. [T11/T10, U11/U10, H11/H10](#candidate-execution-record) | **unverified** |
| Listing `unlisted` | Previously discarded; now raw `JsonElement`, no eligibility semantics | L `UnlistedForms` 16 named specimens; S `MarkerForms` six absence/null/string specimens, actual leaf/composite/fallback routes | Pass: absent/null/all JSON kinds retained with exact decoded strings, owned read clones, lookup and writer controls; ignored provenance/candidates stay ignored. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| Listing `date` | Custom string lookup only, not manifest converter | L `ListingOptionalForms`: `date/<label>`, `date/exact-last`, `date/insensitive-first`; retained `VersionJson` | Pass: strings including empty/whitespace accepted verbatim; numeric date/bool/object/arrays ignored to null; malformed JSON rejected. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| Listing `fhirVersion` | Nonblank string or array string entries, presence and singular-first order retained | L `fhirVersion/<label>`, `fhir/exact-last`, `fhir/insensitive-first`, `combined/deduplicated`; S shared-policy examples | Pass: supplied nonblank strings accepted; blanks/non-string/malformed entries ignored, mixed arrays retain only usable strings; malformed JSON rejected. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |
| Listing `fhirVersions` | Same lookup/entry contract; plural follows singular, case-insensitive deduplication | L `fhirVersions/<label>`, `combined/deduplicated`; S plural declarations | Pass: supplied nonblank strings accepted, other shapes/entries ignored; explicit empty metadata preserved on writing; malformed JSON rejected. [T11/T10, U11/U10](#candidate-execution-record) | **unverified** |

L `UnlistedForms` exact IDs are `absent`, `null`, `empty`, `string-true`,
`string-false`, `whitespace`, `escaped-string`, `boolean-true`, `boolean-false`,
`zero`, `negative`, `fraction-exponent`, `object`, `duplicate-object-members`,
`array`, `empty-array`. `UnlistedLookupCases` supplies 12 literal spelling/
duplicate cases × both casing options; `UnlistedWriterCases` supplies each of
the 16 specimens × `Never`/`WhenWritingNull`/`WhenWritingDefault`. The exact
test IDs are `Deserialize_UnlistedForms_PreservesPresenceAndOwnedValue`,
`Deserialize_UnlistedCasingAndDuplicates_UsesExistingListingLookup`,
`Serialize_UnlistedForms_RoundTripsWithoutDroppingNull`, and
`Serialize_ListingWithUnlisted_RetainsIgnoredProvenance`.

L `ListingOptionalForms` labels for **each** date/singular/plural field are
`absent`, `null`, `empty-string`, `whitespace`, `compact-string`,
`ordinary-string`, `compact-number`, `boolean`, `object`, `empty-array`,
`string-array`, `mixed-array`, plus the five combined lookup/deduplication
controls above: **41 data cases**. Each checks direct/stream reading with both
casing options and round-trip writing in
`Deserialize_ListingOptionalForms_PreservesExistingDateAndFhirRules`.
`Deserialize_ListingOptionalMalformedJsonOrRequiredFields_RemainRejected`
adds seven malformed/required-string controls.

#### Projection, native, and policy companion controls

| Exact test IDs / fixture recipe | Bounded outcome established | Candidate evidence |
|--------------------------------|-----------------------------|--------------------|
| M/R `Deserialize_RepositoryForms_PreservesProjection`, `Deserialize_RepositoryMembers_RespectsCasingAndEveryOccurrence`, `Deserialize_RepositoryAndDateCasing_UsesExplicitOptions` | Three reader routes, object/string/null/empty/unknown, option casing, every recognized nested occurrence and clearing | Pass [T11/T10, U11/U10](#candidate-execution-record) |
| M/D `Deserialize_CompactDateForms_PreservesLexicalText`, `Deserialize_InvalidRepositoryOrDate_ThrowsJsonException`, `Deserialize_DuplicateRepositoryOrDate_ValidatesEveryOccurrence`, `Deserialize_RepositoryOrDateMalformedJsonAndMissingRequiredFields_Throws`, `Serialize_RepositoryAndDate_UsesExistingPublicTypes` | 8/14 numeric/string, noncalendar and arbitrary strings; signed/fraction/exponent/length/kind/invalid-JSON rejection; top-level duplicates in both orders, external/helper output/null policy | Pass [T11/T10, U11/U10](#candidate-execution-record) |
| Retained A `Deserialize_AuthorForms_ReturnExpectedValue`, `Deserialize_AuthorMemberCasing_RespectsSerializerOptions`, `Deserialize_AuthorDuplicateMembers_UsesLastRecognizedValue`, `Deserialize_InvalidAuthorShapes_ThrowsJsonException`, author serialization tests | R0 projection/validation unchanged; every-occurrence/casing/null/empty/writer behavior retained | Pass [T11/T10, U11/U10](#candidate-execution-record) |
| C `ImportAsync_RepositoryDateAuthorMatrix_PreservesBytesAndTree`, `RepositoryDateAuthorMatrix` | Three repository forms × two fourteen-digit date forms × two author forms = **12 cold imports per TFM**, each isolated identity/cache, SHA-256, JSON/XML, root opaque bytes, empty directory; complete bytes/tree/readback | Pass [U11/U10](#candidate-execution-record); 24 imports across the two TFMs |
| C `InstallAsync_RepositoryDateForms_ValidatesAndPreservesBytes`, `ImportAsync_RepositoryDateControls_PreservesBytesAndTree`, `RepositoryDateInstallControls` | `shorthand14`, `url14`, `object8`, `shorthand8`, `baseline`, `absent`, `null`, `empty` × ordinary/unmodified: **16 expected installs + 16 extra imports per TFM** | Pass [U11/U10](#candidate-execution-record) |
| C `ImportAsync_MalformedRepositoryOrDate_ReturnsTypedManifestFailure`, `MalformedRepositoryOrDate`; `ImportOrInstallAsync_InvalidIdentityAndArchive_PreservesPriorAndNeighbour`, `MetadataIdentityAndArchiveFailures` | **131 malformed fixtures × missing/overwrite = 262 failed-import controls**, plus **32 identity/archive controls per TFM**; typed paths/inner JSON, no publication/debris, prior/neighbour snapshots before SDK read | Pass [U11/U10](#candidate-execution-record) |
| S `IndividualClients_UnlistedForms_PreserveSameResponseMetadata`, `CompositeListing_UnlistedMarkers_StayPairedWithSource`, `CompositeListing_UnlistedMarkers_RepresentativeOnlyFallbackStaysPaired`, `ResolveAndDownload_UnlistedMarker_DoesNotChangeSdkPolicy` | 12 individual, 12 composite, 12 fallback, 18 policy/download cases per TFM; configured order, exact record/source pairing, one response per queried source, unchanged 270 resolutions/18 explicit downloads | Pass [U11/U10](#candidate-execution-record) |
| [PackageInstallLimitsTests](../test/FhirPkg.Tests/PackageInstallLimitsTests.cs) `DirectCache_CompleteExplicitPolicy_OverridesAllAmbientFields`, `DirectCache_PartialPolicy_UsesExplicitEnvironmentDefaultPrecedence`, `DirectCache_PartialPolicy_MixesExplicitEnvironmentAndDefaults`, `DirectCache_InvalidPolicy_FailsBeforeRootCreation`, `DirectCache_PolicySnapshots_AreIndependent`, `DirectCache_PerCallLimits_OnlyTightenConstructionPolicy`, higher-finite and retry controls | All six fields, complete/partial malformed ambient precedence, safe validation before root, two independent mutable caller policies, per-call equality/tightening/upward pre-read rejection | Pass [T11/T10, U11/U10](#candidate-execution-record) |
| [RegistryClientFactoryTests](../test/FhirPkg.Tests/Registry/RegistryClientFactoryTests.cs) `EndpointPolicyOverloads_CompleteExplicitPolicy_AvoidsAmbientParsing`, `EndpointPolicyOverloads_PartialAndInvalidPolicy_RetainValidation`, partial-mixed/invalid-explicit/invalid-retry/ordinary-argument/higher-finite/retry-snapshot controls; `EndpointPolicyOverloads_NpmSnapshots_ControlPublishPreparation` | Both transport families × four endpoint types, all six fields, precedence/snapshots/no HTTP on invalid configuration; NPM accepted preparation reaches offline PUT or retained unverified-body guard, quota failures send no request | Pass [T11/T10, U11/U10](#candidate-execution-record); no live publication |
| Same factory tests `StandaloneConstructorsAndLegacyEndpointFactories_PreserveBindings`, `PublicPolicySurface_AdditionalRequiredArity_PreservesLegacySignatures` | Public-only parameter types/names/default flags, exactly one new required-five cache/two required-six endpoint overloads; old null/default/named/typed-null bindings | Pass [T11/T10, U11/U10](#candidate-execution-record); friend-assembly/source evidence, not external packaged binding |
| H `DirectCache_ConfiguredLimits_BelowAtAbove_UseNativeValidation`, `NativeLimitBoundaries` | **18 cases per TFM**, six measured quantities q with ceilings q+1/q/q−1; below/at pass, above typed failure. Expanded q=4146 bytes, entry=4096, yielded=5 (3 files/2 explicit directories), path=54 ASCII UTF-16 units, depth=5; compressed q is runtime gzip length logged by each case | Pass [H11/H10](#candidate-execution-record); small-policy mechanics, not all default envelopes |
| H `DirectCache_MetadataEntryBudget_BelowAtAbove_PreservesPreflight`; `DirectCache_DefaultDepth32And33_RetainsFiniteCeiling` | **3 hidden-metadata cases**, four hidden GNU entries/39 bytes versus two yielded, ceilings 5/4/3; **1 depth case with 3 operations**: default32 accepted, default33 rejected, explicit33 accepted | Pass [H11/H10](#candidate-execution-record); environment restored, no filesystem relabel/mandatory skip |
| H `EndpointDownload_NativeImport_EnforcesReportedAndActualLength`, `EndpointLengthCases` | **80 cases per TFM**: both transports/FHIR-NPM/NPM; `accurate`, `absent`, `under`, `over-within`, `negative`, `over-rejected`, `accurate-rejected`, `absent-excess`, `under-excess` × SHA-256 presence, plus `bad-sha1`/`bad-sha256`; pre-read vs actual checks, mappings and response disposal | Pass [H11/H10](#candidate-execution-record); no download spool/strict length equality added |
| H `DirectCache_FailedImportOrReplacement_PreservesPriorAndNeighbourTrees`, `NativeFailureCases` | **84 cold/overwrite import/expected-install cases per TFM**: six quotas, both hashes, invalid/mismatched identity, JSON/repository/date, traversal/link/truncated/ambiguous archive, read failure, acquisition/extraction cancellation. No import-only expected mismatch. Prior/neighbour/cache metadata and cleanup checked before recovery-capable reads; caller stream remains open, caller cancellation unwrapped | Pass [H11/H10](#candidate-execution-record); not a Candle adapter repair |
| H `ManagerSourceModes_RepositoryDateMetadata_UsesSameProjection`, `NativeMetadataSourceModes` | **96 cases per TFM**: four stream/URI import/expected modes × two layouts × ordinary/unmodified × three repository forms × two date forms; actual manager resources, bytes/empty directories, cache reopen | Pass [H11/H10](#candidate-execution-record) |

Phase-2 additions total **206 cases per TFM**: 16 raw reads, 24 lookup,
48 writer, 16 listing serialization, 41 optional, 7 malformed/required,
12 individual, 12 composite, 12 fallback, 18 resolve/download.
Phase-3 additions total **786 cases per TFM** (95 cache-policy + 691 factory):
70 accepted imports, 36 quota failures, 156 upward pre-read rejections;
152 accepted NPM preparations (76 offline PUTs + 76 unverified-body guards)
and 72 quota failures without request.
Phase-4 generated additions total **282 per TFM**:
18 six-limit + 3 metadata + 1 default-depth (three operations) + 80 endpoint +
84 failure + 96 manager. These are executed additions, not entire filter
totals. No new mandatory case skipped.

#### Candidate execution record

Source candidate HEAD: **`cd87e9996abcd2a83c79e23dcbfa4616b25ed366`**.
Implementation commits: phase1 `fa2ce5ee40d5ffd44b7b8c3981dd3464216401fc`,
phase2 `127c5c4696f160dc5c734951dcefc755760dc19e`,
phase3 `5a586e2e188fe752f19ed08625c5b06790af3cb0`,
phase4 `cd87e9996abcd2a83c79e23dcbfa4616b25ed366`.
Documentation is the uncommitted phase-5 working-tree addition to that source,
not a new release/asset identity.

Executed locally on **2026-11-13, Windows 10.0.26300 win-x64**, SDK
**11.0.100**, Microsoft.NETCore.App **11.0.0 and 10.0.15**.
Conventions source: full root `AGENTS.md` (no fallback).
`dotnet --info` confirmed the installed SDK/runtimes. All commands below were
run from the repository root with Windows backslash paths, the documented
VSTest runner/filter semantics, and the exact framework selectors; no extra
logger, parallelism, qualification setup, or tooling was added.

| Evidence ID | Actual outcome |
|-------------|----------------|
| T11 / T10 | Phase-5 targeted filter: each **2440 passed, 0 failed, 0 skipped, total 2440** |
| Restore | Solution restore succeeded (exit 0), no warnings/errors reported |
| Release | Solution Release build succeeded for both TFMs, **0 warnings, 0 errors** |
| U11 / U10 | Full unpinned unit project: each **5481 passed, 0 failed, 20 skipped, total 5501** |
| N11 / N10 | Non-hardened integration: each **351 passed, 0 failed, 0 skipped, total 351** |
| H11 / H10 | Hardened/cross-process integration: each **348 passed, 0 failed, 3 skipped, total 351** |

Every invocation exited 0; no build/test warning or error was emitted.
The exact executed phase-5 and final commands were:

```powershell
dotnet test test\FhirPkg.Tests\FhirPkg.Tests.csproj --framework net11.0 --filter "FullyQualifiedName~PackageManifestTests|FullyQualifiedName~PackageListingTests|FullyQualifiedName~PackageInstallLimitsTests|FullyQualifiedName~RegistryClientFactoryTests"
dotnet test test\FhirPkg.Tests\FhirPkg.Tests.csproj --framework net10.0 --filter "FullyQualifiedName~PackageManifestTests|FullyQualifiedName~PackageListingTests|FullyQualifiedName~PackageInstallLimitsTests|FullyQualifiedName~RegistryClientFactoryTests"
dotnet restore FhirPkg.sln
dotnet build FhirPkg.sln --configuration Release --no-restore
dotnet test test\FhirPkg.Tests\FhirPkg.Tests.csproj
dotnet test test\FhirPkg.IntegrationTests\FhirPkg.IntegrationTests.csproj --framework net11.0 --filter "FullyQualifiedName!~HardenedInstallationIntegrationTests&FullyQualifiedName!~CrossProcessCacheIntegrationTests"
dotnet test test\FhirPkg.IntegrationTests\FhirPkg.IntegrationTests.csproj --framework net10.0 --filter "FullyQualifiedName!~HardenedInstallationIntegrationTests&FullyQualifiedName!~CrossProcessCacheIntegrationTests"
dotnet test test\FhirPkg.IntegrationTests\FhirPkg.IntegrationTests.csproj --framework net11.0 --filter "FullyQualifiedName~HardenedInstallationIntegrationTests|FullyQualifiedName~CrossProcessCacheIntegrationTests"
dotnet test test\FhirPkg.IntegrationTests\FhirPkg.IntegrationTests.csproj --framework net10.0 --filter "FullyQualifiedName~HardenedInstallationIntegrationTests|FullyQualifiedName~CrossProcessCacheIntegrationTests"
```

Full-unit skips on each TFM were the existing Windows-inapplicable native Unix/
macOS tests: `UnixDescriptorInteropTests.RealUnix_FailuresRetainImmediateErrnoAcrossLaterCalls`,
`RealUnix_DuplicateUsesNontrivialMinimumAndCloseOnExec`,
`RealUnix_ModeOpenAndNoModeOpenPreservePermissions`, three
`RealMacOS_ReadAheadUsesProtectedHandle` options; two
`NativeFileSystemTests.UnixNative_RegularOpenAndRecursiveDeleteDoNotFollowLinks`
platforms; and twelve `PositionedFileStreamTests` permission/mode cases
(`OwnedCreation_MatchesManagedPermissionsAndCanReopen` six,
`OwnedCreation_ExistingFilePreservesModeAndModeSemantics` four,
`OwnedCreation_CreateNewCollisionPreservesForeignFile` two).
Hardened skips on each TFM were existing retained-input opt-ins:
`RetainedR4CorePrimary_CliCache_IsUsable`,
`RetainedR4CorePrimary_ExpectedStreamInstall_IsUsable`,
`RetainedR4CoreSecondary_ExpectedStreamInstall_IsUsable`.
No retained archive/cache setup was invented or run. These skips establish
neither generated-case failure nor external retained-input success.

Inherited coordinator phase evidence, **not rerun as those filters by the
documentation implementer**, is separate from the final-source runs above:

| Checkpoint | Exact VSTest class selector (unit unless stated) | Recorded result on each net11.0/net10.0 |
|------------|------------------------------------------------|----------------------------------------|
| Phase1 | `FullyQualifiedName~PackageManifestTests\|FullyQualifiedName~DiskPackageCacheTests` | 1693 passed, 0 failed, 0 skipped |
| Phase2 | `FullyQualifiedName~PackageListingTests\|FullyQualifiedName~PackageRegistryResolutionTests\|FullyQualifiedName~RedundantRegistryClientTests\|FullyQualifiedName~RegistryOperationExceptionTests` | 264 passed, 0 failed, 0 skipped |
| Phase3 | `FullyQualifiedName~PackageInstallLimitsTests\|FullyQualifiedName~RegistryClientFactoryTests\|FullyQualifiedName~DiskPackageCacheTests\|FullyQualifiedName~NpmRegistryClientTests` | 1084 passed, 0 failed, 0 skipped |
| Phase4 native unit | `FullyQualifiedName~TarballExtractorLimitTests\|FullyQualifiedName~PackageContentAcquirerTests\|FullyQualifiedName~PackageIdentityValidatorTests\|FullyQualifiedName~PortableArchivePathTests\|FullyQualifiedName~RegistryOperationExceptionTests` | 147 passed, 0 failed, 0 skipped |
| Phase4 integration | `FullyQualifiedName~HardenedInstallationIntegrationTests\|FullyQualifiedName~CrossProcessCacheIntegrationTests` | 348 passed, 0 failed, 3 retained-core skips |

The pipes above are VSTest OR operators (escaped only for Markdown tables).
Earlier phase1/3
hardened runs were each 66 passed/3 retained-core skips before phase4 additions.
Coordinator SDK net11 builds reported 0 warnings/errors. The final full unit
and integration runs now execute those retained and new local controls on the
coherent source candidate.

Manual comparison covered exact signatures, nullable/required arguments,
examples, all inventory rows/recipes and caller routes in the four changed SDK
files and tests, plus unchanged resolver/provenance/acquisition/NPM mechanisms.
Existing license headers, explicit types, file-scoped namespaces, Allman
braces, nullable handling and net10-compatible converter APIs were retained.
No format/lint command or review dispatch was run.

Local **L1–L7** have passing bounded evidence on the exercised Windows TFMs.
This is **source-local readiness only**, not publication, packaged-binary or
external-consumer compatibility, Firely equivalence, Candle migration, all
production-default envelopes, or multi-platform qualification:

| Later gate | Obligation | Exact-candidate state |
|------------|------------|-----------------------|
| Q1 | Same independent archives through Firely 5.0.2; independent/copied cache byte/tree and old/replacement readback comparison | **NOT RUN**; inherited author evidence is not these cases |
| Q2 | Actual synchronized packaged net10/net11 SDK/CLI assets, version/commit/hashes and externally compiled old-source/additive public bindings | **NOT RUN**; friend-assembly public-only inspection is narrower |
| Q3 | Measured production-default below/at/above boundaries for all six fields and Candle finite envelope decision | **NOT RUN**; small-policy/default-depth controls are narrower |
| Q4 | Candle listed-only/C1 fallback/C2 publication safety and R4/R4B/R5 cold resource/order/dependency/failure controls on both TFMs (72 core cases before extras) | **NOT RUN**; no Candle edits or eligibility inference |
| Q5 | Exact-candidate Windows/Linux/macOS supported-runtime release provenance/license/validation and one outcome checklist | **NOT RUN**; Windows-local testing does not certify Linux/macOS or release assets |

Original-supplier, Firely and live corpus contents were not independently
inspected for this inventory: **unverified in every row**. Unseen forms are not
silently accepted or rejected on someone else's behalf. No pack, publish,
release/recovery/drift workflow, external consumer project, qualification
harness, or other platform run was performed.

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

### CiBuildRecord & CiBuildManifest

Metadata published by the FHIR CI build server. `FhirCiBuildClient` reads
`CiBuildRecord` entries from the server's `qas.json` index when resolving
`current` and `current$branch` directives for an implementation guide, and
reads a `CiBuildManifest` from the `package.manifest.json` published beside a
repository's default build.

```csharp
public record CiBuildRecord
{
    public string? Url { get; init; }                // "url"
    public string? Name { get; init; }               // "name"
    public string? Title { get; init; }              // "title"
    public required string PackageId { get; init; }  // "package-id"
    public string? IgVersion { get; init; }          // "ig-ver"
    public required string Date { get; init; }       // "date"; raw build date
    public string? DateISO8601 { get; init; }        // "dateISO8601"
    public required string Repo { get; init; }       // "repo"; see ParseRepo
    public string? FhirVersion { get; init; }        // "fhir-version"
    public int? Errors { get; init; }                // "errors"
    public int? Warnings { get; init; }              // "warnings"

    public (string Org, string RepoName, string Branch)? ParseRepo();
}

public record CiBuildManifest
{
    public string? Name { get; init; }
    public string? Version { get; init; }                         // often "current" or "current$branch"
    public string? Date { get; init; }
    public IReadOnlyList<string>? FhirVersion { get; init; }       // "fhirVersion"; emitted by the CI build server
    public IReadOnlyList<string>? FhirVersions { get; init; }      // "fhirVersions"; npm-style
    public IReadOnlyList<string>? EffectiveFhirVersions { get; }   // FhirVersion ?? FhirVersions; JSON-ignored
    public string? Jurisdiction { get; init; }
}
```

`ParseRepo` accepts `Org/Repo/Branch/qa.json` and
`Org/Repo/branches/Branch/qa.json`, and returns `null` for every other shape;
records whose `Repo` cannot be parsed are discarded during CI build
resolution. `EffectiveFhirVersions` prefers the singular `fhirVersion` key the
build server actually emits over the plural npm-style `fhirVersions`.

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
    public IReadOnlyList<string>? ResolutionWarnings { get; init; }
}
```

`ResolutionWarnings` carries non-fatal diagnostics about how the source was
chosen, and is `null` when the resolution raised none. CI build resolution
populates it when the selected build is not the canonical repository's default
build, when canonical selection fell through to the oldest build, when an
explicit `@current$branch` resolved to a non-canonical publisher, or when the
requested FHIR release excluded every build from the canonical organization. The
CLI prints these under `resolve` and includes them in `--json` output.

### PackageInstallResult

Returned per-directive from `InstallManyAsync`.

```csharp
public record PackageInstallResult
{
    public required string Directive { get; init; }
    public PackageRecord? Package { get; init; }
    public PackageInstallStatus Status { get; init; }
    public PackageInstallDisposition? Disposition { get; init; }
    public string? PreviousManifestDate { get; init; }
    public string? ManifestDate { get; init; }
    public string? ErrorMessage { get; init; }
    public PackageInstallErrorCode? ErrorCode { get; init; }
    public PackageInstallStage? ErrorStage { get; init; }
    public IReadOnlyList<PackageInstallResult> DependencyFailures { get; init; }
}
```

`Disposition` refines successful mutable-CI directives only. `Installed`,
`Updated`, `AlreadyCurrent`, and `Refreshed` all retain
`Status == PackageInstallStatus.Installed`; existing status-based callers
therefore keep the same compatibility behavior. Non-CI installs, failures, and
mutable-CI download/commit paths whose cache cannot report an authoritative
effect leave `Disposition`, `PreviousManifestDate`, and `ManifestDate` null.
Manager-owned already-current short circuits can still report their recognized
outcome without a cache commit.

The date fields are nullable `package.json` `date` projections: verbatim
strings or the lexical text of accepted compact numeric tokens. For an update,
`PreviousManifestDate` identifies the replaced cache entry and `ManifestDate`
identifies the resulting package. Dates are presentation data and are not used
to decide whether the cache changed.

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
    public IReadOnlyList<PackageReference> ResolvedPackages { get; init; } = [];
    public IReadOnlyList<PackageInstallationIdentity> InstallationIdentities { get; init; } = [];
    public required IReadOnlyDictionary<string, string> Missing { get; init; }
    public IReadOnlyList<DependencyResolutionFailure> Failures { get; init; } = [];
    public IReadOnlyList<PackageReference> InstallOrder { get; init; } = [];
    public IReadOnlyList<PackageReference> BootstrapInstallOrder { get; init; } = [];
    public bool InstallOrderIsComplete { get; init; }
    public bool IsComplete { get; }  // true when Missing and Failures are empty
}
```

`ResolvedPackages` is the authoritative complete closure: every reachable exact
name/version identity appears once, including coexisting versions of the same
package. `Resolved` is the conflict-policy-selected preferred projection keyed
by package name and is retained for compatibility and convenient lookup.

`InstallationIdentities` maps each reference that may appear in installation or
bootstrap order to its exact resolved manifest identity. `InstallOrder` may
preserve a mutable CI alias as the cache/install reference; consumers that
install those references should use the mapping instead of treating an alias as
an exact identity.
`BootstrapInstallOrder` identifies CI aliases whose exact identity or
authoritative dependency metadata is only available after installation; the
manager installs those aliases and re-resolves before installing the final
exact order. An empty `InstallOrder` is authoritative when
`InstallOrderIsComplete` is `true`.

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

### PackageInstallationIdentity

```csharp
public sealed record PackageInstallationIdentity
{
    public required PackageReference InstallationReference { get; init; }
    public required PackageReference ResolvedReference { get; init; }
}
```

`InstallationReference` is the directive passed to the installer and can retain
mutable aliases such as `current` or `dev`. `ResolvedReference` is the exact
name/version identity reported by the resolved package manifest.

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

FHIR-aware semantic version with exact numeric precision, pre-release
hierarchies, part-wise wildcard patterns, and range helpers.

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

    // Throws ArgumentException/FormatException on invalid input; never returns null.
    public static FhirSemVer Parse(string versionString);
    public static bool TryParse(string? versionString, out FhirSemVer? result);

    public bool Satisfies(string versionSpecifier);
    public bool Satisfies(FhirSemVer other);

    // Highest version satisfying one exact/wildcard specifier (not range syntax).
    public static FhirSemVer? MaxSatisfying(
        IEnumerable<FhirSemVer> versions, string specifier, bool includePreRelease = false);

    // ALL versions in 'versions' that satisfy the range expression (static).
    public static IEnumerable<FhirSemVer> SatisfyingRange(
        IEnumerable<FhirSemVer> versions, string rangeExpression);

    public int CompareTo(FhirSemVer? other);
    // Also defines ==, !=, <, >, <=, >= operators (via IComparable/IEquatable).
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
| Exact | `4.0`, `4.0.1`, `6.0.0-ballot1` | Exact; two-part and three-part precision are distinct |
| Numeric wildcard | `2.*`, `2.x.x`, `*.0.0`, `2.*.0` | Matches only the stated numeric parts and part count |
| Label/build wildcard | `2.0.0-*`, `2.0.0+*`, `2.0.x-*` | Requires the wildcarded label/build part |
| Trailing remainder | `2.0?`, `2.0.1?`, `2.x?` | Matches the current part, then ignores all remaining parts |
| All versions | `*` | Any concrete supported version, subject to pre-release policy |
| Range (caret) | `^4.0.0` | Range — compatible (≥4.0.0, <5.0.0) |
| Range (tilde) | `~4.0.0` | Range — approximately (≥4.0.0, <4.1.0) |
| Range (between) | `4.0.0 - 5.0.0` | Range — inclusive |
| Range (or) | `4.0.1 \| 5.0.0` | Range — either |

`*` may occupy any supported part. `x`/`X` alias numeric minor and patch
wildcards only; they remain literal label text. Omitted parts must be absent
unless a trailing `?` ignores them. See the
[versioning reference](../reference/versioning.md#2-part-wise-wildcard-patterns)
for the authoritative matching table and package-level prerelease policy.

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
| `Exact` | `4.0`, `4.0.1` | Exact two-part or three-part version |
| `Wildcard` | `4.0.x`, `4.x?`, `6.0.x-*` | Defined part-wise FHIR pattern |
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
| `Installed` | Successful compatibility status, including every mutable-CI disposition |
| `AlreadyCached` | Already present in cache (skipped) |
| `Failed` | Installation failed (see `ErrorMessage`) |
| `NotFound` | Package could not be found in any registry |

### PackageInstallDisposition

Nullable refinement for successful `current` and `current$branch` installs.

| Value | Description |
|-------|-------------|
| `Installed` | The mutable alias was committed into a previously missing cache target |
| `Updated` | Existing cached content was replaced, regardless of manifest-date equality |
| `AlreadyCurrent` | The source/cache check confirmed that no replacement was needed |
| `Refreshed` | An explicit overwrite downloaded the alias but confirmed unchanged content |

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
| `HighestWins` | Prefer the highest version in `Resolved` (default); retain all exact versions |
| `FirstWins` | Prefer the first version encountered in `Resolved`; retain all exact versions |
| `Error` | Prefer the first version in `Resolved`, retain all exact versions, and record a typed conflict failure |

### DependencyResolutionFailureCode

| Value | Description |
|-------|-------------|
| `PackageNotFound` | A requested version could not be resolved |
| `VersionConflict` | Active parent edges selected different exact versions under the `Error` strategy |
| `DepthLimitExceeded` | An active edge exceeded the root-relative maximum depth |
| `MetadataUnavailable` | Dependency metadata could not be proven complete |
| `RegistryUnavailable` | Registry failures prevented authoritative version resolution |
| `UnstableResolution` | Retained for source compatibility; the built-in exact-node resolver does not emit it |
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
| `Acquiring` | Acquiring content into bounded local staging |
| `Validating` | Validating archive shape, layout, manifest, and identity |
| `WaitingForLock` | Waiting to acquire the package mutation lock |
| `Repairing` | Repairing an invalid existing cache entry |
| `Committing` | Committing validated content to the cache |

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

### CorruptCacheBehavior

Controls how installation handles an existing cache entry that is invalid.

| Value | Description |
|-------|-------------|
| `Repair` | Quarantine and replace the invalid entry after validation succeeds (default) |
| `Strict` | Reject installation with a typed corruption error |
| `Throw` | Alias for `Strict` |

### PackageInstallErrorCode

Stable failure categories carried by `PackageInstallException.ErrorCode` and
`PackageInstallResult.ErrorCode`.

| Value | Description |
|-------|-------------|
| `InvalidPolicy` | The effective installation policy is invalid |
| `ResolutionFailed` | The package directive could not be resolved |
| `DownloadFailed` | The package source could not be acquired |
| `CompressedSizeLimitExceeded` | The compressed package size exceeded policy |
| `ExpandedSizeLimitExceeded` | The expanded package size exceeded policy |
| `EntrySizeLimitExceeded` | One archive entry exceeded policy |
| `ArchiveEntryCountLimitExceeded` | The archive entry count exceeded policy |
| `ArchivePathLengthLimitExceeded` | An exact or canonical archive path exceeded extraction policy |
| `ArchiveDepthLimitExceeded` | An archive path nesting depth exceeded policy |
| `ChecksumMismatch` | A package checksum did not match its expected value |
| `InvalidArchive` | The package archive is structurally invalid |
| `InvalidPackageIdentity` | The requested or discovered package identity is invalid |
| `CorruptCache` | An existing cache target is corrupt |
| `CoordinationFailed` | Package operation coordination failed |
| `CommitFailed` | The validated package could not be committed to the cache |
| `UnsupportedManagerCapability` | The manager does not support the requested capability |
| `UnsupportedCacheCapability` | The configured cache does not implement the hardened capability |
| `DependencyInstallationFailed` | One or more requested dependencies could not be installed |

### PackageInstallStage

Identifies the installation stage at which a failure occurred (carried by
`PackageInstallException.Stage` and `PackageInstallResult.ErrorStage`).

| Value | Description |
|-------|-------------|
| `PolicyValidation` | Manager and per-call policy validation |
| `Resolution` | Directive resolution |
| `Acquisition` | Package source acquisition |
| `ChecksumValidation` | Checksum verification |
| `ArchiveValidation` | Archive shape and content validation |
| `IdentityValidation` | Manifest identity validation |
| `CacheInspection` | Existing cache inspection |
| `Coordination` | Cross-instance or cross-process coordination |
| `Commit` | Cache promotion and metadata commit |
| `DependencyInstallation` | Dependency installation |

### RegistryFailureCategory

Safe, public category for a failed registry attempt
(`RegistryAttemptFailure.Category`).

| Value | Description |
|-------|-------------|
| `Network` | The registry could not be reached |
| `Timeout` | The registry operation exceeded its deadline |
| `HttpResponse` | The registry returned an unsuccessful HTTP response |
| `InvalidResponse` | The registry returned data that could not be processed |
| `Unexpected` | The registry attempt failed for another reason |

---

## Cache

### IPackageCache

Interface for local package storage operations.

```csharp
public interface IPackageCache : IDisposable
{
    string CacheDirectory { get; }

    Task<bool> IsInstalledAsync(PackageReference reference, CancellationToken ct = default);
    Task<PackageRecord?> GetPackageAsync(PackageReference reference, CancellationToken ct = default);
    Task<IReadOnlyList<PackageRecord>> ListPackagesAsync(
        string? packageIdFilter = null, string? versionFilter = null, CancellationToken ct = default);
    Task<IReadOnlyList<PackageRecord>> ListPackageSummariesAsync(
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

`ListPackageSummariesAsync` returns the same package-level record shape with
`Index == null`. Use it for inventory or display work:

```csharp
IReadOnlyList<PackageRecord> summaries =
    await cache.ListPackageSummariesAsync("hl7.fhir.r4");
```

Use `ListPackagesAsync` when resource indexes are required. The default
interface fallback clones hydrated records for compatibility and can therefore
remain expensive until a cache implementation overrides the summary method.
`DiskPackageCache` provides an optimized override that reads manifests and
cache metadata without loading or validating `.index.json`.

`GetPackageContentPath` returns the selected content directory, which may be
the identity root. `GetFileContentAsync` still requires a safe contained
content-relative path; use the record's `DirectoryPath` for known standard
siblings rather than trying `../` in this API. `GetIndexAsync` returns only a
usable on-disk index and cannot return another manager's transient index.

`CacheMetadataEntry.ContentGeneration` exposes the opaque identifier of the
currently committed content generation. The cache owns this value:
`UpdateMetadataAsync` preserves the current generation even when passed an
older metadata snapshot.

#### InstallCacheOptions

```csharp
public class InstallCacheOptions
{
    public bool OverwriteExisting { get; set; }    // default: false
    public bool Unmodified { get; set; }           // default: false
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

`Unmodified` controls layout preparation for that direct `InstallAsync` or
`ImportAsync` call. The low-level cache does not generate resource indexes.
It preserves standard archive siblings in both modes and wraps the complete
root-layout tree only in ordinary mode. It stores no protection marker.
Manager callers configure `FhirPackageManagerOptions.Unmodified` instead so
the same policy also covers dependencies, restore, and index operations.

`Limits` inherits the direct cache snapshot and only tightens assigned fields.
Map endpoint `PackageDownloadResult.ContentLength` to `ReportedContentLength`,
the **selected source's** `ResolvedDirective.ShaSum` to `ExpectedShaSum`, and
its available `Sha256Sum` to `ExpectedSha256Sum`, retaining `VerifyChecksum`.
Do not combine checksums from different candidates. Native acquisition rejects
negative reports as `DownloadFailed` / `Acquisition`; reports above the ceiling
fail with `CompressedSizeLimitExceeded` before reading. Absent/underreported
actual excess fails during acquisition. Within-ceiling discrepancies do not
introduce a strict reported-versus-actual length-equality contract.

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

`InspectAsync` returns a path-neutral `HardenedPackageCacheInspection`:

```csharp
public sealed record HardenedPackageCacheInspection
{
    public required HardenedPackageCacheState State { get; init; }
    public bool IsRepairable { get; init; } = true;   // may transactionally repair corruption
    public string? CorruptionReason { get; init; }    // non-sensitive; set when State is Corrupt
}

public enum HardenedPackageCacheState
{
    Missing = 0,   // no cache target exists for the identity
    Valid   = 1,   // the cache target is valid and matches its identity
    Corrupt = 2,   // the cache target exists but is invalid
}
```

### DiskPackageCache

The default `IPackageCache` implementation. Uses the standard FHIR content
directory by default and also reads preserved root-layout entries. When no
explicit path is provided, the `PACKAGE_CACHE_FOLDER`
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

The original and retry-configured constructors remain; exactly one new
required-five-argument overload supplies installation policy without changing
old null/default/named-argument bindings:

```csharp
public DiskPackageCache(
    string? cacheDirectory = null,
    ILogger<DiskPackageCache>? logger = null,
    TimeProvider? timeProvider = null);

public DiskPackageCache(
    string? cacheDirectory,
    ILogger<DiskPackageCache>? logger,
    TimeProvider? timeProvider,
    FileSystemRetryOptions fileSystemRetry);

public DiskPackageCache(
    string? cacheDirectory,
    ILogger<DiskPackageCache>? logger,
    TimeProvider? timeProvider,
    FileSystemRetryOptions fileSystemRetry,
    PackageInstallLimits installLimits);
```

**All five arguments are required**, including nullable logger/time-provider
positions; null there selects the existing defaults. No optional policy
parameter or exposed internal context overload competes with old calls:
zero–three argument calls still bind to the original constructor, four to the
retry constructor. Retry and installation settings are validated and
snapshotted before cache-root discovery or creation. Use the
[complete six-field example](#packageinstalllimits) for finite higher ceilings
and the per-call only-tightening rule.

Key behaviors:

- **Validated reads** — a hit requires real selected directories, a real regular
  `package/package.json` or root `package.json`, a readable selected manifest,
  and matching exact/alias identity. Foreign cached entries retain standard-slot
  preference when both regular slots exist; new archives with both are rejected.
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
- **Summary enumeration** — `ListPackageSummariesAsync` returns
  package-level metadata with `Index == null` without loading or validating
  persisted resource indexes.
- **Hidden state** — cache-root `.fhirpkg/staging`, `.fhirpkg/transactions`,
  `.fhirpkg/backup`, `.fhirpkg/quarantine`, and `.fhirpkg/locks` are SDK-owned.
  Same-named distributed files/directories inside a package target are payload.
- **Raw path caveat** — SDK reads wait through replacement. A caller retaining
  a previously returned `ContentPath` and accessing it outside the SDK may
  briefly observe the target absent between replacement renames, never mixed
  generations.

#### Standalone Compatibility Normalization

`TarballExtractor.NormalizePackageStructure(string extractedPath)` remains an
explicitly requested **in-place mutation**, not installation policy. Its narrow
standard allowlist accepts only `package/` and optional lowercase `openapi/`
and `other/` siblings, checks all destinations before moving those siblings
under `package/`, and rejects occupied destinations without merging.
Its unambiguous legacy-root compatibility policy is also unchanged. It does
not validate package identity or provide transactional rollback for filesystem
failures. Installation/import use read-only archive discovery and a separate
whole-tree default legacy wrapper instead. Publish identity discovery and
standard-only NPM publication still use the compatibility bridge; their
validation/admission policy is unchanged.

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

Both existing `CreateClientForEndpoint` overload families retain their optional
fourth `TimeProvider? timeProvider = null` argument. Their configured
additional-arity overloads require that position and the new fifth argument:

```csharp
public static IRegistryClient CreateClientForEndpoint(
    RegistryEndpoint endpoint,
    HttpClient httpClient,
    ILoggerFactory loggerFactory,
    TimeProvider? timeProvider,
    FileSystemRetryOptions fileSystemRetry);

public static IRegistryClient CreateClientForEndpoint(
    RegistryEndpoint endpoint,
    RegistryHttpTransport transport,
    ILoggerFactory loggerFactory,
    TimeProvider? timeProvider,
    FileSystemRetryOptions fileSystemRetry);
```

These are methods on `RegistryClientFactory`. Each snapshots once. The existing
`BuildRegistryClient` overloads instead use `FhirPackageManagerOptions.FileSystemRetry`
for the whole owned chain; no additional public context parameter is exposed.
HTTP transport/header/redirect requirements are unchanged by retry options.

Explicit finite installation policy uses the new required-six-argument
[RegistryClientFactory](#registryclientfactory) overloads below.

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

### RegistryClientFactory

Exactly two additive endpoint signatures, one per existing transport family:

```csharp
public static IRegistryClient CreateClientForEndpoint(
    RegistryEndpoint endpoint,
    HttpClient httpClient,
    ILoggerFactory loggerFactory,
    TimeProvider? timeProvider,
    FileSystemRetryOptions fileSystemRetry,
    PackageInstallLimits installLimits);

public static IRegistryClient CreateClientForEndpoint(
    RegistryEndpoint endpoint,
    RegistryHttpTransport transport,
    ILoggerFactory loggerFactory,
    TimeProvider? timeProvider,
    FileSystemRetryOptions fileSystemRetry,
    PackageInstallLimits installLimits);
```

**All six arguments are required.** The existing three/four-argument calls
(optional `timeProvider = null`) and required-five retry calls retain their
public parameter types, names, default flags, and null/default bindings.
Typed null transport arguments retain `ArgumentNullException`; required null
installation/retry policies retain typed `InvalidPolicy` /
`PolicyValidation`. No GitHub-token/context parameter or new NPM leaf policy
constructor was made public. These wrappers use existing resolver/context
routes and pass no GitHub token.

Both families support all four endpoint types. An `HttpClient` overload
remains unverified; a supplied redirect-controlled transport retains its
guarantees, without granting new header/redirect privileges. Each validates
and snapshots policy as described under [PackageInstallLimits](#packageinstalllimits).
NPM uses its snapshot for **local publication preparation**, including bounded
archive reading/extraction before PUT. This does not add a download spool:
FHIR-NPM/NPM downloads remain response-backed; direct native cache acquisition
and extraction enforce their **separate cache policy**.

For a direct download/install route, reuse the complete all-six-field `limits`
and normal `retry` objects from the preceding example. The caller owns the
HTTP client; a real handler must have automatic redirects disabled. Disposing
`PackageDownloadResult` releases the response and content stream; cache
installation borrows that stream and does not dispose it:

```csharp
using HttpClient httpClient = new(
    new HttpClientHandler { AllowAutoRedirect = false });
RegistryHttpTransport transport = RegistryHttpTransport.CreateRedirectControlled(
    httpClient, TimeSpan.FromSeconds(30), 5);
RegistryEndpoint endpoint = new()
{
    Url = "https://registry.example.test/",
    Type = RegistryType.Npm,
};
IRegistryClient client = RegistryClientFactory.CreateClientForEndpoint(
    endpoint, transport, NullLoggerFactory.Instance, null, retry, limits);
// For uncredentialed reads, the other required-six family is:
IRegistryClient unverified = RegistryClientFactory.CreateClientForEndpoint(
    endpoint, httpClient, NullLoggerFactory.Instance, null, retry, limits);
using DiskPackageCache cache = new(
    "./endpoint-cache", null, null, retry, limits);

ResolvedDirective? selected = await client.ResolveAsync(
    PackageDirective.Parse("example.package#1.0.0"),
    cancellationToken: cancellationToken);
if (selected is not null)
{
    await using PackageDownloadResult? download =
        await client.DownloadAsync(selected, cancellationToken);
    if (download is not null)
    {
        PackageRecord installed = await cache.InstallAsync(
            selected.Reference,
            download.Content,
            new InstallCacheOptions
            {
                Unmodified = true,
                VerifyChecksum = true,
                ReportedContentLength = download.ContentLength,
                ExpectedShaSum = selected.ShaSum,
                ExpectedSha256Sum = selected.Sha256Sum,
            },
            cancellationToken);
    }
}
```

`NullLoggerFactory` is from `Microsoft.Extensions.Logging.Abstractions`.
This example is acquisition only, not permission or a recipe to publish.

### Built-in Registry Clients

| Client | Registry Type | Description |
|--------|--------------|-------------|
| `FhirNpmRegistryClient` | `FhirNpm` | FHIR registries; raw-gzip publication |
| `FhirCiBuildClient` | `FhirCiBuild` | CI builds from build.fhir.org |
| `Hl7WebsiteClient` | `FhirHttp` | HL7 website fallback (core packages only) |
| `NpmRegistryClient` | `Npm` | Standard NPM registries; packument publication |
| `RedundantRegistryClient` | *(composite)* | Chains multiple clients with automatic fallback |

The publishing clients retain their original three-argument constructors and
add these required fourth-argument overloads:

```csharp
public FhirNpmRegistryClient(
    HttpClient httpClient,
    RegistryEndpoint endpoint,
    ILogger<FhirNpmRegistryClient> logger,
    FileSystemRetryOptions fileSystemRetry);

public NpmRegistryClient(
    HttpClient httpClient,
    RegistryEndpoint endpoint,
    ILogger<NpmRegistryClient> logger,
    FileSystemRetryOptions fileSystemRetry);
```

`RedundantRegistryClient` retains its enumerable and expanded-params
constructors. Configured construction uses a **named factory**, not a
competing constructor:

```csharp
public RedundantRegistryClient(
    IEnumerable<IRegistryClient> clients,
    ILogger<RedundantRegistryClient>? logger = null);
public RedundantRegistryClient(params IRegistryClient[] clients);

public static RedundantRegistryClient CreateWithFileSystemRetry(
    IEnumerable<IRegistryClient> clients,
    FileSystemRetryOptions fileSystemRetry,
    ILogger<RedundantRegistryClient>? logger = null);
```

The factory snapshots policy for composite-owned file preparation/rewinds; it
does not mutate supplied clients. Terminal local exceptions or failed
publication results stop fallback, while genuine network failures retain
existing fallback. Deferred NPM attachment reads keep the client policy.

### RegistryClientBase

The abstract base of every built-in client except the composite
`RedundantRegistryClient`, and the supported extension point for a custom
`IRegistryClient`. It owns header scoping, redirect control, deadlines, and
JSON handling; a derived client supplies only its registry's routes.

```csharp
public abstract class RegistryClientBase : IRegistryClient
{
    protected RegistryClientBase(
        HttpClient httpClient, RegistryEndpoint endpoint, ILogger logger);
    protected RegistryClientBase(
        HttpClient httpClient, RegistryEndpoint endpoint, ILogger logger,
        FileSystemRetryOptions fileSystemRetry);
    protected RegistryClientBase(
        RegistryHttpTransport transport, RegistryEndpoint endpoint, ILogger logger);
    protected RegistryClientBase(
        RegistryHttpTransport transport, RegistryEndpoint endpoint, ILogger logger,
        FileSystemRetryOptions fileSystemRetry);

    public RegistryEndpoint Endpoint { get; }
    public abstract IReadOnlyList<PackageNameType> SupportedNameTypes { get; }
    public abstract IReadOnlyList<VersionType> SupportedVersionTypes { get; }

    protected static readonly JsonSerializerOptions JsonOptions;
    protected HttpClient Http { get; }
    protected RegistryEndpoint EndpointConfig { get; }
    protected ILogger Logger { get; }
    protected string BaseUrl { get; }   // normalized endpoint URL, no trailing '/'

    protected Task<T?> GetJsonAsync<T>(
        string requestUri, CancellationToken cancellationToken) where T : class;
    protected Task<T?> GetJsonValueAsync<T>(
        string requestUri, CancellationToken cancellationToken) where T : struct;
    protected Task<HttpResponseMessage?> GetResponseAsync(
        string requestUri, CancellationToken cancellationToken);
    protected Task<HttpResponseMessage> PostJsonAsync<T>(
        string requestUri, T content, CancellationToken cancellationToken);
    protected Task<HttpResponseMessage> PutStreamAsync(
        string requestUri, Stream stream, string contentType,
        CancellationToken cancellationToken);
    protected Task<HttpResponseMessage> PutContentAsync(
        string requestUri, HttpContent content, CancellationToken cancellationToken);
    protected static Task<PackageDownloadResult> CreateDownloadResultAsync(
        HttpResponseMessage response, CancellationToken cancellationToken);

    protected PackageListing WithSourceProvenance(PackageListing listing);
    protected static string? ResolveVersion(
        PackageDirective directive, PackageListing listing, VersionResolveOptions? options);
    protected static string? ResolveExact(PackageListing listing, string requestedVersion);
}
```

`SupportedNameTypes` and `SupportedVersionTypes` are abstract. The five
`IRegistryClient` operations are virtual with inert defaults, so a derived
client overrides only what its registry supports:

| Member | Default when not overridden |
|--------|----------------------------|
| `SearchAsync` | empty result list |
| `GetPackageListingAsync` | `null` |
| `ResolveAsync` | `null` |
| `DownloadAsync` | `null` |
| `PublishAsync` | throws `NotSupportedException` |

The protected helpers apply one error mapping for every derived client: HTTP
404 becomes `null`, any other unsuccessful status becomes
`HttpRequestException`, and an expired `HttpTimeout` becomes
`RegistryResponseTimeoutException`. Headers are built per request message
rather than on `HttpClient.DefaultRequestHeaders` — a client that defines
default headers is rejected — so one `HttpClient` can be shared across
registry clients. The user-agent is sent on every request, while
`AuthHeaderValue` and `CustomHeaders` are sent only to the endpoint's own
origin or a listed `TrustedHeaderOrigins` entry. GET redirects are followed
manually up to `MaxRedirects` when the transport is redirect-controlled;
credentialed requests and the POST/PUT helpers require such a transport and
otherwise throw `InvalidOperationException` before network access.
`WithSourceProvenance` stamps the credential-free provenance described under
[PackageListing & PackageVersionInfo](#packagelisting--packageversioninfo)
onto a listing before it is returned.

The additional-arity protected constructors snapshot filesystem settings for
external derived callers without exposing internal context types.
`PutStreamAsync` protects an ordinary caller file before its leave-open HTTP
wrapper, prepares file position/length with async retry waits, and retries
only safe source reads, never HTTP destination writes or a whole request.
Opaque/derived streams remain one-shot. Existing transport-shaped
exception-to-result behavior retains terminal local provenance so a composite
cannot resend after local exhaustion.

---

## Resolution

### IVersionResolver

Resolves version specifiers (wildcards, ranges, `latest`) to exact versions by
querying registries and filtering original candidate entries with the shared
FHIR matcher.

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

- **Exact-node traversal** — graph identity is package name plus exact version,
  so different versions and their child subgraphs coexist while repeated exact
  identities are deduplicated.
- **Preferred projection** — conflict policy selects the name-keyed `Resolved`
  entry independently from the complete `ResolvedPackages` closure.
- **Cycle handling** — exact-version cycles terminate, while shared DAG paths
  and alternate versions remain active.
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

Standalone construction retains the original signature and adds a required
second argument for a construction-time retry snapshot:

```csharp
public PackageIndexer(ILogger logger);
public PackageIndexer(ILogger logger, FileSystemRetryOptions fileSystemRetry);
```

Directory snapshots, file metadata/reads, and referenced-file checks use that
policy; JSON/resource parsing is not replayed. Existing linked content/index/
resource reads and ordinal ordering remain compatible. Referenced-file
validation retains its no-follow protection and derivative-index fallback.

For managed package workflows, prefer `IFhirPackageResourceManager`. It
validates indexes through `DiskPackageCache`, persists them atomically in
ordinary mode or registers validated transient indexes in unmodified mode,
performs lazy indexing before queries, and invalidates package-specific state
on cache mutation. It supplies explicit package identity for root layouts.
`ForceReindex` never overrides the manager's unmodified policy; a later ordinary
manager may persist an index because protection is not stored in the cache.

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

### ServiceCollectionExtensions

The static class that carries `AddFhirPackageManagement`.

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFhirPackageManagement(
        this IServiceCollection services,
        Action<FhirPackageManagerOptions>? configure = null);
}
```

Every service is registered as a singleton with `TryAdd`, so an implementation
already present in the collection is left in place:

| Service | Registered implementation |
|---------|--------------------------|
| `IPackageCache` | `DiskPackageCache` at the configured `CachePath` |
| `IHardenedPackageCache` | the same cache instance; a cache without the capability fails with `UnsupportedCacheCapability` |
| `IRegistryClient` | the composite chain built by `RegistryClientFactory` from `Registries`, `IncludeCiBuilds`, and `IncludeHl7WebsiteFallback` |
| `IVersionResolver` | `VersionResolver` |
| `IDependencyResolver` | `DependencyResolver` |
| `IPackageIndexer` | `PackageIndexer` |
| `IFhirPackageManager` | `FhirPackageManager` |
| `IHardenedFhirPackageManager` | the same manager instance; a manager without the capability fails with `UnsupportedManagerCapability` |
| `IFhirPackageResourceManager` | the same manager instance; a manager without the capability throws `InvalidOperationException` |

The method also registers the named `HttpClient` `"FhirPackages"` with
automatic redirects disabled and `MaxRedirects` applied, so the manager it
builds always has a redirect-controlled transport and therefore accepts
`GitHubToken`. A `MemoryResourceCache` is created only when `ResourceCacheSize`
is positive. Options are configured through
`AddOptions<FhirPackageManagerOptions>()`; when a `FhirPackageManagerOptions`
instance is already registered in the collection, that instance configures the
manager instead of the one `configure` receives.

---

## Utilities

### FhirReleaseMapping

Static helper for mapping between FHIR releases, version strings, and package
names.

```csharp
public static class FhirReleaseMapping
{
    public static readonly string[] KnownCoreTypes;  // ["core", "expansions", "examples", "search", "corexml", "elements"]

    public static FhirRelease? FromVersionString(string version);
    public static string? ToVersionString(FhirRelease release);
    public static string ToPackagePrefix(FhirRelease release);
    public static FhirRelease? FromPackageName(string packageName);
    public static IReadOnlyList<string> GetCorePackageNames(FhirRelease release);
}
```

### CheckSum

SHA-1 (default, for NPM registry compatibility) and SHA-256 checksum computation
and verification.

```csharp
public static class CheckSum
{
    public static string ComputeSha1(Stream stream);
    public static string ComputeSha1(byte[] data);
    public static string ComputeSha256(Stream stream);
    public static string ComputeSha256(byte[] data);
    public static bool Verify(Stream stream, string? expectedHash, bool resetPosition = true);
    public static bool VerifySha256(Stream stream, string? expectedHash, bool resetPosition = true);
}
```

Stream overloads protect ordinary files once and leave them open. Compute
methods consume from the current position; `Verify*` defaults to restoring
that original position, not seeking to zero. Hashing is not replayed, and
null/empty/whitespace expected hashes skip verification. Byte-array and
memory-only paths do not resolve filesystem retry environment settings.

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

---

## Public Surface Coverage

This appendix records the disposition of every public type in `src/FhirPkg/` as
of this release: each one is either documented by a section of this reference or
deliberately left out of it with the reason stated. The list is derived from the
source rather than maintained by hand, so the next audit is a re-run of one
command from the repository root rather than a re-derivation:

```powershell
Get-ChildItem src\FhirPkg -Recurse -Filter *.cs -File |
  Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
  Select-String -Pattern '^\s*public\s+(?:[a-z]+\s+)*(class|record\s+struct|record|struct|interface|enum|delegate)\s+(\w+)'
```

The type name is capture group 2. The run behind this table returned **94**
declarations across 62 files — 40 classes, 27 records, 17 enumerations, 9
interfaces, and 1 record struct — with 94 distinct names, one per row below.

| Type | Namespace/dir | Disposition |
|------|---------------|-------------|
| `CacheMetadata` | `FhirPkg.Cache` | Intentionally undocumented — cache metadata file model, surfaced only by `IPackageCache.GetMetadataAsync`, whose contract is stated in [§IPackageCache](#ipackagecache) |
| `CacheMetadataEntry` | `FhirPkg.Cache` | Intentionally undocumented — per-package metadata entry, surfaced only by `IPackageCache.UpdateMetadataAsync`; its `ContentGeneration` rule is stated in [§IPackageCache](#ipackagecache) |
| `CatalogEntry` | `FhirPkg.Models` | Documented → [§CatalogEntry](#catalogentry) |
| `CheckSum` | `FhirPkg.Utilities` | Documented → [§CheckSum](#checksum) |
| `CiBuildManifest` | `FhirPkg.Models` | Documented → [§CiBuildRecord & CiBuildManifest](#cibuildrecord--cibuildmanifest) |
| `CiBuildRecord` | `FhirPkg.Models` | Documented → [§CiBuildRecord & CiBuildManifest](#cibuildrecord--cibuildmanifest) |
| `ConflictResolutionStrategy` | `FhirPkg.Models` | Documented → [§ConflictResolutionStrategy](#conflictresolutionstrategy) |
| `CorePackageType` | `FhirPkg.Models` | Documented → [§CorePackageType](#corepackagetype) |
| `CorruptCacheBehavior` | `FhirPkg` | Documented → [§CorruptCacheBehavior](#corruptcachebehavior) |
| `DependencyInstallationException` | `FhirPkg.Installation` | Documented → [§PackageInstallResult](#packageinstallresult) |
| `DependencyResolutionFailure` | `FhirPkg.Models` | Documented → [§PackageClosure](#packageclosure) |
| `DependencyResolutionFailureCode` | `FhirPkg.Models` | Documented → [§DependencyResolutionFailureCode](#dependencyresolutionfailurecode) |
| `DependencyResolveOptions` | `FhirPkg.Resolution` | Documented → [§IDependencyResolver](#idependencyresolver) |
| `DependencyResolver` | `FhirPkg.Resolution` | Intentionally undocumented — the concrete resolver registered by `AddFhirPackageManagement`; the documented contract is the interface, [§IDependencyResolver](#idependencyresolver) |
| `DirectiveParser` | `FhirPkg.Resolution` | Documented → [§DirectiveParser](#directiveparser) |
| `DiskPackageCache` | `FhirPkg.Cache` | Documented → [§DiskPackageCache](#diskpackagecache) |
| `FhirCiBuildClient` | `FhirPkg.Registry` | Documented → [§Built-in Registry Clients](#built-in-registry-clients) |
| `FhirNpmRegistryClient` | `FhirPkg.Registry` | Documented → [§Built-in Registry Clients](#built-in-registry-clients) |
| `FhirPackageManager` | `FhirPkg` | Documented → [§Constructors (FhirPackageManager)](#constructors-fhirpackagemanager) |
| `FhirPackageManagerOptions` | `FhirPkg` | Documented → [§FhirPackageManagerOptions](#fhirpackagemanageroptions) |
| `FhirPackageManagerResourceExtensions` | `FhirPkg` | Documented → [§FhirPackageManagerResourceExtensions](#fhirpackagemanagerresourceextensions) |
| `FhirPreReleaseType` | `FhirPkg.Models` | Documented → [§FhirPreReleaseType](#fhirprereleasetype) |
| `FhirRelease` | `FhirPkg.Models` | Documented → [§FhirRelease](#fhirrelease) |
| `FhirReleaseMapping` | `FhirPkg.Models` | Documented → [§FhirReleaseMapping](#fhirreleasemapping) |
| `FhirSemVer` | `FhirPkg.Models` | Documented → [§FhirSemVer](#fhirsemver) |
| `FileSystemRetryOptions` | `FhirPkg` | Documented → [§FileSystemRetryOptions](#filesystemretryoptions) |
| `HardenedPackageCacheInspection` | `FhirPkg.Cache` | Documented → [§IHardenedPackageCache](#ihardenedpackagecache) |
| `HardenedPackageCacheState` | `FhirPkg.Cache` | Documented → [§IHardenedPackageCache](#ihardenedpackagecache) |
| `Hl7WebsiteClient` | `FhirPkg.Registry` | Documented → [§Built-in Registry Clients](#built-in-registry-clients) |
| `IDependencyResolver` | `FhirPkg.Resolution` | Documented → [§IDependencyResolver](#idependencyresolver) |
| `IFhirPackageManager` | `FhirPkg` | Documented → [§Core Interface — IFhirPackageManager](#core-interface--ifhirpackagemanager) |
| `IFhirPackageResourceManager` | `FhirPkg` | Documented → [§Resource Capability — IFhirPackageResourceManager](#resource-capability--ifhirpackageresourcemanager) |
| `IHardenedFhirPackageManager` | `FhirPkg` | Documented → [§Hardened Manager Sources](#hardened-manager-sources) |
| `IHardenedPackageCache` | `FhirPkg.Cache` | Documented → [§IHardenedPackageCache](#ihardenedpackagecache) |
| `IndexingOptions` | `FhirPkg.Indexing` | Documented → [§IPackageIndexer](#ipackageindexer) |
| `IniParser` | `FhirPkg.Utilities` | Intentionally undocumented — implementation detail, not part of the supported contract |
| `InstallCacheOptions` | `FhirPkg.Cache` | Documented → [§InstallCacheOptions](#installcacheoptions) |
| `InstallOptions` | `FhirPkg` | Documented → [§InstallOptions](#installoptions) |
| `IPackageCache` | `FhirPkg.Cache` | Documented → [§IPackageCache](#ipackagecache) |
| `IPackageIndexer` | `FhirPkg.Indexing` | Documented → [§IPackageIndexer](#ipackageindexer) |
| `IRegistryClient` | `FhirPkg.Registry` | Documented → [§IRegistryClient](#iregistryclient) |
| `IVersionResolver` | `FhirPkg.Resolution` | Documented → [§IVersionResolver](#iversionresolver) |
| `MemoryResourceCache` | `FhirPkg.Cache` | Documented → [§MemoryResourceCache](#memoryresourcecache) |
| `NpmDistribution` | `FhirPkg.Models` | Documented → [§PackageListing & PackageVersionInfo](#packagelisting--packageversioninfo) |
| `NpmRegistryClient` | `FhirPkg.Registry` | Documented → [§Built-in Registry Clients](#built-in-registry-clients) |
| `NpmRepository` | `FhirPkg.Models` | Documented → [§PackageManifest](#packagemanifest) |
| `PackageCacheKey` | `FhirPkg.Installation` | Intentionally undocumented — implementation detail, not part of the supported contract |
| `PackageClosure` | `FhirPkg.Models` | Documented → [§PackageClosure](#packageclosure) |
| `PackageDirective` | `FhirPkg.Models` | Documented → [§PackageDirective](#packagedirective) |
| `PackageDownloadResult` | `FhirPkg.Models` | Response ownership and direct-cache mappings documented → [§RegistryClientFactory](#registryclientfactory); no `IFhirPackageManager` operation surfaces it |
| `PackageFixups` | `FhirPkg.Utilities` | Intentionally undocumented — implementation detail, not part of the supported contract |
| `PackageIndex` | `FhirPkg.Indexing` | Documented → [§PackageIndex & ResourceIndexEntry](#packageindex--resourceindexentry) |
| `PackageIndexer` | `FhirPkg.Indexing` | Standalone retry constructors and interface contract documented → [§IPackageIndexer](#ipackageindexer) |
| `PackageInstallationIdentity` | `FhirPkg.Models` | Documented → [§PackageInstallationIdentity](#packageinstallationidentity) |
| `PackageInstallDisposition` | `FhirPkg.Models` | Documented → [§PackageInstallDisposition](#packageinstalldisposition) |
| `PackageInstallErrorCode` | `FhirPkg.Installation` | Documented → [§PackageInstallErrorCode](#packageinstallerrorcode) |
| `PackageInstallException` | `FhirPkg.Installation` | Intentionally undocumented — the typed failure it carries is enumerated as [§PackageInstallErrorCode](#packageinstallerrorcode) and [§PackageInstallStage](#packageinstallstage), which is where callers branch |
| `PackageInstallLimits` | `FhirPkg` | Documented → [§PackageInstallLimits](#packageinstalllimits) |
| `PackageInstallResult` | `FhirPkg.Models` | Documented → [§PackageInstallResult](#packageinstallresult) |
| `PackageInstallStage` | `FhirPkg.Installation` | Documented → [§PackageInstallStage](#packageinstallstage) |
| `PackageInstallStatus` | `FhirPkg.Models` | Documented → [§PackageInstallStatus](#packageinstallstatus) |
| `PackageListing` | `FhirPkg.Models` | Documented → [§PackageListing & PackageVersionInfo](#packagelisting--packageversioninfo) |
| `PackageManifest` | `FhirPkg.Models` | Documented → [§PackageManifest](#packagemanifest) |
| `PackageNameType` | `FhirPkg.Models` | Documented → [§PackageNameType](#packagenametype) |
| `PackageProgress` | `FhirPkg.Models` | Documented → [§PackageProgress](#packageprogress) |
| `PackageProgressPhase` | `FhirPkg.Models` | Documented → [§PackageProgressPhase](#packageprogressphase) |
| `PackageRecord` | `FhirPkg.Models` | Documented → [§PackageRecord](#packagerecord) |
| `PackageReference` | `FhirPkg.Models` | Documented → [§PackageReference](#packagereference) |
| `PackageSearchCriteria` | `FhirPkg.Models` | Documented → [§PackageSearchCriteria](#packagesearchcriteria) |
| `PackageSourceInstallOptions` | `FhirPkg` | Documented → [§PackageSourceInstallOptions](#packagesourceinstalloptions) |
| `PackageVersionInfo` | `FhirPkg.Models` | Documented → [§PackageListing & PackageVersionInfo](#packagelisting--packageversioninfo) |
| `PublishResult` | `FhirPkg.Models` | Documented → [§PublishResult](#publishresult) |
| `RedundantRegistryClient` | `FhirPkg.Registry` | Documented → [§Built-in Registry Clients](#built-in-registry-clients) |
| `RegistryAttemptFailure` | `FhirPkg.Registry` | Documented → [§IRegistryClient](#iregistryclient) |
| `RegistryClientBase` | `FhirPkg.Registry` | Documented → [§RegistryClientBase](#registryclientbase) |
| `RegistryClientFactory` | `FhirPkg.Registry` | Endpoint policy documented → [§RegistryClientFactory](#registryclientfactory); existing retry families in [§RegistryEndpoint](#registryendpoint), `BuildRegistryClient` in [§FhirPackageManagerOptions](#fhirpackagemanageroptions) |
| `RegistryEndpoint` | `FhirPkg.Registry` | Documented → [§RegistryEndpoint](#registryendpoint) |
| `RegistryFailureCategory` | `FhirPkg.Registry` | Documented → [§RegistryFailureCategory](#registryfailurecategory) |
| `RegistryHttpTransport` | `FhirPkg.Registry` | Intentionally undocumented — the transport guarantee it carries is described where it is required, in [§RegistryEndpoint](#registryendpoint) and [§FhirPackageManagerOptions](#fhirpackagemanageroptions) |
| `RegistryOperationException` | `FhirPkg.Registry` | Documented → [§IRegistryClient](#iregistryclient) |
| `RegistryResponseTimeoutException` | `FhirPkg.Registry` | Intentionally undocumented — the `HttpTimeout` deadline that throws it is stated in [§IRegistryClient](#iregistryclient); it adds no members to `TimeoutException` |
| `RegistryType` | `FhirPkg.Models` | Documented → [§RegistryType](#registrytype) |
| `ResolvedDirective` | `FhirPkg.Models` | Documented → [§ResolvedDirective](#resolveddirective) |
| `ResourceIndexEntry` | `FhirPkg.Indexing` | Documented → [§PackageIndex & ResourceIndexEntry](#packageindex--resourceindexentry) |
| `ResourceInfo` | `FhirPkg.Indexing` | Documented → [§ResourceInfo & ResourceSearchCriteria](#resourceinfo--resourcesearchcriteria) |
| `ResourceSearchCriteria` | `FhirPkg.Indexing` | Documented → [§ResourceInfo & ResourceSearchCriteria](#resourceinfo--resourcesearchcriteria) |
| `RestoreOptions` | `FhirPkg` | Documented → [§RestoreOptions](#restoreoptions) |
| `SafeMode` | `FhirPkg.Models` | Documented → [§SafeMode](#safemode) |
| `ServiceCollectionExtensions` | `FhirPkg` | Documented → [§ServiceCollectionExtensions](#servicecollectionextensions) |
| `TarballExtractor` | `FhirPkg.Cache` | Compatibility normalization documented → [§Standalone Compatibility Normalization](#standalone-compatibility-normalization); extraction internals are not a supported extension point |
| `TempDirectory` | `FhirPkg.Utilities` | Intentionally undocumented — implementation detail, not part of the supported contract |
| `VersionResolveOptions` | `FhirPkg` | Documented → [§VersionResolveOptions](#versionresolveoptions) |
| `VersionResolver` | `FhirPkg.Resolution` | Intentionally undocumented — the concrete resolver registered by `AddFhirPackageManagement`; the documented contract is the interface, [§IVersionResolver](#iversionresolver) |
| `VersionType` | `FhirPkg.Models` | Documented → [§VersionType](#versiontype) |
