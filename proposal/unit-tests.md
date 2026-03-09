# Unit Test Plan

This document defines the unit test strategy and detailed test cases for the FHIR Package Management library.

## Test Framework & Conventions

- **Framework:** xUnit
- **Assertions:** FluentAssertions
- **Mocking:** Moq
- **Snapshots:** Verify.Xunit (for JSON serialization)
- **Naming:** `MethodName_Scenario_ExpectedBehavior`
- **Organization:** Mirror source project namespace structure

---

## Mocking Strategy

All external dependencies are abstracted behind interfaces, allowing pure unit tests with no I/O:

| Dependency | Interface | Mock Strategy |
|------------|-----------|---------------|
| HTTP calls | `HttpMessageHandler` | Custom `MockHttpHandler` returning canned responses |
| File system | `IPackageCache` | In-memory `Dictionary<PackageReference, byte[]>` |
| Registries | `IRegistryClient` | Return pre-built `PackageListing` / `CatalogEntry` objects |
| Logging | `ILogger<T>` | `NullLogger<T>` or capture-based `TestLogger` |

### `MockHttpHandler`

A reusable test helper that maps request URIs to canned responses:

```csharp
public class MockHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode, string)> _responses = new();

    public void MapGet(string url, HttpStatusCode status, string body);
    public void MapGet(string url, HttpStatusCode status, byte[] body, string contentType);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken);
}
```

### Test Data

All test data (sample manifests, registry responses, tarballs) are stored as embedded resources in the test project under `TestData/`:

```
TestData/
├── Manifests/
│   ├── us-core-6.1.0.json
│   ├── r4-core-4.0.1.json
│   └── minimal.json
├── Listings/
│   ├── us-core-primary.json
│   ├── us-core-secondary.json
│   └── r4-core-primary.json
├── Catalogs/
│   ├── primary-pascalcase.json
│   └── secondary-camelcase.json
├── CiBuilds/
│   ├── qas-sample.json
│   ├── package-manifest.json
│   └── version-info.ini
├── Tarballs/
│   ├── valid-package.tgz
│   ├── missing-package-dir.tgz
│   └── corrupted.tgz
└── Indexes/
    ├── index-v2.json
    └── firely-index.json
```

---

## Test Cases by Component

### 1. `FhirSemVer` Tests

**File:** `Models/FhirSemVerTests.cs`

#### Parsing

| Test | Input | Expected |
|------|-------|----------|
| `Parse_ExactVersion_ReturnsCorrectComponents` | `"4.0.1"` | Major=4, Minor=0, Patch=1, PreRelease=null |
| `Parse_PreReleaseVersion_ParsesTag` | `"6.0.0-ballot1"` | Major=6, PreRelease="ballot1" |
| `Parse_PreReleaseSnapshot_ParsesTag` | `"1.0.0-snapshot2"` | PreRelease="snapshot2", Type=Snapshot |
| `Parse_CiBuildPreRelease_ParsesTag` | `"5.0.0-cibuild"` | PreRelease="cibuild", Type=CiBuild |
| `Parse_BuildMetadata_Ignored` | `"1.2.3+20240115"` | Major=1, BuildMetadata="20240115" |
| `Parse_WildcardPatch_IsWildcard` | `"4.0.x"` | Major=4, Minor=0, IsWildcard=true |
| `Parse_WildcardMinor_IsWildcard` | `"4.x"` | Major=4, IsWildcard=true |
| `Parse_WildcardStar_IsWildcard` | `"4.*"` | Major=4, IsWildcard=true |
| `Parse_WildcardAll_IsWildcard` | `"*"` | IsWildcard=true |
| `Parse_UpperCaseX_AcceptedAsWildcard` | `"4.0.X"` | IsWildcard=true |
| `Parse_TwoSegmentVersion_TreatedAsWildcard` | `"4.0"` | Treated as `4.0.x` |
| `Parse_EmptyString_Throws` | `""` | ArgumentException |
| `Parse_InvalidFormat_Throws` | `"not.a.version"` | FormatException |
| `TryParse_ValidVersion_ReturnsTrue` | `"4.0.1"` | true, result != null |
| `TryParse_InvalidVersion_ReturnsFalse` | `"abc"` | false, result == null |

