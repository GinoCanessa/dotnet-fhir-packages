# Package Request Process

This document describes the end-to-end process that occurs when a FHIR package is
requested — from parsing the initial directive through resolution, download,
extraction, and caching.

## Overview

Every package operation begins with a **directive** — a string such as
`hl7.fhir.r4.core#4.0.1` that identifies the package and optionally a version.
The SDK parses the directive, checks the local cache, resolves the version against
one or more registries, downloads the tarball, verifies its integrity, extracts it
into the disk cache, and optionally indexes the FHIR resources inside.

The high-level flow for a single `InstallAsync` call is:

```
Directive ("hl7.fhir.r4.core#4.0.1")
  │
  ▼
Parse & apply fixups
  │
  ▼
Check local cache ──── hit ──▶ return cached PackageRecord
  │ miss
  ▼
Resolve version (registry chain)
  │
  ▼
Re-check cache with exact version ──── hit ──▶ return cached PackageRecord
  │ miss
  ▼
Download tarball
  │
  ▼
Verify SHA-1 checksum
  │
  ▼
Extract to cache
  │
  ▼
(Optionally) install dependencies
  │
  ▼
Return PackageRecord
```

## 1 — Parsing the Directive

A directive is parsed into a **`PackageReference`** (name + version) and a
**`PackageDirective`** (richer metadata used during resolution).

### PackageReference

`PackageReference.Parse` accepts both FHIR-style (`name#version`) and NPM-style
(`@scope/name@version`) syntax. The result is a lightweight struct carrying the
package name, version string, and optional NPM scope.

### PackageDirective

`DirectiveParser.Parse` produces a `PackageDirective` that classifies both the
**name** and the **version** of the request:

| Name type | Example | Description |
|-----------|---------|-------------|
| `CoreFull` | `hl7.fhir.r4.core` | Fully-qualified core package |
| `CorePartial` | `hl7.fhir.r4` | Short-hand; expanded to candidate IDs |
| `GuideWithFhirSuffix` | `hl7.fhir.us.core.r4` | IG with FHIR-version suffix |
| `GuideWithoutSuffix` | `hl7.fhir.us.core` | IG without FHIR-version suffix |
| `NonHl7Guide` | `acme.custom.ig` | Third-party package |

| Version type | Example | Description |
|--------------|---------|-------------|
| `Exact` | `4.0.1` | Pinned semantic version |
| `Latest` | `latest` or omitted | Highest published release |
| `Wildcard` | `4.0.x`, `4.x`, `*` | Glob-style pattern |
| `Range` | `^4.0.0`, `~4.0.0`, `>=4.0.0` | NPM-style semver range |
| `CiBuild` | `current` | Latest CI build from build.fhir.org |
| `CiBuildBranch` | `current$main` | CI build for a specific branch |
| `LocalBuild` | `dev` | Locally-built package |

### Package Fixups

Before any network call, the parsed reference is passed through
`PackageFixups.Apply`. Fixups correct known ecosystem errors — for example,
rewriting `hl7.fhir.r4.core#4.0.0` to `4.0.1` because version 4.0.0 was
published with errata. Fixups are configured in
`FhirPackageManagerOptions.VersionFixups` and applied in both single-install and
dependency-resolution paths.

## 2 — Cache Lookup

The SDK checks the local disk cache **before** contacting any registry. The cache
is stored in the standard FHIR tooling layout (default `~/.fhir/packages`,
configurable via `CachePath` or the `PACKAGE_CACHE_FOLDER` environment variable).

A package is considered installed when the directory
`{cacheRoot}/{name}#{version}/package/` exists. If the directive already specifies
an exact version and the package is cached, `InstallAsync` returns the cached
`PackageRecord` immediately — no network traffic is generated.

### Cache Structure

```
~/.fhir/packages/
├── packages.ini                              # metadata index
├── hl7.fhir.r4.core#4.0.1/
│   └── package/
│       ├── package.json                      # package manifest
│       ├── .index.json                       # resource index (optional)
│       ├── StructureDefinition-Patient.json
│       ├── ValueSet-*.json
│       └── …
├── hl7.fhir.us.core#6.1.0/
│   └── package/
│       └── …
└── …
```

`packages.ini` tracks installation metadata (install timestamp, package size, and
file count) in a simple INI-section-per-package format.

## 3 — Version Resolution

If the cache does not satisfy the request, the SDK resolves the directive to an
exact version by querying the **registry chain**.

### Registry Chain

Resolution uses a **`RedundantRegistryClient`** that wraps multiple registry
clients and queries them in priority order. The default chain is:

| Priority | Client | Endpoint | Purpose |
|----------|--------|----------|---------|
| 1 | `FhirNpmRegistryClient` | `packages.fhir.org` | Primary FHIR registry |
| 2 | `FhirNpmRegistryClient` | `packages2.fhir.org` | Secondary / mirror |
| 3 | `FhirCiBuildClient` | `build.fhir.org` | CI builds (if `IncludeCiBuilds` is true) |
| 4 | `Hl7WebsiteClient` | `hl7.org/fhir` | Fallback for core packages (if `IncludeHl7WebsiteFallback` is true) |

