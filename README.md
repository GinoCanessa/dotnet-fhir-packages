# FhirPkg

A C# SDK and CLI tool for discovering, resolving, downloading, caching, and
managing [FHIR packages](https://registry.fhir.org/) from multiple registries.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/GinoCanessa/dotnet-fhir-packages/blob/main/LICENSE)

## Packages

| Package | Description | Status |
|---------|-------------|--------|
| **fhir-pkg-lib** | SDK library - add to your .NET projects | [![NuGet fhir-pkg-lib](https://img.shields.io/nuget/v/fhir-pkg-lib.svg)](https://www.nuget.org/packages/fhir-pkg-lib) / [Synchronized release workflow](https://github.com/GinoCanessa/dotnet-fhir-packages/actions/workflows/nuget-generator.yaml) |
| **fhir-pkg-cli** | CLI tool - installs the `fhir-pkg` command | [![NuGet fhir-pkg-cli](https://img.shields.io/nuget/v/fhir-pkg-cli.svg)](https://www.nuget.org/packages/fhir-pkg-cli) / [Synchronized release workflow](https://github.com/GinoCanessa/dotnet-fhir-packages/actions/workflows/nuget-generator.yaml) |


## Features

- **Multi-registry resolution** - queries the primary FHIR registry
  (`packages.fhir.org`), secondary registry, CI builds (`build.fhir.org`), HL7
  website, NPM registries, and custom/private registries with automatic fallback.
- **Local disk cache** - uses the standard content-directory layout by default,
  with validated reads, transactional replacement, crash recovery, and
  same-identity coordination across SDK processes.
- **Hardened package sources** - safely installs expected-identity or
  manifest-discovered packages from caller-owned streams and absolute
  HTTP/HTTPS URIs under finite compressed/archive limits.
- **Dependency resolution** - resolves full transitive dependency closures,
  retains every required exact package version, and uses conflict strategies
  only to select the preferred name-keyed projection.
- **Always-live restore** - resolves each project restore from the current
  manifest plus registry/cache state and installs coexisting exact versions.
- **FHIR-aware versioning** - understands pre-release hierarchies, wildcards,
  ranges, CI builds, and branch-specific builds.
- **Resource indexing** - indexes FHIR resources inside packages with fast lookup
  by resource type, canonical URL, or StructureDefinition flavor.
- **Unmodified mode** - optionally preserves root-layout archives without a
  wrapper and uses in-memory indexes rather than writing package payloads.
- **Publish** - publish package tarballs to a registry.
- **Async & DI-ready** - fully async with `CancellationToken` support and
  first-class `IServiceCollection` integration.

## Quick Start

### CLI

```bash
# Install the tool
dotnet tool install --global fhir-pkg-cli

# Install a FHIR package
fhir-pkg install hl7.fhir.r4.core#4.0.1

# Install with transitive dependencies
fhir-pkg install hl7.fhir.us.core#6.1.0 --with-dependencies

# Restore project dependencies from package.json
fhir-pkg restore ./my-ig-project

# Search registries
fhir-pkg search --name hl7.fhir.us --fhir-version R4

# List cached packages
fhir-pkg list

# Get package info
fhir-pkg info hl7.fhir.us.core --versions
```

### SDK

```bash
dotnet add package fhir-pkg-lib
```

```csharp
using System.Text.Json.Nodes;
using FhirPkg;
using FhirPkg.Indexing;
using FhirPkg.Models;

// Create a manager with default options
using var manager = new FhirPackageManager();

// Use your own cancellation token where you have one
CancellationToken cancellationToken = default;

// Install a package
var record = await manager.InstallAsync("hl7.fhir.r4.core#4.0.1");
Console.WriteLine($"Installed to {record?.ContentPath}");

// Install caller-owned content from its current stream position.
// The manager leaves the stream open.
await using FileStream packageStream = File.OpenRead("./package.tgz");
PackageRecord direct = await manager.InstallAsync(
    new PackageReference("example.package", "1.0.0"),
    packageStream,
    new PackageSourceInstallOptions
    {
        ExpectedSha256 = "..."
    },
    cancellationToken);

// Or discover the validated identity from a URI package manifest.
PackageRecord imported = await manager.ImportAsync(
    new Uri("https://packages.example.test/package.tgz"),
    options: null,
    cancellationToken);

// Search registries
var results = await manager.SearchAsync(
    new PackageSearchCriteria { Name = "hl7.fhir.us", FhirVersion = "R4" });

// Resolve without downloading
var resolved = await manager.ResolveAsync("hl7.fhir.us.core#latest");

// Search cached package resources. Ordinary managers persist missing indexes
// lazily; unmodified managers build them in memory. New installs are indexed eagerly.
ResourceInfo? profile = await manager.FindByCanonicalUrlAsync(
    "http://hl7.org/fhir/StructureDefinition/Patient",
    "hl7.fhir.r4.core#4.0.1");
JsonNode? resource = profile is null
    ? null
    : await manager.ReadResourceAsync(profile);
```

#### Dependency Injection

```csharp
services.AddFhirPackageManagement(options =>
{
    options.CachePath = "/my/cache";
    options.IncludeCiBuilds = false;
    options.Registries.Add(new RegistryEndpoint
    {
        Url = "https://my-registry.example.com",
        Type = RegistryType.FhirNpm,
        AuthHeaderValue = "Bearer my-token",
    });
});

// IFhirPackageManager exposes resource operations through additive extension
// methods. IFhirPackageResourceManager can also be injected directly; both
// resolve to the same singleton.
```

`FhirPackageManager` implements `IHardenedFhirPackageManager`, and the default
`DiskPackageCache` implements `IHardenedPackageCache`. Custom cache
implementations must advertise the hardened capability before any manager
install source is read. URI requests use the configured `HttpClient`,
`ResponseHeadersRead`, redirect policy, and a timeout covering the response
body copy. Network allow-list, proxy, and credential policy remain application
responsibilities.

Package acquisition and extraction are finite by default and can be tightened
with `FhirPackageManagerOptions.InstallLimits`, per-call `InstallLimits`, or the
`FHIRPKG_MAX_*` environment variables. Cache coordination applies to SDK users
of the same cache root; external tools that ignore `.fhirpkg/locks` are outside
that coordination boundary.

Resource indexes are derivative cache data. The manager validates existing
schema-v2 `.index.json` files, regenerates missing or invalid indexes under the
package identity lease, and ordinarily persists them atomically before making
them searchable. Unmodified managers reuse valid indexes or register validated
in-memory indexes without writing payload files, even with `ForceReindex`.
Indexing failures from explicit or lazy queries are surfaced and
can be retried; eager post-install indexing failures are logged without
changing installation success. Parsed resources use an identity-aware LRU
cache controlled by `ResourceCacheSize` and `ResourceCacheSafeMode`. Custom
`IPackageCache` implementations without the SDK's generation-aware read
capability bypass parsed-resource caching.

### Filesystem Retries

Safely replayable filesystem failures share one bounded policy: **5 retries
after the first attempt** (6 executions), a **10 ms minimum delay**, and fresh
inclusive **0–10 ms additive jitter**. Zero retries means one attempt.
Configure `FhirPackageManagerOptions.FileSystemRetry`, standalone component
overloads, or CLI flags:

```bash
fhir-pkg --fs-retry-count 5 --fs-min-delay-ms 10 --fs-jitter-max-ms 10 list
```

Each explicit property/flag overrides its corresponding environment variable,
then the default. Managers/components snapshot settings at construction;
static file helpers resolve once per invocation. Retries do not replay package
parsing or HTTP requests, change normal cache-lock coordination, or guarantee an
OS-call timeout. See the [SDK contract](https://github.com/GinoCanessa/dotnet-fhir-packages/blob/main/docs/sdk-api-reference.md#filesystemretryoptions),
[CLI settings](https://github.com/GinoCanessa/dotnet-fhir-packages/blob/main/docs/cli-reference.md#filesystem-retries),
and [coverage and verification](https://github.com/GinoCanessa/dotnet-fhir-packages/blob/main/docs/filesystem-retry-coverage.md).

### Archive Layout and Unmodified Mode

Both `FhirPackageManagerOptions.Unmodified` and
`InstallCacheOptions.Unmodified` are optional booleans, defaulting to `false`.
The manager snapshots its setting at construction; it covers all installation
sources, imports, dependencies, restore, and eager/lazy/forced index operations.
The low-level cache option controls one direct install/import call. The CLI
equivalents are `--unmodified` and the `unmodified` configuration key.

| Archive | Ordinary mode | Unmodified mode |
|---------|---------------|-----------------|
| Standard (`package/package.json`) | Preserves all safe root files and sibling trees alongside `package/` | Same distributed layout |
| Root-layout (`package.json`) | Wraps **all** archive payload once beneath `package/` for tooling compatibility | Preserves the archive at the identity root |

`PackageRecord.ContentPath` is the selected content directory. It may equal
`DirectoryPath` for an unmodified root-layout package. Use `DirectoryPath` to
access distributed standard siblings; content-relative SDK reads still reject
`..` and cannot escape `ContentPath`. `SizeBytes` records expanded bytes from
the whole archive, including siblings and a publisher-supplied index; later
derived indexing does not recompute it.

Unmodified is a runtime payload policy, **not persistent protection or a
read-only cache**. Staging, cache-root bookkeeping, recovery, and explicit
replacement/removal/clean remain allowed. A later ordinary manager can persist
an index in the same package, including a preserved root layout. Cache hits do
not reconstruct historical archive paths or index bytes: explicitly reinstall
with SDK `OverwriteExisting` or CLI `install --overwrite`, not `install --force`.

Older SDKs and external tools may reject preserved root-layout entries or modify
them; use a **dedicated cache** when mixing such tools. Native filesystem
Unicode normalization still limits exact visible filename fidelity. Unmodified
managers reject unsupported injected caches/indexers early, before source reads
or potentially mutating callbacks; ordinary custom-collaborator support remains
unchanged. The standalone, explicitly mutating
`TarballExtractor.NormalizePackageStructure` compatibility tool and the
publishing validation bridge retain their narrower policies.
See the [SDK contract](https://github.com/GinoCanessa/dotnet-fhir-packages/blob/main/docs/sdk-api-reference.md#archive-layout-and-runtime-preservation)
for the full layout and index behavior.

## Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download) or later (8, 9, and 10 are supported; .NET 10 is recommended)

## Building from Source

```bash
git clone https://github.com/GinoCanessa/dotnet-fhir-packages.git
cd dotnet-fhir-packages
dotnet build FhirPkg.sln
```

## Running Tests

```bash
# Unit tests
dotnet test test/FhirPkg.Tests

# Integration tests (offline / recorded mode)
dotnet test test/FhirPkg.IntegrationTests
```

<!-- mirror-of: docs/index.md -->
## Documentation

| Document | Description |
|----------|-------------|
| [Documentation Index](https://github.com/GinoCanessa/dotnet-fhir-packages/blob/main/docs/index.md) | Landing page for all developer docs |
| [Changelog](https://github.com/GinoCanessa/dotnet-fhir-packages/blob/main/CHANGELOG.md) | User-visible changes for both packages, by release. |
| [SDK Overview](https://github.com/GinoCanessa/dotnet-fhir-packages/blob/main/docs/sdk-overview.md) | Introduction, quick start, DI setup, configuration |
| [SDK API Reference](https://github.com/GinoCanessa/dotnet-fhir-packages/blob/main/docs/sdk-api-reference.md) | Reference for the supported public surface — interfaces, models, and enums |
| [Package Request Process](https://github.com/GinoCanessa/dotnet-fhir-packages/blob/main/docs/process.md) | End-to-end walkthrough of resolution, download, extraction, caching, and indexing. |
| [Version Resolution Policy](https://github.com/GinoCanessa/dotnet-fhir-packages/blob/main/docs/versioning-policy.md) | Configuration snapshots, fixups, pre-release rules, and FHIR-release filtering. |
| [Release Process and Evidence](https://github.com/GinoCanessa/dotnet-fhir-packages/blob/main/docs/releases/README.md) | Exact-commit source CI, immutable candidate handoff, 3 x 3 qualification, publication gates, and evidence. |
| [CLI Overview](https://github.com/GinoCanessa/dotnet-fhir-packages/blob/main/docs/cli-overview.md) | Installation, quick start, command summary |
| [CLI Reference](https://github.com/GinoCanessa/dotnet-fhir-packages/blob/main/docs/cli-reference.md) | All commands, options, exit codes, and config |
| [FHIR Package Reference](https://github.com/GinoCanessa/dotnet-fhir-packages/blob/main/reference/index.md) | Package naming, versioning, resolution, registry API, caching, dependencies, client implementations, security, and errors. |

## License

This project is licensed under the [MIT License](https://github.com/GinoCanessa/dotnet-fhir-packages/blob/main/LICENSE).
