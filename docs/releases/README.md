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
5. Confirm that the `nuget.org` environment's deployment branch and tag rule
   admits the release tags for `workflow_dispatch` runs too, not only for
   `release` events: a [recovery](#recovery) dispatch runs at the release tag
   and publishes through the same environment.
6. Brief the environment's required reviewers to compare, before approving
   `publish-nuget`, both identities in the run's `release-identity` summary
   with the release being approved: the product candidate, and the
   qualification inputs it was qualified on (see
   [Required release gates](#required-release-gates)).

The workflow publishes the CLI primary first. A key that lacks CLI ownership
or push scope therefore fails before a new SDK-only release can be created.

## Fixture drift check

The `Fixture drift check` workflow (`.github/workflows/fixture-drift.yaml`)
answers one question: do the upstream sources still serve exactly the fixture
bytes that the qualification corpus pins? It runs:

- weekly, on Mondays at 06:23 UTC;
- whenever it is dispatched, at any ref:
  `gh workflow run fixture-drift.yaml --ref <ref>`; and
- before every release, as the first step of
  [preparing the changelog](#preparing-the-changelog).

Its `fixture-drift` job runs the qualification harness in `--freeze-bundle`
mode against `test/FhirPkg.Qualification/qualification-corpus.json` at that
ref. The harness downloads every pinned artifact and checks it against its
corpus SHA-256, captures the CI-build (`current`) artifacts, and freezes a
bundle only when every artifact verifies (see
[Frozen qualification inputs](#frozen-qualification-inputs)). The job summary
holds a drift table with one row per artifact: its pinned and observed
SHA-256, upstream `Last-Modified`, size, entry count, and result, where:

- `verified` means the bytes match their pin;
- **changed** means upstream now serves different bytes for a pinned version
  (`ImmutableHashMismatch`);
- **unreachable** means the artifact could not be downloaded
  (`SourceUnreachable`);
- `captured` means a CI-build artifact was downloaded; it has no pin to
  compare; and
- **failed** means any other failure, which the report names.

The table's footer is one of:

```text
<n> of <m> pinned artifacts changed · bundle sha256:<digest> frozen
<n> of <m> pinned artifacts changed · no bundle frozen
```

Every changed, unreachable, or failed artifact is also an error annotation, and
the report is uploaded as `fixture-drift-report-attempt-<n>` for 30 days.

A red `fixture-drift` job, and therefore a red scheduled run, is the drift
signal: no bundle was frozen, and the table and annotations say why.
**unreachable** is an outage rather than drift and may clear on a re-run;
**changed** means that no bundle can be frozen for that corpus until the pin
is resolved in source.

The separate `live-registry-interop` job runs the whole corpus against the live
registries, under the summary title "LIVE — informational; never gates
publication; never feeds qualification". It is informational and never gates
anything: it runs with `continue-on-error`, so its failure does not fail the
run; it never freezes or uploads fixture bytes; and bundle trust reads only
the `fixture-drift` job's result, so even a failed or cancelled LIVE job
cannot disqualify a bundle. Its report is uploaded as
`live-registry-interop-report-attempt-<n>` for 30 days.

The workflow has only `contents: read` permission and uses no environment or
secret. GitHub disables a scheduled workflow after 60 days without repository
activity, so a quiet repository can silently lose the weekly run; re-enable it
from the repository's Actions page. The dispatch before each release does not
depend on the schedule.

## Preparing the changelog

Before creating the release commit and tag, confirm that the qualification
fixtures still verify, then stamp the changelog so the released version has
its own section:

1. Dispatch the [fixture drift check](#fixture-drift-check) on `main` and wait
   for it to pass before stamping the changelog:

   ```bash
   gh workflow run fixture-drift.yaml --ref main
   ```

   A passing run freezes a bundle for the corpus on `main`, which the release
   qualifies from as long as its tag carries the same corpus. A failing run
   means a pinned fixture changed upstream or could not be downloaded; resolve
   that before preparing the release.
2. In [`CHANGELOG.md`](../../CHANGELOG.md), rename `## Current` to
   `## [<version>] - <YYYY-MM-DD>`, using the CalVer version and release date.
3. Add a fresh, empty `## Current` section above it for the next cycle.
4. Commit both edits to `main` before tagging.

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

A [recovery](#recovery) dispatch passes `--allow-published-version`, so
`validate-release` skips the last two checks (the fresh-version and
highest-version checks) only in recovery: finishing an authorized release is
the one case where the version may already be partly published or superseded
by a later version. Every other gate still applies.

Validation, publication, and recovery runs for the same tag share
non-cancelling workflow concurrency, so they cannot publish that tag
concurrently.

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

The pack job exposes only the SDK SHA-256, CLI SHA-256, workflow artifact
digest, bundle SHA-256, and its own run id to downstream jobs, which read them
through `release-identity`. Runner-local paths are never cross-job inputs.

## Frozen qualification inputs

Qualification never fetches a fixture from an upstream source. Every release,
validation, and recovery run qualifies from a frozen fixture bundle, a
directory that holds exactly:

```text
bundle-manifest.json
blobs/<sha256>.tgz
```

`blobs/` holds one archive per distinct artifact SHA-256, named by that hash,
so identical bytes share one blob. The manifest records the schema version,
the corpus SHA-256 (`canonical-json-sha256-v1`), when and by which run the
bundle was recorded, and, for every pinned and CI-build corpus artifact, its
id, kind, URI, SHA-256, sizes, entry count, and upstream `Last-Modified`. A
bundle derived for a recovery also records its base bundle and its approvals
(see [Fixture approvals](#fixture-approvals)). The bundle digest is the
SHA-256 of the `bundle-manifest.json` bytes; because the manifest names every
blob by its SHA-256, that digest identifies the whole bundle.

The drift check uploads its bundle as `fixture-bundle-v1-<corpus blob id>`
for 90 days. The key is the git blob id of
`test/FhirPkg.Qualification/qualification-corpus.json` at the drift run's
commit, so a release finds the bundle frozen for its exact corpus. In a
release or validation run, `validate-release` (`locate-fixture-bundle`) looks
up the key for the corpus at the release commit and trusts an artifact only
when:

- it has not expired, has at least 24 hours of retention left, and has a
  digest recorded by GitHub;
- it was produced by a `.github/workflows/fixture-drift.yaml` run in this
  repository, whose head repository is this repository too, triggered by
  `schedule` or `workflow_dispatch`;
- that run's `fixture-drift` job concluded `success` in its latest attempt;
  the run's overall conclusion is not consulted, so the LIVE job can never
  disqualify a bundle; and
- that run's head commit is an ancestor of the freshly fetched `origin/main`.

The newest qualifying run wins. Recovery skips this lookup and reuses the
original run's copy (see [Recovery](#recovery)). With no trusted bundle, a
release or validation run fails in `validate-release`, before source CI and
before anything is built, and never falls back to downloading fixtures:

```text
No frozen fixture bundle for test/FhirPkg.Qualification/qualification-corpus.json at <commit> (corpus blob <blob>). Run 'gh workflow run fixture-drift.yaml --ref <tag>', wait for it to pass, then re-run this workflow's failed jobs.
```

Do exactly that. A drift check dispatched at the tag freezes a bundle for the
tag's own corpus, and its head commit is already on `main`.

Until `hl7.fhir.us.core#6.0.0` is re-pinned in the corpus, no bundle can be
frozen: packages2.fhir.org now serves bytes for it that differ from its pin,
so every drift run fails on that row, and every release and validation run
stops at this error. The re-pin is a separate change.

`pack-release-candidate` downloads the located bundle with `fetch-artifact`,
which requires the downloaded zip's SHA-256 to equal the digest GitHub
recorded for the artifact, and records the manifest SHA-256 as the bundle
digest. Immediately after uploading the candidate, it uploads the same bundle
into the release run as `fhir-pkg-<version>-fixture-bundle`, also for 90
days; uploading it after the candidate lets the copy outlive the candidate.
That copy, not the drift artifact, is what the qualification jobs and any
later recovery use.

Each `qualify-<os>-<framework>` job downloads
`fhir-pkg-<version>-fixture-bundle` from its own run and runs the corpus with
`--bundle`, `--bundle-sha256`, and `--release-tag`. Before any case runs, the
harness verifies the bundle against the release commit's corpus: the manifest
digest, the corpus SHA-256, the exact file inventory, every blob's hash, and
every artifact's id, URI, and pin or approved SHA-256. A failure
(`BundleVerificationFailed`) runs no case. Every HTTP request the harness
makes, for pinned archives, CI-build captures, and package listings
synthesized from the frozen archives, is then answered in-process by a
fail-closed fixture registry, installed through the SDK's public
`AddFhirPackageManagement` registration. The step also points `HTTP_PROXY`,
`HTTPS_PROXY`, and `ALL_PROXY` at the dead port `http://127.0.0.1:1`, with an
empty `NO_PROXY`, so a request that escaped the fixture registry would fail
instead of reaching an upstream host. Each job's summary names the inputs it
used:

```text
Qualification inputs: bundle sha256:<digest> · corpus <sha256> · approvals: <none | list>
```

`UnroutedFixtureRequest` means a case made a request the bundle cannot
answer: a URL outside the frozen routes, or a method other than `GET` or
`HEAD`. The fixture registry answers it with `404` and records it, and the
case fails with `Unrouted fixture request(s): <METHOD> <URL>; …`, identically
in all nine jobs. It points at a gap between the harness and its frozen
inputs; it is neither upstream drift nor a transient failure that a re-run
would clear.

## Required release gates

The workflow enforces this dependency chain:

1. `validate-release` verifies the version, tag, exact source, fresh version
   (except in recovery), and `origin/main` ancestry. In a release or
   validation run it then locates the trusted frozen fixture bundle for the
   release commit's corpus, and fails when there is none; in a recovery run it
   authorizes the recovery instead (see [Recovery](#recovery)).
2. `exact-source-ci` calls the reusable `Tests` workflow with the validated
   full commit SHA. No secrets are inherited.
3. `pack-release-candidate` downloads the located bundle by digest, creates
   and validates the shared seven-file candidate, and uploads the candidate
   and then the bundle copy.
4. `retrieve-release-candidate` runs only in recovery, which skips
   `exact-source-ci` and `pack-release-candidate`. It fetches the original
   run's candidate and bundle by artifact id and digest, revalidates the
   candidate against the tag commit, applies any approval, and uploads the
   qualification inputs into this run. It never packs or builds either
   package.
5. `release-identity` takes the candidate and qualification-input identity
   from whichever of `pack-release-candidate` and `retrieve-release-candidate`
   ran, and fails unless the expected one succeeded and the other was skipped.
   It checks every hash, digest, and run id, and writes the
   `### Release identity` summary. Every later job reads the identity from it.
6. Nine `qualify-<os>-<framework>` jobs run Ubuntu, Windows, and macOS crossed
   with `net8.0`, `net9.0`, and `net10.0`.
7. `publish-nuget` runs only after all nine qualification jobs pass, and only
   for a published GitHub Release (`release.published`) or for a recovery
   dispatch with `validation_only=false` whose GitHub Release is published.
   Just before it uses the credential, it re-checks that the Release is
   published (`verify-release-published`). Tag pushes, validation-only
   dispatches, and recovery dry runs cannot publish.

The `release-identity` summary is on the run page before the `nuget.org`
approval prompt, and lists two identities:

- **Product candidate:** the version, tag, and commit; the candidate run; the
  workflow artifact digest, which is the `sha256:<hex>` string GitHub records
  for the candidate artifact, in release and recovery runs alike; both
  package SHA-256 values; and source CI, which is this run, or in recovery the
  original run, linked and not re-run.
- **Qualification inputs:** the bundle SHA-256; the drift run that froze it
  and the corpus blob id, or in recovery the base bundle SHA-256 it came
  from; and the approval commit and author, or `none`.

A recovery run adds a **Recovery** block with this run and its
`workflow_sha`.

Every qualification job downloads and revalidates the same candidate, from the
run that produced it, and records the same SDK hash, CLI hash, artifact
digest, and bundle SHA-256. It then:

- restores `fhir-pkg-lib` from the candidate, rejects a project reference,
  proves `lib/<framework>/FhirPkg.dll` was selected, and runs the
  qualification corpus offline from the verified bundle (see
  [Frozen qualification inputs](#frozen-qualification-inputs));
- installs `fhir-pkg-cli` with explicit version, framework, tool path,
  NuGet configuration, and `--no-cache`;
- checks the tool store's `project.assets.json` target and
  `tools/<framework>/any` assets;
- hashes the restored CLI `.nupkg` against the candidate;
- invokes the exact installed shim and verifies
  `<version>+<full-release-commit>`; and
- runs an isolated empty-cache JSON `list` smoke test with isolated home and
  working directories and failing local HTTP proxies.

Each job's summary shows the candidate identity, the qualification inputs, any
rejected artifacts, and the failed cases, each classified as dependent (it
failed only because a required artifact was unavailable) or independent.
Every rejected artifact and independent failure is also an error annotation.

No qualification or publication job rebuilds either package.

## Validation-only run

After the workflow is present on the default branch, create a test tag on a
commit already contained in `main`. A trusted bundle for that commit's corpus
must exist first: a passing [fixture drift check](#fixture-drift-check) on
`main` with the same corpus, or one dispatched at the tag. Then dispatch
against that tag:

```bash
gh workflow run nuget-generator.yaml \
  --ref v<version> \
  -f version=<version> \
  -f tag=v<version> \
  -f validation_only=true
```

Without a bundle, `validate-release` fails with the no-bundle error described
under [Frozen qualification inputs](#frozen-qualification-inputs); run the
drift check at the tag, wait for it to pass, and re-run the failed jobs.

A manual dispatch can publish only in recovery mode: with `recover_run_id`
naming the original release run, `validation_only=false`, and an
already-published GitHub Release for the tag, and only through the same
protected `nuget.org` environment (see [Recovery](#recovery)). Any other
dispatch must set `validation_only=true`; otherwise `Resolve release inputs`
fails with:

```text
Manual workflow dispatch is validation-only unless recover_run_id names the release run being finished.
```

Inspect the exact head and all jobs:

```bash
gh run view <run-id> --json headSha,conclusion,jobs
```

The head SHA must equal the tag SHA, `validate-release` must locate the
bundle, source CI must pass, the candidate must contain exactly seven files,
`pack-release-candidate` must upload `fhir-pkg-<version>-fixture-bundle` after
the candidate, `release-identity` must show both identities, all nine
qualification jobs must report the same two package hashes, artifact digest,
and bundle SHA-256, each tool install must select its matrix framework, and
`publish-nuget` must remain skipped.

For an actual release, publish a GitHub Release for that already-validated tag.
The `release.published` event is the publication authorization; a recovery
dispatch only finishes a release that event already authorized.

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
- If `validate-release` finds no frozen fixture bundle, run the drift check at
  the tag, wait for it to pass, and re-run the failed jobs; no new version is
  needed.
- Fixture drift no longer requires a new version. Qualification reads only
  frozen inputs, so an upstream change after a bundle was frozen cannot fail a
  release that qualifies from it, and a recovery re-qualifies on the original
  run's own inputs. If those pin-verified bytes themselves fail a case and the
  fixture, not the product, is at fault, merge a
  [fixture approval](#fixture-approvals) and recover with it.
- If a qualification job fails transiently, rerun only failed jobs within
  GitHub's 30-day re-run window. After that window, finish the release with a
  [recovery](#recovery) dispatch while its candidate and qualification inputs
  are retained.
- If publication partially succeeds, rerun only the failed `publish-nuget`
  job, or use a recovery dispatch after the re-run window. Either way, its
  preflight verifies every primary package already accepted by NuGet.org
  before resuming the missing package or symbol pushes.
- Never rebuild an expired candidate for the same version. If the candidate or
  its qualification inputs have expired, or the original run predates frozen
  qualification inputs, recovery refuses; release a new version.
- If any visible package differs from the candidate in bytes, metadata,
  signature, or provenance, stop. NuGet versions are immutable; deprecate the
  incomplete version and release a new synchronized version.

## Fixture approvals

A fixture approval accepts exact replacement bytes for pinned fixtures in one
release. It is the file `docs/releases/approvals/v<version>.json`, named for
the release it applies to:

```json
{
  "schemaVersion": 1,
  "release": "v<version>",
  "approvedBy": "<approver>",
  "reason": "<why the replacement bytes are acceptable>",
  "replacements": [
    {
      "artifact": "<package>#<package-version>",
      "pinnedSha256": "<SHA-256 pinned in qualification-corpus.json>",
      "approvedSha256": "<SHA-256 of the replacement bytes>",
      "observedLastModified": "<YYYY-MM-DDThh:mm:ssZ>"
    }
  ]
}
```

The harness parses the file strictly, refusing any unknown property or
comment, and accepts it only when:

- `schemaVersion` is `1`, `release` is the tag of the release being recovered
  and matches the file name, and `approvedBy` and `reason` are not blank;
- each replacement names one exact `name#version`, without `*`, `?`, `[`, or
  `]`, at most once, and that artifact is an immutable artifact of the release
  commit's corpus: CI-build artifacts cannot be approved;
- `pinnedSha256` equals that artifact's corpus pin and its entry in the
  original bundle, and `approvedSha256` differs from it, both as 64 lowercase
  hexadecimal characters; and
- `observedLastModified`, which is optional, is an ISO 8601 timestamp, not the
  HTTP-date form of the `Last-Modified` header.

Review an approval as a pull request to `main`. Source CI runs for any change
under `docs/releases/approvals/` and parses every approval file, checking its
structure and that it is named for its release; the corpus checks run when the
approval is applied.

Merging an approval changes nothing by itself. It takes effect only through a
[recovery](#recovery) dispatch that names the commit holding it, with
`-f approval_commit=<sha>`. R5 then requires that commit to be on
`origin/main`, and the file on `origin/main` to be identical to the file at
that commit: deleting the file from `main` revokes the approval, and editing
it supersedes the older commit, so a recovery must name a commit that holds
the current file.

When an approval is applied, `retrieve-release-candidate` runs the harness's
`--derive-bundle` mode. It verifies the original bundle, which must carry no
approvals, validates the approval against the release commit's corpus and tag,
and downloads each approved artifact once, live, from its corpus URI. If
upstream no longer serves exactly the approved bytes, derivation fails with
`ApprovedBytesUnavailable`, and a new approval is needed. Otherwise it derives
a bundle from the original with each approved artifact replaced by its
approved bytes, recording the base bundle SHA-256 and every approval with its
approval commit. Every qualification job re-verifies the derived bundle
against the release tag, so their checksum checks apply to the approved bytes.
The derivation report is uploaded as `fixture-derivation-report-attempt-<n>`.

An approval is rarely useful. Under frozen inputs, upstream drift cannot fail
a release that already has its bundle, or a recovery of it: both qualify from
bytes that were verified against their pins when they were frozen. An approval
is useful only when those pin-verified bytes themselves fail qualification and
the maintainer judges the fixture, not the product, at fault. It cannot help a
release that has no bundle at all.

## Recovery

Recovery finishes an already-authorized release from its original run,
including after GitHub's 30-day re-run window has closed. It is a third,
dispatch-only mode of the same workflow, run at the release tag. It verifies
GitHub's own records of the original run, retrieves the original candidate and
qualification inputs by digest, re-qualifies them through the same nine jobs,
and publishes through the same protected `publish-nuget` job.

Recovery never re-runs source CI and never rebuilds: `exact-source-ci` and
`pack-release-candidate` are skipped, the original run's source-CI jobs are
linked as evidence, and the original candidate is used byte for byte. Applying
an approval builds the qualification harness from source to derive the inputs;
that build never touches the candidate.

A release is eligible when:

- its GitHub Release is published;
- its original `release` run passed source CI and packed its candidate; and
- that run, cut after frozen qualification inputs were introduced, still
  retains its candidate and its qualification inputs.

Dispatch at the release tag, with `recover_run_id` naming the original run:

```bash
gh workflow run nuget-generator.yaml \
  --ref v<version> \
  -f version=<version> \
  -f tag=v<version> \
  -f validation_only=false \
  -f recover_run_id=<run-id>
```

To apply a [fixture approval](#fixture-approvals), add
`-f approval_commit=<sha>`, the full SHA of the `main` commit holding
`docs/releases/approvals/v<version>.json`; that commit's author is recorded as
the approval's commit author. Only `validation_only=false` can publish; any
other value makes the dispatch a [dry run](#recovery-dry-run-and-rehearsal).
`Resolve release inputs` refuses a malformed `recover_run_id` or
`approval_commit`, or an `approval_commit` without `recover_run_id`, before
anything else runs.

`validate-release` then authorizes the recovery with `verify-recovery`, from
GitHub's records and git rather than from values typed by the operator:

- **R1 `release-published`:** the GitHub Release for the tag exists and is
  not a draft.
- **R2 `original-run`:** `recover_run_id` is a completed `release` run of
  `.github/workflows/nuget-generator.yaml` in this repository, at the tag
  commit.
- **R3 `source-ci`:** in that run, taking each job's latest attempt, there is
  at least one `exact-source-ci / *` job, and every one of them and
  `pack-release-candidate` concluded `success`. A job retried to success
  counts.
- **R4 `retained`:** that run holds exactly one
  `fhir-pkg-<version>-candidate` and exactly one
  `fhir-pkg-<version>-fixture-bundle`, each unexpired and with a digest
  recorded by GitHub. A missing bundle is reported as "the original run
  predates frozen qualification inputs".
- **R5 `approval`:** with `approval_commit` only, the commit is a full SHA on
  `origin/main` that contains `docs/releases/approvals/v<version>.json`, and
  that file is identical on `origin/main`; a deleted file is refused as
  revoked and an edited one as superseded. The commit's author is recorded.
  Without `approval_commit`, R5 is `reported` and the original inputs are
  reused unchanged.
- **R6:** `retrieve-release-candidate` downloads the original candidate by
  artifact id with `fetch-artifact`, requires the zip's SHA-256 to equal the
  digest GitHub recorded for it, and revalidates the candidate against the
  tag commit.
- **R7:** the same digest check for the original
  `fhir-pkg-<version>-fixture-bundle`.

Each of R1–R5 is `passed`, `failed`, `reported`, or `not-evaluated`. R3 and R4
read the original run's own records, so they are `not-evaluated` when R2
fails; no other rule is ever `not-evaluated`. Recovery proceeds only when
every rule passed or was reported. The `validate-release` summary shows a
`### Recovery authorization` table with each rule's result and detail,
followed by links to the original source-CI jobs (linked, not re-run), and
each failed rule is also an error annotation titled
`Recovery refused (<id> <name>)`.

Reviewers then see the `release-identity` summary (see
[Required release gates](#required-release-gates)): the original candidate
run, with its source CI linked; the qualification inputs, with the base bundle
SHA-256 and any approval commit and author; and the **Recovery** block, with
this run and its `workflow_sha`. `publish-nuget` runs only when
`validation_only` is `false` and R1 found the Release published, and
`verify-release-published` re-checks the Release just before the credential
is used. Its preflight then verifies any package NuGet.org already accepted
and pushes only what is missing.

Recovery passes `--allow-published-version` (see
[Version and source gates](#version-and-source-gates)), so it can finish a
version that is already partly published, or one lower than the latest
published version; consumers of floating versions still resolve the highest.

Releases cut before frozen qualification inputs, including `v2026.1016.1417`,
cannot be recovered: the workflow at their tags has no recovery mode, and
their runs hold no `fhir-pkg-<version>-fixture-bundle`. One residual gap
remains: if `Upload qualification inputs` fails after
`Upload immutable release candidate` succeeded, the run holds a candidate
without its inputs, R4 refuses, and the release needs a new version.

### Recovery dry run and rehearsal

Dispatch with `validation_only=true` to rehearse a recovery with no route to
publication:

```bash
gh workflow run nuget-generator.yaml \
  --ref v<version> \
  -f version=<version> \
  -f tag=v<version> \
  -f validation_only=true \
  -f recover_run_id=<run-id>
```

A dry run may target a release run, or a validation-only run at a test tag:
only in a dry run does R2 also accept a `workflow_dispatch` original. R1 is
only reported, so the Release need not be published, and `publish-nuget` is
always skipped, because it requires `validation_only` to be `false`.
Everything else runs as in a real recovery: R2–R5, the digest-checked
retrieval, any approval, the nine qualification jobs, and `release-identity`.

To rehearse the whole path before a real release depends on it, dispatch a
[validation-only run](#validation-only-run) at a test tag, then a dry run at
the same tag with `recover_run_id` naming that run. Expect R1 `reported`,
R2–R4 `passed`, `retrieve-release-candidate` to fetch both artifacts by digest
and revalidate the candidate, all nine qualification jobs to pass from the
original bundle, and `publish-nuget` to be skipped.

## Evidence record

Each synchronized release gets a `<version>.md` record after NuGet.org finishes
repository signing. Record, for both package IDs:

- the pre-upload SHA-256 of the exact unsigned `.nupkg`;
- the published SHA-256 of the repository-signed `.nupkg`;
- signature and repository metadata verification;
- primary and symbol publication status; and
- NuGet.org availability.

Record the release's identities in four groups:

- **Product:** the release commit, tag, and version; the original run that
  produced the candidate; the candidate's workflow artifact digest; both
  package SHA-256 values; and the source-CI link.
- **Qualification inputs:** the bundle SHA-256; the drift run that froze it;
  the corpus SHA-256 and blob id; and each approval, with its commit,
  artifact, pinned → approved SHA-256, approver, commit author, and reason.
- **Recovery** (recovered releases only): the recovery run and its
  `workflow_sha`.
- **Informational:** the latest LIVE interoperability result, which never
  gates a release.

Each qualification job's `qualification-<os>-<framework>-attempt-<n>`
artifact holds its report and a `qualification-candidate.json` that records,
among other fields, the version, tag, commit, both package SHA-256 values, the
candidate run, the bundle and corpus SHA-256, the approval commit, the mode,
and the run, attempt, and `workflow_sha`. Its `workflowArtifactDigest` is the
`sha256:<hex>` digest GitHub records for the candidate artifact, in release
and recovery runs alike. The `release-identity` summary adds the drift run,
corpus blob, and approval author; for a recovered release, the drift run and
corpus blob are in the original run's summary. The approval file at its
commit gives each approval's artifact, hashes, approver, and reason.

Also record all nine qualification reports, the selected framework and CLI
informational version for each matrix job, and the corpus outcome. NuGet.org
indexes symbols asynchronously, so symbol indexing is post-workflow evidence.
Never rewrite the release tag to add evidence.

See [`2026.722.1030.md`](2026.722.1030.md) for the historical SDK-only release
that prompted the synchronized pipeline.
