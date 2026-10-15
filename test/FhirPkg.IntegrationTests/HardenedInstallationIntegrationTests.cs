// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FhirPkg.Cli;
using FhirPkg.Cli.Commands;
using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Utilities;
using Shouldly;
using Xunit;

namespace FhirPkg.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class HardenedInstallationIntegrationTests
{
    private const string RetainedArchiveDirectoryVariable =
        "FHIRPKG_RETAINED_CORE_ARCHIVES";
    private const string RetainedCliCacheVariable =
        "FHIRPKG_RETAINED_CORE_CLI_CACHE";
    private const string PrimaryArchiveFileName = "primary-r4-4.0.1.tgz";
    private const string PrimaryArchiveSha256 =
        "ebd7731df7d36b5b7d39d5fb6c9d77b44bb7fe5742f1a2e87f164738c3289d44";
    private const string SecondaryArchiveFileName = "secondary-r4-4.0.1.tgz";
    private const string SecondaryArchiveSha256 =
        "b090bf929e1f665cf2c91583720849695bc38d2892a7c5037c56cb00817fb091";
    private const string PatientCanonical =
        "http://hl7.org/fhir/StructureDefinition/Patient";
    private const string OpenApiCanonical =
        "https://example.test/StructureDefinition/OpenApiOnly";
    private const string OtherCanonical =
        "https://example.test/StructureDefinition/OtherOnly";
    private static readonly PackageReference s_coreReference =
        new("hl7.fhir.r4.core", "4.0.1");

    [Fact]
    public async Task ManagerSourceModes_InstallThroughOneValidatedContract()
    {
        using TestWorkspace workspace = new();
        using FhirPackageManager manager = new(
            new FhirPackageManagerOptions
            {
                CachePath = workspace.CachePath,
                IncludeCiBuilds = false,
                IncludeHl7WebsiteFallback = false
            });

        await using MemoryStream expectedStream =
            CreateArchive("source.expected.stream", "1.0.0");
        PackageRecord expectedStreamRecord =
            await manager.InstallAsync(
                new PackageReference(
                    "source.expected.stream",
                    "1.0.0"),
                expectedStream,
                options: null,
                TestContext.Current.CancellationToken);

        await using MemoryStream importStream =
            CreateArchive("source.import.stream", "1.0.0");
        PackageRecord importStreamRecord =
            await manager.ImportAsync(
                importStream,
                options: null,
                TestContext.Current.CancellationToken);

        byte[] expectedUriArchive =
            CreateArchive("source.expected.uri", "1.0.0").ToArray();
        await using SingleResponseHttpServer expectedServer =
            await SingleResponseHttpServer.StartAsync(
                expectedUriArchive,
                TestContext.Current.CancellationToken);
        PackageRecord expectedUriRecord =
            await manager.InstallAsync(
                new PackageReference(
                    "source.expected.uri",
                    "1.0.0"),
                expectedServer.Uri,
                options: null,
                TestContext.Current.CancellationToken);

        byte[] importUriArchive =
            CreateArchive("source.import.uri", "1.0.0").ToArray();
        await using SingleResponseHttpServer importServer =
            await SingleResponseHttpServer.StartAsync(
                importUriArchive,
                TestContext.Current.CancellationToken);
        PackageRecord importUriRecord =
            await manager.ImportAsync(
                importServer.Uri,
                options: null,
                TestContext.Current.CancellationToken);

        expectedStreamRecord.Reference.Name.ShouldBe(
            "source.expected.stream");
        importStreamRecord.Reference.Name.ShouldBe(
            "source.import.stream");
        expectedUriRecord.Reference.Name.ShouldBe(
            "source.expected.uri");
        importUriRecord.Reference.Name.ShouldBe(
            "source.import.uri");
        IReadOnlyList<PackageRecord> cached =
            await manager.ListCachedAsync(
                cancellationToken:
                    TestContext.Current.CancellationToken);
        cached.Count.ShouldBe(4);
    }

