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
Bound acquisition + SHA-256/SHA-1 verification
  │
  ▼
Validate archive, layout, manifest, and identity
  │
  ▼
Transactionally commit to cache
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

A package is considered installed only when the target and `package/` directory
are real directories, `package/package.json` is a real regular file, the
manifest is readable, and its exact or alias identity matches the cache key. If
the directive already specifies an exact version and this validation succeeds,
`InstallAsync` returns the cached `PackageRecord` immediately — no network
traffic is generated.

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

`packages.ini` tracks installation time, package size, mutable-source
publication time, and compressed archive SHA-256 while preserving unrelated
sections.

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

The redundant client distinguishes an authoritative absence from a failed
attempt. Eligible listing sources are queried under
`MaxParallelRegistryQueries`; successful listings are merged into a union of
version keys while every complete source version record remains intact with its
registry provenance.
Public provenance retains only the registry origin and type; configured
credentials, headers, paths, queries, and user information are not copied into
results.

- If all successful sources report absence, the result is `null`.
- If no source produces a result and any attempt fails, the SDK throws
  `RegistryOperationException` with sanitized attempt snapshots.
- If at least one listing succeeds and another source fails, the merged listing
  is returned with `IsComplete == false` and `QueryFailures`.
- Search results are merged by package name. Downloads use the exact source that
  supplied the selected version; legacy fallback applies only when provenance is
  absent.

### Resolving a Version

The **`VersionResolver`** receives the `PackageDirective` and the merged
`PackageListing` from the registry and selects the best source candidate:

1. **Exact** — returns the version verbatim if it exists in the listing.
2. **Latest** — uses `dist-tags.latest` when that candidate is eligible,
   otherwise returns the highest eligible version.
3. **Wildcard** — delegates to `FhirSemVer.MaxSatisfying`, which evaluates glob
   patterns (`4.0.x`) against all published versions and returns the highest
   match.
4. **Range** — delegates to `FhirSemVer.SatisfyingRange`, then selects the
   highest match from ranges such as `^4.0.0`, `~4.0.0`, and `>=4.0.0`.
5. **CI Build / CI Build Branch** — handled directly by the `FhirCiBuildClient`;
   the version resolver returns `null` and the CI client resolves the build.

Exact requests may resolve from an incomplete listing when one successful
source supplies a complete policy-compatible candidate. Exact misses and all
latest, wildcard, and range selections fail with `RegistryOperationException`
when the listing is incomplete, because a failed source could change the
answer.

When multiple sources publish the selected version, resolution chooses one
whole policy-compatible candidate after the version is selected. It normally
follows source priority; if the leading candidate omits dependency metadata, a
later compatible candidate that supplies it is selected as a whole. Tarball
URL, checksum, integrity value, dependencies, FHIR metadata, publication date,
and source registry all come from that same candidate; metadata from
conflicting copies is never spliced together.

#### Supported Range Grammar

The supported subset is exact versions; wildcards (`4.0.x`, `4.x`, `4.0`,
`*`); caret and tilde ranges; inclusive hyphen ranges (`1.0.0 - 2.0.0`);
comparators (`<`, `<=`, `>`, `>=`, `=`); whitespace-separated comparator
intersections (`>=1.0.0 <2.0.0`); and single-pipe alternatives
(`^1.0.0|~2.3.0`). Comparator operators may be separated from their exact
operand by whitespace. Caret, tilde, hyphen, and comparator operands must be
exact versions.

Caret ceilings use the first non-zero component: `^1.2.3` is below `2.0.0`,
`^0.2.3` is below `0.3.0`, and `^0.0.3` is below `0.0.4`. Hyphen bounds are
inclusive. Matching preserves the registry candidate order until the resolver
explicitly selects the highest result.

Pre-release versions follow the FHIR ordering hierarchy:
`Release > Ballot > Draft > Snapshot > CiBuild > Other`. When
`AllowPreRelease` is false, pre-releases are excluded for every request type,
including exact requests. When a `PreferredFhirRelease` is specified, explicit
`fhirVersion`/`fhirVersions` metadata is authoritative. Package-name inference
is used only when that metadata is absent, and candidates with missing or
incompatible release information are rejected.

### Resolution Result

A successful resolution produces a **`ResolvedDirective`** containing:

- `PackageReference` — the exact name and version.
- `TarballUri` — the download URL.
- `ShaSum` — the expected SHA-1 hash of the tarball (may be null).
- `Integrity` — the source's Subresource Integrity value (may be null).
- `SourceRegistry` — which registry provided the result.
- `PublicationDate` — when the version was published.

After resolution, the cache is checked **again** with the now-exact version (the
original directive may have been a wildcard or `latest`). If the exact version is
already cached, the download is skipped.