#### Comparison

| Test | Left | Right | Expected |
|------|------|-------|----------|
| `CompareTo_Release_GreaterThanBallot` | `"1.0.0"` | `"1.0.0-ballot1"` | > 0 |
| `CompareTo_Ballot_GreaterThanDraft` | `"1.0.0-ballot1"` | `"1.0.0-draft1"` | > 0 |
| `CompareTo_Draft_GreaterThanSnapshot` | `"1.0.0-draft1"` | `"1.0.0-snapshot1"` | > 0 |
| `CompareTo_Snapshot_GreaterThanCiBuild` | `"1.0.0-snapshot1"` | `"1.0.0-cibuild"` | > 0 |
| `CompareTo_HigherMajor_Greater` | `"5.0.0"` | `"4.0.1"` | > 0 |
| `CompareTo_HigherMinor_Greater` | `"4.1.0"` | `"4.0.1"` | > 0 |
| `CompareTo_HigherPatch_Greater` | `"4.0.2"` | `"4.0.1"` | > 0 |
| `CompareTo_SameVersion_Equal` | `"4.0.1"` | `"4.0.1"` | == 0 |
| `Equals_SameVersion_True` | `"4.0.1"` | `"4.0.1"` | true |
| `Equals_DifferentVersion_False` | `"4.0.1"` | `"4.0.2"` | false |

#### Wildcard & Range Matching

| Test | Version | Specifier | Expected |
|------|---------|-----------|----------|
| `Satisfies_ExactMatch_True` | `"4.0.1"` | `"4.0.1"` | true |
| `Satisfies_ExactMismatch_False` | `"4.0.1"` | `"4.0.2"` | false |
| `Satisfies_WildcardPatch_MatchesSameMajorMinor` | `"4.0.1"` | `"4.0.x"` | true |
| `Satisfies_WildcardPatch_RejectsDifferentMinor` | `"4.1.0"` | `"4.0.x"` | false |
| `Satisfies_WildcardMinor_MatchesSameMajor` | `"4.3.0"` | `"4.x"` | true |
| `Satisfies_WildcardAll_MatchesAnything` | `"4.0.1"` | `"*"` | true |
| `MaxSatisfying_PatchWildcard_ReturnsHighestPatch` | `["4.0.0","4.0.1","4.1.0"]` | `"4.0.x"` | `"4.0.1"` |
| `MaxSatisfying_NoMatch_ReturnsNull` | `["3.0.0"]` | `"4.0.x"` | null |
| `MaxSatisfying_ExcludesPrerelease_ByDefault` | `["1.0.0","1.0.1-beta"]` | `"1.0.x"` | `"1.0.0"` |
| `MaxSatisfying_IncludesPrerelease_WhenEnabled` | `["1.0.0","1.0.1-beta"]` | `"1.0.x"` | `"1.0.1-beta"` |
| `SatisfyingRange_Caret_IncludesMinorBumps` | `["3.0.1","3.1.0","4.0.0"]` | `"^3.0.1"` | `["3.0.1","3.1.0"]` |
| `SatisfyingRange_Tilde_IncludesPatchOnly` | `["3.0.1","3.0.2","3.1.0"]` | `"~3.0.1"` | `["3.0.1","3.0.2"]` |
| `SatisfyingRange_Pipe_EitherVersion` | `["1.0.0","2.0.0","3.0.0"]` | `"1.0.0\|3.0.0"` | `["1.0.0","3.0.0"]` |

---

### 2. `PackageDirective` / `DirectiveParser` Tests