Custom or private registries can be prepended via
`FhirPackageManagerOptions.Registries`. A standard `NpmRegistryClient` is also
available for plain NPM registries.

### Fallback Behavior

The redundant client tries each registry in order for resolution, download, and
listing operations. If a client returns `null` or throws an exception the next
client is tried. For **search** operations, all clients are queried and results
are merged (deduplicated by package name, first occurrence wins).

### Resolving a Version

The **`VersionResolver`** receives the `PackageDirective` and a
`PackageListing` (the list of all published versions for the package) from the
registry and selects the best match:

1. **Exact** — returns the version verbatim if it exists in the listing.
2. **Latest** — returns the highest non-pre-release version (or the highest
   pre-release if `AllowPreRelease` is true and no release exists).
3. **Wildcard / Range** — delegates to `FhirSemVer.MaxSatisfying` which evaluates
   glob patterns (`4.0.x`) and NPM ranges (`^4.0.0`, `~4.0.0`, `>=4.0.0`) against
   all published versions and returns the highest match.
4. **CI Build / CI Build Branch** — handled directly by the `FhirCiBuildClient`;
   the version resolver returns `null` and the CI client resolves the build.

Pre-release versions follow the FHIR ordering hierarchy:
`Release > Ballot > Draft > Snapshot > CiBuild > Other`. Pre-releases are
excluded by default unless `AllowPreRelease` is set. When a
`PreferredFhirRelease` is specified, the resolver filters the listing to versions
targeting that FHIR release (e.g., R4, R5).

### Resolution Result

A successful resolution produces a **`ResolvedDirective`** containing:

- `PackageReference` — the exact name and version.
- `TarballUri` — the download URL.
- `ShaSum` — the expected SHA-1 hash of the tarball (may be null).
- `SourceRegistry` — which registry provided the result.
- `PublicationDate` — when the version was published.

After resolution, the cache is checked **again** with the now-exact version (the
original directive may have been a wildcard or `latest`). If the exact version is
already cached, the download is skipped.

## 4 — Download

If the resolved version is not in the cache, the SDK downloads the package
tarball.

The `RedundantRegistryClient.DownloadAsync` method sends an HTTP `GET` request to
the `TarballUri` from the resolved directive. The response is returned as a
`PackageDownloadResult` containing a `Stream` (the raw `.tgz` content) and an
optional `ContentLength`.

HTTP behavior is governed by `FhirPackageManagerOptions`:

| Option | Default | Effect |
|--------|---------|--------|
| `HttpTimeout` | 30 s | Per-request timeout |
| `MaxRedirects` | 5 | Maximum HTTP redirect hops |

If the primary registry fails, the redundant client automatically retries the
download against the next registry in the chain.

## 5 — Checksum Verification

When `VerifyChecksums` is enabled (the default) and the registry provided a
`ShaSum`, the downloaded stream is buffered into memory and its SHA-1 hash is
computed. If the hash does not match the expected value, an
`InvalidOperationException` is thrown and the package is **not** installed. This
prevents corrupted or tampered packages from entering the cache.

## 6 — Extraction and Installation

After verification, the tarball is extracted into the disk cache through
`DiskPackageCache.InstallAsync`. This is a multi-step atomic operation:

### Step-by-step

1. **Create a temporary directory** — `{cacheRoot}/.tmp-{guid}/` is created to
   hold the extraction output. No changes are made to the live cache at this
   point.

2. **Extract the tarball** — `TarballExtractor.ExtractAsync` decompresses the
   gzip stream, reads each tar entry, sanitizes file paths (removing leading `./`
   or `/`), validates against **path traversal attacks** (any entry that would
   escape the destination directory throws an `InvalidOperationException`), and
   writes the files to the temporary directory.

3. **Normalize structure** — FHIR tarballs contain a `package/` subdirectory by
   convention. `NormalizePackageStructure` ensures this layout exists — if files
   were extracted at the root level they are moved into `package/`.

4. **Read the manifest** — the package manifest (`package/package.json`) is
   deserialized from the extracted content to build the `PackageRecord`.

5. **Atomic move** — the temporary directory is moved to its final location at
   `{cacheRoot}/{name}#{version}/`. On Windows, if the source and destination are
   on different volumes, a copy-then-delete fallback is used. Because the live
   cache directory only appears once the move is complete, concurrent readers
   never see a partially-extracted package.

6. **Update metadata** — `packages.ini` is updated with the installation
   timestamp, directory size, and file count.

7. **Build PackageRecord** — a `PackageRecord` is constructed from the manifest,
   an existing `.index.json` (if present), and the computed metadata.

### Thread Safety

A `SemaphoreSlim` serializes concurrent calls to `InstallAsync` on the same cache
instance, ensuring that two parallel installs of the same package do not race.

## 7 — Dependency Installation

