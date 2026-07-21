# Releases and evidence

The `Publish fhir-pkg-lib` workflow accepts only a canonical three-component
version and its matching `v<version>` tag. The checked-out tag must resolve to
the workflow head commit, and that commit must be an ancestor of the freshly
fetched `origin/main`.

## Preparing the changelog

Before creating the release commit and tag, stamp the changelog so the released
version has its own section:

1. In [`CHANGELOG.md`](../../CHANGELOG.md), rename the `## Current` heading to
   `## [<version>] - <YYYY-MM-DD>`, using the CalVer version being released and
   its calendar date.
2. Add a fresh, empty `## Current` section above it for the next cycle.
3. Commit both edits to `main` before tagging.

Each package's `<PackageReleaseNotes>` is derived from the matching changelog
section at pack time: a release build (`-p:Version=<version>`) selects the
`## [<version>]` section, and any other build falls back to `## Current`.
Because of that fallback an un-renamed `## Current` still ships the correct
about-to-release notes, but renaming keeps the published NuGet release notes
version-accurate and preserves the historical record.

## Required release gates

The workflow enforces this dependency chain:

1. `validate-release` verifies the inputs, tag identity, unpublished version,
   and `origin/main` ancestry.
2. `exact-source-ci` calls the repository's reusable `Tests` workflow with the
   validated full commit SHA. Every called job checks out and asserts that
   exact SHA. No secrets are inherited.
3. `pack-release-candidate` runs only after source CI. It packs once and uploads
   one immutable artifact containing the `.nupkg`, `.snupkg`, SHA-512 manifest,
   and release metadata.
4. Nine `qualify-<os>-<framework>` jobs download that same artifact. The matrix
   is Ubuntu, Windows, and macOS crossed with `net8.0`, `net9.0`, and
   `net10.0`.
5. `publish-nuget` runs only for a published GitHub Release event, after all
   nine qualification jobs pass. Merely pushing a tag cannot publish a NuGet
   package. The protected `nuget.org` environment and `GINOC_NUGET` secret
   exist only on this final job.

Each package-mode job restores `fhir-pkg-lib` from the downloaded candidate,
checks its SHA-256 and SHA-512 values, rejects a source-project reference,
proves that `lib/<framework>/FhirPkg.dll` was selected, and then executes the
qualification corpus. No qualification or publish job rebuilds the package.

## Validation-only run

After the workflow is present on the default branch, push a non-publishing test
tag on a commit already contained in `main`, then dispatch against that tag.
Tag creation alone does not start the publishing workflow:

```bash
gh workflow run nuget-generator.yaml \
  --ref v<version> \
  -f version=<version> \
  -f tag=v<version> \
  -f validation_only=true
```

Manual dispatch is always validation-only, even if a caller attempts to clear
the flag. Inspect the exact head and all jobs with:

```bash
gh run view <run-id> --json headSha,conclusion,jobs
```

The head SHA must equal the tag SHA, source CI must complete its existing OS
and framework matrices, all nine package jobs must record the same candidate
hash, and `publish-nuget` must remain skipped.

For an actual release, publish a GitHub Release for the validated tag. The
`release.published` event is the publication authorization; draft creation and
tag pushes do not receive the protected environment or NuGet credential.

Before enabling publication, a repository administrator must create the
`nuget.org` environment, add required reviewers or other deployment protection
rules, define the environment variable
`NUGET_PUBLISH_ENVIRONMENT_READY=true`, and move `GINOC_NUGET` into that
environment. Remove any repository-scoped copy of the secret. The workflow
fails before package upload when the readiness variable is absent.

## Failure recovery

- If validation or source CI fails, fix the source, merge it to `main`, and use
  a new version and tag.
- If a qualification job fails transiently, rerun only failed jobs so they
  consume the already uploaded candidate.
- If publication fails before NuGet.org accepts the package, rerun only the
  failed publish job after correcting environment approval or credentials.
- The primary and symbols packages are pushed separately. A publish-job retry
  accepts an existing primary package only after its repository metadata and
  every unsigned archive entry match the immutable candidate, then retries the
  symbols package.
- If NuGet.org contains different bytes for the version, stop. Published
  versions are immutable and must not be replaced.

## Evidence record

Each published `fhir-pkg-lib` release gets a `<version>.md` evidence record
after NuGet.org finishes repository signing. Record both package hashes:

- **Pre-upload SHA-256:** the exact unsigned `.nupkg` produced, qualified, and
  uploaded by the release workflow.
- **Published SHA-256:** the repository-signed `.nupkg` downloaded from
  NuGet.org. This is expected to differ from the pre-upload hash.

The evidence record also includes the release commit and tag, NuGet version and
feed, signature verification, package repository metadata, source-CI result,
all nine qualification reports, corpus outcome, symbol-package validation and
indexing status, and accepted limitations. NuGet.org validates and indexes
symbols asynchronously, so that status is confirmed after the workflow rather
than treated as synchronous upload evidence. Never rewrite the release tag to
add post-publication evidence.
