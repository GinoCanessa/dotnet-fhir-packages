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
| `scratch/` | Local feature requests / plans / analyses (**gitignored**). |

`FhirPkg.sln` contains seven projects: two `src/` projects, four `test/`
projects, and `tools/FhirPkg.Release`.

---

## Toolchain pins

- **.NET SDK `10.0.302`**, pinned in `global.json` with
  `"rollForward": "disable"` and `"allowPrerelease": false`. Do not suggest a
  different SDK.
- All projects **multi-target `net10.0;net9.0;net8.0`** and use
  `LangVersion` **14**, `Nullable` enable, `ImplicitUsings` enable. Building
  and testing therefore fans out over three TFMs unless you pass
  `--framework`.
- CI additionally installs the `9.0.119` and `8.0.423` SDKs so the `net9.0` and
  `net8.0` targets have matching reference assemblies.
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

There is **no** repo-wide formatter or linter configured — no `.editorconfig`,
no `dotnet format` step in CI. Do not add one without being asked.

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

## Code style

- **Every `.cs` file starts with the license header** — all 205 source files do:

  ```csharp
  // Copyright (c) Gino Canessa. Licensed under the MIT License.
  ```

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

## Scratch / slot convention

Local inner-loop work is organized into **slots** under `scratch/`:

```
scratch/<MMDD>-<##>/
  featurerequest.md    # authored by the dev-request skill
  bugreport.md         # authored by the dev-report skill
  plan.md              # authored by dev-plan, updated by dev-do
  analysis.md          # authored by dev-review
```

- `<MMDD>` is the local date (zero-padded month + day); `<##>` is a
  zero-padded two-digit slot number.
- `scratch/` is **gitignored** (`.gitignore` line 389). Nothing in it is ever
  committed.

---

## Agent guardrails

- Read this file before proposing any build, test, or lint command. **Never
  invent a command.** If something you need is not documented here, say so
  rather than guessing.
- Do not add new linting, building, or testing tooling without being asked.
  There is deliberately no `.editorconfig` and no `dotnet format` gate.
- Never add a `Version=` attribute to a `PackageReference`; central package
  management owns versions.
- Prefer the smallest targeted verification that covers the change; escalate to
  the full solution / all-TFM run only when the targeted run indicates it is
  needed.
- Never run the publish workflow, `dotnet nuget push`, or anything that touches
  nuget.org.
