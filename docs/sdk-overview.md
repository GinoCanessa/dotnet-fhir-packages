# FhirPkg SDK Overview

FhirPkg is a C# SDK for discovering, resolving, downloading, caching, and managing
[FHIR packages](https://registry.fhir.org/) from multiple registries. It targets
**.NET 8+** (8, 9, and 10; 10 recommended) and is distributed as the
**fhir-pkg-lib** NuGet package.

## Features

- **Multi-registry resolution** — queries the primary FHIR registry, secondary
  registry, CI builds, HL7 website, and custom/private registries with automatic
  fallback.
- **Local disk cache** — stores packages in the standard `~/.fhir/packages`
  layout (or the directory specified by the `PACKAGE_CACHE_FOLDER` environment
  variable), compatible with other FHIR tooling.
- **Dependency resolution** — resolves full transitive dependency closures with
  conflict strategies, lock-file support, and circular-dependency detection.
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
| `IRegistryClient` | `RedundantRegistryClient` (chains all configured endpoints) |
| `IVersionResolver` | `VersionResolver` |
| `IDependencyResolver` | `DependencyResolver` |
| `IPackageIndexer` | `PackageIndexer` |
| `IFhirPackageManager` | `FhirPackageManager` |
| `IFhirPackageResourceManager` | Same `FhirPackageManager` singleton |

### Restore from a Project Manifest

If your project contains a `package.json` with FHIR dependencies:

```csharp
var closure = await manager.RestoreAsync("./my-ig-project", new RestoreOptions
{
    LockFilePath = "./locks/fhirpkg.lock.json",
    ConflictStrategy = ConflictResolutionStrategy.HighestWins,
    WriteLockFile = true,
    MaxDepth = 20,
});

if (closure.IsComplete)
    Console.WriteLine($"Restored {closure.Resolved.Count} packages");
else
    foreach (var (name, reason) in closure.Missing)
        Console.WriteLine($"Missing: {name} — {reason}");
```

## Configuration

All behavior is controlled through `FhirPackageManagerOptions`:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `CachePath` | `string?` | `PACKAGE_CACHE_FOLDER` env var, or `~/.fhir/packages` | Local package cache directory |
| `Registries` | `List<RegistryEndpoint>` | `[]` | Custom registry endpoints (priority order) |
| `IncludeCiBuilds` | `bool` | `true` | Query the FHIR CI build registry |
| `IncludeHl7WebsiteFallback` | `bool` | `true` | Fall back to hl7.org/fhir for core packages |
| `HttpTimeout` | `TimeSpan` | 30 s | Per-request HTTP timeout |
| `MaxRedirects` | `int` | `5` | Maximum HTTP redirects per request |
| `VerifyChecksums` | `bool` | `true` | Verify SHA-1 checksums on download |
| `MaxParallelRegistryQueries` | `int` | `3` | Concurrency limit for batch registry queries |
| `ResourceCacheSize` | `int` | `200` | Max entries in in-memory resource cache (0 = disabled) |
| `ResourceCacheSafeMode` | `SafeMode` | `Off` | Cache return behavior: `Off`, `Clone`, `Freeze` |
| `VersionFixups` | `Dictionary<string, string>` | `{"hl7.fhir.r4.core@4.0.0": "4.0.1"}` | Known version corrections |

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
before registration. Explicit and lazy indexing failures propagate and can be
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