## 4 — Source Acquisition

Directive downloads, direct URI installs, caller streams, and discovery imports
all enter the same install contract.

- Directive downloads use the registry chain and pass the response stream
  directly to the coordinated cache.
- URI methods accept absolute HTTP/HTTPS only and use the manager's configured
  `HttpClient` with `ResponseHeadersRead`. `HttpTimeout` covers response headers
  and the complete body copy; `MaxRedirects` configures automatic redirects.
- Caller streams are consumed from their current position and remain open.
- Expected-identity installs acquire the identity lock before reading source
  bytes. A waiter therefore returns the winning valid cache entry without
  consuming its source unless overwrite was requested.
- Discovery imports must stage enough bounded content to validate the manifest
  before the identity is known, so concurrent discovery calls may each consume
  their source and then converge on one cache entry.

Content is copied incrementally to
`{cacheRoot}/.fhirpkg/staging/{operationId}/archive.tgz`. A reported
`Content-Length` above policy is rejected before body copy; actual bytes are
counted independently with overflow-safe accounting. SHA-256 is always computed
and SHA-1 is computed when requested. Caller streams are never rewound.

## 5 — Limits, Integrity, and Validation

Installation has finite defaults and can be tightened at manager or call scope:

| Limit | Default | Environment variable |
|-------|---------|----------------------|
| Compressed bytes | 100 MiB | `FHIRPKG_MAX_COMPRESSED_BYTES` |
| Expanded bytes | 1 GiB | `FHIRPKG_MAX_EXPANDED_BYTES` |
| One entry | 128 MiB | `FHIRPKG_MAX_ENTRY_BYTES` |
| Archive entries | 50,000 | `FHIRPKG_MAX_ARCHIVE_ENTRIES` |
| Normalized path length | 1,024 | `FHIRPKG_MAX_ARCHIVE_PATH_LENGTH` |
| Path depth | 32 | `FHIRPKG_MAX_ARCHIVE_DEPTH` |

Checksum failures and policy/archive/identity failures are reported as typed
`PackageInstallException` values; cancellation remains
`OperationCanceledException`.

Before any live-cache mutation, the extractor:

1. bounds raw tar metadata and regular-file expansion;
2. accepts only regular files and directories after safely consuming tar
   metadata records;
3. canonicalizes both slash styles and rejects rooted, traversal, control,
   reserved-device, trailing-dot/space, overlong, and colliding paths;
4. rejects duplicate, case/Unicode collision, and file-directory ancestor
   conflicts before later writes;
5. validates the complete standard or legacy package layout;
6. reads the sole real regular `package.json`; and
7. validates exact or allowed alias identity against the requested cache key,
   or derives a canonical identity for import.

## 6 — Transactional Cache Commit and Process Coordination

Acquisition, extraction, and validation remain under the cache root so final
promotion is a same-volume rename, never copy promotion. The SDK coordinates
through:

- a process-wide keyed `SemaphoreSlim` per cache root and canonical identity;
- persistent OS lock files in `.fhirpkg/locks` (process termination releases
  ownership);
- an operation-owner lock for each staging directory; and
- a short global lock for final promotion, metadata replacement, remove, and
  clear mutations.

Lock order is identity first, then global. Different identities can acquire,
hash, and extract concurrently. Every SDK cache read holds the identity lease
through validation/open/read, so it observes the old or new valid generation.
A caller using a previously returned raw path outside the SDK may briefly see
that target absent between replacement renames, but cannot see mixed content.

Windows and Linux use `FileStream.Lock`; macOS uses non-blocking native
`flock` because managed file locking is unsupported there. Durable replacement
attempts `F_FULLFSYNC`/`fsync` on macOS and treats documented unsupported
directory-sync errors (`EINVAL`/`ENOTSUP`, plus `ENOTTY` for `F_FULLFSYNC`) as a
platform durability limitation while still surfacing real I/O errors.

Final mutations use durable journals in `.fhirpkg/transactions` and hidden
backup/quarantine artifacts. Journal states cover preparation, old-generation
movement, new promotion, metadata commit, completion, and durable rollback.
Atomic journal and `packages.ini` replacement flushes the file and performs the
strongest supported directory synchronization. Recovery runs after the next
identity acquisition and before cleanup. Cancellation is honored immediately
before destructive work; once renames begin, forward completion or rollback is
non-cancellable.

`ListPackagesAsync` recovers pending journal identities before enumeration.
`ClearAsync` snapshots visible and journal identities under the global lock,
then processes that snapshot in canonical identity order. An install that begins
outside the snapshot may complete after clear; a represented package cannot be
missed or resurrected by clear.

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
