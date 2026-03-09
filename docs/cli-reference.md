# CLI Reference

Complete reference for every `fhir-pkg` command, option, and argument.
For installation instructions and a quick-start guide, see the
[CLI Overview](cli-overview.md).

---

## Table of Contents

- [Global Options](#global-options)
- [Commands](#commands)
  - [install](#install)
  - [restore](#restore)
  - [list](#list)
  - [remove](#remove)
  - [clean](#clean)
  - [search](#search)
  - [info](#info)
  - [resolve](#resolve)
  - [publish](#publish)
- [Exit Codes](#exit-codes)
- [Configuration File](#configuration-file)
- [Environment Variables](#environment-variables)
- [Output Formats](#output-formats)

---

## Global Options

These options are available on **every** command and are applied before
command-specific logic runs.

| Option | Short | Type | Default | Description |
|--------|-------|------|---------|-------------|
| `--cache-path <path>` | `-c` | `string` | `FHIR_PACKAGE_CACHE` env var, or `~/.fhir/packages` | Override the local FHIR package cache directory. |
| `--verbose` | `-v` | `bool` | `FHIR_PKG_VERBOSE` env var | Enable verbose / debug output. |
| `--quiet` | `-q` | `bool` | `false` | Suppress all non-essential output. Only errors are shown. |
| `--no-color` | — | `bool` | `NO_COLOR` env var | Disable ANSI colored output. |
| `--json` | — | `bool` | `FHIR_PKG_JSON` env var | Emit machine-readable JSON instead of human-readable tables. |
| `--help` | `-h` | — | — | Show help for the command. |
| `--version` | — | — | — | Show the tool version. |

---

## Commands

### install

Install one or more FHIR packages into the local cache.

```
fhir-pkg install <packages...> [options]
```

#### Arguments

| Argument | Type | Description |
|----------|------|-------------|
| `packages` | `string[]` | One or more package directives to install. |

A **package directive** is a package name optionally followed by `#` and a
version specifier:

```
hl7.fhir.r4.core#4.0.1          # exact version
hl7.fhir.us.core#latest         # latest published
hl7.fhir.us.core                # implicit latest
hl7.fhir.us.core#6.1.x          # wildcard
hl7.fhir.us.core#^6.0.0         # range
hl7.fhir.us.core#current        # latest CI build
hl7.fhir.us.core#current$main   # CI build for a branch
```

#### Options

| Option | Short | Type | Default | Description |
|--------|-------|------|---------|-------------|
| `--with-dependencies` | `-d` | `bool` | `false` | Also install transitive dependencies declared in each package's manifest. |
| `--overwrite` | — | `bool` | `false` | Re-download and overwrite packages that are already in the cache. |
| `--fhir-version <release>` | `-f` | `string` | — | Preferred FHIR release when a package publishes multiple versions. Accepted values: `R4`, `R4B`, `R5`, `R6`. |
| `--pre-release` | — | `bool` | `true` | Include pre-release versions when resolving wildcards, ranges, or `latest`. |
| `--no-pre-release` | — | `bool` | `false` | Exclude pre-release versions. Takes precedence over `--pre-release`. |
| `--registry <url>` | `-r` | `string` | — | Query a custom registry URL in addition to the default registries. |
| `--auth <value>` | — | `string` | — | Authorization header value for the custom registry (e.g., `"Bearer <token>"`). |
| `--no-ci` | — | `bool` | `false` | Exclude the FHIR CI build registry from resolution. |
| `--progress` | — | `bool` | `true` | Show a download progress indicator. |

#### Examples

```bash
# Install a single package
fhir-pkg install hl7.fhir.r4.core#4.0.1

# Install two packages with all their dependencies
fhir-pkg install hl7.fhir.us.core#6.1.0 hl7.fhir.uv.extensions.r4#1.0.0 -d

# Install from a private registry
fhir-pkg install my.custom.ig#1.0.0 \
  --registry https://registry.example.com \
  --auth "Bearer my-token"

# Force re-download
fhir-pkg install hl7.fhir.r4.core#4.0.1 --overwrite

# Install latest CI build
fhir-pkg install hl7.fhir.us.core#current

# JSON output for scripting
fhir-pkg install hl7.fhir.r4.core#4.0.1 --json
```

#### Output

**Console mode** — shows a status icon per package:

```
✓ hl7.fhir.r4.core#4.0.1           Installed
● hl7.fhir.us.core#6.1.0           Already cached
✗ some.missing.package#1.0.0       Not found
```

**JSON mode** — structured result with summary:

```json
{
  "results": [
    {
      "directive": "hl7.fhir.r4.core#4.0.1",
      "status": "installed",
      "package": { "name": "hl7.fhir.r4.core", "version": "4.0.1" }
    }
  ],
  "summary": { "total": 1, "installed": 1, "alreadyCached": 0, "failed": 0 }
}
```

---

### restore

Restore FHIR package dependencies from a project manifest (`package.json` or
`.fhir-pkg.json`).

```
fhir-pkg restore [project-path] [options]
```

#### Arguments

| Argument | Type | Default | Description |
|----------|------|---------|-------------|
| `project-path` | `string` | `.` (current directory) | Path to the project directory containing the manifest file. |

#### Options

| Option | Short | Type | Default | Description |
|--------|-------|------|---------|-------------|
| `--lock-file <path>` | `-l` | `string` | — | Path to a lock file. If it exists, restore from it for deterministic results. |
| `--no-lock` | — | `bool` | `false` | Do not write or update a lock file after restore. |
| `--conflict-strategy <strategy>` | — | `enum` | `HighestWins` | How to handle version conflicts. Values: `HighestWins`, `FirstWins`, `Error`. |
| `--max-depth <n>` | — | `int` | `20` | Maximum recursion depth for transitive dependency resolution. |
| `--fhir-version <release>` | `-f` | `string` | — | Preferred FHIR release (`R4`, `R4B`, `R5`, `R6`). |

#### Examples

```bash
# Restore from current directory
fhir-pkg restore

# Restore a specific project
fhir-pkg restore ./my-ig-project

# Restore with a lock file
fhir-pkg restore --lock-file ./fhirpkg.lock.json

# Fail on version conflicts instead of auto-resolving
fhir-pkg restore --conflict-strategy Error
```

#### Output

**Console mode** — shows resolved packages in a table and lists any missing
dependencies:

```
Resolved 12 packages:
  Name                              Version    FHIR
  hl7.fhir.r4.core                  4.0.1      R4
  hl7.fhir.us.core                  6.1.0      R4
  ...

✓ Restore complete (12 resolved, 0 missing)
```

**JSON mode** — structured closure:

```json
{
  "timestamp": "2026-03-09T17:00:00Z",
  "isComplete": true,
  "resolved": {
    "hl7.fhir.r4.core": { "name": "hl7.fhir.r4.core", "version": "4.0.1" },
    "hl7.fhir.us.core": { "name": "hl7.fhir.us.core", "version": "6.1.0" }
  },
  "missing": {}
}
```

---

### list

List FHIR packages in the local cache.

```
fhir-pkg list [filter] [options]
```

#### Arguments

| Argument | Type | Default | Description |
|----------|------|---------|-------------|
| `filter` | `string` | — | Optional filter to match package names. Supports glob patterns. |

#### Options

| Option | Short | Type | Default | Description |
|--------|-------|------|---------|-------------|
| `--sort <field>` | `-s` | `string` | `name` | Sort order. Values: `name`, `version`, `date`, `size`. |
| `--show-size` | — | `bool` | `false` | Include package sizes in the output. |

#### Examples

```bash
# List all cached packages
fhir-pkg list

# Filter by name prefix
fhir-pkg list hl7.fhir.r4

# Show sizes, sorted by size
fhir-pkg list --show-size --sort size

# JSON output
fhir-pkg list --json
```

#### Output

**Console mode:**

```
Name                        Version    FHIR    Installed
hl7.fhir.r4.core            4.0.1      R4      2026-03-01
hl7.fhir.us.core            6.1.0      R4      2026-03-05
```

**JSON mode:**

```json
{
  "count": 2,
  "packages": [
    { "name": "hl7.fhir.r4.core", "version": "4.0.1", "fhirVersion": "R4", "installed": "2026-03-01" }
  ]
}
```

---

### remove

Remove one or more FHIR packages from the local cache.

```
fhir-pkg remove <packages...> [options]
```

#### Arguments

| Argument | Type | Description |
|----------|------|-------------|
| `packages` | `string[]` | One or more package directives to remove (e.g., `hl7.fhir.r4.core#4.0.1`). |

#### Options

| Option | Short | Type | Default | Description |
|--------|-------|------|---------|-------------|
| `--force` | `-f` | `bool` | `false` | Skip the confirmation prompt. |

#### Examples

```bash
# Remove a specific version (will prompt for confirmation)
fhir-pkg remove hl7.fhir.us.core#6.1.0

# Remove without prompting
fhir-pkg remove hl7.fhir.us.core#6.1.0 --force

# Remove multiple packages
fhir-pkg remove hl7.fhir.r4.core#4.0.1 hl7.fhir.us.core#6.1.0 -f
```

---

### clean

Clear the local FHIR package cache.

```
fhir-pkg clean [options]
```

#### Options

| Option | Short | Type | Default | Description |
|--------|-------|------|---------|-------------|
| `--force` | `-f` | `bool` | `false` | Skip the confirmation prompt. |
| `--ci-only` | — | `bool` | `false` | Only remove CI build (pre-release snapshot) packages. |
| `--older-than <days>` | — | `int` | — | Only remove packages not accessed in the last N days. |

#### Examples

```bash
# Clean everything (will prompt)
fhir-pkg clean

# Clean without prompting
fhir-pkg clean --force

# Remove only CI build packages
fhir-pkg clean --ci-only --force

# Remove packages not used in the last 30 days
fhir-pkg clean --older-than 30 --force
```

---

### search

Search FHIR package registries.

```
fhir-pkg search [options]
```

#### Options

| Option | Short | Type | Default | Description |
|--------|-------|------|---------|-------------|
| `--name <prefix>` | `-n` | `string` | — | Package name prefix to search for. |
| `--canonical <url>` | — | `string` | — | Filter by implementation guide canonical URL. |
| `--fhir-version <release>` | `-f` | `string` | — | Filter by FHIR version (`R4`, `R4B`, `R5`, `R6`). |
| `--sort <field>` | `-s` | `string` | — | Sort results. Values: `name`, `date`, `version`. |
| `--limit <n>` | — | `int` | `50` | Maximum number of results to return. |
| `--registry <url>` | `-r` | `string` | — | Search a custom registry URL. |

#### Examples

```bash
# Search by name prefix
fhir-pkg search --name hl7.fhir.us

# Search for R4 packages
fhir-pkg search --name hl7.fhir.us --fhir-version R4

# Search by canonical URL
fhir-pkg search --canonical http://hl7.org/fhir/us/core

# Limit results
fhir-pkg search --name hl7 --limit 10 --sort name

# Search a private registry
fhir-pkg search --name my.org --registry https://registry.example.com
```

#### Output

**Console mode:**

```
Name                        Version    FHIR    Description
hl7.fhir.us.core            6.1.0      R4      US Core Implementation Guide
hl7.fhir.us.davinci-pas     2.0.1      R4      Da Vinci Prior Authorization
```

**JSON mode:**

```json
[
  {
    "name": "hl7.fhir.us.core",
    "version": "6.1.0",
    "fhirVersion": "R4",
    "description": "US Core Implementation Guide",
    "canonical": "http://hl7.org/fhir/us/core",
    "kind": "IG",
    "date": "2024-01-15"
  }
]
```

---

### info

Display detailed information about a FHIR package.

```
fhir-pkg info <package> [options]
```

#### Arguments

| Argument | Type | Description |
|----------|------|-------------|
| `package` | `string` | Package identifier (e.g., `hl7.fhir.us.core`). |

#### Options

| Option | Short | Type | Default | Description |
|--------|-------|------|---------|-------------|
| `--versions` | — | `bool` | `false` | Show all available versions. |
| `--dependencies` | — | `bool` | `false` | Show package dependencies. |

#### Examples

```bash
# Basic package info
fhir-pkg info hl7.fhir.us.core

# Show all versions
fhir-pkg info hl7.fhir.us.core --versions

# Show dependencies
fhir-pkg info hl7.fhir.us.core --dependencies

# Both
fhir-pkg info hl7.fhir.us.core --versions --dependencies
```

#### Output

**Console mode:**

```
Package:     hl7.fhir.us.core
Description: US Core Implementation Guide
Latest:      6.1.0
FHIR:        R4

Dist Tags:
  latest → 6.1.0

Versions:
  Version    Date          Cached
  6.1.0      2024-01-15    ✓
  6.0.0      2023-07-01
  5.0.1      2023-01-15
```

**JSON mode** — full listing with all version metadata.

---

### resolve

Resolve a package directive to a concrete version and download URL **without
downloading** the package.

```
fhir-pkg resolve <directive>
```

#### Arguments

| Argument | Type | Description |
|----------|------|-------------|
| `directive` | `string` | Package directive to resolve (e.g., `hl7.fhir.us.core#latest`). |

#### Options

None (only global options apply).

#### Examples

```bash
# Resolve latest
fhir-pkg resolve hl7.fhir.us.core#latest

# Resolve a wildcard
fhir-pkg resolve hl7.fhir.r4.core#4.0.x

# Resolve CI build
fhir-pkg resolve hl7.fhir.us.core#current
```

#### Output

**Console mode:**

```
Name:       hl7.fhir.us.core
Version:    6.1.0
Tarball:    https://packages.fhir.org/hl7.fhir.us.core/6.1.0
SHA:        a1b2c3d4e5f6...
Registry:   https://packages.fhir.org
Published:  2024-01-15
```

**JSON mode:**

```json
{
  "name": "hl7.fhir.us.core",
  "version": "6.1.0",
  "tarball": "https://packages.fhir.org/hl7.fhir.us.core/6.1.0",
  "shaSum": "a1b2c3d4e5f6...",
  "registry": "https://packages.fhir.org",
  "publicationDate": "2024-01-15"
}
```

---

### publish

Publish a FHIR package tarball to a registry.

```
fhir-pkg publish <tarball> --registry <url> --auth <value>
```

#### Arguments

| Argument | Type | Description |
|----------|------|-------------|
| `tarball` | `string` | Path to the `.tgz` package tarball to publish. |

#### Options

| Option | Short | Type | Default | Required | Description |
|--------|-------|------|---------|----------|-------------|
| `--registry <url>` | `-r` | `string` | — | **Yes** | Registry URL to publish to. |
| `--auth <value>` | — | `string` | — | **Yes** | Authorization header value (e.g., `"Bearer <token>"`). |

#### Examples

```bash
# Publish to a private registry
fhir-pkg publish ./output/my.ig.package-1.0.0.tgz \
  --registry https://registry.example.com \
  --auth "Bearer my-publish-token"
```

#### Output

**Console mode:**

```
✓ Published my.ig.package#1.0.0 to https://registry.example.com
```

**JSON mode:**

```json
{
  "success": true,
  "message": "Published successfully",
  "statusCode": 201
}
```

---

## Exit Codes

| Code | Constant | Description |
|------|----------|-------------|
| `0` | `Success` | Command completed successfully. |
| `1` | `GeneralError` | An unspecified error occurred. |
| `2` | `InvalidArgs` | Invalid or missing command-line arguments. |
| `3` | `NotFound` | One or more requested packages were not found. |
| `4` | `NetworkError` | A network or connectivity error occurred. |
| `5` | `ChecksumFail` | SHA-1 checksum verification failed for a downloaded package. |
| `6` | `DependencyResolutionFail` | Dependency resolution could not be completed. |
| `7` | `CacheError` | An error occurred reading from or writing to the local cache. |
| `8` | `AuthError` | Authentication or authorization failed. |

Use exit codes in scripts:

```bash
fhir-pkg install hl7.fhir.r4.core#4.0.1
if [ $? -ne 0 ]; then
  echo "Installation failed"
  exit 1
fi
```

Or in PowerShell:

```powershell
fhir-pkg install hl7.fhir.r4.core#4.0.1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Installation failed with code $LASTEXITCODE"
}
```

---

## Configuration File

`fhir-pkg` reads an optional `.fhir-pkg.json` file for default settings. The
tool checks two locations:

1. The **current working directory** (project-level config)
2. The **user's home directory** (user-level defaults)

**Precedence order** (highest to lowest):

1. Command-line options
2. Environment variables
3. `.fhir-pkg.json` in current directory
4. `.fhir-pkg.json` in home directory
5. Built-in defaults

### Schema

```json
{
  "cachePath": "/path/to/cache",
  "httpTimeout": 60,
  "includeCiBuilds": true,
  "verifyChecksums": true,
  "registries": [
    {
      "url": "https://registry.example.com",
      "type": "FhirNpm",
      "auth": "Bearer my-token"
    }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `cachePath` | `string` | Override the default cache directory. |
| `httpTimeout` | `int` | HTTP timeout in seconds. |
| `includeCiBuilds` | `bool` | Whether to include the CI build registry. |
| `verifyChecksums` | `bool` | Whether to verify SHA-1 checksums on download. |
| `registries` | `array` | Additional registry endpoints. |
| `registries[].url` | `string` | Registry base URL. |
| `registries[].type` | `string` | Registry type: `FhirNpm`, `FhirCiBuild`, `FhirHttp`, `Npm`. |
| `registries[].auth` | `string` | Authorization header value. |

---

## Environment Variables

| Variable | Description | Equivalent Option |
|----------|-------------|-------------------|
| `FHIR_PACKAGE_CACHE` | Override the default cache directory. | `--cache-path` |
| `FHIR_REGISTRY` | Custom registry URL added to the default chain. | `--registry` |
| `FHIR_REGISTRY_TOKEN` | Bearer token for registry authentication. | `--auth "Bearer ..."` |
| `FHIR_PKG_NO_CI` | Set to `1` to disable CI build resolution. | `--no-ci` |
| `FHIR_PKG_VERBOSE` | Set to `1` to enable verbose logging. | `--verbose` |
| `FHIR_PKG_JSON` | Set to `1` to default to JSON output. | `--json` |
| `NO_COLOR` | Set to any value to disable colored output. | `--no-color` |
| `HTTPS_PROXY` | HTTPS proxy URL. | — |
| `HTTP_PROXY` | HTTP proxy URL. | — |
| `NO_PROXY` | Comma-separated list of hosts to bypass proxy. | — |

---

## Output Formats

### Console (Default)

Human-readable output with:

- **Status icons** — `✓` (success), `●` (info/cached), `✗` (error), `⚠` (warning)
- **Tables** — formatted with Spectre.Console
- **Color** — green (success), red (error), yellow (warning), grey (verbose)

Disable color with `--no-color` or the `NO_COLOR` environment variable.

### JSON (`--json`)

Machine-readable JSON output written to stdout. All JSON output uses:

- **camelCase** property names
- **Indented** formatting
- **Null suppression** — null properties are omitted
- **camelCase enums** — enum values are serialized as camelCase strings

Errors in JSON mode are written as:

```json
{ "error": "description of the error" }
```

JSON mode is designed for use with tools like `jq`, scripting, and CI/CD
pipeline integration.
