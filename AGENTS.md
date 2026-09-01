# AGENTS.md

Canonical, machine-readable conventions for automated agents working in
**dotnet-fhir-packages** (the `FhirPkg` SDK + CLI). This file is the single
source of truth that the `.github/skills/dev-*` skills read before naming any
build, test, or lint command.

Rationale, public API detail, and end-user documentation live in
[`README.md`](README.md) and [`docs/`](docs/index.md); this file restates only
the commands and rules an agent needs. When the two disagree, `README.md` wins —
fix this file.

---

## What this repository is

`FhirPkg` is a C# SDK and CLI for discovering, resolving, downloading, caching,
and managing [FHIR packages](https://registry.fhir.org/) across multiple
registries. It ships as **two NuGet packages that other people consume** —
`fhir-pkg-lib` (the SDK) and `fhir-pkg-cli` (the `fhir-pkg` global tool).

That is the framing fact that settles most design arguments: a change to the
SDK's public surface is a change to somebody else's build. Both packages share
one CalVer version and release together, so there is no way to slip a library
breaking change out behind a CLI-only release. When a question comes down to
"convenient internally" versus "stable for consumers", stability wins.

The second deciding argument is the on-disk cache. The standard
`~/.fhir/packages` layout is a **shared, cross-tool contract this repository
does not own** — other FHIR tooling reads and writes the same directory. Cache
layout and coordination behavior are therefore compatibility surfaces too, not
private implementation details.

---

## Repository layout

| Path | Contents |
|-|-|
| `src/FhirPkg/` | SDK library, packed as **`fhir-pkg-lib`**. Cache, Indexing, Installation, Models, Registry, Resolution, Utilities. |
| `src/FhirPkg.Cli/` | .NET global tool, packed as **`fhir-pkg-cli`** (`ToolCommandName` = `fhir-pkg`). |
| `src/common.props`, `src/common.targets` | Shared TFM/versioning/packaging properties and the changelog → `PackageReleaseNotes` task. Imported by both `src/` projects. |
| `test/FhirPkg.Tests/` | xunit.v3 unit tests. Also holds the release-contract tests over the workflow YAML. |
| `test/FhirPkg.IntegrationTests/` | xunit.v3 integration tests (offline / recorded mode). |
| `test/FhirPkg.ProcessTestHost/` | Console host used by cross-process cache tests. Not a test project. |
| `test/FhirPkg.Qualification/` | Console qualification harness + `qualification-corpus.json`. Not a test project. |
| `tools/FhirPkg.Release/` | C# release-validation tool driven by the publish workflow. |
| `docs/` | Developer/user documentation, versioning policy, release process and evidence. |
| `proposal/`, `reference/` | Design proposals and FHIR/registry reference material. |
| `.github/skills/`, `.github/agents/` | The `dev-*` inner-loop skills and the shared sub-agent role definitions they dispatch. Tracked, and refreshed from the canonical source rather than edited here. |
| `scratch/` | Local feature requests / plans / analyses (**gitignored**). |

`FhirPkg.sln` contains seven projects: two `src/` projects, four `test/`
projects, and `tools/FhirPkg.Release`.

---

## Toolchain pins

- **.NET SDK `10.0.302` floor**, declared in `global.json` with
  `"rollForward": "latestFeature"` and `"allowPrerelease": false`. This is a
  **floor, not an exact pin**: any installed `10.0.3xx` or later feature band
  satisfies it, so a machine with `10.0.400` builds without installing
  `10.0.302`. Do not suggest a different major version, and do not tighten the
  policy back to `disable` — that made the repo unbuildable on any machine
  lacking that one patch.
- All projects **multi-target `net10.0;net9.0;net8.0`** and use
  `LangVersion` **14**, `Nullable` enable, `ImplicitUsings` enable. Building
  and testing therefore fans out over three TFMs unless you pass
  `--framework`.
- CI additionally installs the `9.0.119` and `8.0.423` SDKs so the `net9.0` and
  `net8.0` targets have matching reference assemblies. CI installs `10.0.302`
  itself via `setup-dotnet`, so the roll-forward policy above changes nothing
  there — it resolves to exactly `10.0.302` on the runners and only gives local
  machines room to use a newer feature band. `ReleaseWorkflowContractTests`
  asserts the workflow's `dotnet-version: 10.0.302`; changing `global.json`
  alone does not satisfy it.
- **Central package management.** All package versions live in
  `Directory.Packages.props`. Add a `<PackageVersion>` there and a bare
  `<PackageReference Include="..." />` in the project — never a `Version=`
  attribute in a `.csproj`.
- Tests use **xunit.v3** with `xunit.runner.visualstudio` +
  `Microsoft.NET.Test.Sdk`, i.e. the **VSTest** host. `Shouldly` for
  assertions, `Moq` for mocking (unit tests only), `coverlet.collector` for
  coverage.
- The repository is **cross-platform**. CI runs the full matrix on
  `ubuntu-latest`, `windows-latest`, and `macos-latest`; nothing here is
  platform-specific.

---

## Build commands

There is one build track: the .NET solution.

```bash
dotnet restore FhirPkg.sln
dotnet build FhirPkg.sln
```

Release-configuration build, matching CI:

```bash
dotnet build FhirPkg.sln --configuration Release --no-restore
```

Single-project builds are fine for tight loops; add `--framework net10.0` when
you only need one TFM:

```bash
dotnet build src/FhirPkg/FhirPkg.csproj --framework net10.0
```

The expected baseline is **0 warnings, 0 errors**. Warnings are *not* errors
here — no project sets `TreatWarningsAsErrors` and there is no
`Directory.Build.props` — so a new warning fails nothing and will be missed
unless you look. Investigate anything else you see before attributing it:
confirm against a clean checkout or `HEAD` before calling it a regression.

---

## Test commands

Full suite:

```bash
dotnet test FhirPkg.sln
```

Per-project, matching CI:

```bash
dotnet test test/FhirPkg.Tests/FhirPkg.Tests.csproj
dotnet test test/FhirPkg.IntegrationTests/FhirPkg.IntegrationTests.csproj
```

CI appends `-p:TestTfmsInParallel=false` to the unit-test invocation so the three
per-TFM `vstest.console` / `testhost` pairs start sequentially on the hosted
runners (4 vCPU on ubuntu/windows, 3 vCPU on macOS); concurrent testhost startup
there exhausted the connect window and silently aborted two of the three runs.
Local runs may omit it, and pinning `--framework` avoids the fanout entirely.

Targeted runs use **VSTest filter syntax**, which *is* valid in this repo:

```bash
dotnet test test/FhirPkg.Tests/FhirPkg.Tests.csproj \
  --filter "FullyQualifiedName~DiskPackageCacheTests"

dotnet test test/FhirPkg.Tests/FhirPkg.Tests.csproj \
  --filter "FullyQualifiedName~DiskPackageCacheTests.Constructor_ExplicitPath_UsesExplicitPath"
```

Prefer pinning a single TFM while iterating — otherwise every run executes
three times:

```bash
dotnet test test/FhirPkg.Tests/FhirPkg.Tests.csproj --framework net10.0 \
  --filter "FullyQualifiedName~DiskPackageCacheTests"
```

### Integration-test partitioning

CI splits `FhirPkg.IntegrationTests` in two, and a change touching either half
should be verified the same way:

```bash
# Everything except the hardened / cross-process suites, net10.0 only.
dotnet test test/FhirPkg.IntegrationTests/FhirPkg.IntegrationTests.csproj \
  --framework net10.0 \
  --filter "FullyQualifiedName!~HardenedInstallationIntegrationTests&FullyQualifiedName!~CrossProcessCacheIntegrationTests"

# The hardened / cross-process suites, run per-TFM (net8.0, net9.0, net10.0).
dotnet test test/FhirPkg.IntegrationTests/FhirPkg.IntegrationTests.csproj \
  --framework net10.0 \
  --filter "FullyQualifiedName~HardenedInstallationIntegrationTests|FullyQualifiedName~CrossProcessCacheIntegrationTests"
```

### Qualification corpus validation

```bash
dotnet run --project test/FhirPkg.Qualification/FhirPkg.Qualification.csproj \
  --framework net10.0 -- \
  --validate-only true \
  --cache /tmp/qualification-validation-cache \
  --output /tmp/qualification-validation.json \
  --corpus test/FhirPkg.Qualification/qualification-corpus.json
```

Run this whenever `qualification-corpus.json` or the qualification harness
changes.

### Release tooling

`tools/FhirPkg.Release` is exercised by the publish workflow and by the
release-contract tests inside `test/FhirPkg.Tests`. Do not invoke the publish
workflow locally. The release process is documented in
[`docs/releases/README.md`](docs/releases/README.md).

---

## Verification matrix

| Change area | Minimum verification |
|-|-|
| `src/FhirPkg/**` | `dotnet test test/FhirPkg.Tests/…` filtered to the affected area, then the unit project in full. |
| Cache / install / cross-process code | Above **plus** the hardened / cross-process integration filter. |
| `src/FhirPkg.Cli/**` | Unit tests plus the non-hardened integration filter. |
| Workflow YAML, `tools/FhirPkg.Release/**` | `dotnet test test/FhirPkg.Tests/…` (release-contract tests read the YAML from build output). |
| `qualification-corpus.json`, `test/FhirPkg.Qualification/**` | The qualification `--validate-only` run above. |
| Multi-TFM-sensitive code (APIs newer than net8.0, polyfills) | Run without `--framework` so all three TFMs execute. |

**Rule: never claim a target framework or platform is green that you did not
actually run.** State which TFMs and which host OS you verified, and name the
ones you could not.

---

## Lint / format

There is **no lint or format step, deliberately.** No `.editorconfig`, no
`.globalconfig`, no `dotnet format` gate in CI. Do not add one without being
asked, and do not run `dotnet format` across existing files — it would rewrite
code that is styled the way it is on purpose.

Style is enforced by review against `## Code style` below, not by a tool.

---

## Run

The SDK (`src/FhirPkg/`) is a library and is not run directly. The CLI is run
from source with `dotnet run`, pinning a TFM so the multi-target fanout does
not build three copies:

```bash
dotnet run --project src/FhirPkg.Cli/FhirPkg.Cli.csproj --framework net10.0 -- --help
```

Everything after `--` is passed to the tool. Commands are `install`, `restore`,
`list`, `remove`, `clean`, `search`, `info`, `resolve`, and `publish`. Only two
global options matter for a local run: `--package-cache-folder`, which the
throwaway-cache rule below depends on, and `--json` for machine-readable
output. The full option list is enumerated in
[`docs/cli-reference.md`](docs/cli-reference.md#global-options) under
`## Global Options`; this file does not restate it.

Point the tool at a throwaway cache whenever you exercise install/remove/clean
so a local run cannot disturb the real package cache:

```bash
dotnet run --project src/FhirPkg.Cli/FhirPkg.Cli.csproj --framework net10.0 -- \
  --package-cache-folder /tmp/fhir-pkg-scratch list
```

Never run `publish` against a real registry. No environment variables or
configuration files are required for the commands above.

---

## Code style

There is **no authoritative style config file** — see `## Lint / format` above.
The rules below are the whole of it, and review is what enforces them.

- **Every `.cs` file starts with a license header** whose first line begins
  `// Copyright (c) Gino Canessa.`. More than one wording of that line is in
  use across the tree, so a new file takes the header of the files around it
  rather than a normalized one.
- **File-scoped namespaces** everywhere (`namespace FhirPkg.Cache;`). There are
  zero block-scoped namespaces in the repo.
- **Explicit types, not `var`.** This is a real convention here: `var` appears
  three times in all of `src/`. Write
  `List<PackageRecord> results = [];`, not `var results = ...`.
- **`[]` for empty collection initializers.** Example:
  `HashSet<PackageCacheKey> canonicalKeys = [];`
- Allman braces, 4-space indent, nullable reference types enabled and honored.
- Public SDK surface is fully async with `CancellationToken` parameters.
- Match the surrounding file. Consistency with neighbouring code beats any
  general preference.

### Architectural invariants

These are decisions, not preferences. Violating one is a review Blocker.

- **Central package management owns every version.** A `Version=` attribute on
  a `PackageReference` is never correct here — add a `<PackageVersion>` to
  `Directory.Packages.props` and reference the package bare. There is no
  sanctioned per-project override.
- **Every `.cs` file carries the license header**, including test helpers and
  anything that merely looks generated.
- **Everything must compile on `net8.0`.** The three TFMs are one source tree,
  so an API introduced after net8.0 needs a `#if` guard or a polyfill — never a
  bare call that only the `net10.0` leg ever proves. This is why the
  verification matrix sends multi-TFM-sensitive changes through all three.
- **The CHANGELOG `## Current` heading is machine-read.** `src/common.targets`
  extracts that section into `<PackageReleaseNotes>` at pack time, so renaming
  it, stamping a version onto it, or malforming it silently ships a release
  with empty notes. Version headings are stamped by the release process, never
  by hand.
- **`global.json` and the publish workflow are a coupled pair.**
  `ReleaseWorkflowContractTests` asserts the workflow's
  `dotnet-version: 10.0.302`, so changing the SDK pin means changing both and
  re-running that test — it reads the workflow YAML from build output.
- **Nothing touches nuget.org from a local machine.** No `dotnet nuget push`,
  no local run of the publish workflow, and no `publish` command against a real
  registry. Publication happens only through the process in
  [`docs/releases/README.md`](docs/releases/README.md).

---

## Commit conventions

- **Conventional commits**: `<type>(<scope>): <subject>` using `feat`, `fix`,
  `refactor`, `test`, `chore`, `docs`, `build`, `ci`, `perf`. Subject in the
  imperative, ≤ 72 characters. Scope is optional but encouraged — recent
  history uses scopes like `cache`, `resolution`, `release`, `workflow`,
  `qualification`.
- **Both trailers are required** on agent-authored commits:

  ```
  Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
  Copilot-Session: <session-id>
  ```

- One logical change per commit.
- The **GitHub integration below is on**, so when a slot carries an `Issue`
  binding, `dev-do` adds an `Issue: #N` trailer to each phase commit **in
  addition to** both trailers above. An unbound slot adds nothing.
- Agents **do not push** and **do not open pull requests** unless the user
  explicitly asks.

---

## Changelog

- User-visible changes go in [`CHANGELOG.md`](CHANGELOG.md) under the
  `## Current` heading, in an `### Added` / `### Changed` / `### Fixed`
  subsection.
- Both packages share one CalVer version (`yyyy.MMdd.HHmm`) and release
  together. `src/common.targets` extracts the matching changelog section into
  `<PackageReleaseNotes>` at pack time, so a malformed heading silently drops
  release notes.
- Do **not** rename `## Current` or stamp a version heading yourself; that is a
  release step (see [`docs/releases/README.md`](docs/releases/README.md)).

---

## GitHub Integration

**Off by default, in two independent ways.** A repository whose
`AGENTS.md` has **no** `## GitHub Integration` section is off. A section
whose `Enabled` row says **`no`** is equally off. In either case no skill
prompts about GitHub, and the `dev-*` loop behaves exactly as it did
before this feature existed.

The block below is **machine-managed**. This section is the **normative
definition** of both sentinel strings: every skill that reads or writes
the block reproduces the opener and the closer byte-for-byte from here,
and no skill re-derives, paraphrases, or reformats them.

<!-- >>> dev-* github integration (managed by dev-* skills) >>> -->
| Setting | Value |
|-|-|
| Enabled | yes |
| Repository | GinoCanessa/dotnet-fhir-packages |
| Label — feature request | enhancement |
| Label — bug report | bug |
| Label — docs-only (additive) | documentation |
| Changelog file | CHANGELOG.md |
| Changelog entry format | Bullet under the `## Current` heading, in an `### Added` / `### Changed` / `### Fixed` subsection; past-tense prose; add a trailing `(#N)` issue reference when an issue is bound |
| PR opens as draft | no |
<!-- <<< dev-* github integration (managed by dev-* skills) <<< -->

**These sentinels are not `dev-setup`'s ignore-file sentinels.** The
ignore-file block that `dev-setup` maintains in `.gitignore` or
`.git/info/exclude` is delimited by
`# >>> dev-* skills (managed by dev-setup) >>>` and
`# <<< dev-* skills (managed by dev-setup) <<<`. That is a **different
block in a different file**, with a `#` comment prefix rather than an
HTML comment. Do not conflate the two, and never substitute one pair for
the other.

Rules for the block:

- Only `dev-setup`, `dev-issue`, and `dev-pr-open` may rewrite it, and
  only **in place** — never a second copy, never appended to the end of
  the file.
- Hand-written text outside the sentinels is never touched. Everything a
  human writes in this section survives every rewrite.
- A recorded value of `no`, `none`, or `n/a` is a **resolved answer**, not
  a missing one. It must never re-trigger a prompt on a later run.
- When `Enabled` is `no`, every other row is `n/a`.

The integration is **on**, so `dev-issue` can publish a request or report as a
GitHub issue and `dev-pr-open` can push a branch and open a PR. Neither is
automatic: every write is confirmed with you in the moment, `analysis.md` and
`approach*.md` are **never** published, and the `Repository` row above is
cross-checked against `origin` before any write. The standing rule is unchanged
— agents **do not push** and **do not open pull requests** unless the user
explicitly asks, and `dev-pr-open` is the only skill permitted to do either.

---

## Scratch / slot convention

Local inner-loop work is organized into **slots** under `scratch/`:

```
scratch/<MMDD>-<##>/
  featurerequest.md    # authored by the dev-request skill
  bugreport.md         # authored by the dev-report skill
  approach-a|b|c.md    # authored by dev-approach (optional stage)
  approach.md          # dev-approach's judged winner
  plan.md              # authored by dev-plan, updated by dev-do
  analysis.md          # authored by dev-review
```

- `<MMDD>` is the local date (zero-padded month + day); `<##>` is a
  zero-padded two-digit slot number.
- `scratch/` is **gitignored** (`/scratch` in `.gitignore`). Nothing in it is
  ever committed.
- Because the slot is ignored, **no plan phase may declare a `scratch/` path as
  an owned path.** `plan.md` is a control file that `dev-do` edits continuously
  and never stages or commits.

---

## Agent guardrails

- Read this file before proposing any build, test, or lint command. **Never
  invent a command.** If something you need is not documented here, say so
  rather than guessing.
- Subagents follow the **subagent model policy** recorded below.
- Do not add new linting, building, or testing tooling without being asked.
  There is deliberately no `.editorconfig` and no `dotnet format` gate.
- Never add a `Version=` attribute to a `PackageReference`; central package
  management owns versions.
- Prefer the smallest targeted verification that covers the change; escalate to
  the full solution / all-TFM run only when the targeted run indicates it is
  needed.
- Never run the publish workflow, `dotnet nuget push`, or anything that touches
  nuget.org.

### Subagent model policy

Every `dev-*` skill that fans out reads this table before it spawns anything,
and each skill classifies **its own** roles as reasoning or mechanical. An
absent or unreadable table means `uniform` — the conservative default, and the
behavior this repository had before the table existed.

| Setting | Value |
|-|-|
| Policy | uniform |
| Mechanical-tier model | n/a |

- **`uniform`** — every sub-agent runs the spawning agent's model
  configuration, whatever its role.
- **`tiered`** — a sub-agent in a **reasoning** role runs the spawning agent's
  configuration; a sub-agent in a **mechanical** role runs the recorded
  mechanical-tier model.

The role classification lives in the skills, not here: it is a property of the
loop and does not vary between repositories. Only the policy and the model id
do, which is why they are the two rows recorded.

A recorded value here is a **resolved answer**. `uniform` was chosen
deliberately and must never re-trigger a prompt. Switching to `tiered` later
means editing **both** rows — a `tiered` policy with no real model id is worse
than `uniform`, because it is a policy no skill can resolve.
