# FhirPkg Documentation

FhirPkg is a C# SDK and CLI tool for discovering, resolving, downloading,
caching, and managing [FHIR packages](https://registry.fhir.org/) from multiple
registries.

## How this documentation is organized

Each document has one job. The layer it belongs to says which job that is, and
the invariant below the table says what follows from it.

| Layer | Documents | Owns | Must not contain |
|-------|-----------|------|------------------|
| Publish | `README.md` | Orientation for the nuget.org package page; absolute links only | Repo-relative links; unmarked enumerations of the shipped surface |
| Route | `docs/index.md`, `reference/index.md` | The list of documents and what each is for | Any fact about the code |
| Teach | `docs/cli-overview.md`, `docs/sdk-overview.md` | Narrative, quick start, worked examples | Unmarked enumerations |
| Enumerate | `docs/cli-reference.md`, `docs/sdk-api-reference.md`, `docs/process.md`, `docs/versioning-policy.md`, `docs/releases/README.md` | The exhaustive tables — single source of truth | — |
| Domain | `reference/**` | The FHIR package ecosystem, vendor-neutral | `FhirPkg`-specific API detail |

**One owner per enumeration.** An enumeration of the shipped surface — a table
of commands, global options, configuration keys, environment variables, or exit
codes — has exactly one owning document, named in the *Enumerate* row above.
Every other appearance of it is either a link to that owner or a marked mirror.

A marked mirror is the single sanctioned exception, for the case where
repeating a table genuinely serves the reader where they are standing. It must
carry an HTML comment naming its owner, immediately above the heading it
duplicates, in this literal form:
`<!-- mirror-of: docs/cli-reference.md#global-options -->`. The marker turns
silent drift into greppable drift: when the owner gains a row, `grep` finds
every copy that has to gain it too.

`AGENTS.md` is deliberately **not** a documentation layer. It is the agent
contract, and its own preamble already says it "restates only the commands and
rules an agent needs" — it consumes the owners above rather than being one of
them.

## SDK (Library)

Use the **fhir-pkg-lib** NuGet package to integrate FHIR package management
into your .NET applications.

| Document | Description |
|----------|-------------|
| [SDK Overview](sdk-overview.md) | Introduction, quick start, DI setup, configuration, and architecture. |
| [SDK API Reference](sdk-api-reference.md) | Reference for the supported public surface — interfaces, models, enums, and options. |
| [Package Request Process](process.md) | End-to-end walkthrough of resolution, download, extraction, caching, and indexing. |
| [Version Resolution Policy](versioning-policy.md) | Configuration snapshots, fixups, pre-release rules, and FHIR-release filtering. |
| [Release Process and Evidence](releases/README.md) | Exact-commit source CI, immutable candidate handoff, 3 x 4 qualification (twelve jobs), publication gates, and evidence. |
| [Changelog](../CHANGELOG.md) | User-visible changes for both packages, by release. |

## CLI (Command-Line Tool)

Use the **fhir-pkg-cli** .NET global tool to install the `fhir-pkg` command for
the terminal or CI/CD pipelines.

| Document | Description |
|----------|-------------|
| [CLI Overview](cli-overview.md) | Installation, quick start, command summary, and environment variables. |
| [CLI Reference](cli-reference.md) | Complete reference for all commands, options, arguments, and exit codes. |

## Domain Reference

Background on the FHIR package ecosystem itself, vendor-neutral and independent
of any one implementation.

| Document | Description |
|----------|-------------|
| [FHIR Package Reference](../reference/index.md) | Package naming, versioning, resolution, registry API, caching, dependencies, client implementations, security, and errors. |

## Dependencies

### CLI (.NET Global Tool)

Running `fhir-pkg` requires the [.NET 8 SDK or runtime](https://dotnet.microsoft.com/)
or later (.NET 8, 9, 10, and 11 are supported; .NET 11 is recommended).

The CLI pulls in the following packages (resolved automatically on install):

| Package | Purpose |
|---------|---------|
| [System.CommandLine](https://www.nuget.org/packages/System.CommandLine) | Command-line argument parsing |
| [Spectre.Console](https://www.nuget.org/packages/Spectre.Console) | Rich terminal output (tables, progress bars, colors) |
| **fhir-pkg-lib** (SDK) | Core FHIR package management logic (see below) |

### SDK (NuGet Library)

Integrating **fhir-pkg-lib** into your application requires a project targeting **net8.0**
or later (`net8.0`, `net9.0`, `net10.0`, and `net11.0` are supported;
`net11.0` is recommended).

| Package | Purpose |
|---------|---------|
| [Microsoft.Extensions.DependencyInjection.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions) | Service registration abstractions |
| [Microsoft.Extensions.Http](https://www.nuget.org/packages/Microsoft.Extensions.Http) | `IHttpClientFactory` support |
| [Microsoft.Extensions.Logging.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions) | Logging abstractions |
| [Microsoft.Extensions.Options](https://www.nuget.org/packages/Microsoft.Extensions.Options) | Options / configuration binding |

All four packages are transitive — they are restored automatically when you add
**fhir-pkg-lib** to your project.

### Development (Working With This Project)

Building and testing this checkout requires the **stable [.NET 11 SDK](https://dotnet.microsoft.com/),
version `11.0.100` or later within .NET 11**. `global.json` permits later
.NET 11 feature bands with `latestFeature` roll-forward and disables prerelease
SDKs. This contributor requirement is separate from the SDK/CLI consumer
compatibility above.

All seven projects inherit the four supported targets from
`Directory.Build.props`. Unqualified builds and tests cover `net8.0`, `net9.0`,
`net10.0`, and `net11.0`; explicit single-framework development uses `net11.0`.
Running every test target requires the .NET 8, 9, 10, and 11 runtimes, not just
the .NET 11 SDK.

| Package | Purpose |
|---------|---------|
| [xunit.v3](https://www.nuget.org/packages/xunit.v3) | Test framework |
| [xunit.runner.visualstudio](https://www.nuget.org/packages/xunit.runner.visualstudio) | VS / `dotnet test` integration |
| [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk) | Test-host plumbing |
| [Moq](https://www.nuget.org/packages/Moq) | Mocking (unit tests) |
| [Shouldly](https://www.nuget.org/packages/Shouldly) | Fluent assertions |
| [coverlet.collector](https://www.nuget.org/packages/coverlet.collector) | Code-coverage collection |

All test dependencies are restored automatically by `dotnet restore`.

#### Building and testing this checkout

Primary single-framework development examples, matching [AGENTS.md](../AGENTS.md):

```bash
dotnet build src/FhirPkg/FhirPkg.csproj --framework net11.0
dotnet test test/FhirPkg.Tests/FhirPkg.Tests.csproj --framework net11.0 \
  --filter "FullyQualifiedName~DiskPackageCacheTests"
```

Comprehensive build and per-project tests (all four frameworks):

```bash
dotnet restore FhirPkg.sln
dotnet build FhirPkg.sln
dotnet test test/FhirPkg.Tests/FhirPkg.Tests.csproj
dotnet test test/FhirPkg.IntegrationTests/FhirPkg.IntegrationTests.csproj
```

Legacy .NET 10 compatibility variants (not the development default):

```bash
dotnet build src/FhirPkg/FhirPkg.csproj --framework net10.0
dotnet test test/FhirPkg.Tests/FhirPkg.Tests.csproj --framework net10.0 \
  --filter "FullyQualifiedName~DiskPackageCacheTests"
```

## Getting Started

**Install the SDK:**

```bash
dotnet add package fhir-pkg-lib
```

**Install the CLI:**

```bash
dotnet tool install --global fhir-pkg-cli
```

**Install a package (CLI):**

```bash
fhir-pkg install hl7.fhir.r4.core#4.0.1
```

**Install a package (SDK):**

```csharp
using FhirPkg;

using var manager = new FhirPackageManager();
var record = await manager.InstallAsync("hl7.fhir.r4.core#4.0.1");
```
