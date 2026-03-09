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

## CLI (Command-Line Tool)

Use the **fhir-pkg** .NET global tool to manage FHIR packages from the terminal
or CI/CD pipelines.

| Document | Description |
|----------|-------------|
| [CLI Overview](cli-overview.md) | Installation, quick start, command summary, and environment variables. |
| [CLI Reference](cli-reference.md) | Complete reference for all commands, options, arguments, and exit codes. |

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
