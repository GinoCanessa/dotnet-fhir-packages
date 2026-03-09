# Integration Test Plan

This document defines the integration test strategy for the FHIR Package Management library. Integration tests verify that components work correctly together and against real (or realistic) external services.

## Test Framework & Conventions

- **Framework:** xUnit
- **Assertions:** FluentAssertions
- **Traits:** `[Trait("Category", "Integration")]` for all integration tests
- **Naming:** `Scenario_Condition_ExpectedOutcome`
- **Isolation:** Each test uses a dedicated temporary cache directory
- **Cleanup:** Temp directories deleted in `Dispose()`

---

## Test Environment

### Test Modes

Integration tests support two modes, controlled via environment variable:

| Mode | Variable | Description |
|------|----------|-------------|
| **Live** | `FHIR_INTEGRATION_LIVE=1` | Runs against real registries (CI/CD only, requires internet) |
| **Recorded** | _(default)_ | Uses pre-recorded HTTP responses (offline, deterministic) |

### Recorded Response Infrastructure

A `RecordingHttpHandler` captures and replays HTTP interactions:

```csharp
public class RecordingHttpHandler : DelegatingHandler
{
    // In record mode: proxies to real server, saves response
    // In replay mode: returns saved response, no network
    public RecordingHttpHandler(string cassettePath, RecordMode mode);
}
```

Recorded responses are stored in `TestData/Cassettes/`:

```
TestData/Cassettes/
├── install-us-core-6.1.0/
│   ├── packages.fhir.org_hl7.fhir.us.core.json
│   └── packages.simplifier.net_hl7.fhir.us.core_6.1.0.tgz
├── restore-us-core-project/
│   ├── ...multiple request-response pairs...
├── ci-build-us-core-current/
│   ├── build.fhir.org_ig_qas.json
│   └── build.fhir.org_ig_HL7_US-Core_package.tgz
└── ...
```

### Temporary Cache Setup

```csharp
public abstract class IntegrationTestBase : IDisposable
{
    protected readonly string TempCacheDir;
    protected readonly IFhirPackageManager Manager;

    protected IntegrationTestBase()
    {
        TempCacheDir = Path.Combine(Path.GetTempPath(), $"fhir-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempCacheDir);

        Manager = new FhirPackageManager(new FhirPackageManagerOptions
        {
            CachePath = TempCacheDir,
            // Uses recorded or live HTTP depending on test mode
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(TempCacheDir))
            Directory.Delete(TempCacheDir, recursive: true);
    }
}
```

---

## Test Suites

### 1. Registry Integration Tests

**File:** `RegistryIntegrationTests.cs`

Tests that verify communication with FHIR package registries works correctly end-to-end.

#### Published Package Discovery

| Test | Description |
|------|-------------|
| `Search_PrimaryRegistry_ByName_ReturnsResults` | Catalog search on `packages.fhir.org` for `hl7.fhir.us.core` returns non-empty results |
| `Search_SecondaryRegistry_ByName_ReturnsResults` | Catalog search on `packages2.fhir.org` for `hl7.fhir.us.core` returns non-empty results with extra metadata (kind, date) |
| `Search_PrimaryRegistry_ByFhirVersion_FilteredCorrectly` | Filter by `R4` only returns R4 packages |
| `Search_PrimaryRegistry_HandlesPascalCaseResponse` | Primary response with `Name`, `Description`, `FhirVersion` deserialized correctly |
| `Search_SecondaryRegistry_HandlesCamelCaseResponse` | Secondary response with `name`, `version`, `canonical`, `kind` deserialized correctly |
| `Search_NonExistentPackage_ReturnsEmpty` | Search for `nonexistent.package.xyz` returns empty array |

#### Package Version Listing

