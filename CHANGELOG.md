# Changelog

All notable changes to **fhir-pkg-lib** (SDK) and **fhir-pkg-cli** (CLI) are
documented in this file. Both packages share one version and release together
using CalVer (`yyyy.MMdd.HHmm`). The format is based on
[Keep a Changelog](https://keepachangelog.com/), adapted so the unreleased
section is titled **Current**. Release provenance (package hashes, CI and
qualification evidence) lives alongside this file under
[`docs/releases/`](docs/releases/README.md).

## Current

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
- Pack, qualify, publish, and independently verify `fhir-pkg-lib` and
  `fhir-pkg-cli` as one synchronized release candidate.

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
