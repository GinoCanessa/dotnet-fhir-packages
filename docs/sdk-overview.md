# FhirPkg SDK Overview

FhirPkg is a C# SDK for discovering, resolving, downloading, caching, and managing
[FHIR packages](https://registry.fhir.org/) from multiple registries. It targets
**.NET 8+** (8, 9, and 10; 10 recommended) and is distributed as the
**fhir-pkg-lib** NuGet package.

## Features

- **Multi-registry resolution** — queries the primary FHIR registry, secondary
  registry, CI builds, HL7 website, and custom/private registries with automatic
  fallback.
- **Local disk cache** — uses the standard content-directory layout by default
  in `~/.fhir/packages` (or a configured directory), compatible with other FHIR
  tooling. Unmodified root-layout storage is an explicit interoperability opt-out.
- **Dependency resolution** — resolves full transitive dependency closures,
  preserves coexisting exact versions and their subgraphs, and applies conflict
  strategies only to the preferred name-keyed projection.
- **Always-live restore** — resolves the current project manifest against live
  registry/cache state on every call.
- **FHIR-aware versioning** — understands FHIR pre-release hierarchies
  (`release > ballot > draft > snapshot > cibuild`), wildcards, ranges, CI builds,
  and branch-specific builds.
- **Resource indexing** — indexes FHIR resources inside packages and provides
  fast lookup by resource type, canonical URL, or StructureDefinition flavor.
- **In-memory resource cache** — optional LRU cache with configurable safe modes
  (Off / Clone / Freeze).
- **Publish** — publish a package tarball to a registry.
- **Dependency injection** — first-class `IServiceCollection` integration.
- **Fully async** — every I/O operation is `async`/`await` with `CancellationToken`
  support.

## Installation

```bash
dotnet add package fhir-pkg-lib
```

## Quick Start

### Standalone Usage

```csharp
using System.Text.Json.Nodes;
using FhirPkg;
using FhirPkg.Indexing;
using FhirPkg.Models;

// Create a manager with default options
using FhirPackageManager manager = new();

// Install a single package
PackageRecord? record =
    await manager.InstallAsync("hl7.fhir.r4.core#4.0.1");
Console.WriteLine($"Installed to {record?.ContentPath}");

// Install multiple packages and inspect mutable-CI outcomes
IReadOnlyList<PackageInstallResult> results =
    await manager.InstallManyAsync(
        ["hl7.fhir.us.core#current", "hl7.fhir.uv.extensions.r4#1.0.0"],
        new InstallOptions { IncludeDependencies = true });

foreach (PackageInstallResult result in results)
{
    Console.WriteLine(
        $"{result.Directive}: {result.Status} / " +
        $"{result.Disposition?.ToString() ?? "no CI disposition"}");
    if (result.Disposition == PackageInstallDisposition.Updated)
    {
        Console.WriteLine(
            $"  manifest date: {result.PreviousManifestDate ?? "unavailable"} -> " +
            $"{result.ManifestDate ?? "unavailable"}");
    }
    else if (result.Disposition is not null)
    {
        Console.WriteLine(
            $"  manifest date: {result.ManifestDate ?? "unavailable"}");
    }
}

// List cached package summaries
IReadOnlyList<PackageRecord> cached =
    await manager.ListCachedSummariesAsync();
foreach (PackageRecord pkg in cached)
    Console.WriteLine($"{pkg.Reference.FhirDirective} ({pkg.SizeBytes} bytes)");

// Search registries
IReadOnlyList<CatalogEntry> entries =
    await manager.SearchAsync(
        new PackageSearchCriteria
        {
            Name = "hl7.fhir.us",
            FhirVersion = "R4"
        });

// Resolve without downloading
ResolvedDirective? resolved =
    await manager.ResolveAsync("hl7.fhir.us.core#latest");
Console.WriteLine($"Resolved to {resolved?.Reference.Version} at {resolved?.TarballUri}");

// Search and read resources from cached packages
ResourceInfo? profile = await manager.FindByCanonicalUrlAsync(
    "http://hl7.org/fhir/StructureDefinition/Patient",
    "hl7.fhir.r4.core#4.0.1");
JsonNode? resource = profile is null
    ? null
    : await manager.ReadResourceAsync(profile);
```

Use `ListCachedAsync` instead when the caller needs populated
`PackageRecord.Index` values.

When dependency installation is requested, a failed child makes the aggregate
root operation fail. `PackageInstallResult.Package` retains the committed root
and `DependencyFailures` lists failed child directives. Single-package
`InstallAsync` overloads throw `DependencyInstallationException` with the same
partial-state information.

The directive-based single-package `InstallAsync` overload still returns only
`PackageRecord?`. Use `InstallManyAsync` when callers need the nullable mutable
CI disposition and manifest-date fields.

### Dependency Injection

Register all SDK services with a single call:

```csharp
using FhirPkg;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddFhirPackageManagement(options =>
{
    options.CachePath = "/my/cache";
    options.IncludeCiBuilds = false;
    options.HttpTimeout = TimeSpan.FromSeconds(60);
    options.Registries.Add(new RegistryEndpoint
    {
        Url = "https://my-private-registry.example.com",
        Type = RegistryType.FhirNpm,
        AuthHeaderValue = "Bearer my-token"
    });
});

var provider = services.BuildServiceProvider();
var manager = provider.GetRequiredService<IFhirPackageManager>();
```

`AddFhirPackageManagement` registers:

| Service | Implementation |
|---------|---------------|
| `FhirPackageManagerOptions` | Singleton (configured via the callback) |
| `HttpClient` | Via `IHttpClientFactory` |
| `IPackageCache` | `DiskPackageCache` |
| `IHardenedPackageCache` | Same `DiskPackageCache` singleton (cast; throws `UnsupportedCacheCapability` if unsupported) |
| `IRegistryClient` | `RedundantRegistryClient` (chains all configured endpoints) |
| `IVersionResolver` | `VersionResolver` |
| `IDependencyResolver` | `DependencyResolver` |
| `IPackageIndexer` | `PackageIndexer` |
| `IFhirPackageManager` | `FhirPackageManager` |
| `IHardenedFhirPackageManager` | Same `FhirPackageManager` singleton (cast; throws `UnsupportedManagerCapability` if unsupported) |
| `IFhirPackageResourceManager` | Same `FhirPackageManager` singleton |

### Restore from a Project Manifest

If your project contains a `package.json` with FHIR dependencies:

```csharp
PackageClosure closure = await manager.RestoreAsync(
    "./my-ig-project",
    new RestoreOptions
    {
        ConflictStrategy = ConflictResolutionStrategy.HighestWins,
        MaxDepth = 20,
    });

if (closure.IsComplete)
{
    Console.WriteLine(
        $"Restored {closure.ResolvedPackages.Count} exact package identities");
}
else
{
    foreach ((string name, string reason) in closure.Missing)
    {
        Console.WriteLine($"Missing: {name} — {reason}");
    }
}

foreach (PackageReference exact in closure.ResolvedPackages)
{
    Console.WriteLine(exact.FhirDirective);
}
```

`ResolvedPackages` is the complete exact-version closure and may contain more
than one version of a package. `Resolved` is the preferred name-keyed projection
selected by `ConflictStrategy`. `InstallationIdentities` maps each exact or
mutable installation reference to the exact manifest identity it represents;
installers should use that mapping rather than infer identity from aliases.

<!-- mirror-of: docs/sdk-api-reference.md#fhirpackagemanageroptions -->
## Configuration

All behavior is controlled through `FhirPackageManagerOptions`:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `InstallLimits` | `PackageInstallLimits` | 100 MiB compressed, 1 GiB expanded, 128 MiB/entry, 50,000 entries, path 1,024, depth 32 | Archive-processing safety limits (env-overridable) |
| `CorruptCacheBehavior` | `CorruptCacheBehavior` | `Repair` | How an invalid cache target is handled (`Repair`, `Strict`) |
| `Unmodified` | `bool` | `false` | Preserve root layout and suppress payload/index writes for this manager's runtime |
| `CachePath` | `string?` | `PACKAGE_CACHE_FOLDER` env var, or `~/.fhir/packages` | Local package cache directory |
| `Registries` | `List<RegistryEndpoint>` | `[]` | Custom registry endpoints (priority order) |
| `IncludeCiBuilds` | `bool` | `true` | Query the FHIR CI build registry |
| `GitHubToken` | `string?` | `null` → unauthenticated | Token used **only** for the `api.github.com` repository lookups behind canonical CI build selection; never sent to a package registry |
| `IncludeHl7WebsiteFallback` | `bool` | `true` | Fall back to hl7.org/fhir for core packages |
| `HttpTimeout` | `TimeSpan` | 30 s | Per-request HTTP timeout |
| `MaxRedirects` | `int` | `5` | Maximum HTTP redirects per request |
| `VerifyChecksums` | `bool` | `true` | Verify SHA-1 checksums on download |
| `MaxParallelRegistryQueries` | `int` | `3` | Concurrency limit for batch registry queries |
| `ResourceCacheSize` | `int` | `200` | Max entries in in-memory resource cache (0 = disabled) |
| `ResourceCacheSafeMode` | `SafeMode` | `Off` | Cache return behavior: `Off`, `Clone`, `Freeze` |
| `VersionFixups` | `Dictionary<string, string>` | `{"hl7.fhir.r4.core@4.0.0": "4.0.1", "hl7.fhir.r4b.core@4.3.0-snapshot1": "4.3.0"}` | Known version corrections |

### Preserving Archive Payloads

```csharp
using FhirPackageManager manager = new(new FhirPackageManagerOptions
{
    CachePath = "./unmodified-cache",
    Unmodified = true,
});
```

`Unmodified` is snapshotted when the manager is constructed, including through
`AddFhirPackageManagement`. It governs every manager-owned registry/directive,
URI, caller-stream and local-archive install, import, batch install, dependency
installation, restore, and eager/lazy/explicitly forced index operation.
Changing the original options later does not change that manager. Direct cache
callers instead set the optional, default-false `InstallCacheOptions.Unmodified`
on `DiskPackageCache.InstallAsync` or `ImportAsync`; it is not a per-call
override of manager policy. The CLI exposes `--unmodified` and the `unmodified`
configuration key.

| Archive / mode | Committed payload | `ContentPath` |
|----------------|-------------------|---------------|
| Standard / ordinary | Distributed `package/` plus all safe root files and sibling trees | `DirectoryPath/package` |
| Standard / unmodified | Same distributed tree | `DirectoryPath/package` |
| Root-layout / ordinary | **All** payload wrapped once in `package/` | `DirectoryPath/package` |
| Root-layout / unmodified | Original tree at the identity root | `DirectoryPath` |

Standard ancillary directories, including arbitrary names and opaque nested
manifests, are not relocated. Default root wrapping is deliberate compatibility
with tooling that requires `package/`; even an incidental distributed `package`
entry remains inside that one wrapper. Use `PackageRecord.DirectoryPath` for
known standard sibling paths. Content-file and resource reads remain relative
to `ContentPath` and reject traversal such as `../other/file.json`. Package
identity comes from `PackageRecord.Reference`, not the parent of `ContentPath`.

Ordinary managers reuse a valid active `.index.json` unless forced and atomically
write a missing, unusable, or forced index. Unmodified managers reuse valid
indexes or build validated indexes **in memory**, leaving missing/unusable
targets and their bytes untouched. `ForceReindex` requests recomputation, not
permission to write. An occupied `.index.json` directory is never deleted to
make room: ordinary persistence fails, while an unmodified manager can use a
transient index. `DiskPackageCache.GetIndexAsync` reports only usable on-disk
indexes, not another manager's transient state.

No package protection marker is stored. Another ordinary manager may persist an
index later, even in an existing raw root; cache hits neither rewrap it nor
reconstruct original archive bytes. Use SDK `OverwriteExisting` or CLI
`install --overwrite` (not `install --force`) to reinstall from a source under
another mode. Staging, cache-root metadata/journals, recovery, and explicit
whole-package replacement, removal, and clean remain available. Distributed
`packages.ini` or `.fhirpkg` inside a package target are payload, not cache-root
bookkeeping.

Older SDKs or external tools may treat raw-root entries as corrupt or mutate
them. Prefer a dedicated cache for unmodified work when those tools are in use.
Native filesystem Unicode normalization still applies to visible filenames;
extraction retains exact safe spelling rather than normalizing it itself.
`SizeBytes` counts expanded files from the complete archive, including siblings
and supplied indexes, and is not recalculated after derived indexing.

Unmodified managers initially require the built-in coordinated cache and
read-only managed index builder. Unsupported injected caches fail early with
`UnsupportedCacheCapability` / `PolicyValidation`, and unsupported indexers with
`NotSupportedException`, before source access or callbacks. Ordinary injection
behavior is unchanged; no new public capability framework is required.
The standalone `TarballExtractor.NormalizePackageStructure` remains an
explicitly mutating, narrower compatibility utility; installation does not call
it, and publishing's compatibility bridge and admission policy are unchanged.

## Architecture

```
┌─────────────────────────────────────────────────┐
│              IFhirPackageManager                │
│          (FhirPackageManager)                   │
├─────────────────────────────────────────────────┤
│  IVersionResolver   │  IDependencyResolver      │
│  IPackageIndexer    │  MemoryResourceCache       │
├─────────────────────┼───────────────────────────┤
│   IRegistryClient   │     IPackageCache          │
│   ┌───────────────┐ │  ┌──────────────────────┐ │
│   │ Redundant     │ │  │  DiskPackageCache     │ │
│   │  ├ FhirNpm    │ │  │  (~/.fhir/packages)  │ │
│   │  ├ FhirCiBuild│ │  └──────────────────────┘ │
│   │  ├ Hl7Website │ │                           │
│   │  └ NpmRegistry│ │                           │
│   └───────────────┘ │                           │
└─────────────────────┴───────────────────────────┘
```

The **`IFhirPackageManager`** interface is the primary entry point. It
orchestrates the registry client chain, version resolver, dependency resolver,
cache, and indexer to provide high-level workflows (install, restore, search,
publish). Additive extension methods expose resource indexing, lookup, and read
operations without changing the original interface contract.

The concrete manager also implements **`IFhirPackageResourceManager`**. New
installs are indexed eagerly, while searches lazily generate any missing
relevant indexes. Valid schema-v2 indexes are loaded from disk after restart;
generated indexes are atomically persisted under the package identity lease
before registration in ordinary mode, or validated and registered in memory in
unmodified mode. Explicit and lazy indexing failures propagate and can be
retried. Eager indexing failures are logged without changing installation
success.

`ReadResourceAsync` returns `JsonNode` and caches parsed resources by canonical
package identity plus normalized contained path. Set `ResourceCacheSize` to
zero to bypass memory caching. Custom package caches without generation-aware
reads also bypass parsed-resource caching. Package overwrite, repair, removal,
force reindex, and cache clean operations invalidate the affected resource
state.

## Next Steps

- [SDK API Reference](sdk-api-reference.md) — full interface signatures, model
  details, and enum values.
- [CLI Overview](cli-overview.md) — the `fhir-pkg` command-line tool.
- [CLI Reference](cli-reference.md) — complete command and option reference.