| Test | Description |
|------|-------------|
| `GetListing_KnownPackage_ReturnsAllVersions` | `hl7.fhir.us.core` listing includes multiple versions |
| `GetListing_KnownPackage_HasDistTagLatest` | dist-tags.latest is present and non-null |
| `GetListing_KnownPackage_VersionsHaveShasum` | Each version has `dist.shasum` |
| `GetListing_KnownPackage_TarballUrlsValid` | Tarball URLs are valid HTTPS URIs |
| `GetListing_PrimaryAndSecondary_MayDifferOnLatest` | Document: dist-tags.latest may differ between registries |
| `GetListing_NonExistentPackage_ReturnsNull` | Unknown package returns null |

#### Package Download

| Test | Description |
|------|-------------|
| `Download_KnownVersion_ReturnsValidTarball` | Download `hl7.fhir.us.core@6.1.0` returns non-empty stream |
| `Download_KnownVersion_ContentTypeIsTarGzip` | Content-Type is `application/tar+gzip` |
| `Download_KnownVersion_ShasumMatches` | Computed SHA matches registry's shasum |
| `Download_NonExistentVersion_Returns404` | `hl7.fhir.us.core@99.99.99` returns null/404 |

#### Redundant Registry Fallback

| Test | Description |
|------|-------------|
| `Resolve_PrimaryDown_FallsBackToSecondary` | Mock primary to fail, secondary succeeds |
| `Resolve_AllRegistriesDown_ReturnsNull` | Both fail → null result |
| `Resolve_FirstRegistryTimeout_SecondSucceeds` | Timeout on primary triggers fallback |

---

### 2. CI Build Integration Tests

**File:** `CiBuildIntegrationTests.cs`

Tests that verify CI build resolution from `build.fhir.org`.

#### QA Index

| Test | Description |
|------|-------------|
| `QasJson_Download_ParsesSuccessfully` | Download and deserialize `qas.json` without errors |
| `QasJson_ContainsKnownPackage` | `hl7.fhir.us.core` appears in qas.json |
| `QasJson_EntryHasRequiredFields` | Each entry has `package-id`, `date`, `repo` |
| `QasJson_RepoFieldFormats_ParseCorrectly` | Both `org/repo/branch/qa.json` and `org/repo/branches/branch/qa.json` formats |

#### IG CI Build Resolution

| Test | Description |
|------|-------------|
| `Resolve_IgCiBuild_FindsPackageInQas` | `hl7.fhir.us.core#current` resolves to a valid URL |
| `Resolve_IgCiBuild_ConstructsCorrectDownloadUrl` | URL matches `build.fhir.org/ig/{org}/{repo}/package.tgz` |
| `Resolve_IgCiBuild_ManifestHasDate` | Package manifest has parseable date |
| `Resolve_IgCiBuild_WithBranch_UsesCorrectBranch` | `#current$branchname` filters to that branch |

#### Core CI Build Resolution

| Test | Description |
|------|-------------|
| `Resolve_CoreCiBuild_FixedUrlPattern` | `hl7.fhir.r6.core#current` → `build.fhir.org/hl7.fhir.r6.core.tgz` |
| `Resolve_CoreCiBuild_ManifestAvailable` | Core manifest at `{package}.manifest.json` is downloadable |
| `Resolve_CoreCiBuild_VersionInfo_Parseable` | `version.info` INI format parsed correctly |

#### CI Build Date Comparison

| Test | Description |
|------|-------------|
| `CiBuild_CacheNewer_SkipsDownload` | Cached package with newer date is kept |
| `CiBuild_CiNewer_Downloads` | Newer CI build triggers download |
| `CiBuild_NoCache_AlwaysDownloads` | Missing cache entry triggers download |

---

### 3. Cache Integration Tests

**File:** `CacheIntegrationTests.cs`

Tests that verify the disk cache works correctly with real file system operations.

#### Installation

