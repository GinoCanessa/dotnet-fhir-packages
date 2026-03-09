# FhirPkg

A C# SDK and CLI tool for discovering, resolving, downloading, caching, and
managing [FHIR packages](https://registry.fhir.org/) from multiple registries.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

## Features

- **Multi-registry resolution** — queries the primary FHIR registry
  (`packages.fhir.org`), secondary registry, CI builds (`build.fhir.org`), HL7
  website, NPM registries, and custom/private registries with automatic fallback.
- **Local disk cache** — stores packages in the standard `~/.fhir/packages`
  layout, compatible with other FHIR tooling.
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
| **FhirPkg** | SDK library — add to your .NET projects |
| **fhir-pkg** | CLI tool — install as a .NET global tool |

## Quick Start

### CLI

```bash
# Install the tool
dotnet tool install --global fhir-pkg

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
dotnet add package FhirPkg
```

```csharp
using FhirPkg;

// Create a manager with default options
using var manager = new FhirPackageManager();

// Install a package
var record = await manager.InstallAsync("hl7.fhir.r4.core#4.0.1");
Console.WriteLine($"Installed to {record?.ContentPath}");

// Search registries
var results = await manager.SearchAsync(
    new PackageSearchCriteria { Name = "hl7.fhir.us", FhirVersion = "R4" });

// Resolve without downloading
var resolved = await manager.ResolveAsync("hl7.fhir.us.core#latest");
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

// Then inject IFhirPackageManager wherever needed
```

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
| [SDK Overview](docs/sdk-overview.md) | Introduction, quick start, DI setup, configuration |
| [SDK API Reference](docs/sdk-api-reference.md) | Complete interface, model, and enum reference |
| [CLI Overview](docs/cli-overview.md) | Installation, quick start, command summary |
| [CLI Reference](docs/cli-reference.md) | All commands, options, exit codes, and config |

## License

This project is licensed under the [MIT License](LICENSE).
