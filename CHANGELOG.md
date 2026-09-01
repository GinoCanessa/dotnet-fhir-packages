# Changelog

All notable changes to **fhir-pkg-lib** (SDK) and **fhir-pkg-cli** (CLI) are
documented in this file. Both packages share one version and release together
using CalVer (`yyyy.MMdd.HHmm`). The format is based on
[Keep a Changelog](https://keepachangelog.com/), adapted so the unreleased
section is titled **Current**. Release provenance (package hashes, CI and
qualification evidence) lives alongside this file under
[`docs/releases/`](docs/releases/README.md).

## Current

## [2026.901.1609] - 2026-09-01

### Added
- Added the tested `FhirPkg.Release` C# tool for validating release inputs,
  package and symbol contents, synchronized candidates, publication state, and
  published package provenance.
- Restore output now lists every exact resolved package identity, including
  coexisting versions of the same package, in console and JSON formats.
- Added `ResolvedDirective.ResolutionWarnings`, an additive, null-defaulted list
  of non-fatal diagnostics describing how a package source was chosen. The CLI's
  `resolve` command prints each warning, and `--json` emits them as a
  `resolutionWarnings` array.
- Added `FhirPackageManagerOptions.GitHubToken`, the CLI's `--github-token`
  global option, and the `.fhir-pkg.json` `githubToken` key, which authenticate
  the `api.github.com` repository lookups used when choosing the canonical
  repository for a CI build. `null` — the default — keeps those lookups
  unauthenticated and sends no `Authorization` header.

### Changed
- Pack, qualify, publish, and independently verify `fhir-pkg-lib` and
  `fhir-pkg-cli` as one synchronized release candidate, with safe recovery from
  partial NuGet publication.
- Migrated release workflow validation from PowerShell scripts to the C#
  release tool.
- Updated GitHub workflows to the Node 24-compatible `actions/checkout@v6` and
  `actions/setup-dotnet@v5`.
- Relaxed the `global.json` SDK policy from `rollForward: disable` to
  `latestFeature`, so building from source no longer requires the exact
  `10.0.302` patch and any `10.0.3xx`-or-later SDK works. CI still installs and
  resolves `10.0.302` exactly.
- Audited the documentation set for currency, completeness, and correctness
  ahead of this release: the CLI and SDK overviews now mirror their reference
  documents and cover `--github-token` / `githubToken` / `GitHubToken`,
  `docs/sdk-api-reference.md` carries a public-surface coverage table, and
  `README.md`'s links resolve from the NuGet package page.

### Fixed
- Fixed the deployment regression that could publish the SDK without the CLI.
- Made transient Windows cache-replacement retries asynchronous and
  cancellation-aware.
- Restored the defined FHIR/FHIRsmith wildcard grammar, including exact
  two-part versions, part-specific numeric/label/build wildcards, and trailing
  `?` remainder matching.
- Preserved and installed every required exact package version and its transitive
  subgraph during recursive dependency resolution.
- Fixed `@current` implementation-guide resolution selecting an arbitrary fork or
  feature branch. A plain `@current` now resolves the canonical repository's
  default build — chosen by a package-id prefix table, then a GitHub non-fork
  check, then the oldest build — and takes its version and date from that
  repository's `package.manifest.json`. Previously the most recent build from any
  publisher won, which made `hl7.fhir.uv.subscriptions-backport@current` fail to
  install outright and silently mislabelled roughly 139 other packages.
- Fixed branch-qualified CI build URLs being collapsed to the default-branch
  form. The winning record's branch is no longer discarded, so
  `@current$branch` emits `.../branches/{branch}/package.tgz`.
- Fixed CI build dates being ranked by a lexical string comparison across two
  incompatible published formats. Dates are now parsed to instants before
  comparison.

### Removed
- Removed SDK and CLI project restore-lock APIs and options. Restore now always
  resolves the live manifest, registry, and cache graph.

## [2026.722.1030] - 2026-07-22

### Added
- Hardened, caller-owned package install contract: install from caller-owned
  streams or absolute HTTP/HTTPS URIs with bounded acquisition/extraction
  limits and archive layout + identity validation.
- Cross-process package-source coordination and transactional cache
  replacement for safe concurrent SDK use of one cache root.
- Durable resource-lookup indexing and authoritative durable lock files for
  restore.
- Lightweight package summaries powering faster `list` / `info` / `clean`.
- CLI surfaces mutable CI install dispositions and outcomes.

### Changed
- Centralized version-resolution policy; hardened registry request/stream
  handling and merged redundant source knowledge; publish targets the exact
  registry protocol.
- Release pipeline gates publication behind qualification and exact-candidate
  checks.

### Fixed
- Propagate installation failures and recompute the active resolution graph.
- Make cache-cleanup selection safe.

## [2026.622.1701] - 2026-06-22

### Changed
- Adopted Central Package Management (`Directory.Packages.props`) and refreshed
  dependencies.
- Migrated test projects from xUnit v2 to xUnit.v3 and threaded `TestContext`
  cancellation tokens.

## [2026.324.1648] - 2026-03-24

### Added
- Initial public release of the FHIR package SDK and `fhir-pkg` CLI:
  multi-registry resolution, local disk cache, transitive dependency
  resolution, resource indexing, publish, and `IServiceCollection` DI
  integration; multi-targeting `net8.0`/`net9.0`/`net10.0`; developer docs and
  the packaging/qualification pipeline.