| Test | Description |
|------|-------------|
| `Install_ValidPackage_CreatesCorrectDirectoryStructure` | `{name}#{version}/package/` with package.json inside |
| `Install_ValidPackage_ManifestReadable` | `ReadManifestAsync` returns valid manifest |
| `Install_ValidPackage_IndexGenerated` | `.index.json` or scanned index available |
| `Install_PackageWithoutPackageDir_NormalizesStructure` | Files moved into `package/` |
| `Install_ConcurrentInstalls_NoCorruption` | Two parallel installs of different packages succeed |
| `Install_DuplicateInstall_SkipsSecondTime` | Already-installed package returns immediately |
| `Install_OverwriteExisting_ReplacesPackage` | `overwriteExisting: true` replaces cache entry |
| `Install_UpdatesMetadata_PackagesIni` | packages.ini entry created |

#### Listing & Querying

| Test | Description |
|------|-------------|
| `List_EmptyCache_ReturnsEmpty` | Fresh cache has no packages |
| `List_AfterInstall_ContainsPackage` | Installed package appears in list |
| `List_WithFilter_ReturnsSubset` | Prefix filter works |
| `GetFileContent_ReturnsFileFromPackage` | Read a specific resource file |

#### Removal

| Test | Description |
|------|-------------|
| `Remove_InstalledPackage_DeletesDirectory` | Directory no longer exists |
| `Remove_InstalledPackage_UpdatesMetadata` | packages.ini entry removed |
| `Remove_NonExistentPackage_ReturnsFalse` | No error, returns false |
| `Clear_RemovesAllPackages` | All directories deleted |
| `Clear_EmptyCache_ReturnsZero` | No-op on empty cache |

#### Edge Cases

| Test | Description |
|------|-------------|
| `Cache_SpecialCharacters_InDirective_HandledCorrectly` | Directives with `$` (branch names) |
| `Cache_LongPathNames_WorkOnWindows` | Packages with long names don't exceed path limits |
| `Cache_ReadOnly_FileSystem_GracefulError` | Permission denied → clear error message |

---

### 4. Install Workflow Integration Tests

**File:** `InstallIntegrationTests.cs`

End-to-end tests for the full package install workflow.

| Test | Description |
|------|-------------|
| `Install_PublishedIgPackage_ExactVersion` | Install `hl7.fhir.us.core#6.1.0` — resolves, downloads, caches, returns record |
| `Install_PublishedIgPackage_LatestVersion` | Install `hl7.fhir.us.core` — resolves latest, downloads |
| `Install_PublishedIgPackage_WildcardVersion` | Install `hl7.fhir.us.core#6.1.x` — resolves to 6.1.0 |
| `Install_PublishedCorePackage_ExactVersion` | Install `hl7.fhir.r4.core#4.0.1` |
| `Install_PublishedCorePackage_PartialName` | Install `hl7.fhir.r4#4.0.1` — expands to core + expansions |
| `Install_CiBuildIgPackage_Current` | Install `hl7.fhir.us.core#current` — resolves via qas.json |
| `Install_CiBuildCorePackage_Current` | Install `hl7.fhir.r6.core#current` |
| `Install_WithDependencies_InstallsTransitiveDeps` | Install with `IncludeDependencies=true` — all deps installed |
| `Install_AlreadyCached_ReturnsWithoutNetwork` | Second install of same package skips download |
| `Install_ChecksumVerification_Passes` | SHA matches → install succeeds |
| `Install_ChecksumVerification_Fails` | Corrupted download → throws, no cache entry |
| `Install_NonExistentPackage_ReturnsNull` | Unknown package → null result |
| `Install_NetworkError_GracefulFailure` | Registry down → clear error |
| `Install_CancellationToken_Cancels` | Token cancelled → OperationCanceledException |
| `Install_ProgressReporting_AllPhasesReported` | Resolving → Downloading → Extracting → Complete |

---

### 5. Restore Workflow Integration Tests

**File:** `RestoreIntegrationTests.cs`

End-to-end tests for dependency restoration.

#### Setup

Each test uses a temporary project directory with a `package.json`:

```csharp
private string CreateTestProject(string manifestJson)
{
    var dir = Path.Combine(TempCacheDir, "test-project");
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "package.json"), manifestJson);
    return dir;
}
```