**File:** `Models/PackageDirectiveTests.cs`, `Resolution/DirectiveParserTests.cs`

| Test | Input | Expected Name | Expected Version | Name Type | Version Type |
|------|-------|---------------|-----------------|-----------|-------------|
| `Parse_FhirStyleExact` | `"hl7.fhir.r4.core#4.0.1"` | `hl7.fhir.r4.core` | `4.0.1` | CoreFull | Exact |
| `Parse_NpmStyleExact` | `"hl7.fhir.r4.core@4.0.1"` | `hl7.fhir.r4.core` | `4.0.1` | CoreFull | Exact |
| `Parse_NameOnly_ImpliesLatest` | `"hl7.fhir.us.core"` | `hl7.fhir.us.core` | null | GuideWithoutSuffix | Latest |
| `Parse_CorePartial` | `"hl7.fhir.r4#4.0.1"` | `hl7.fhir.r4` | `4.0.1` | CorePartial | Exact |
| `Parse_IgWithSuffix` | `"hl7.fhir.uv.ig.r4@1.0.0"` | `hl7.fhir.uv.ig.r4` | `1.0.0` | GuideWithFhirSuffix | Exact |
| `Parse_IgWithoutSuffix` | `"hl7.fhir.uv.ig@1.0.0"` | `hl7.fhir.uv.ig` | `1.0.0` | GuideWithoutSuffix | Exact |
| `Parse_NonHl7Package` | `"us.nlm.vsac@0.18.0"` | `us.nlm.vsac` | `0.18.0` | NonHl7Guide | Exact |
| `Parse_WildcardVersion` | `"hl7.fhir.us.core#6.1.x"` | `hl7.fhir.us.core` | `6.1.x` | GuideWithoutSuffix | Wildcard |
| `Parse_LatestTag` | `"hl7.fhir.us.core@latest"` | `hl7.fhir.us.core` | `latest` | GuideWithoutSuffix | Latest |
| `Parse_CurrentTag` | `"hl7.fhir.us.core#current"` | `hl7.fhir.us.core` | `current` | GuideWithoutSuffix | CiBuild |
| `Parse_CurrentBranch` | `"hl7.fhir.us.core#current$R5"` | `hl7.fhir.us.core` | `current$R5` | GuideWithoutSuffix | CiBuildBranch |
| `Parse_DevTag` | `"hl7.fhir.uv.ig#dev"` | `hl7.fhir.uv.ig` | `dev` | GuideWithoutSuffix | LocalBuild |
| `Parse_NpmAlias` | `"v610@npm:hl7.fhir.us.core@6.1.0"` | `hl7.fhir.us.core` | `6.1.0` | GuideWithoutSuffix | Exact |
| `Parse_NpmAliasFhirStyle` | `"v610@npm:hl7.fhir.us.core#6.1.0"` | `hl7.fhir.us.core` | `6.1.0` | GuideWithoutSuffix | Exact |
| `Parse_PreReleaseVersion` | `"hl7.fhir.r6.core@6.0.0-ballot1"` | `hl7.fhir.r6.core` | `6.0.0-ballot1` | CoreFull | Exact |
| `Parse_StarWildcard` | `"hl7.fhir.r4.core@*"` | `hl7.fhir.r4.core` | `*` | CoreFull | Wildcard |
| `Parse_EmptyString_Throws` | `""` | — | — | — | ArgumentException |
| `Parse_NullString_Throws` | `null` | — | — | — | ArgumentNullException |
| `CorePartial_ExpandsToExpectedNames` | `"hl7.fhir.r4"` | — | — | — | Expanded: `r4.core`, `r4.expansions` |

---

### 3. `PackageManifest` Tests

**File:** `Models/PackageManifestTests.cs`

