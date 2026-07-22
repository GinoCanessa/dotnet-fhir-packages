# fhir-pkg CLI Overview

`fhir-pkg` is a command-line tool for managing FHIR packages — install, restore,
search, publish, and maintain a local package cache. It is built on the
[FhirPkg SDK](sdk-overview.md) and is distributed as a
**.NET global tool**.

## Features

- **Install** packages by directive (e.g., `hl7.fhir.r4.core#4.0.1`) with
  optional transitive dependency resolution.
- **Restore** all dependencies declared in a project `package.json` with
  lock-file support for deterministic builds.
- **Search** the FHIR package registries by name, canonical URL, or FHIR version.
- **List**, **remove**, and **clean** cached packages.
- **Inspect** package metadata and available versions.
- **Resolve** a directive to an exact version and download URL without downloading.
- **Publish** a `.tgz` tarball to a registry.
- **JSON output** mode for machine-readable integration with scripts and CI pipelines.

## Prerequisites

- [.NET 8.0 SDK or Runtime](https://dotnet.microsoft.com/download) or later (8, 9, and 10 are supported; .NET 10 is recommended)

## Installation

```bash
dotnet tool install --global fhir-pkg-cli
```

To update:

```bash
dotnet tool update --global fhir-pkg-cli
```

## Quick Start

```bash
# Install a package
fhir-pkg install hl7.fhir.r4.core#4.0.1

# Install with transitive dependencies
fhir-pkg install hl7.fhir.us.core#6.1.0 --with-dependencies

# Restore project dependencies from package.json
fhir-pkg restore ./my-ig-project

# List cached packages
fhir-pkg list

# Search registries for US Core packages
fhir-pkg search --name hl7.fhir.us --fhir-version R4

# Show package information
fhir-pkg info hl7.fhir.us.core --versions --dependencies

# Resolve a directive without downloading
fhir-pkg resolve hl7.fhir.us.core#latest

# Remove a package
fhir-pkg remove hl7.fhir.us.core#6.1.0

# Clean the cache
fhir-pkg clean --force

# Publish a package
fhir-pkg publish ./my-package.tgz --registry https://my-registry.example.com --auth "Bearer token"
```

## Global Options

These options apply to **every** command:

| Option | Short | Type | Default | Description |
|--------|-------|------|---------|-------------|
| `--package-cache-folder` | — | string | `PACKAGE_CACHE_FOLDER` env var or `~/.fhir/packages` | Local FHIR package cache directory |
| `--verbose` | `-v` | bool | `FHIR_PKG_VERBOSE` env var | Enable verbose/debug output |
| `--quiet` | `-q` | bool | `false` | Suppress all non-essential output |
| `--no-color` | — | bool | `NO_COLOR` env var | Disable colored output |
| `--json` | — | bool | `FHIR_PKG_JSON` env var | Output results as JSON |
| `--help` | `-h` | — | — | Show help |
| `--version` | — | — | — | Show version |

## Commands

| Command | Description |
|---------|-------------|
| [`install`](cli-reference.md#install) | Install one or more FHIR packages into the local cache |
| [`restore`](cli-reference.md#restore) | Restore dependencies from a project manifest |
| [`list`](cli-reference.md#list) | List packages in the local cache |
| [`remove`](cli-reference.md#remove) | Remove packages from the local cache |
| [`clean`](cli-reference.md#clean) | Clear the local package cache |
| [`search`](cli-reference.md#search) | Search FHIR package registries |
| [`info`](cli-reference.md#info) | Display detailed information about a package |
| [`resolve`](cli-reference.md#resolve) | Resolve a directive to an exact version and URL |
| [`publish`](cli-reference.md#publish) | Publish a tarball to a registry |

## Output Modes

By default, `fhir-pkg` writes human-readable output using tables and color. Pass
`--json` to get machine-readable JSON output suitable for piping into `jq` or
consuming from scripts:

```bash
# Human-readable
fhir-pkg list

# JSON output
fhir-pkg list --json | jq '.packages[].name'
```

## Environment Variables

| Variable | Description |
|----------|-------------|
| `PACKAGE_CACHE_FOLDER` | Override the default cache directory |
| `FHIR_PKG_VERBOSE` | Enable verbose logging (see note) |
| `FHIR_PKG_JSON` | Default to JSON output (see note) |
| `NO_COLOR` | Disable colored output (see note) |
| `FHIRPKG_MAX_*` | Override archive-safety limits (see the [CLI reference](cli-reference.md#environment-variables)) |
| `HTTPS_PROXY` / `HTTP_PROXY` / `NO_PROXY` | Proxy configuration (honored by .NET's `HttpClient`) |

> **Note:** The boolean variables `FHIR_PKG_VERBOSE`, `FHIR_PKG_JSON`, and
> `NO_COLOR` are enabled only by the values `1`, `true`, or `yes`
> (case-insensitive); any other value — including `0` or `false` — leaves the
> feature off. A custom registry and its credentials are configured with
> `--registry`/`-r` and `--auth`, or the `registries` array in `.fhir-pkg.json`
> — there is no `FHIR_REGISTRY` or `FHIR_REGISTRY_TOKEN` variable. CI build
> resolution is disabled with `install --no-ci` or `"includeCiBuilds": false` in
> `.fhir-pkg.json` — there is no `FHIR_PKG_NO_CI` variable.

## Configuration File

`fhir-pkg` looks for an optional `.fhir-pkg.json` configuration file in the
current directory and, if none is found there, in the user's home directory.
Only the first file found is used — the two are never merged. CLI options take
precedence over the config file, and the config file takes precedence over
built-in defaults.

**Precedence order:** CLI options → environment variables → `.fhir-pkg.json`
(current dir) → `.fhir-pkg.json` (home dir) → built-in defaults.

Example `.fhir-pkg.json`:

```json
{
  "cachePath": "/custom/cache/path",
  "httpTimeout": 60,
  "includeCiBuilds": false,
  "verifyChecksums": true,
  "registries": [
    {
      "url": "https://my-registry.example.com",
      "type": "FhirNpm",
      "auth": "Bearer my-token"
    }
  ]
}
```

## Exit Codes

| Code | Name | Description |
|------|------|-------------|
| 0 | Success | Command completed successfully |
| 1 | GeneralError | Unspecified error |
| 2 | InvalidArgs | Invalid command-line arguments |
| 3 | NotFound | Package not found |
| 4 | NetworkError | Network or connectivity error |
| 5 | ChecksumFail | SHA-1 checksum verification failed |
| 6 | DependencyResolutionFail | Dependency resolution could not complete |
| 7 | CacheError | Local cache read/write error |
| 8 | AuthError | Authentication or authorization failure |

## Next Steps

- [CLI Reference](cli-reference.md) — complete command and option documentation.
- [SDK Overview](sdk-overview.md) — use the library directly in your C# projects.
- [SDK API Reference](sdk-api-reference.md) — full interface and model details.
