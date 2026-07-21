# FhirPkg

A C# SDK and CLI tool for discovering, resolving, downloading, caching, and
managing [FHIR packages](https://registry.fhir.org/) from multiple registries.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

## Features

- **Multi-registry resolution** — queries the primary FHIR registry
  (`packages.fhir.org`), secondary registry, CI builds (`build.fhir.org`), HL7
  website, NPM registries, and custom/private registries with automatic fallback.
- **Local disk cache** — stores packages in the standard `~/.fhir/packages`
  layout with validated reads, transactional replacement, crash recovery, and
  same-identity coordination across SDK processes.
- **Hardened package sources** — safely installs expected-identity or
  manifest-discovered packages from caller-owned streams and absolute
  HTTP/HTTPS URIs under finite compressed/archive limits.
- **Dependency resolution** — resolves full transitive dependency closures with
  conflict strategies, lock-file support, and circular-dependency detection.
- **FHIR-aware versioning** — understands pre-release hierarchies, wildcards,
  ranges, CI builds, and branch-specific builds.
- **Resource indexing** — indexes FHIR resources inside packages with fast lookup
  by resource type, canonical URL, or StructureDefinition flavor.
- **Publish** — publish package tarballs to a registry.
- **Async & DI-ready** — fully async with `CancellationToken` support and
  first-class `IServiceCollection` integration.

## Packages

| Package | Description |
|---------|-------------|
| **fhir-pkg-lib** | SDK library — add to your .NET projects |
| **fhir-pkg-cli** | CLI tool — installs the `fhir-pkg` command |

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

// Create a manager with default options
using var manager = new FhirPackageManager();

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

// Search cached package resources. Missing indexes are generated and persisted
// lazily; newly installed packages are indexed eagerly.
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
package identity lease, and atomically persists them before making them
searchable. Indexing failures from explicit or lazy queries are surfaced and
can be retried; eager post-install indexing failures are logged without
changing installation success. Parsed resources use an identity-aware LRU
cache controlled by `ResourceCacheSize` and `ResourceCacheSafeMode`. Custom
`IPackageCache` implementations without the SDK's generation-aware read
capability bypass parsed-resource caching.

## Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download) or later (8, 9, and 10 are supported; .NET 10 is recommended)

## Building from Source

```bash
git clone <repo-url>
cd cs-fhir-packages
dotnet build
```

## Running Tests

```bash
# Unit tests
dotnet test test/FhirPkg.Tests

# Integration tests (offline / recorded mode)
dotnet test test/FhirPkg.IntegrationTests
```

## Project Structure

```
cs-fhir-packages/
├── src/
│   ├── FhirPkg/              # SDK library
│   └── FhirPkg.Cli/          # CLI tool
├── test/
│   ├── FhirPkg.Tests/        # Unit tests
│   └── FhirPkg.IntegrationTests/
├── docs/                      # Developer documentation
├── proposal/                  # Design proposals
└── reference/                 # Reference material
```

## Documentation

| Document | Description |
|----------|-------------|
| [Documentation Index](docs/index.md) | Landing page for all developer docs |
| [Changelog](CHANGELOG.md) | User-visible changes for both packages, by release. |
| [SDK Overview](docs/sdk-overview.md) | Introduction, quick start, DI setup, configuration |
| [SDK API Reference](docs/sdk-api-reference.md) | Complete interface, model, and enum reference |
| [CLI Overview](docs/cli-overview.md) | Installation, quick start, command summary |
| [CLI Reference](docs/cli-reference.md) | All commands, options, exit codes, and config |

## License

This project is licensed under the [MIT License](LICENSE).