| Test | Description |
|------|-------------|
| `Deserialize_FullManifest_AllFieldsPopulated` | Complete package.json with all NPM + FHIR fields |
| `Deserialize_MinimalManifest_OnlyRequiredFields` | Only `name` and `version` |
| `Deserialize_WithDependencies_ParsedCorrectly` | Dependencies map populated |
| `Deserialize_WithFhirVersions_ParsedAsArray` | `fhirVersions: ["4.0.1"]` |
| `Deserialize_WithDistribution_ShasumAndTarball` | Distribution metadata parsed |
| `InferredFhirRelease_FromFhirVersions_Correct` | `["4.0.1"]` → `R4` |
| `InferredFhirRelease_FromDependencies_FallsBack` | Infers from `hl7.fhir.r4.core` dependency |
| `Deserialize_NullDependencies_ReturnsNull` | Missing dependencies field |
| `Deserialize_CaseInsensitive_HandlesVariations` | Both PascalCase and camelCase keys |

---

### 4. Registry Client Tests

**File:** `Registry/FhirNpmRegistryClientTests.cs`

| Test | Description |
|------|-------------|
| `SearchAsync_PrimaryRegistry_PascalCaseResponse` | Deserializes `Name`, `Description`, `FhirVersion` |
| `SearchAsync_SecondaryRegistry_CamelCaseResponse` | Deserializes `name`, `version`, `fhirVersion`, `kind` |
| `GetPackageListingAsync_ReturnsAllVersions` | All versions with dist-tags |
| `GetPackageListingAsync_SecondaryRegistry_ExtraFields` | date, kind, count, canonical |
| `ResolveAsync_ExactVersion_ReturnsMatchingVersion` | Direct lookup |
| `ResolveAsync_Latest_UsesDistTagsLatest` | dist-tags.latest value |
| `ResolveAsync_Wildcard_UsesMaxSatisfying` | Highest matching version |
| `ResolveAsync_PackageNotFound_ReturnsNull` | HTTP 404 handling |
| `ResolveAsync_VersionNotFound_ReturnsNull` | No matching version |
| `DownloadAsync_ReturnsTarballStream` | HTTP 200 with content |
| `DownloadAsync_VerifiesShasum` | Checksum validation |
| `DownloadAsync_ShasumMismatch_Throws` | Checksum failure |
| `SearchAsync_NetworkError_ThrowsWithContext` | Timeout/connection error |

**File:** `Registry/FhirCiBuildClientTests.cs`

| Test | Description |
|------|-------------|
| `ResolveAsync_IgCiBuild_ParsesQasJson` | Finds package by package-id, newest by date |
| `ResolveAsync_IgCiBuild_WithBranch_FiltersByRepo` | Filters `current$R5` to matching branch |
| `ResolveAsync_IgCiBuild_ConstructsCorrectUrl` | `build.fhir.org/ig/{org}/{repo}/package.tgz` |
| `ResolveAsync_CoreCiBuild_FixedUrlPattern` | `build.fhir.org/{package}.tgz` |
| `ResolveAsync_CoreCiBuild_WithBranch_BranchUrl` | `build.fhir.org/branches/{branch}/{package}.tgz` |
| `ResolveAsync_ComparesBuildDates_CacheNewer` | Returns null (cache is current) |
| `ResolveAsync_ComparesBuildDates_CiNewer` | Returns download URI |
| `ResolveAsync_ManifestParsing_DateAndVersion` | Parses `YYYYMMDDHHmmss` date format |
| `ResolveAsync_FhirVersionArray_ParsedCorrectly` | `fhirVersion: ["4.0.1"]` array format |

**File:** `Registry/Hl7WebsiteClientTests.cs`

| Test | Description |
|------|-------------|
| `ResolveAsync_CorePackage_R4_CorrectUrl` | `hl7.org/fhir/R4/hl7.fhir.r4.core.tgz` |
| `ResolveAsync_CorePackage_R5_CorrectUrl` | `hl7.org/fhir/R5/hl7.fhir.r5.expansions.tgz` |
| `ResolveAsync_NonCorePackage_ReturnsNull` | Not supported for IGs |
| `ResolveAsync_ServerError_ReturnsNull` | Graceful fallback |

