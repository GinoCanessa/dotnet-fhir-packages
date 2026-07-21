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
| `--package-cache-folder <path>` | — | `string` | `PACKAGE_CACHE_FOLDER` env var, or `~/.fhir/packages` | Override the local FHIR package cache directory. |
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
| `--overwrite` | — | `bool` | `false` | Re-download packages already in the cache. An unchanged mutable CI archive is reported as refreshed. |
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

# Explicitly refresh the current CI build
fhir-pkg install hl7.fhir.us.core#current --overwrite

# JSON output for scripting
fhir-pkg install hl7.fhir.us.core#current --json
```

With `--with-dependencies`, the root package is committed before its active
dependency closure is installed. If any requested dependency fails, the command
returns exit code `6`, lists each failed child directive, and reports the root
cache path as committed partial state.

#### Output

**Console mode** — exact and other immutable directives retain their existing
installed/already-cached labels. Mutable CI aliases distinguish all successful
cache effects:

```text
  first.package#current                   ✓ installed
    manifest date: 20260721
  updated.package#current                 ✓ updated from CI
    manifest date: 20260720 -> 20260721
  current.package#current                 ✓ already current
    manifest date: 20260721
  refreshed.package#current               ✓ refreshed
    manifest date: 20260721 (unchanged)

Summary: 1 installed, 1 updated, 1 already current, 1 refreshed,
         0 already cached, 0 failed
```

Missing manifest dates render as `unavailable`. `AlreadyCurrent` is a
successful outcome and keeps exit code `0`.

**JSON mode** — structured result with summary:

```json
{
  "results": [
    {
      "directive": "updated.package#current",
      "status": "Installed",
      "dependencyFailures": [],
      "package": {
        "name": "updated.package",
        "version": "current",
        "directoryPath": "C:\\Users\\me\\.fhir\\packages\\updated.package#current"
      },
      "disposition": "Updated",
      "previousManifestDate": "20260720",
      "manifestDate": "20260721"
    }
  ],
  "summary": {
    "total": 1,
    "installed": 1,
    "alreadyCached": 0,
    "failed": 0,
    "dispositions": {
      "installed": 0,
      "updated": 1,
      "alreadyCurrent": 0,
      "refreshed": 0
    }
  }
}
```

The existing coarse `summary.installed` count is unchanged: every successful
mutable-CI disposition contributes to it. The nested `summary.dispositions`
object is non-overlapping and its four buckets total that coarse installed
count. For a recognized mutable-CI disposition, both date keys are present and
may be explicit JSON `null`. The disposition and date keys are omitted for
non-CI results, failures, and successful installs whose cache implementation
cannot report an authoritative effect.

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
| `--lock-file <path>` | `-l` | `string` | `<project-path>/fhirpkg.lock.json` | Lock file to read and write. Relative paths are resolved against `project-path`; absolute paths are unchanged. The filename `.fhirpkg-restore.lock` is reserved for coordination. |
| `--no-lock` | — | `bool` | `false` | Do not write or update the lock file. A current existing lock can still be read. |
| `--conflict-strategy <strategy>` | — | `enum` | `HighestWins` | How to handle version conflicts. Values: `HighestWins`, `FirstWins`, `Error`. |
| `--max-depth <n>` | — | `int` | `20` | Maximum root-relative depth for transitive dependency resolution. Must be non-negative; direct dependencies are depth `0`. |
| `--fhir-version <release>` | `-f` | `string` | — | Preferred FHIR release (`R4`, `R4B`, `R5`, `R6`). |

#### Examples

```bash
# Restore from current directory
fhir-pkg restore

# Restore a specific project
fhir-pkg restore ./my-ig-project

# Restore with a lock file
fhir-pkg restore ./my-ig-project --lock-file ./locks/fhirpkg.lock.json

# Fail on version conflicts instead of auto-resolving
fhir-pkg restore --conflict-strategy Error
```

A schema-v2 lock is used as a fast path only when its project package identity
and root directives exactly match the manifest and its conflict, prerelease,
FHIR-release, depth, and version-fixup policies match the current request.
Package names are matched case-insensitively; version text is matched exactly.
Root order must also match for `FirstWins`. Legacy schema-v1, incomplete, stale,
or missing locks are re-resolved. Unknown future schemas are rejected without
changing the file. Locked dependency values must be concrete semantic-version
pins, each effective root must be represented, and an empty root set requires
an empty dependency map.

Only a complete resolution is written. Lock replacement is a durable,
same-directory atomic operation, so cancellation or a pre-commit failure leaves
the previous lock unchanged. `--overwrite` is not a restore option: cache
replacement and lock freshness are independent SDK concerns.

#### Output

**Console mode** — shows resolved packages in a table and lists any missing
dependencies:

```
Resolved 12 packages:
  Name                              Version    FHIR
  hl7.fhir.r4.core                  4.0.1      R4
  hl7.fhir.us.core                  6.1.0      R4
  ...

✓ Restore complete — 12 package(s) resolved.
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

The command reads package manifests and cache metadata, but does not hydrate
or validate persisted resource indexes (`.index.json`).

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
    { "name": "hl7.fhir.r4.core", "version": "4.0.1", "fhirVersion": "R4", "installedAt": "2026-03-01" }
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
| `--ci-only` | — | `bool` | `false` | Only remove the `current` and `current$branch` CI aliases. |
| `--older-than <days>` | — | non-negative `int` | — | Only remove packages installed more than N days ago. |

