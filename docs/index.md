# FhirPkg Documentation

FhirPkg is a C# SDK and CLI tool for discovering, resolving, downloading,
caching, and managing [FHIR packages](https://registry.fhir.org/) from multiple
registries.

## SDK (Library)

Use the **FhirPkg** NuGet package to integrate FHIR package management into your
.NET applications.

| Document | Description |
|----------|-------------|
| [SDK Overview](sdk-overview.md) | Introduction, quick start, DI setup, configuration, and architecture. |
| [SDK API Reference](sdk-api-reference.md) | Complete reference for all public interfaces, models, enums, and options. |
| [Package Request Process](process.md) | End-to-end walkthrough of resolution, download, extraction, caching, and indexing. |

## CLI (Command-Line Tool)

Use the **fhir-pkg** .NET global tool to manage FHIR packages from the terminal
or CI/CD pipelines.

| Document | Description |
|----------|-------------|
| [CLI Overview](cli-overview.md) | Installation, quick start, command summary, and environment variables. |
| [CLI Reference](cli-reference.md) | Complete reference for all commands, options, arguments, and exit codes. |

## Dependencies

### CLI (.NET Global Tool)

Running `fhir-pkg` requires the [.NET 8 SDK or runtime](https://dotnet.microsoft.com/) or later (.NET 8, 9, and 10 are supported; .NET 10 is recommended).

The CLI pulls in the following packages (resolved automatically on install):

| Package | Purpose |
|---------|---------|
| [System.CommandLine](https://www.nuget.org/packages/System.CommandLine) | Command-line argument parsing |
| [Spectre.Console](https://www.nuget.org/packages/Spectre.Console) | Rich terminal output (tables, progress bars, colors) |
| **FhirPkg** (SDK) | Core FHIR package management logic (see below) |

### SDK (NuGet Library)

Integrating **FhirPkg** into your application requires a project targeting **net8.0**
or later (net8.0, net9.0, and net10.0 are supported; net10.0 is recommended).

| Package | Purpose |
|---------|---------|
| [Microsoft.Extensions.DependencyInjection.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions) | Service registration abstractions |
| [Microsoft.Extensions.Http](https://www.nuget.org/packages/Microsoft.Extensions.Http) | `IHttpClientFactory` support |
| [Microsoft.Extensions.Logging.Abstractions](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Abstractions) | Logging abstractions |
| [Microsoft.Extensions.Options](https://www.nuget.org/packages/Microsoft.Extensions.Options) | Options / configuration binding |

All four packages are transitive — they are restored automatically when you add
**FhirPkg** to your project.

### Development (Working With This Project)

Building and testing locally requires the [.NET 8 SDK](https://dotnet.microsoft.com/) or later (.NET 10 is recommended).

| Package | Purpose |
|---------|---------|
| [xunit](https://www.nuget.org/packages/xunit) | Test framework |
| [xunit.runner.visualstudio](https://www.nuget.org/packages/xunit.runner.visualstudio) | VS / `dotnet test` integration |
| [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk) | Test-host plumbing |
| [Moq](https://www.nuget.org/packages/Moq) | Mocking (unit tests) |
| [Shouldly](https://www.nuget.org/packages/Shouldly) | Fluent assertions |
| [coverlet.collector](https://www.nuget.org/packages/coverlet.collector) | Code-coverage collection |

All test dependencies are restored automatically by `dotnet restore`.

## Getting Started

**Install the SDK:**

```bash
dotnet add package FhirPkg
```

**Install the CLI:**

```bash
dotnet tool install --global fhir-pkg
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