#### Test Cases

| Test | Description |
|------|-------------|
| `Restore_SimpleProject_AllDependenciesResolved` | Project with 3 deps → all resolved, closure.IsComplete == true |
| `Restore_TransitiveDependencies_FullTreeResolved` | Deps of deps also resolved |
| `Restore_WritesLockFile` | `fhirpkg.lock.json` created with all resolved versions |
| `Restore_FromLockFile_UsesLockedVersions` | Subsequent restore uses lock file versions |
| `Restore_MissingDependency_TrackedInClosure` | Unavailable dep → closure.Missing contains it |
| `Restore_VersionConflict_HighestWins` | Two deps require different versions → highest selected |
| `Restore_CircularDependency_Terminates` | No infinite loop |
| `Restore_NpmAlias_ResolvedCorrectly` | Aliased dependency processed |
| `Restore_KnownFixup_Applied` | `hl7.fhir.r4.core@4.0.0` → `4.0.1` |
| `Restore_NoLockOption_SkipsLockFileWrite` | `WriteLockFile=false` → no file created |
| `Restore_EmptyDependencies_EmptyClosure` | No deps → empty closure, complete |
| `Restore_PartialCoreName_ExpandedAndResolved` | `hl7.fhir.r4` dependency expands to core + expansions |

---

### 6. CLI Integration Tests

**File:** `CliIntegrationTests.cs`

Tests that verify the CLI tool works correctly as a subprocess.

#### Test Infrastructure

```csharp
private async Task<(int ExitCode, string StdOut, string StdErr)> RunCli(params string[] args)
{
    var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project {CliProjectPath} -- {string.Join(' ', args)} " +
                        $"--cache-path {TempCacheDir}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }
    };
    process.Start();
    var stdout = await process.StandardOutput.ReadToEndAsync();
    var stderr = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return (process.ExitCode, stdout, stderr);
}
```

#### Install Command

| Test | Description |
|------|-------------|
| `Install_ValidPackage_ExitCode0` | `fhir-pkg install hl7.fhir.r4.core#4.0.1` → exit 0 |
| `Install_ValidPackage_OutputShowsSuccess` | stdout contains "Installed" |
| `Install_InvalidDirective_ExitCode2` | `fhir-pkg install ""` → exit 2 |
| `Install_NotFound_ExitCode3` | `fhir-pkg install nonexistent.pkg#1.0.0` → exit 3 |
| `Install_JsonOutput_ValidJson` | `--json` flag produces parseable JSON |
| `Install_MultiplePackages_AllInstalled` | Two packages in one command |

#### List Command

| Test | Description |
|------|-------------|
| `List_EmptyCache_ExitCode0` | `fhir-pkg list` → exit 0, shows empty |
| `List_AfterInstall_ShowsPackage` | Package appears after install |
| `List_WithFilter_FiltersOutput` | `fhir-pkg list hl7.fhir.r4` filters correctly |
| `List_JsonOutput_ValidJson` | `--json` produces parseable JSON array |

#### Remove Command

| Test | Description |
|------|-------------|
| `Remove_InstalledPackage_ExitCode0` | Package removed, exit 0 |
| `Remove_NotInstalled_ExitCode3` | Not found → exit 3 |

#### Search Command

| Test | Description |
|------|-------------|
| `Search_ByName_ReturnsResults` | `fhir-pkg search --name hl7.fhir.us.core` → results |
| `Search_ByFhirVersion_FiltersResults` | `--fhir-version R4` only R4 results |
| `Search_JsonOutput_ValidJson` | `--json` produces parseable JSON |

#### Restore Command

| Test | Description |
|------|-------------|
| `Restore_ValidProject_ExitCode0` | Creates lock file, exit 0 |
| `Restore_MissingManifest_ExitCode1` | No package.json → exit 1 |
| `Restore_MissingDependencies_ExitCode6` | Unresolvable deps → exit 6 |

#### Resolve Command