#### Examples

```bash
# Clean everything (will prompt)
fhir-pkg clean

# Clean without prompting
fhir-pkg clean --force

# Remove current and current$branch aliases
fhir-pkg clean --ci-only --force

# Remove packages installed more than 30 days ago
fhir-pkg clean --older-than 30 --force
```

Age is based on the cache installation/download timestamp, not file access
time. Packages without a recorded timestamp are never selected by
`--older-than`. When filters are combined, all filters must match; a package
installed exactly at the cutoff is retained. Malformed timestamps are treated
as unknown, and each selected package is removed only if its cache generation
is unchanged since selection.

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
{
  "count": 1,
  "results": [
    {
      "name": "hl7.fhir.us.core",
      "version": "6.1.0",
      "fhirVersion": "R4",
      "description": "US Core Implementation Guide",
      "canonical": "http://hl7.org/fhir/us/core",
      "kind": "IG",
      "date": "2024-01-15",
      "url": "https://packages.fhir.org/hl7.fhir.us.core/6.1.0"
    }
  ]
}
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

Publish a FHIR package tarball to a FHIR-NPM registry.

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

The CLI uses `RegistryType.FhirNpm`, validates the archive under the configured
package limits, and sends the `.tgz` as the raw request body to exactly the
specified endpoint. It does not fall back to configured read registries.
Standard NPM packument publication is available through the SDK by passing a
`RegistryEndpoint` whose type is `RegistryType.Npm`.

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

An `already current` mutable-CI result is successful and returns code `0`.

Notes on specific codes:

- `2 InvalidArgs` is returned by explicit command validation — currently
  `restore` with a negative `--max-depth` or an unsupported `--fhir-version`.
  Malformed parser input (a missing required option, an unknown flag, a bad
  arity) is reported by the command-line parser and exits with `1`.
- `8 AuthError` is currently produced only by `publish` (on an
  `UnauthorizedAccessException`). Other commands surface registry auth or HTTP
  failures as `4 NetworkError`.
- Cancellation (Ctrl-C) exits with `1 GeneralError`.

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
tool checks two locations, in order, and uses the **first file that exists** —
the two files are never merged:

1. The **current working directory** (project-level config)
2. The **user's home directory** (user-level defaults)

**Precedence order** (highest to lowest):

1. Command-line options
2. Environment variables (for settings that have one — e.g. `PACKAGE_CACHE_FOLDER`)
3. The first `.fhir-pkg.json` found (current directory, else home directory)
4. Built-in defaults

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
| `PACKAGE_CACHE_FOLDER` | Override the default cache directory. | `--package-cache-folder` |
| `FHIR_PKG_VERBOSE` | Set to `1`, `true`, or `yes` (case-insensitive) to enable verbose logging. | `--verbose` |
| `FHIR_PKG_JSON` | Set to `1`, `true`, or `yes` (case-insensitive) to default to JSON output. | `--json` |
| `NO_COLOR` | Set to `1`, `true`, or `yes` (case-insensitive) to disable colored output. | `--no-color` |
| `FHIRPKG_MAX_COMPRESSED_BYTES` | Max compressed (downloaded) archive size, in bytes (default 104857600). | — |
| `FHIRPKG_MAX_EXPANDED_BYTES` | Max total expanded archive size, in bytes (default 1073741824). | — |
| `FHIRPKG_MAX_ENTRY_BYTES` | Max expanded size of a single archive entry, in bytes (default 134217728). | — |
| `FHIRPKG_MAX_ARCHIVE_ENTRIES` | Max number of entries in an archive (default 50000). | — |
| `FHIRPKG_MAX_ARCHIVE_PATH_LENGTH` | Max length of a normalized entry path, in characters (default 1024). | — |
| `FHIRPKG_MAX_ARCHIVE_DEPTH` | Max directory-nesting depth of any entry (default 32). | — |
| `HTTPS_PROXY` | HTTPS proxy URL (honored by .NET's `HttpClient`). | — |
| `HTTP_PROXY` | HTTP proxy URL (honored by .NET's `HttpClient`). | — |
| `NO_PROXY` | Comma-separated list of hosts to bypass proxy. | — |

A custom registry and its credentials are configured with `--registry`/`-r` and
`--auth` (or the `registries` array in `.fhir-pkg.json`) — there is no
`FHIR_REGISTRY` or `FHIR_REGISTRY_TOKEN` variable. CI build resolution is
disabled with `install --no-ci` or `"includeCiBuilds": false` — there is no
`FHIR_PKG_NO_CI` variable. Each `FHIRPKG_MAX_*` value must be a positive base-10
integer (invariant culture); a non-positive or non-numeric value aborts the
operation.

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
- **Null suppression** — null properties are normally omitted; recognized
  mutable-CI install results retain explicit null manifest-date keys
- **Enum strings** — enum-typed fields use camelCase; the install `status` and
  `disposition` compatibility strings retain their documented PascalCase values

Errors in JSON mode are written as:

```json
{ "error": "description of the error" }
```

JSON mode is designed for use with tools like `jq`, scripting, and CI/CD
pipeline integration.