    [Fact]
    public async Task ManagerExpectedUri_AncillaryLayout_IsUsableAfterReopen()
    {
        using TestWorkspace workspace = new(useTemporaryDirectory: true);
        await using MemoryStream archive = CreateAncillaryCoreArchive();
        CoreArchiveInventory inventory = await InspectCoreArchiveAsync(
            archive,
            hasAncillaryRoots: true);
        await using SingleResponseHttpServer server =
            await SingleResponseHttpServer.StartAsync(
                archive.ToArray(),
                TestContext.Current.CancellationToken);

        using (FhirPackageManager manager = CreateCoreManager(workspace.CachePath))
        {
            (await manager.ListCachedAsync(
                cancellationToken: TestContext.Current.CancellationToken))
                .ShouldBeEmpty();
            PackageRecord record = await manager.InstallAsync(
                s_coreReference,
                server.Uri,
                options: null,
                TestContext.Current.CancellationToken);

            await AssertCorePayloadsAsync(record, workspace.CachePath, inventory);
        }

        await AssertCoreUsableAfterReopenAsync(workspace.CachePath, inventory);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"PASS {nameof(ManagerExpectedUri_AncillaryLayout_IsUsableAfterReopen)}");
    }

    [Fact]
    public async Task ManagerImportStream_AncillaryLayout_IsUsableAfterReopen()
    {
        using TestWorkspace workspace = new(useTemporaryDirectory: true);
        await using MemoryStream archive = CreateAncillaryCoreArchive();
        CoreArchiveInventory inventory = await InspectCoreArchiveAsync(
            archive,
            hasAncillaryRoots: true);
        archive.Position = 0;

        using (FhirPackageManager manager = CreateCoreManager(workspace.CachePath))
        {
            (await manager.ListCachedAsync(
                cancellationToken: TestContext.Current.CancellationToken))
                .ShouldBeEmpty();
            PackageRecord record = await manager.ImportAsync(
                archive,
                options: null,
                TestContext.Current.CancellationToken);

            archive.CanRead.ShouldBeTrue();
            archive.Position.ShouldBe(archive.Length);
            await AssertCorePayloadsAsync(record, workspace.CachePath, inventory);
        }

        await AssertCoreUsableAfterReopenAsync(workspace.CachePath, inventory);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"PASS {nameof(ManagerImportStream_AncillaryLayout_IsUsableAfterReopen)}");
    }

    // These acceptance replays only read retained local files; they never download.
    // An absent opt-in skips explicitly, while an enabled but invalid setup fails.
    [Fact]
    public async Task RetainedR4CorePrimary_ExpectedStreamInstall_IsUsable()
    {
        await AssertRetainedCoreInstallAsync(
            PrimaryArchiveFileName,
            PrimaryArchiveSha256,
            hasAncillaryRoots: true);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"PASS {nameof(RetainedR4CorePrimary_ExpectedStreamInstall_IsUsable)}");
    }

    [Fact]
    public async Task RetainedR4CoreSecondary_ExpectedStreamInstall_IsUsable()
    {
        await AssertRetainedCoreInstallAsync(
            SecondaryArchiveFileName,
            SecondaryArchiveSha256,
            hasAncillaryRoots: false);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"PASS {nameof(RetainedR4CoreSecondary_ExpectedStreamInstall_IsUsable)}");
    }

    [Fact]
    public async Task RetainedR4CorePrimary_CliCache_IsUsable()
    {
        string? configuredCachePath =
            Environment.GetEnvironmentVariable(RetainedCliCacheVariable);
        Assert.SkipWhen(
            configuredCachePath is null,
            $"Set {RetainedCliCacheVariable} to an existing disposable CLI cache and " +
            $"{RetainedArchiveDirectoryVariable} to its retained archive inputs; " +
            "this verifier never installs or downloads content.");
        string cachePath = AssertCliCachePath(configuredCachePath);
        string retainedDirectory = AssertRetainedCoreArchiveDirectory(
            Environment.GetEnvironmentVariable(RetainedArchiveDirectoryVariable));
        string archivePath = Path.Combine(retainedDirectory, PrimaryArchiveFileName);
        CoreArchiveInventory inventory;
        long compressedBytes;
        await using (FileStream inspectionStream = File.OpenRead(archivePath))
        {
            compressedBytes = inspectionStream.Length;
            await AssertRetainedArchiveHashAsync(inspectionStream, PrimaryArchiveSha256);
            inspectionStream.Position = 0;
            inventory = await InspectCoreArchiveAsync(
                inspectionStream,
                hasAncillaryRoots: true);
        }

        await using (FileStream secondaryStream = File.OpenRead(
            Path.Combine(retainedDirectory, SecondaryArchiveFileName)))
        {
            await AssertRetainedArchiveHashAsync(secondaryStream, SecondaryArchiveSha256);
        }

        // The executor establishes freshness, CLI/configuration attribution and exclusive use.
        // This must be the first SDK reader: even listing can recover or clean cache state.
        AssertCliCacheEvidenceQuiescent(cachePath);
        string indexPath = Path.Combine(
            cachePath, s_coreReference.FhirDirective, "package", ".index.json");
        byte[] indexBefore = await File.ReadAllBytesAsync(
            indexPath,
            TestContext.Current.CancellationToken);
        using DiskPackageCache cache = new(cachePath);
        CacheMetadata metadataBefore = await cache.GetMetadataAsync(
            TestContext.Current.CancellationToken);
        metadataBefore.Packages.Keys.ShouldHaveSingleItem()
            .ShouldBe(s_coreReference.FhirDirective);
        CacheMetadataEntry entryBefore =
            metadataBefore.Packages[s_coreReference.FhirDirective];
        entryBefore.ArchiveSha256.ShouldBe(PrimaryArchiveSha256);
        entryBefore.SizeBytes.ShouldBe(inventory.Files.Values.Sum(bytes => bytes.LongLength));
        entryBefore.ContentGeneration.ShouldNotBeNullOrWhiteSpace();

        // Listing must expose a usable persisted index before lookup can request an index.
        await AssertCoreUsableAfterReopenAsync(cachePath, inventory);
        AssertCliCacheEvidenceQuiescent(cachePath);
        PackageRecord record = (await cache.GetPackageAsync(
            s_coreReference,
            TestContext.Current.CancellationToken)).ShouldNotBeNull();
        await AssertCorePayloadsAsync(record, cachePath, inventory);
        (await File.ReadAllBytesAsync(indexPath, TestContext.Current.CancellationToken))
            .SequenceEqual(indexBefore).ShouldBeTrue(
                "The existing CLI index must not be regenerated or rewritten by verification.");
        CacheMetadata metadataAfter = await cache.GetMetadataAsync(
            TestContext.Current.CancellationToken);
        metadataAfter.Packages.Keys.ShouldHaveSingleItem()
            .ShouldBe(s_coreReference.FhirDirective);
        CacheMetadataEntry entryAfter =
            metadataAfter.Packages[s_coreReference.FhirDirective];
        entryAfter.ArchiveSha256.ShouldBe(entryBefore.ArchiveSha256);
        entryAfter.ContentGeneration.ShouldBe(entryBefore.ContentGeneration);
        entryAfter.SizeBytes.ShouldBe(entryBefore.SizeBytes);
        AssertCliCacheEvidenceQuiescent(cachePath);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"PASS {nameof(RetainedR4CorePrimary_CliCache_IsUsable)}; " +
            $"existing CLI cache: {cachePath}; retained primary: {archivePath}; " +
            $"compressed bytes: {compressedBytes}; SHA-256: {entryBefore.ArchiveSha256}; " +
            $"generation: {entryBefore.ContentGeneration}; expanded bytes: {entryBefore.SizeBytes}. " +
            "Verified complete payloads, persisted index and scoped Patient API read without " +
            "changing payload/index bytes or metadata hash/generation. The external cache is retained; " +
            "CLI freshness, effective settings and acquisition attribution require separate evidence.");
    }

    [Theory]
    [InlineData("transactions", false)]
    [InlineData("staging", false)]
    [InlineData("transactions", true)]
    [InlineData("staging", true)]
    public async Task CliCacheEvidencePreflight_PendingState_IsRejectedWithoutMutation(
        string stateDirectory,
        bool statePathIsFile)
    {
        using TestWorkspace workspace = new(useTemporaryDirectory: true);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.CachePath, "packages.ini"),
            "[cache]\nversion = 3\n",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(workspace.CachePath, "untouched-empty"));
        string statePath = Path.Combine(workspace.CachePath, ".fhirpkg", stateDirectory);
        string markerPath = statePathIsFile
            ? statePath
            : stateDirectory == "transactions"
                ? Path.Combine(statePath, "pending-journal.json")
                : Path.Combine(statePath, "abandoned-operation", "payload.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        byte[] markerBytes = Encoding.UTF8.GetBytes("pending evidence must not be recovered");
        await File.WriteAllBytesAsync(
            markerPath,
            markerBytes,
            TestContext.Current.CancellationToken);
        CacheEvidenceSnapshot before =
            await SnapshotCliCacheEvidenceFixtureAsync(workspace.RootPath);

        Should.Throw<ShouldAssertException>(() =>
            AssertCliCacheEvidenceQuiescent(workspace.CachePath));

        (await File.ReadAllBytesAsync(markerPath, TestContext.Current.CancellationToken))
            .SequenceEqual(markerBytes).ShouldBeTrue();
        await AssertCliCacheEvidenceFixtureUnchangedAsync(workspace.RootPath, before);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task CliCacheEvidencePreflight_AbsentOrEmptyState_IsAccepted(
        bool hasControlDirectory,
        bool hasTransactionsDirectory,
        bool hasStagingDirectory)
    {
        using TestWorkspace workspace = new(useTemporaryDirectory: true);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.CachePath, "packages.ini"),
            "[cache]\nversion = 3\n",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(workspace.CachePath, "untouched-empty"));
        string controlPath = Path.Combine(workspace.CachePath, ".fhirpkg");
        if (hasControlDirectory)
        {
            string lockPath = Path.Combine(controlPath, "locks");
            Directory.CreateDirectory(lockPath);
            await File.WriteAllTextAsync(
                Path.Combine(lockPath, "metadata.lock"),
                "coordination bookkeeping is not pending state",
                TestContext.Current.CancellationToken);
        }

        if (hasTransactionsDirectory)
        {
            Directory.CreateDirectory(Path.Combine(controlPath, "transactions"));
        }

        if (hasStagingDirectory)
        {
            Directory.CreateDirectory(Path.Combine(controlPath, "staging"));
        }

        CacheEvidenceSnapshot before =
            await SnapshotCliCacheEvidenceFixtureAsync(workspace.RootPath);

        AssertCliCacheEvidenceQuiescent(workspace.CachePath);

        await AssertCliCacheEvidenceFixtureUnchangedAsync(workspace.RootPath, before);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("current$feature")]
    public async Task MutableAlias_OverwritePublishesSecondGeneration(
        string alias)
    {
        using TestWorkspace workspace = new();
        using FhirPackageManager manager = new(
            new FhirPackageManagerOptions
            {
                CachePath = workspace.CachePath,
                IncludeCiBuilds = false,
                IncludeHl7WebsiteFallback = false
            });
        PackageReference aliasReference =
            new("alias.package", alias);
        await using MemoryStream first =
            CreateArchive(
                "alias.package",
                "1.0.0",
                "generation.txt",
                "first");
        await manager.InstallAsync(
            aliasReference,
            first,
            options: null,
            TestContext.Current.CancellationToken);
        await using MemoryStream second =
            CreateArchive(
                "alias.package",
                "2.0.0",
                "generation.txt",
                "second");

        PackageRecord record = await manager.InstallAsync(
            aliasReference,
            second,
            new PackageSourceInstallOptions
            {
                OverwriteExisting = true
            },
            TestContext.Current.CancellationToken);

        record.Reference.ShouldBe(aliasReference);
        record.Manifest.Version.ShouldBe("2.0.0");
        string generation = await File.ReadAllTextAsync(
            Path.Combine(
                record.ContentPath,
                "generation.txt"),
            TestContext.Current.CancellationToken);
        generation.ShouldBe("second");
    }

    [Fact]
    public async Task DevDirective_RemainsLocalAuthoritativeWithOverwrite()
    {
        using TestWorkspace workspace = new();
        DiskPackageCache cache = new(workspace.CachePath);
        await using MemoryStream archive =
            CreateArchive("dev.package", "9.9.9");
        await cache.InstallAsync(
            new PackageReference("dev.package", "dev"),
            archive,
            ct:
                TestContext.Current.CancellationToken);
        using FhirPackageManager manager = new(
            new FhirPackageManagerOptions
            {
                CachePath = workspace.CachePath,
                IncludeCiBuilds = false,
                IncludeHl7WebsiteFallback = false
            });

        PackageRecord? record = await manager.InstallAsync(
            "dev.package#dev",
            new InstallOptions
            {
                OverwriteExisting = true
            },
            TestContext.Current.CancellationToken);

        record.ShouldNotBeNull();
        record!.Reference.Version.ShouldBe("dev");
        record.Manifest.Version.ShouldBe("9.9.9");
    }

    [Fact]
    public async Task Clear_RemovesMetadataOnlyManagedEntryAndPreservesUnknownSection()
    {
        using TestWorkspace workspace = new();
        using DiskPackageCache cache = new(workspace.CachePath);
        PackageReference reference =
            new("metadata.only", "1.0.0");
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        await cache.UpdateMetadataAsync(
            reference,
            new CacheMetadataEntry
            {
                DownloadDateTime = DateTime.UtcNow,
                SizeBytes = 42,
                SourcePublicationDate =
                    new DateTimeOffset(
                        2026,
                        7,
                        17,
                        12,
                        0,
                        0,
                        TimeSpan.Zero),
                ArchiveSha256 = new string('a', 64)
            },
            TestContext.Current.CancellationToken);
        string metadataPath = Path.Combine(
            workspace.CachePath,
            "packages.ini");
        await File.AppendAllTextAsync(
            metadataPath,
            $"{Environment.NewLine}[external]{Environment.NewLine}keep = value{Environment.NewLine}",
            TestContext.Current.CancellationToken);

        int removed = await cache.ClearAsync(
            TestContext.Current.CancellationToken);

        removed.ShouldBe(0);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
            sections = IniParser.ParseFile(metadataPath);
        sections["packages"].ContainsKey(
            cacheKey.MetadataKey).ShouldBeFalse();
        sections["package-sizes"].ContainsKey(
            cacheKey.MetadataKey).ShouldBeFalse();
        sections["package-source-publication-dates"].ContainsKey(
            cacheKey.MetadataKey).ShouldBeFalse();
        sections["package-archive-sha256"].ContainsKey(
            cacheKey.MetadataKey).ShouldBeFalse();
        sections["external"]["keep"].ShouldBe("value");
    }

    [Fact]
    public async Task Clear_RemovesMalformedLegacyAndNonCanonicalManagedKeys()
    {
        using TestWorkspace workspace = new();
        using DiskPackageCache cache = new(workspace.CachePath);
        string metadataPath = Path.Combine(
            workspace.CachePath,
            "packages.ini");
        await File.WriteAllTextAsync(
            metadataPath,
            """
            [cache]
            version = 3

            [packages]
            legacy.package@1.0.0 = 20260717120000
            UPPER.package#1.0.0 = 20260717120000
            ../malformed#1.0.0 = 20260717120000
            canonical.package#1.0.0 = 20260717120000

            [package-sizes]
            legacy.package@1.0.0 = 1
            @scope\package#1.0.0 = 2

            [package-source-publication-dates]
            UPPER.package#1.0.0 = 2026-07-17T12:00:00Z

            [package-archive-sha256]
            ../malformed#1.0.0 = abc

            [external]
            keep = value
            """,
            TestContext.Current.CancellationToken);

        int removed = await cache.ClearAsync(
            TestContext.Current.CancellationToken);

        removed.ShouldBe(0);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
            sections = IniParser.ParseFile(metadataPath);
        sections["packages"].ShouldBeEmpty();
        sections["package-sizes"].ShouldBeEmpty();
        sections["package-source-publication-dates"].ShouldBeEmpty();
        sections["package-archive-sha256"].ShouldBeEmpty();
        sections["external"]["keep"].ShouldBe("value");
    }

    [Fact]
    public void InstallExitCode_NotFoundRequiresNotFoundStatus()
    {
        int notFound = InstallCommand.GetExitCode(
        [
            new PackageInstallResult
            {
                Directive = "installed.package#1.0.0",
                Status = PackageInstallStatus.Installed
            },
            new PackageInstallResult
            {
                Directive = "missing.package#1.0.0",
                Status = PackageInstallStatus.NotFound,
                ErrorCode = PackageInstallErrorCode.ResolutionFailed,
                ErrorStage = PackageInstallStage.Resolution
            }
        ]);
        int failedResolution = InstallCommand.GetExitCode(
        [
            new PackageInstallResult
            {
                Directive = "network.package#1.0.0",
                Status = PackageInstallStatus.Failed,
                ErrorCode = PackageInstallErrorCode.ResolutionFailed,
                ErrorStage = PackageInstallStage.Resolution
            }
        ]);

        notFound.ShouldBe(ExitCodes.NotFound);
        failedResolution.ShouldBe(ExitCodes.NetworkError);
    }

    [Fact]
    public void InstallExitCode_MixedResultsUseStablePrecedence()
    {
        PackageInstallResult notFound = new()
        {
            Directive = "missing.package#1.0.0",
            Status = PackageInstallStatus.NotFound,
            ErrorCode = PackageInstallErrorCode.ResolutionFailed,
            ErrorStage = PackageInstallStage.Resolution
        };
        PackageInstallResult networkFailure = new()
        {
            Directive = "network.package#1.0.0",
            Status = PackageInstallStatus.Failed,
            ErrorCode = PackageInstallErrorCode.ResolutionFailed,
            ErrorStage = PackageInstallStage.Resolution
        };
        PackageInstallResult checksumFailure = new()
        {
            Directive = "checksum.package#1.0.0",
            Status = PackageInstallStatus.Failed,
            ErrorCode = PackageInstallErrorCode.ChecksumMismatch,
            ErrorStage = PackageInstallStage.ChecksumValidation
        };
        PackageInstallResult dependencyFailure = new()
        {
            Directive = "root.package#1.0.0",
            Status = PackageInstallStatus.Failed,
            ErrorCode =
                PackageInstallErrorCode.DependencyInstallationFailed,
            ErrorStage = PackageInstallStage.DependencyInstallation,
            DependencyFailures =
            [
                new PackageInstallResult
                {
                    Directive = "dependency.package#1.0.0",
                    Status = PackageInstallStatus.NotFound,
                    ErrorCode =
                        PackageInstallErrorCode.ResolutionFailed,
                    ErrorStage = PackageInstallStage.Resolution,
                }
            ],
        };

        InstallCommand.GetExitCode(
            [notFound, networkFailure])
            .ShouldBe(ExitCodes.NetworkError);
        InstallCommand.GetExitCode(
            [networkFailure, checksumFailure])
            .ShouldBe(ExitCodes.ChecksumFail);
        InstallCommand.GetExitCode(
            [networkFailure, dependencyFailure])
            .ShouldBe(ExitCodes.DependencyResolutionFail);
    }

    private static FhirPackageManager CreateCoreManager(string cachePath) =>
        new(new FhirPackageManagerOptions
        {
            CachePath = cachePath,
            IncludeCiBuilds = false,
            IncludeHl7WebsiteFallback = false,
            VerifyChecksums = true
        });

    private static string AssertCliCachePath(string? cachePath)
    {
        string existingCachePath = AssertExistingEvidenceDirectory(cachePath);
        string packagePath = Path.Combine(existingCachePath, s_coreReference.FhirDirective);
        AssertExistingEvidenceDirectory(packagePath);
        string contentPath = Path.Combine(packagePath, "package");
        AssertExistingEvidenceDirectory(contentPath);
        AssertExistingEvidenceFile(Path.Combine(existingCachePath, "packages.ini"));
        AssertExistingEvidenceFile(Path.Combine(contentPath, "package.json"));
        AssertExistingEvidenceFile(Path.Combine(contentPath, ".index.json"));
        return existingCachePath;
    }

    private static string AssertRetainedCoreArchiveDirectory(string? retainedDirectory)
    {
        string existingDirectory = AssertExistingEvidenceDirectory(retainedDirectory);
        foreach (string requiredFile in new[] { PrimaryArchiveFileName, SecondaryArchiveFileName })
        {
            AssertExistingEvidenceFile(Path.Combine(existingDirectory, requiredFile));
        }

        return existingDirectory;
    }

    private static string AssertExistingEvidenceDirectory(string? path)
    {
        path.ShouldNotBeNullOrWhiteSpace();
        Path.IsPathFullyQualified(path).ShouldBeTrue("Evidence paths must be absolute.");
        string fullPath = Path.GetFullPath(path);
        File.GetAttributes(fullPath).HasFlag(FileAttributes.Directory).ShouldBeTrue(
            $"Evidence directory must already exist: {fullPath}");
        return fullPath;
    }

    private static void AssertExistingEvidenceFile(string path)
    {
        File.GetAttributes(path).HasFlag(FileAttributes.Directory).ShouldBeFalse(
            $"Evidence must be an existing readable file: {path}");
        using FileStream stream = File.OpenRead(path);
        stream.Length.ShouldBeGreaterThan(0L, $"Evidence file must not be empty: {path}");
    }

    private static void AssertCliCacheEvidenceQuiescent(string cachePath)
    {
        // Enumerate instead of Exists: inaccessible/error states must fail, never look absent.
        EnumerationOptions options = new()
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false
        };
        string? controlPath = Directory.EnumerateFileSystemEntries(cachePath, "*", options)
            .SingleOrDefault(path => string.Equals(
                Path.GetFileName(path), ".fhirpkg", StringComparison.OrdinalIgnoreCase));
        if (controlPath is null)
        {
            return;
        }

        File.GetAttributes(controlPath).HasFlag(FileAttributes.Directory).ShouldBeTrue(
            $"CLI cache control path must be a directory: {controlPath}");
        string[] controlEntries =
            Directory.EnumerateFileSystemEntries(controlPath, "*", options).ToArray();
        foreach (string stateDirectory in new[] { "transactions", "staging" })
        {
            string? statePath = controlEntries.SingleOrDefault(path => string.Equals(
                Path.GetFileName(path), stateDirectory, StringComparison.OrdinalIgnoreCase));
            if (statePath is null)
            {
                continue;
            }

            File.GetAttributes(statePath).HasFlag(FileAttributes.Directory).ShouldBeTrue(
                $"CLI cache state path must be absent or a directory: {statePath}");
            Directory.EnumerateFileSystemEntries(statePath, "*", options).ShouldBeEmpty(
                $"CLI cache evidence must not need recovery or staging cleanup: {statePath}");
        }
    }

    private static async Task<CacheEvidenceSnapshot> SnapshotCliCacheEvidenceFixtureAsync(
        string rootPath)
    {
        string[] directories = Directory.EnumerateDirectories(
                rootPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(rootPath, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(
            rootPath, "*", SearchOption.AllDirectories))
        {
            files.Add(
                Path.GetRelativePath(rootPath, path),
                await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }

        return new CacheEvidenceSnapshot(files, directories);
    }

    private static async Task AssertCliCacheEvidenceFixtureUnchangedAsync(
        string rootPath,
        CacheEvidenceSnapshot before)
    {
        CacheEvidenceSnapshot after = await SnapshotCliCacheEvidenceFixtureAsync(rootPath);
        after.Directories.ShouldBe(before.Directories);
        after.Files.Keys.OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(before.Files.Keys.OrderBy(path => path, StringComparer.Ordinal));
        foreach (KeyValuePair<string, byte[]> file in before.Files)
        {
            after.Files[file.Key].SequenceEqual(file.Value).ShouldBeTrue(
                $"Evidence fixture changed: {file.Key}");
        }
    }

    private sealed record CacheEvidenceSnapshot(
        IReadOnlyDictionary<string, byte[]> Files,
        string[] Directories);

    private static async Task AssertRetainedArchiveHashAsync(Stream archive, string pinnedHash)
    {
        string actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(archive, TestContext.Current.CancellationToken))
            .ToLowerInvariant();
        actualHash.ShouldBe(pinnedHash);
    }

    private static async Task AssertRetainedCoreInstallAsync(
        string archiveFileName,
        string pinnedHash,
        bool hasAncillaryRoots)
    {
        string? retainedDirectory =
            Environment.GetEnvironmentVariable(RetainedArchiveDirectoryVariable);
        Assert.SkipWhen(
            retainedDirectory is null,
            $"Set {RetainedArchiveDirectoryVariable} to a local directory containing " +
            "primary-r4-4.0.1.tgz and secondary-r4-4.0.1.tgz to run retained-byte acceptance; " +
            "these tests never download archives.");
        retainedDirectory = AssertRetainedCoreArchiveDirectory(retainedDirectory);

        PackageInstallLimits limits = PackageInstallLimits.FromEnvironment();
        limits.MaxCompressedBytes.ShouldBe(PackageInstallLimits.DefaultMaxCompressedBytes);
        limits.MaxExpandedBytes.ShouldBe(PackageInstallLimits.DefaultMaxExpandedBytes);
        limits.MaxEntryBytes.ShouldBe(PackageInstallLimits.DefaultMaxEntryBytes);
        limits.MaxArchiveEntries.ShouldBe(PackageInstallLimits.DefaultMaxArchiveEntries);
        limits.MaxArchivePathLength.ShouldBe(PackageInstallLimits.DefaultMaxArchivePathLength);
        limits.MaxArchiveDepth.ShouldBe(PackageInstallLimits.DefaultMaxArchiveDepth);

        string archivePath = Path.Combine(retainedDirectory, archiveFileName);
        CoreArchiveInventory inventory;
        long compressedBytes;
        await using (FileStream inspectionStream = File.OpenRead(archivePath))
        {
            compressedBytes = inspectionStream.Length;
            await AssertRetainedArchiveHashAsync(inspectionStream, pinnedHash);
            inspectionStream.Position = 0;
            inventory = await InspectCoreArchiveAsync(
                inspectionStream,
                hasAncillaryRoots);
        }

        using TestWorkspace workspace = new(useTemporaryDirectory: true);
        Directory.EnumerateFileSystemEntries(workspace.CachePath).ShouldBeEmpty();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Retained input: {archivePath}; compressed bytes: {compressedBytes}; " +
            $"SHA-256: {pinnedHash}; fresh cache: {workspace.CachePath}");
        using (FhirPackageManager manager = CreateCoreManager(workspace.CachePath))
        {
            (await manager.ListCachedAsync(
                cancellationToken: TestContext.Current.CancellationToken))
                .ShouldBeEmpty();

            // Reopen the same original file, not an extracted or repacked substitute.
            await using FileStream installStream = File.OpenRead(archivePath);
            PackageRecord record = await manager.InstallAsync(
                new PackageReference("hl7.fhir.r4.core", "4.0.1"),
                installStream,
                new PackageSourceInstallOptions
                {
                    ExpectedSha256 = pinnedHash,
                    IncludeDependencies = false,
                    OverwriteExisting = false
                },
                TestContext.Current.CancellationToken);

            installStream.CanRead.ShouldBeTrue();
            installStream.Length.ShouldBe(compressedBytes);
            installStream.Position.ShouldBe(compressedBytes);
            await AssertCorePayloadsAsync(record, workspace.CachePath, inventory);
        }

        await AssertCoreUsableAfterReopenAsync(workspace.CachePath, inventory);
        using DiskPackageCache cache = new(workspace.CachePath);
        CacheMetadata metadata = await cache.GetMetadataAsync(
            TestContext.Current.CancellationToken);
        metadata.Packages.Keys.ShouldHaveSingleItem()
            .ShouldBe(s_coreReference.FhirDirective);
        CacheMetadataEntry entry = metadata.Packages[s_coreReference.FhirDirective];
        entry.ArchiveSha256.ShouldBe(pinnedHash);
        entry.SizeBytes.ShouldBe(inventory.Files.Values.Sum(bytes => bytes.LongLength));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Verified original payloads, metadata hash and {entry.SizeBytes} expanded bytes, " +
            "reopened exact listing/index, Patient canonical lookup/read, and no ancillary indexing. " +
            "The disposable test cache is removed on exit.");
    }

    private static async Task<CoreArchiveInventory> InspectCoreArchiveAsync(
        Stream archive,
        bool hasAncillaryRoots)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        HashSet<string> directories = new(StringComparer.Ordinal);
        HashSet<string> explicitPaths = new(StringComparer.OrdinalIgnoreCase);
        int entryCount = 0;
        await using GZipStream gzip = new(
            archive,
            CompressionMode.Decompress,
            leaveOpen: true);
        await using TarReader reader = new(gzip, leaveOpen: true);
        while (await reader.GetNextEntryAsync(
            cancellationToken: TestContext.Current.CancellationToken) is TarEntry entry)
        {
            entryCount++;
            (entry.EntryType is TarEntryType.RegularFile
                or TarEntryType.V7RegularFile
                or TarEntryType.Directory)
                .ShouldBeTrue($"Unexpected tar entry type for {entry.Name}");
            string path = entry.Name.Replace('\\', '/').TrimEnd('/');
            string[] segments = path.Split('/');
            segments.ShouldAllBe(segment =>
                segment.Length > 0 && segment != "." && segment != "..");
            explicitPaths.Add(path.Normalize()).ShouldBeTrue(
                $"Duplicate or case/Unicode-colliding entry: {path}");
            for (int depth = 1; depth < segments.Length; depth++)
            {
                directories.Add(string.Join("/", segments.Take(depth)));
            }

            if (entry.EntryType == TarEntryType.Directory)
            {
                directories.Add(path);
                continue;
            }

            using MemoryStream content = new();
            if (entry.DataStream is not null)
            {
                await entry.DataStream.CopyToAsync(
                    content,
                    TestContext.Current.CancellationToken);
            }

            content.Length.ShouldBe(entry.Length);
            files.Add(path, content.ToArray());
        }

        files.ShouldNotBeEmpty();
        string[] allPaths = files.Keys.Concat(directories).ToArray();
        allPaths.Select(path => path.Normalize())
            .Distinct(StringComparer.OrdinalIgnoreCase).Count()
            .ShouldBe(allPaths.Length, "Inventory paths must not collide.");
        string[] expectedRoots = hasAncillaryRoots
            ? ["openapi", "other", "package"]
            : ["package"];
        directories.Where(path => !path.Contains('/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(expectedRoots);
        files.Keys.ShouldAllBe(path => path.Contains('/'));
        files.Keys.Where(path => string.Equals(
                path.Split('/')[^1],
                "package.json",
                StringComparison.OrdinalIgnoreCase))
            .ShouldHaveSingleItem().ShouldBe("package/package.json");
        JsonNode? manifest = JsonNode.Parse(files["package/package.json"]);
        manifest.ShouldNotBeNull();
        manifest["name"]!.GetValue<string>().ShouldBe(s_coreReference.Name);
        manifest["version"]!.GetValue<string>().ShouldBe(s_coreReference.Version);
        files.ContainsKey("package/StructureDefinition-Patient.json").ShouldBeTrue();
        AssertPatientJson(JsonNode.Parse(files["package/StructureDefinition-Patient.json"]));

        foreach (string root in expectedRoots.Where(root => root != "package"))
        {
            files.Keys.Any(path => path.StartsWith(root + "/", StringComparison.Ordinal))
                .ShouldBeTrue($"The retained {root}/ tree must contain payloads.");
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Inspected complete inventory: {entryCount} entries, {files.Count} files, " +
            $"{files.Values.Sum(bytes => bytes.LongLength)} expanded bytes; " +
            $"roots: {string.Join(", ", expectedRoots)}; one exact manifest.");
        return new CoreArchiveInventory(files, directories);
    }

    private static async Task AssertCorePayloadsAsync(
        PackageRecord record,
        string cachePath,
        CoreArchiveInventory inventory)
    {
        record.Reference.ShouldBe(s_coreReference);
        record.Manifest.Name.ShouldBe(s_coreReference.Name);
        record.Manifest.Version.ShouldBe(s_coreReference.Version);
        record.DirectoryPath.ShouldBe(Path.Combine(cachePath, s_coreReference.FhirDirective));
        record.ContentPath.ShouldBe(Path.Combine(record.DirectoryPath, "package"));
        record.SizeBytes.ShouldBe(inventory.Files.Values.Sum(bytes => bytes.LongLength));

        Directory.EnumerateDirectories(record.DirectoryPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(record.DirectoryPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(inventory.Directories.OrderBy(path => path, StringComparer.Ordinal));

        foreach (KeyValuePair<string, byte[]> file in inventory.Files)
        {
            // The SDK may regenerate only this derivative file, never a payload.
            if (file.Key == "package/.index.json")
            {
                continue;
            }

            byte[] installedBytes = await File.ReadAllBytesAsync(
                Path.Combine(record.DirectoryPath, file.Key),
                TestContext.Current.CancellationToken);
            installedBytes.SequenceEqual(file.Value).ShouldBeTrue(
                $"Original payload changed: {file.Key}");
        }

        string[] expectedFiles = inventory.Files.Keys
            .Append("package/.index.json")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Directory.EnumerateFiles(record.DirectoryPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(record.DirectoryPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(expectedFiles);

        record.Index.ShouldNotBeNull();
        record.Index.IndexVersion.ShouldBe(2);
        record.Index.Files.ShouldNotBeEmpty();
        ResourceIndexEntry patient = record.Index.Files
            .Where(file => file.Url == PatientCanonical).ShouldHaveSingleItem();
        patient.Filename.ShouldBe("StructureDefinition-Patient.json");
        patient.ResourceType.ShouldBe("StructureDefinition");
        patient.Id.ShouldBe("Patient");
        record.Index.Files.ShouldAllBe(file =>
            !IsAncillaryResourcePath(file.Filename)
            && file.Url != OpenApiCanonical
            && file.Url != OtherCanonical);
    }

    private static async Task AssertCoreUsableAfterReopenAsync(
        string cachePath,
        CoreArchiveInventory inventory)
    {
        using FhirPackageManager reopened = CreateCoreManager(cachePath);
        IReadOnlyList<PackageRecord> cached = await reopened.ListCachedAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        PackageRecord record = cached.ShouldHaveSingleItem();
        await AssertCorePayloadsAsync(record, cachePath, inventory);
        ResourceInfo? patient = await reopened.FindByCanonicalUrlAsync(
            PatientCanonical,
            s_coreReference.FhirDirective,
            TestContext.Current.CancellationToken);
        patient.ShouldNotBeNull();
        patient.PackageName.ShouldBe(s_coreReference.Name);
        patient.PackageVersion.ShouldBe(s_coreReference.Version);
        string patientFilePath = patient.FilePath.ShouldNotBeNull();
        patientFilePath.ShouldBe("StructureDefinition-Patient.json");
        patient.ResourceType.ShouldBe("StructureDefinition");
        patient.Id.ShouldBe("Patient");
        patient.Url.ShouldBe(PatientCanonical);
        JsonNode? resource = await reopened.ReadResourceAsync(
            patient,
            TestContext.Current.CancellationToken);
        AssertPatientJson(resource);
        byte[] patientBytes = inventory.Files["package/StructureDefinition-Patient.json"];
        JsonNode.DeepEquals(resource, JsonNode.Parse(patientBytes)).ShouldBeTrue();
        (await File.ReadAllBytesAsync(
            Path.Combine(record.ContentPath, patientFilePath),
            TestContext.Current.CancellationToken))
            .SequenceEqual(patientBytes).ShouldBeTrue();

        foreach (string ancillaryCanonical in new[] { OpenApiCanonical, OtherCanonical })
        {
            (await reopened.FindByCanonicalUrlAsync(
                ancillaryCanonical,
                s_coreReference.FhirDirective,
                TestContext.Current.CancellationToken))
                .ShouldBeNull();
        }

        IReadOnlyList<ResourceInfo> indexed = await reopened.FindResourcesAsync(
            new ResourceSearchCriteria { PackageScope = s_coreReference.FhirDirective },
            TestContext.Current.CancellationToken);
        indexed.Count.ShouldBe(record.Index!.Files.Count);
        indexed.ShouldAllBe(resourceInfo =>
            resourceInfo.FilePath != null && !IsAncillaryResourcePath(resourceInfo.FilePath));
    }

    private static void AssertPatientJson(JsonNode? resource)
    {
        resource.ShouldNotBeNull();
        resource["resourceType"]!.GetValue<string>().ShouldBe("StructureDefinition");
        resource["id"]!.GetValue<string>().ShouldBe("Patient");
        resource["url"]!.GetValue<string>().ShouldBe(PatientCanonical);
        resource["type"]!.GetValue<string>().ShouldBe("Patient");
    }

    private static bool IsAncillaryResourcePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.StartsWith("openapi/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("other/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("package/openapi/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("package/other/", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CoreArchiveInventory(
        IReadOnlyDictionary<string, byte[]> Files,
        IReadOnlySet<string> Directories);

    private static MemoryStream CreateAncillaryCoreArchive()
    {
        MemoryStream archive = new();
        using (GZipStream gzip = new(archive, CompressionLevel.Fastest, leaveOpen: true))
        using (TarWriter writer = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            WriteEntry(
                writer,
                "package/package.json",
                """{"name":"hl7.fhir.r4.core","version":"4.0.1","fhirVersions":["4.0.1"],"type":"fhir.core"}""");
            WriteEntry(
                writer,
                "package/StructureDefinition-Patient.json",
                $$"""
                {"resourceType":"StructureDefinition","id":"Patient","url":"{{PatientCanonical}}",
                 "version":"4.0.1","name":"Patient","status":"active","kind":"resource","abstract":false,
                 "type":"Patient","baseDefinition":"http://hl7.org/fhir/StructureDefinition/DomainResource",
                 "derivation":"specialization"}
                """);
            WriteEntry(
                writer,
                "openapi/StructureDefinition-OpenApiOnly.json",
                $$"""
                {"resourceType":"StructureDefinition","id":"OpenApiOnly","url":"{{OpenApiCanonical}}",
                 "name":"OpenApiOnly","status":"draft","kind":"resource","abstract":false,"type":"Patient",
                 "baseDefinition":"{{PatientCanonical}}","derivation":"constraint"}
                """);
            WriteEntry(
                writer,
                "other/StructureDefinition-OtherOnly.json",
                $$"""
                {"resourceType":"StructureDefinition","id":"OtherOnly","url":"{{OtherCanonical}}",
                 "name":"OtherOnly","status":"draft","kind":"resource","abstract":false,"type":"Patient",
                 "baseDefinition":"{{PatientCanonical}}","derivation":"constraint"}
                """);
        }

        archive.Position = 0;
        return archive;
    }

    private static MemoryStream CreateArchive(
        string name,
        string version,
        string? extraName = null,
        string? extraContent = null)
    {
        MemoryStream archive = new();
        using (GZipStream gzip = new(
            archive,
            CompressionLevel.Fastest,
            leaveOpen: true))
        using (TarWriter writer = new(
            gzip,
            TarEntryFormat.Pax,
            leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(
                TarEntryType.Directory,
                "package/"));
            WriteEntry(
                writer,
                "package/package.json",
                $$"""{"name":"{{name}}","version":"{{version}}"}""");
            if (extraName is not null)
            {
                WriteEntry(
                    writer,
                    $"package/{extraName}",
                    extraContent ?? string.Empty);
            }
        }

        archive.Position = 0;
        return archive;
    }

    private static void WriteEntry(
        TarWriter writer,
        string path,
        string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        writer.WriteEntry(new PaxTarEntry(
            TarEntryType.RegularFile,
            path)
        {
            DataStream = new MemoryStream(bytes)
        });
    }

    private sealed class TestWorkspace : IDisposable
    {
        internal TestWorkspace(bool useTemporaryDirectory = false)
        {
            RootPath = Path.Combine(
                useTemporaryDirectory ? Path.GetTempPath() : AppContext.BaseDirectory,
                "phase5-hardened-integration",
                Guid.NewGuid().ToString("N"));
            CachePath = Path.Combine(RootPath, "cache");
            Directory.CreateDirectory(CachePath);
        }

        internal string RootPath { get; }
        internal string CachePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
    }

    private sealed class SingleResponseHttpServer :
        IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serverTask;

        private SingleResponseHttpServer(
            TcpListener listener,
            Task serverTask,
            Uri uri)
        {
            _listener = listener;
            _serverTask = serverTask;
            Uri = uri;
        }

        internal Uri Uri { get; }

        internal static Task<SingleResponseHttpServer> StartAsync(
            byte[] content,
            CancellationToken cancellationToken)
        {
            TcpListener listener = new(
                IPAddress.Loopback,
                port: 0);
            listener.Start();
            int port =
                ((IPEndPoint)listener.LocalEndpoint).Port;
            Task serverTask = ServeAsync(
                listener,
                content,
                cancellationToken);
            SingleResponseHttpServer server = new(
                listener,
                serverTask,
                new Uri($"http://127.0.0.1:{port}/package.tgz"));
            return Task.FromResult(server);
        }

        private static async Task ServeAsync(
            TcpListener listener,
            byte[] content,
            CancellationToken cancellationToken)
        {
            using TcpClient client =
                await listener.AcceptTcpClientAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
            await using NetworkStream stream =
                client.GetStream();
            byte[] requestBuffer = new byte[4096];
            int total = 0;
            while (total < requestBuffer.Length)
            {
                int read = await stream.ReadAsync(
                        requestBuffer.AsMemory(total),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                total += read;
                if (Encoding.ASCII
                    .GetString(requestBuffer, 0, total)
                    .Contains(
                        "\r\n\r\n",
                        StringComparison.Ordinal))
                {
                    break;
                }
            }

            byte[] headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/gzip\r\n" +
                $"Content-Length: {content.LongLength}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(
                    headers,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.WriteAsync(
                    content,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException exception)
                when (exception.SocketErrorCode
                    == SocketError.OperationAborted)
            {
            }
        }
    }
}