**File:** `Registry/RedundantRegistryClientTests.cs`

| Test | Description |
|------|-------------|
| `ResolveAsync_FirstClientSucceeds_ReturnsResult` | Uses first client |
| `ResolveAsync_FirstClientFails_FallsBackToSecond` | Tries next on failure |
| `ResolveAsync_AllClientsFail_ReturnsNull` | All exhausted |
| `ResolveAsync_FirstClientTimeout_FallsBackToSecond` | Timeout triggers fallback |
| `SearchAsync_MergesResultsFromAllClients` | Deduplication across registries |

---

### 5. Cache Tests

**File:** `Cache/DiskPackageCacheTests.cs`

| Test | Description |
|------|-------------|
| `InstallAsync_ValidTarball_ExtractsToCacheDir` | Creates `{name}#{version}/package/` |
| `InstallAsync_MissingPackageDir_NormalizesStructure` | Moves files into `package/` |
| `InstallAsync_AtomicInstall_NoPartialState` | Uses temp dir + move |
| `InstallAsync_AlreadyExists_SkipsByDefault` | Returns existing record |
| `InstallAsync_AlreadyExists_OverwriteOption` | Replaces existing |
| `IsInstalledAsync_ExistingPackage_ReturnsTrue` | Directory check |
| `IsInstalledAsync_MissingPackage_ReturnsFalse` | No directory |
| `ListPackagesAsync_ReturnsAllPackages` | Lists all `{name}#{version}` dirs |
| `ListPackagesAsync_WithFilter_FiltersCorrectly` | Prefix match |
| `RemoveAsync_ExistingPackage_DeletesDirectory` | Directory deleted |
| `RemoveAsync_UpdatesMetadata` | packages.ini updated |
| `RemoveAsync_MissingPackage_ReturnsFalse` | Not found |
| `ClearAsync_RemovesAllPackages` | All dirs deleted |
| `ReadManifestAsync_ReturnsManifest` | Reads package/package.json |
| `ReadManifestAsync_MissingManifest_ReturnsNull` | No package.json |
| `GetPackageContentPath_ReturnsCorrectPath` | `{cache}/{name}#{version}/package` |

**File:** `Cache/MemoryResourceCacheTests.cs`

| Test | Description |
|------|-------------|
| `Get_CachedEntry_ReturnsValue` | Hit |
| `Get_MissingEntry_ReturnsNull` | Miss |
| `Set_ExceedsCapacity_EvictsOldest` | LRU eviction |
| `SafeMode_Off_ReturnsSameReference` | Object identity preserved |
| `SafeMode_Clone_ReturnsDifferentReference` | Deep clone |
| `SafeMode_Freeze_PreventsMutation` | Throws on mutation |
| `Clear_RemovesAllEntries` | Empty after clear |

**File:** `Cache/TarballExtractorTests.cs`

| Test | Description |
|------|-------------|
| `Extract_ValidTarball_ProducesCorrectStructure` | `package/` with all files |
| `Extract_MissingPackageDir_CreatesAndMovesFiles` | Normalization |
| `Extract_CorruptedTarball_ThrowsWithContext` | Clear error message |
| `Extract_EmptyTarball_ThrowsWithContext` | No files found |

**File:** `Cache/CacheMetadataTests.cs`

| Test | Description |
|------|-------------|
| `Parse_ValidIni_ReturnsMetadata` | Reads [packages] and [package-sizes] |
| `Parse_EmptyFile_ReturnsDefaults` | Empty packages.ini |
| `Parse_MissingFile_ReturnsDefaults` | No packages.ini |
| `Write_RoundTrips_Correctly` | Write then read matches |
| `Update_AddEntry_PersistsCorrectly` | New entry added |
| `Update_RemoveEntry_PersistsCorrectly` | Entry removed |