When `InstallOptions.IncludeDependencies` is true, the SDK reads the installed
package's manifest for its `Dependencies` map and recursively installs each
dependency by calling `InstallAsync` for every entry.

For full transitive dependency graphs (the `RestoreAsync` workflow), the SDK uses
a dedicated **`DependencyResolver`** — see
[Dependency Resolution](#8--dependency-resolution-restoreasync) below.

## 8 — Dependency Resolution (RestoreAsync)

`RestoreAsync` is a higher-level workflow designed for project-level dependency
management. It reads a project's `package.json`, resolves the full transitive
dependency closure, installs every package, and writes a lock file.

### Workflow

```
Read project package.json
  │
  ▼
Lock file exists & current? ──── yes ──▶ Restore from lock file (fast path)
  │ no
  ▼
Resolve full dependency closure
  │
  ▼
Install all resolved packages (parallel, batched)
  │
  ▼
Write fhirpkg.lock.json
  │
  ▼
Return PackageClosure
```

### Lock File Fast Path

If `fhirpkg.lock.json` exists and every dependency in the manifest is already
present in the lock file, the resolver skips the full resolution and restores
directly from the lock file — installing only packages not yet in the cache.

### Recursive Resolution

When a full resolution is needed, the `DependencyResolver` performs a recursive
depth-first traversal:

1. For each dependency in the root manifest, parse and apply fixups.
2. Resolve the version specifier to an exact version via the registry chain.
3. If the package is already in the resolved set, apply the **conflict strategy**:
   - `HighestWins` — keep the higher semantic version.
   - `FirstWins` — keep the first-encountered version.
   - `Error` — throw an exception on any conflict.
4. Fetch the resolved package's own manifest (from cache if available, otherwise
   from the registry).
5. Recurse into that package's dependencies (incrementing the depth counter).
6. Circular dependencies are detected by tracking `package@version` pairs in a
   visited set; revisiting a pair short-circuits the traversal.
7. Depth is capped at `RestoreOptions.MaxDepth` (default 20).

The result is a **`PackageClosure`** containing:

- `Resolved` — a map of all successfully resolved packages (name → exact
  reference).
- `Missing` — a map of packages that could not be resolved (name → reason).
- `IsComplete` — true when `Missing` is empty.

### Batch Installation

The resolved closure is installed via `InstallManyAsync`, which processes
packages in parallel up to `MaxParallelRegistryQueries` (default 3) concurrent
operations. Each directive is handled independently — a failure in one package
does not block the others. `IncludeDependencies` is set to `false` for this
step because the closure already contains the full transitive graph.

### Lock File Output

After installation, a `fhirpkg.lock.json` is written (when
`RestoreOptions.WriteLockFile` is true) containing the exact resolved versions and
any missing packages. Subsequent restores can use this lock file to skip
resolution entirely.

## 9 — Resource Indexing

After a package is extracted, the **`PackageIndexer`** can scan its contents to
build a `.index.json` file for fast resource lookup.

### Indexing Process

1. Check for an existing `.index.json` — if present and `ForceReindex` is not
   set, return it immediately.
2. Enumerate all `.json` files in the package content directory.
3. Parse each file and look for a `resourceType` property to identify FHIR
   resources.
4. Extract metadata: resource type, `id`, `url` (canonical URL), `version`,
   `name`, and (for StructureDefinitions) the definition flavor.
5. Write the index to `package/.index.json` for future use.

### StructureDefinition Classification

StructureDefinitions are classified into flavors based on their `kind`,
`derivation`, and `type` properties:

| Flavor | Criteria |
|--------|----------|
| Profile | `derivation=constraint`, `kind=resource` |
| Extension | `type=Extension` |
| Logical | `derivation=specialization`, `kind=logical` |
| Type | `kind=primitive-type` or `kind=complex-type` |
| Resource | `kind=resource`, `derivation=specialization` |

### Lookup Methods

Once indexed, resources can be queried through the indexer:

- **`FindByCanonicalUrl`** — exact match on the resource's canonical URL.
- **`FindByResourceType`** — all resources of a given type, optionally scoped to
  a package.
- **`FindResources`** — complex search with multiple filter criteria.

## 10 — Progress Reporting

Throughout the process, the SDK reports progress through an
`IProgress<PackageProgress>` callback (passed via `InstallOptions.Progress`).
The phases reported are:

| Phase | When |
|-------|------|
| `Resolving` | Version resolution has started |
| `Downloading` | Tarball download has started |
| `Extracting` | Extraction into the cache has started |
| `Indexing` | Resource indexing has started |
| `Complete` | The package is fully installed |
| `Failed` | An error occurred during any phase |

## See Also

- [SDK Overview](sdk-overview.md) — quick start, DI setup, configuration, and
  architecture diagram.
- [SDK API Reference](sdk-api-reference.md) — complete interface, model, and enum
  reference.
- [CLI Overview](cli-overview.md) — using the `fhir-pkg` command-line tool.
- [CLI Reference](cli-reference.md) — full command and option reference.
