# Releases and evidence

The `Publish synchronized FhirPkg SDK and CLI` workflow releases
`fhir-pkg-lib` and `fhir-pkg-cli` as one versioned package pair. A successful
release requires both primary packages to match the same immutable candidate;
neither package is considered independently complete.

## Administrator preflight

Before enabling publication, a repository administrator must:

1. Configure the protected `nuget.org` environment with required reviewers or
   other deployment protection rules.
2. Define `NUGET_PUBLISH_ENVIRONMENT_READY=true` in that environment.
3. Store `GINOC_NUGET` only in that environment, not as a repository-scoped
   secret.
4. Confirm that the NuGet account or scoped API key can push both
   `fhir-pkg-cli` and `fhir-pkg-lib`.

The workflow publishes the CLI primary first. A key that lacks CLI ownership
or push scope therefore fails before a new SDK-only release can be created.

## Preparing the changelog

Before creating the release commit and tag, stamp the changelog so the released
version has its own section:

1. In [`CHANGELOG.md`](../../CHANGELOG.md), rename `## Current` to
   `## [<version>] - <YYYY-MM-DD>`, using the CalVer version and release date.
2. Add a fresh, empty `## Current` section above it for the next cycle.
3. Commit both edits to `main` before tagging.

Both projects derive `<PackageReleaseNotes>` from that matching changelog
section at pack time. Non-release builds fall back to `## Current`.

## Version and source gates

The workflow accepts only a canonical three-component numeric version and its
exact `v<version>` tag. It also requires:

- every assembly-version component to fit the .NET assembly limits;
- the checked-out commit to equal the tag commit;
- the commit to be contained in the freshly fetched `origin/main`;
- the version to be absent from both NuGet package indexes; and
- the version to be greater than the highest canonical numeric version in
  either package index.

Validation and publication runs for the same tag share non-cancelling workflow
concurrency, so they cannot publish that tag concurrently.

## Immutable candidate

`pack-release-candidate` restores the CLI project graph, builds the SDK and CLI
once with the exact release version and commit, and packs each project once
with `--no-build`. The uploaded artifact is named
`fhir-pkg-<version>-candidate` and contains exactly seven files:

```text
fhir-pkg-lib.<version>.nupkg
fhir-pkg-lib.<version>.snupkg
fhir-pkg-lib.<version>.sha512
fhir-pkg-cli.<version>.nupkg
fhir-pkg-cli.<version>.snupkg
fhir-pkg-cli.<version>.sha512
release-metadata.json
```

Candidate validation checks both nuspec identities, versions, repository URL
and full commit, SDK library assets, CLI `DotnetTool` metadata and command
settings, both symbol layouts, both SHA-512 manifests, metadata filenames and
hashes, and the fixed seven-file inventory. For every supported framework,
`FhirPkg.dll` embedded in the CLI must be byte-identical to the SDK package.

The pack job exposes only the SDK SHA-256, CLI SHA-256, and workflow artifact
digest to downstream jobs. Runner-local paths are never cross-job inputs.

## Required release gates

The workflow enforces this dependency chain:

1. `validate-release` verifies the version, tag, exact source, fresh version,
   and `origin/main` ancestry.
2. `exact-source-ci` calls the reusable `Tests` workflow with the validated
   full commit SHA. No secrets are inherited.
3. `pack-release-candidate` creates and validates the shared seven-file
   candidate.
4. Nine `qualify-<os>-<framework>` jobs run Ubuntu, Windows, and macOS crossed
   with `net8.0`, `net9.0`, and `net10.0`.
5. `publish-nuget` runs only for a published GitHub Release after all nine
   qualification jobs pass. Tag pushes and manual dispatches cannot publish.

Every qualification job downloads and revalidates the same candidate and
records the same SDK hash, CLI hash, and artifact digest. It then:

- restores `fhir-pkg-lib` from the candidate, rejects a project reference,
  proves `lib/<framework>/FhirPkg.dll` was selected, and runs the qualification
  corpus;
- installs `fhir-pkg-cli` with explicit version, framework, tool path,
  NuGet configuration, and `--no-cache`;
- checks the tool store's `project.assets.json` target and
  `tools/<framework>/any` assets;
- hashes the restored CLI `.nupkg` against the candidate;
- invokes the exact installed shim and verifies
  `<version>+<full-release-commit>`; and
- runs an isolated empty-cache JSON `list` smoke test with isolated home and
  working directories and failing local HTTP proxies.

No qualification or publication job rebuilds either package.

## Validation-only run

After the workflow is present on the default branch, create a test tag on a
commit already contained in `main`, then dispatch against that tag:

```bash
gh workflow run nuget-generator.yaml \
  --ref v<version> \
  -f version=<version> \
  -f tag=v<version> \
  -f validation_only=true
```

Manual dispatch is always validation-only. Inspect the exact head and all jobs:

```bash
gh run view <run-id> --json headSha,conclusion,jobs
```

The head SHA must equal the tag SHA, source CI must pass, the candidate must
contain exactly seven files, all nine qualification jobs must report the same
two package hashes and artifact digest, each tool install must select its
matrix framework, and `publish-nuget` must remain skipped.

For an actual release, publish a GitHub Release for that already-validated tag.
The `release.published` event is the publication authorization.

## Publication ordering and reconciliation

NuGet publication is not transactional across two package IDs. Before using
`GINOC_NUGET`, `publish-nuget` downloads and revalidates the candidate, then
checks both primary package URLs:

- `missing` means that package may be pushed;
- `verified` means the visible repository-signed package passed signature,
  metadata, provenance, and unsigned-entry byte comparison against the
  candidate; and
- any mismatch stops the job before any push.

After that all-package preflight:

1. Push the CLI primary with `--no-symbols --skip-duplicate` when missing.
2. Poll NuGet.org and verify the published CLI against the candidate.
3. Push the SDK primary with `--no-symbols --skip-duplicate` when missing.
4. Poll NuGet.org and verify the published SDK against the candidate.
5. Only after both primaries are verified, explicitly push the CLI `.snupkg`
   and SDK `.snupkg`, each with `--skip-duplicate`.

The protected `nuget.org` environment is the only job that receives the NuGet
credential.

## Failure recovery

- If validation or source CI fails, fix the source, merge it to `main`, and use
  a new version and tag.
- If a qualification job fails transiently, rerun only failed jobs while the
  immutable candidate artifact is retained.
- If publication partially succeeds, rerun only the failed `publish-nuget`
  job. Its preflight verifies every primary package already accepted by
  NuGet.org before resuming the missing package or symbol pushes.
- Never rebuild an expired candidate for the same version. If artifact
  retention has expired, release a new version.
- If any visible package differs from the candidate in bytes, metadata,
  signature, or provenance, stop. NuGet versions are immutable; deprecate the
  incomplete version and release a new synchronized version.

## Evidence record

Each synchronized release gets a `<version>.md` record after NuGet.org finishes
repository signing. Record, for both package IDs:

- the pre-upload SHA-256 of the exact unsigned `.nupkg`;
- the published SHA-256 of the repository-signed `.nupkg`;
- signature and repository metadata verification;
- primary and symbol publication status; and
- NuGet.org availability.

Also record the release commit and tag, workflow run, shared artifact digest,
source-CI result, all nine qualification reports, selected framework and CLI
informational version for each matrix job, and corpus outcome. NuGet.org
indexes symbols asynchronously, so symbol indexing is post-workflow evidence.
Never rewrite the release tag to add evidence.

See [`2026.722.1030.md`](2026.722.1030.md) for the historical SDK-only release
that prompted the synchronized pipeline.