---

### 6. Version Resolver Tests

**File:** `Resolution/VersionResolverTests.cs`

| Test | Description |
|------|-------------|
| `ResolveVersion_Exact_ReturnsIfPresent` | `"4.0.1"` → `4.0.1` |
| `ResolveVersion_Exact_NotFound_ReturnsNull` | `"99.0.0"` → null |
| `ResolveVersion_Wildcard_ReturnsHighest` | `"4.0.x"` from `[4.0.0, 4.0.1]` → `4.0.1` |
| `ResolveVersion_Wildcard_NoMatch_ReturnsNull` | `"5.0.x"` from `[4.0.1]` → null |
| `ResolveVersion_Latest_UsesDistTag` | Returns dist-tags.latest |
| `ResolveVersion_Range_Caret` | `"^3.0.1"` → highest in range |
| `ResolveVersion_Range_Tilde` | `"~3.0.1"` → highest in range |
| `ResolveVersion_PreRelease_ExcludedByDefault` | Skips pre-release |
| `ResolveVersion_PreRelease_IncludedWhenAllowed` | Includes pre-release |

---

### 7. Dependency Resolver Tests

**File:** `Resolution/DependencyResolverTests.cs`

| Test | Description |
|------|-------------|
| `ResolveAsync_NoDependencies_ReturnsEmptyClosure` | Root with no deps |
| `ResolveAsync_SingleDependency_Resolves` | A depends on B |
| `ResolveAsync_TransitiveDependencies_ResolvesAll` | A → B → C |
| `ResolveAsync_CircularDependency_Terminates` | A → B → A (no infinite loop) |
| `ResolveAsync_DiamondDependency_HighestWins` | A → B → D@1, A → C → D@2 → picks D@2 |
| `ResolveAsync_DiamondDependency_FirstWins` | Same, picks D@1 with FirstWins strategy |
| `ResolveAsync_DiamondDependency_ErrorStrategy` | Throws on conflict |
| `ResolveAsync_MissingDependency_AddedToMissing` | Unresolvable dep tracked |
| `ResolveAsync_MixedResolvedAndMissing_PartialClosure` | Some resolved, some missing |
| `ResolveAsync_MaxDepth_Enforced` | Stops at configured depth |
| `ResolveAsync_Fixup_R4Core_400_To_401` | `4.0.0` auto-upgraded to `4.0.1` |
| `ResolveAsync_Fixup_ExtensionMapping` | `hl7.fhir.uv.extensions` → `hl7.fhir.uv.extensions.r4` |
| `ResolveAsync_NpmAlias_StrippedBeforeResolution` | Alias prefix removed |
| `RestoreFromLockFile_UsesLockedVersions` | Exact versions from lock |
| `RestoreFromLockFile_CachedPackagesSkipped` | No re-download |

---

### 8. Package Indexer Tests

**File:** `Indexing/PackageIndexerTests.cs`

| Test | Description |
|------|-------------|
| `IndexPackageAsync_WithExistingIndex_ReadsIt` | Uses `.index.json` |
| `IndexPackageAsync_WithoutIndex_ScansFiles` | Generates from JSON files |
| `IndexPackageAsync_SkipsSubdirectories` | Only top-level package/ files |
| `IndexPackageAsync_SkipsNonJsonFiles` | Ignores .xml, .txt, etc. |
| `IndexPackageAsync_StructureDefinition_DetectsFlavor` | Profile, Extension, Logical, etc. |
| `FindResources_ByType_ReturnsMatching` | Filter by resourceType |
| `FindResources_ByCanonicalUrl_ReturnsExact` | Match by url field |
| `FindResources_ByPackageScope_FiltersByPackage` | Scope restriction |
| `FindByCanonicalUrl_NotFound_ReturnsNull` | No match |

---

### 9. Package Manager (Orchestrator) Tests

**File:** `FhirPackageManagerTests.cs`