| Test | Description |
|------|-------------|
| `Resolve_LatestVersion_ShowsResolved` | `fhir-pkg resolve hl7.fhir.us.core` → shows version |
| `Resolve_Wildcard_ShowsResolved` | `fhir-pkg resolve hl7.fhir.us.core#6.1.x` → shows 6.1.0 |

#### Environment Variables

| Test | Description |
|------|-------------|
| `EnvVar_CachePath_Overrides` | `FHIR_PACKAGE_CACHE` overrides default |
| `EnvVar_Registry_Prepended` | `FHIR_REGISTRY` used as first registry |
| `EnvVar_Verbose_EnablesDebugOutput` | `FHIR_PKG_VERBOSE=1` → debug messages |

#### Configuration File

| Test | Description |
|------|-------------|
| `ConfigFile_InCurrentDir_LoadedAutomatically` | `.fhir-pkg.json` settings applied |
| `ConfigFile_CliOptionsOverride_Config` | CLI flags take precedence over config |

---

## Test Data Management

### Pre-recorded Tarballs

The integration test project includes small, valid FHIR package tarballs for use in offline tests. These are created from minimal packages with just a `package.json` and a few resource files:

| Tarball | Contents |
|---------|----------|
| `test-ig-1.0.0.tgz` | Minimal IG: 1 StructureDefinition, 1 ValueSet |
| `test-core-4.0.1.tgz` | Minimal core: Patient + Observation StructureDefinitions |
| `test-no-package-dir.tgz` | Invalid structure (files at root, no `package/`) |
| `test-with-index.tgz` | Includes `.index.json` |
| `test-with-deps.tgz` | Has dependencies on `test-dep-a` and `test-dep-b` |
| `test-dep-a-1.0.0.tgz` | Dependency A (depends on test-dep-c) |
| `test-dep-b-1.0.0.tgz` | Dependency B (depends on test-dep-c) |
| `test-dep-c-1.0.0.tgz` | Shared transitive dependency |
| `test-circular-a-1.0.0.tgz` | Circular: depends on test-circular-b |
| `test-circular-b-1.0.0.tgz` | Circular: depends on test-circular-a |

### Mock Registry Server

For complex integration scenarios, a lightweight in-process mock server serves pre-built responses:

```csharp
public class MockRegistryServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public MockRegistryServer(int port = 0);
    public string BaseUrl { get; }

    public void AddPackage(string name, string version, byte[] tarball, string? shasum = null);
    public void AddCatalogEntry(CatalogEntry entry);

    public async ValueTask DisposeAsync();
}
```

---

## Running Integration Tests

```bash
# Run all integration tests (recorded/offline mode)
dotnet test test/FhirPkg.IntegrationTests/ \
    --filter "Category=Integration"

# Run against live registries
FHIR_INTEGRATION_LIVE=1 dotnet test test/FhirPkg.IntegrationTests/ \
    --filter "Category=Integration"

# Run a specific test suite
dotnet test --filter "FullyQualifiedName~InstallIntegrationTests"

# Run only CI build tests
dotnet test --filter "FullyQualifiedName~CiBuildIntegrationTests"
```

---

## CI/CD Integration

### GitHub Actions Configuration

```yaml
name: Integration Tests

on:
  pull_request:
  push:
    branches: [main]
  schedule:
    - cron: '0 6 * * 1'  # Weekly live test

jobs:
  integration-tests-offline:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      - run: dotnet test test/FhirPkg.IntegrationTests/

  integration-tests-live:
    runs-on: ubuntu-latest
    if: github.event_name == 'schedule'
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      - run: dotnet test test/FhirPkg.IntegrationTests/
        env:
          FHIR_INTEGRATION_LIVE: '1'
```

**Rationale:**
- Offline (recorded) tests run on every PR and push — fast, deterministic, no network dependency
- Live tests run weekly on a schedule — catches API changes, response format drift, new packages
- Live test failures don't block PRs but alert the team to investigate