| Test | Description |
|------|-------------|
| `InstallAsync_CachedPackage_ReturnsWithoutDownload` | Cache hit optimization |
| `InstallAsync_UncachedPackage_DownloadsAndCaches` | Full workflow |
| `InstallAsync_WithDependencies_InstallsAll` | Recursive install |
| `InstallAsync_InvalidDirective_Throws` | Argument validation |
| `InstallAsync_PackageNotFound_ReturnsNull` | Registry miss |
| `InstallAsync_ChecksumMismatch_Throws` | Integrity failure |
| `InstallManyAsync_ParallelExecution` | Multiple packages |
| `RestoreAsync_FullWorkflow_ProducesClosure` | Read manifest → resolve → install → lock |
| `RestoreAsync_WritesLockFile` | Lock file persisted |
| `RestoreAsync_NoLockOption_SkipsLockFile` | Lock file not written |
| `ListCachedAsync_DelegatesToCache` | Pass-through |
| `RemoveAsync_DelegatesToCache` | Pass-through |
| `SearchAsync_QueriesAllRegistries` | Parallel search |
| `SearchAsync_DeduplicatesResults` | Same package from multiple registries |
| `ResolveAsync_ReturnsResolvedDirective` | Without downloading |
| `PublishAsync_DelegatesToRegistry` | Passes tarball to client |
| `CancellationToken_Respected` | Cancellation works |
| `ProgressCallback_ReportedCorrectly` | Phases reported |

---

### 10. Utility Tests

**File:** `Utilities/CheckSumTests.cs`

| Test | Description |
|------|-------------|
| `ComputeSha1_KnownInput_MatchesExpected` | Deterministic hash |
| `Verify_MatchingHash_ReturnsTrue` | Good checksum |
| `Verify_MismatchedHash_ReturnsFalse` | Bad checksum |
| `Verify_NullExpected_ReturnsTrue` | Skip when no expected hash |

**File:** `Utilities/IniParserTests.cs`

| Test | Description |
|------|-------------|
| `Parse_PackagesIni_AllSections` | `[cache]`, `[packages]`, `[package-sizes]` |
| `Parse_VersionInfo_FhirFields` | `FhirVersion`, `buildId`, `date` |
| `Parse_EmptyFile_ReturnsEmpty` | No sections |
| `Write_RoundTrips` | Write → read matches |

**File:** `Utilities/PackageFixupsTests.cs`

| Test | Description |
|------|-------------|
| `Apply_R4Core400_UpgradesTo401` | Known fixup |
| `Apply_ExtensionMapping_R4` | Generic → R4-specific |
| `Apply_ExtensionMapping_R5` | Generic → R5-specific |
| `Apply_StripsCiBuildSuffix` | `-cibuild` removed |
| `Apply_UnknownPackage_NoChange` | Passthrough |

---

## Test Coverage Targets

| Component | Target Coverage |
|-----------|----------------|
| `FhirSemVer` | ≥ 95% |
| `PackageDirective` / `DirectiveParser` | ≥ 95% |
| `PackageManifest` | ≥ 90% |
| Registry Clients | ≥ 90% |
| `DiskPackageCache` | ≥ 85% |
| `VersionResolver` | ≥ 95% |
| `DependencyResolver` | ≥ 90% |
| `PackageIndexer` | ≥ 85% |
| `FhirPackageManager` | ≥ 85% |
| Utilities | ≥ 90% |
| **Overall** | **≥ 90%** |

---

## Running Tests

```bash
# Run all unit tests
dotnet test test/FhirPkg.Tests/

# Run with coverage
dotnet test test/FhirPkg.Tests/ --collect:"XPlat Code Coverage"

# Run specific test class
dotnet test --filter "FullyQualifiedName~FhirSemVerTests"

# Run specific test
dotnet test --filter "FullyQualifiedName~Parse_ExactVersion_ReturnsCorrectComponents"
```
