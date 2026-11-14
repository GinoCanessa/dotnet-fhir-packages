// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Tests.Support;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Cache;

[Collection("EnvironmentVariable")]
public class DiskPackageCacheTests : IDisposable
{
    private readonly string _tempDir;
    private string? _originalEnvVar;

    public DiskPackageCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fhir-cache-test-{Guid.NewGuid():N}");
        _originalEnvVar = Environment.GetEnvironmentVariable("PACKAGE_CACHE_FOLDER");
    }

    public void Dispose()
    {
        // Restore original env var
        Environment.SetEnvironmentVariable("PACKAGE_CACHE_FOLDER", _originalEnvVar);

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Constructor_ExplicitPath_UsesExplicitPath()
    {
        DiskPackageCache cache = new DiskPackageCache(_tempDir);

        cache.CacheDirectory.ShouldBe(_tempDir);
        Directory.Exists(_tempDir).ShouldBeTrue();
    }

    [Fact]
    public void Constructor_NullRetryOptions_ThrowsBeforeAnyIo()
    {
        Should.Throw<PackageInstallException>(
            () => new DiskPackageCache(_tempDir, null, null, (FileSystemRetryOptions)null!))
            .ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);

        Directory.Exists(_tempDir).ShouldBeFalse();
    }

    [Fact]
    public void Constructor_RetryOptions_AreTheEffectiveRuntimePolicy()
    {
        using DiskPackageCache cache = new DiskPackageCache(
            _tempDir,
            null,
            null,
            new FileSystemRetryOptions { RetryCount = 3, MinimumDelayMilliseconds = 7 });

        cache.FileSystemContext.Policy.RetryCount.ShouldBe(3);
        cache.FileSystemContext.Policy.MinimumDelayMilliseconds.ShouldBe(7);
    }

    [Fact]
    public void Constructor_EnvVarSet_UsesEnvVar()
    {
        string envDir = Path.Combine(_tempDir, "from-env");
        Environment.SetEnvironmentVariable("PACKAGE_CACHE_FOLDER", envDir);

        DiskPackageCache cache = new DiskPackageCache();

        cache.CacheDirectory.ShouldBe(envDir);
        Directory.Exists(envDir).ShouldBeTrue();
    }

    [Fact]
    public void Constructor_ExplicitPath_TakesPriorityOverEnvVar()
    {
        string envDir = Path.Combine(_tempDir, "from-env");
        string explicitDir = Path.Combine(_tempDir, "explicit");
        Environment.SetEnvironmentVariable("PACKAGE_CACHE_FOLDER", envDir);

        DiskPackageCache cache = new DiskPackageCache(explicitDir);

        cache.CacheDirectory.ShouldBe(explicitDir);
        Directory.Exists(explicitDir).ShouldBeTrue();
        Directory.Exists(envDir).ShouldBeFalse("env var path should not be created when explicit path is provided");
    }

    [Fact]
    public void Dispose_DisposesInstallLock()
    {
        DiskPackageCache cache = new DiskPackageCache(_tempDir);

        cache.Dispose();

        // Verify the cache implements IDisposable and does not throw on double-dispose
        cache.Dispose();
    }

    [Fact]
    public void Constructor_NoPathNoEnvVar_UsesDefault()
    {
        Environment.SetEnvironmentVariable("PACKAGE_CACHE_FOLDER", null);

        DiskPackageCache cache = new DiskPackageCache();

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".fhir",
            "packages");
        cache.CacheDirectory.ShouldBe(expected);
    }

    [Fact]
    public async Task InstallAsync_AtomicMove_ProducesValidPackage()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        PackageReference reference = new PackageReference("test.package", "1.0.0");

        using MemoryStream tarball = CreateTestTarball("""{"name":"test.package","version":"1.0.0"}""");

        PackageRecord record = await cache.InstallAsync(reference, tarball, new InstallCacheOptions { VerifyChecksum = false }, ct: TestContext.Current.CancellationToken);

        record.ShouldNotBeNull();
        record.Reference.Name.ShouldBe("test.package");
        record.Reference.Version.ShouldBe("1.0.0");
        Directory.Exists(record.DirectoryPath).ShouldBeTrue();

        string manifestPath = Path.Combine(record.ContentPath, "package.json");
        File.Exists(manifestPath).ShouldBeTrue();
    }

    [Fact]
    public async Task InstallAsync_FirstCommitReportsCreatedOutcome()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        PackageReference reference =
            new PackageReference("test.package", "current");
        InstallCacheOptions options = new()
        {
            VerifyChecksum = false
        };
        using MemoryStream tarball = CreateTestTarball(
            """{"name":"test.package","version":"1.0.0","date":"20260720"}""");

        PackageRecord record = await cache.InstallAsync(
            reference,
            tarball,
            options,
            TestContext.Current.CancellationToken);

        record.Manifest.Date.ShouldBe("20260720");
        options.InstallOutcome.Effect.ShouldBe(
            PackageCacheInstallEffect.Created);
        options.InstallOutcome.PreviousManifestDate.ShouldBeNull();
    }

    [Fact]
    public async Task InstallAsync_OverwriteReportsReplacedOutcomeWithPreviousDate()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        PackageReference reference =
            new PackageReference("test.package", "current");
        using MemoryStream original = CreateTestTarball(
            """{"name":"test.package","version":"1.0.0","date":"20260720"}""");
        await cache.InstallAsync(
            reference,
            original,
            new InstallCacheOptions { VerifyChecksum = false },
            TestContext.Current.CancellationToken);
        InstallCacheOptions replacementOptions = new()
        {
            VerifyChecksum = false,
            OverwriteExisting = true
        };
        using MemoryStream replacement = CreateTestTarball(
            """{"name":"test.package","version":"2.0.0","date":"20260721"}""");

        PackageRecord record = await cache.InstallAsync(
            reference,
            replacement,
            replacementOptions,
            TestContext.Current.CancellationToken);

        record.Manifest.Date.ShouldBe("20260721");
        replacementOptions.InstallOutcome.Effect.ShouldBe(
            PackageCacheInstallEffect.Replaced);
        replacementOptions.InstallOutcome.PreviousManifestDate.ShouldBe(
            "20260720");
    }

    [Fact]
    public async Task InstallAsync_MatchingArchiveReportsUnchangedOutcome()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        PackageReference reference =
            new PackageReference("test.package", "current");
        byte[] archive = CreateTestTarball(
            """{"name":"test.package","version":"1.0.0","date":"20260720"}""")
            .ToArray();
        using MemoryStream original = new MemoryStream(archive);
        await cache.InstallAsync(
            reference,
            original,
            new InstallCacheOptions { VerifyChecksum = false },
            TestContext.Current.CancellationToken);
        InstallCacheOptions refreshOptions = new()
        {
            VerifyChecksum = false,
            OverwriteExisting = true,
            SkipIfArchiveUnchanged = true
        };
        using MemoryStream refresh = new MemoryStream(archive);

        PackageRecord record = await cache.InstallAsync(
            reference,
            refresh,
            refreshOptions,
            TestContext.Current.CancellationToken);

        record.Manifest.Date.ShouldBe("20260720");
        refreshOptions.InstallOutcome.Effect.ShouldBe(
            PackageCacheInstallEffect.Unchanged);
        refreshOptions.InstallOutcome.PreviousManifestDate.ShouldBe(
            "20260720");
    }

    [Fact]
    public async Task InstallAsync_FailureLeavesUnknownOutcome()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        PackageReference reference =
            new PackageReference("test.package", "current");
        InstallCacheOptions options = new()
        {
            VerifyChecksum = false
        };
        using MemoryStream original = CreateTestTarball(
            """{"name":"test.package","version":"1.0.0","date":"20260720"}""");
        await cache.InstallAsync(
            reference,
            original,
            options,
            TestContext.Current.CancellationToken);
        options.InstallOutcome.Effect.ShouldBe(
            PackageCacheInstallEffect.Created);
        options.OverwriteExisting = true;
        using MemoryStream invalidReplacement = CreateTarball(
            ("package/readme.txt", "missing manifest"));

        await Should.ThrowAsync<PackageInstallException>(
            () => cache.InstallAsync(
                reference,
                invalidReplacement,
                options,
                TestContext.Current.CancellationToken));

        options.InstallOutcome.Effect.ShouldBe(
            PackageCacheInstallEffect.Unknown);
        options.InstallOutcome.PreviousManifestDate.ShouldBeNull();
    }

    [Fact]
    public async Task InstallAsync_ConcurrentSameAliasReportsOneCreateAndOneUnchanged()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        PackageReference reference =
            new PackageReference("test.package", "current");
        byte[] archive = CreateTestTarball(
            """{"name":"test.package","version":"1.0.0","date":"20260721"}""")
            .ToArray();
        InstallCacheOptions firstOptions = new()
        {
            VerifyChecksum = false
        };
        InstallCacheOptions secondOptions = new()
        {
            VerifyChecksum = false
        };
        using MemoryStream firstArchive = new MemoryStream(archive);
        using MemoryStream secondArchive = new MemoryStream(archive);

        Task<PackageRecord> firstInstall = cache.InstallAsync(
            reference,
            firstArchive,
            firstOptions,
            TestContext.Current.CancellationToken);
        Task<PackageRecord> secondInstall = cache.InstallAsync(
            reference,
            secondArchive,
            secondOptions,
            TestContext.Current.CancellationToken);
        await Task.WhenAll(firstInstall, secondInstall);

        PackageCacheInstallOutcome[] outcomes =
            [firstOptions.InstallOutcome, secondOptions.InstallOutcome];
        outcomes.Count(
            outcome =>
                outcome.Effect == PackageCacheInstallEffect.Created)
            .ShouldBe(1);
        outcomes.Count(
            outcome =>
                outcome.Effect == PackageCacheInstallEffect.Unchanged)
            .ShouldBe(1);
        outcomes.Single(
                outcome =>
                    outcome.Effect == PackageCacheInstallEffect.Created)
            .PreviousManifestDate.ShouldBeNull();
        outcomes.Single(
                outcome =>
                    outcome.Effect == PackageCacheInstallEffect.Unchanged)
            .PreviousManifestDate.ShouldBe("20260721");
    }

    [Fact]
    public async Task InstallAsync_ScopedReference_UsesCanonicalTwoSegmentPath()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        PackageReference reference = PackageReference.Parse("@Example/Package@1.0.0");
        using MemoryStream tarball = CreateTestTarball(
            """{"name":"@Example/Package","version":"1.0.0"}""");

        PackageRecord record = await cache.InstallAsync(
            reference,
            tarball,
            new InstallCacheOptions { VerifyChecksum = false },
            ct: TestContext.Current.CancellationToken);
        IReadOnlyList<PackageRecord> listed = await cache.ListPackagesAsync(
            "@example",
            ct: TestContext.Current.CancellationToken);

        record.DirectoryPath.ShouldBe(
            Path.Combine(_tempDir, "@example", "package#1.0.0"));
        Directory.Exists(record.ContentPath).ShouldBeTrue();
        listed.Count.ShouldBe(1);
        listed[0].Reference.Name.ShouldBe("@example/package");
    }

    [Fact]
    public async Task InstallAsync_CurrentBranch_EncodesBranchAndPersistsFreshnessMetadata()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        PackageReference reference = new PackageReference(
            "Example.Package",
            "current$feature/fix");
        DateTimeOffset publicationDate = new DateTimeOffset(
            2026,
            7,
            17,
            12,
            0,
            0,
            TimeSpan.Zero);
        using MemoryStream tarball = CreateTestTarball(
            """{"name":"Example.Package","version":"2.0.0"}""");

        PackageRecord record = await cache.InstallAsync(
            reference,
            tarball,
            new InstallCacheOptions
            {
                VerifyChecksum = false,
                SourcePublicationDate = publicationDate,
                ArchiveSha256 = "abc123"
            },
            ct: TestContext.Current.CancellationToken);
        CacheMetadata metadata = await cache.GetMetadataAsync(
            TestContext.Current.CancellationToken);

        record.DirectoryPath.ShouldBe(
            Path.Combine(_tempDir, "example.package#current%24feature%2ffix"));
        CacheMetadataEntry entry = metadata.Packages[
            "example.package#current%24feature%2ffix"];
        entry.SourcePublicationDate.ShouldBe(publicationDate);
        entry.ArchiveSha256.ShouldBe("abc123");
    }

    [Fact]
    public async Task Metadata_RoundTripsCollisionSafeCaseDistinctKeys()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        PackageReference upper = new PackageReference(
            "example.package",
            "1.0.0-Alpha");
        PackageReference lower = new PackageReference(
            "example.package",
            "1.0.0-alpha");
        PackageReference unsafeReference = new PackageReference(
            "example=package",
            "A=a%");
        CacheMetadataEntry entry = new CacheMetadataEntry
        {
            DownloadDateTime = DateTime.UtcNow,
            ArchiveSha256 = "hash"
        };

        await cache.UpdateMetadataAsync(
            upper,
            entry,
            TestContext.Current.CancellationToken);
        await cache.UpdateMetadataAsync(
            lower,
            entry,
            TestContext.Current.CancellationToken);
        await cache.UpdateMetadataAsync(
            unsafeReference,
            entry,
            TestContext.Current.CancellationToken);
        CacheMetadata metadata = await cache.GetMetadataAsync(
            TestContext.Current.CancellationToken);

        PackageCacheKey upperKey = PackageCacheKey.Create(upper);
        PackageCacheKey lowerKey = PackageCacheKey.Create(lower);
        PackageCacheKey unsafeKey = PackageCacheKey.Create(unsafeReference);
        metadata.Packages.Count.ShouldBe(3);
        metadata.Packages.ContainsKey(upperKey.MetadataKey).ShouldBeTrue();
        metadata.Packages.ContainsKey(lowerKey.MetadataKey).ShouldBeTrue();
        metadata.Packages.ContainsKey(unsafeKey.MetadataKey).ShouldBeTrue();
        metadata.Packages.Keys.ShouldAllBe(key => !key.Contains('='));
    }

    [Fact]
    public async Task InstallAsync_InvalidAliasReplacementPreservesPriorPackage()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        PackageReference alias = new PackageReference("example.package", "current");
        using MemoryStream original = CreateTestTarball(
            """{"name":"example.package","version":"1.0.0"}""");
        await cache.InstallAsync(
            alias,
            original,
            new InstallCacheOptions { VerifyChecksum = false },
            ct: TestContext.Current.CancellationToken);
        using MemoryStream invalidReplacement = CreateTarball(
            ("package/readme.txt", "missing manifest"));

        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
            () => cache.InstallAsync(
                alias,
                invalidReplacement,
                new InstallCacheOptions
                {
                    VerifyChecksum = false,
                    OverwriteExisting = true
                },
                TestContext.Current.CancellationToken));
        PackageRecord? retained = await cache.GetPackageAsync(
            alias,
            TestContext.Current.CancellationToken);

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidArchive);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
        retained.ShouldNotBeNull();
        retained!.Manifest.Version.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task InstallAsync_CancelledAliasReplacementPreservesPriorPackage()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        PackageReference alias = new PackageReference("example.package", "current");
        using MemoryStream original = CreateTestTarball(
            """{"name":"example.package","version":"1.0.0"}""");
        await cache.InstallAsync(
            alias,
            original,
            new InstallCacheOptions { VerifyChecksum = false },
            ct: TestContext.Current.CancellationToken);
        using MemoryStream replacement = CreateTestTarball(
            """{"name":"example.package","version":"2.0.0"}""");
        using CancellationTokenSource source = new CancellationTokenSource();
        source.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => cache.InstallAsync(
                alias,
                replacement,
                new InstallCacheOptions
                {
                    VerifyChecksum = false,
                    OverwriteExisting = true
                },
                source.Token));
        PackageRecord? retained = await cache.GetPackageAsync(
            alias,
            TestContext.Current.CancellationToken);

        retained.ShouldNotBeNull();
        retained!.Manifest.Version.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task InstallAsync_MetadataFailureRollsBackAliasContent()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        PackageReference alias = new PackageReference("example.package", "current");
        using MemoryStream original = CreateTestTarball(
            """{"name":"example.package","version":"1.0.0"}""");
        await cache.InstallAsync(
            alias,
            original,
            new InstallCacheOptions { VerifyChecksum = false },
            ct: TestContext.Current.CancellationToken);

        string metadataPath = Path.Combine(_tempDir, "packages.ini");
        File.Delete(metadataPath);
        Directory.CreateDirectory(metadataPath);
        using MemoryStream replacement = CreateTestTarball(
            """{"name":"example.package","version":"2.0.0"}""");

        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
            () => cache.InstallAsync(
                alias,
                replacement,
                new InstallCacheOptions
                {
                    VerifyChecksum = false,
                    OverwriteExisting = true
                },
                TestContext.Current.CancellationToken));
        string? contentPath = cache.GetPackageContentPath(alias);

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.CommitFailed);
        contentPath.ShouldNotBeNull();
        PackageManifest? manifest = await ReadManifestAsync(contentPath!);
        manifest.ShouldNotBeNull();
        manifest!.Version.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task InstallAsync_PathTraversalReturnsTypedArchiveFailure()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        PackageReference reference = new PackageReference("example.package", "1.0.0");
        using MemoryStream tarball = CreateTarball(
            ("../escape.txt", "unsafe"),
            ("package/package.json", """{"name":"example.package","version":"1.0.0"}"""));

        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
            () => cache.InstallAsync(
                reference,
                tarball,
                new InstallCacheOptions { VerifyChecksum = false },
                TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidArchive);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
        File.Exists(Path.Combine(_tempDir, "escape.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task ConcurrentAliasMetadataRefreshAndInstall_PreserveBothEntries()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        PackageReference alias = new PackageReference("example.package", "current");
        PackageReference other = new PackageReference("other.package", "1.0.0");
        using MemoryStream aliasTarball = CreateTestTarball(
            """{"name":"example.package","version":"1.0.0"}""");
        PackageRecord aliasRecord = await cache.InstallAsync(
            alias,
            aliasTarball,
            new InstallCacheOptions
            {
                VerifyChecksum = false,
                ArchiveSha256 = "original"
            },
            ct: TestContext.Current.CancellationToken);
        CacheMetadataEntry refreshedAlias = new CacheMetadataEntry
        {
            DownloadDateTime = aliasRecord.InstalledAt!.Value.UtcDateTime,
            SizeBytes = aliasRecord.SizeBytes,
            ArchiveSha256 = "refreshed"
        };
        using MemoryStream otherTarball = CreateTestTarball(
            """{"name":"other.package","version":"1.0.0"}""");

        Task refreshTask = cache.UpdateMetadataAsync(
            alias,
            refreshedAlias,
            TestContext.Current.CancellationToken);
        Task<PackageRecord> installTask = cache.InstallAsync(
            other,
            otherTarball,
            new InstallCacheOptions { VerifyChecksum = false },
            TestContext.Current.CancellationToken);
        await Task.WhenAll(refreshTask, installTask);
        CacheMetadata metadata = await cache.GetMetadataAsync(
            TestContext.Current.CancellationToken);

        metadata.Packages.ContainsKey(
            PackageCacheKey.Create(alias).MetadataKey).ShouldBeTrue();
        metadata.Packages.ContainsKey(
            PackageCacheKey.Create(other).MetadataKey).ShouldBeTrue();
        metadata.Packages[
            PackageCacheKey.Create(alias).MetadataKey].ArchiveSha256.ShouldBe("refreshed");
    }

    [Fact]
    public async Task InstallAsync_ObjectAuthor_PreservesManifestAndRemainsReadable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        PackageReference reference = new PackageReference("test.package", "1.0.0");
        const string manifestJson = """
            {
                "name": "test.package",
                "version": "1.0.0",
                "author": {"name":"Example Author","email":"author@example.test","url":"https://example.test/"}
            }
            """;
        const string expectedAuthor =
            "Example Author <author@example.test> (https://example.test/)";
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        string directoryPath = Path.Combine(_tempDir, "test.package#1.0.0");
        string contentPath = Path.Combine(directoryPath, "package");
        using MemoryStream tarball = CreateTestTarball(manifestJson);

        using (DiskPackageCache cache = new DiskPackageCache(_tempDir))
        {
            PackageRecord record = await cache.InstallAsync(
                reference,
                tarball,
                new InstallCacheOptions { VerifyChecksum = false },
                ct);

            record.Reference.Name.ShouldBe(reference.Name);
            record.Reference.Version.ShouldBe(reference.Version);
            record.Manifest.Author.ShouldNotBeNull();
            record.Manifest.Author.ShouldBe(expectedAuthor);
            record.DirectoryPath.ShouldBe(directoryPath);
            record.ContentPath.ShouldBe(contentPath);
            Directory.Exists(record.DirectoryPath).ShouldBeTrue();
            byte[] installedBytes = await File.ReadAllBytesAsync(
                Path.Combine(record.ContentPath, "package.json"),
                ct);
            installedBytes.ShouldBe(manifestBytes);
        }

        using DiskPackageCache reopenedCache = new DiskPackageCache(_tempDir);
        PackageRecord? reopenedRecord = await reopenedCache.GetPackageAsync(reference, ct);

        reopenedRecord.ShouldNotBeNull();
        reopenedRecord.Reference.Name.ShouldBe(reference.Name);
        reopenedRecord.Reference.Version.ShouldBe(reference.Version);
        reopenedRecord.Manifest.Author.ShouldBe(expectedAuthor);
        reopenedRecord.ContentPath.ShouldBe(contentPath);
        reopenedCache.GetPackageContentPath(reference).ShouldBe(contentPath);
        byte[] reopenedBytes = await File.ReadAllBytesAsync(
            Path.Combine(contentPath, "package.json"),
            ct);
        reopenedBytes.ShouldBe(manifestBytes);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ImportAsync_AuthorForms_PreserveBytesAndRemainReadable(
        bool objectAuthor,
        bool unmodified)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        PackageReference reference = new PackageReference("test.package", "1.0.0");
        string authorJson = objectAuthor
            ? """{"name":"Example Author","email":"author@example.test","url":"https://example.test/"}"""
            : "\"Example Author <author@example.test> (https://example.test/)\"";
        string manifestJson = $$"""
            {
                "name": "test.package",
                "version": "1.0.0",
                "author": {{authorJson}}
            }
            """;
        const string expectedAuthor =
            "Example Author <author@example.test> (https://example.test/)";
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        using MemoryStream authoredTarball = CreateTestTarball(manifestJson);
        byte[] archiveBytes = authoredTarball.ToArray();
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(archiveBytes))
            .ToLowerInvariant();
        using MemoryStream tarball = new MemoryStream(archiveBytes.ToArray());
        string directoryPath = Path.Combine(_tempDir, "test.package#1.0.0");
        string contentPath = Path.Combine(directoryPath, "package");
        tarball.CanRead.ShouldBeTrue();
        tarball.Position.ShouldBe(0);

        using (DiskPackageCache cache = new DiskPackageCache(_tempDir))
        {
            PackageRecord record = await cache.ImportAsync(
                tarball,
                new InstallCacheOptions
                {
                    Unmodified = unmodified,
                    VerifyChecksum = true,
                    ExpectedSha256Sum = expectedSha256
                },
                ct);

            record.Reference.Name.ShouldBe(reference.Name);
            record.Reference.Version.ShouldBe(reference.Version);
            record.Manifest.Author.ShouldNotBeNull();
            record.Manifest.Author.ShouldBe(expectedAuthor);
            record.DirectoryPath.ShouldBe(directoryPath);
            record.ContentPath.ShouldBe(contentPath);
            Directory.Exists(record.DirectoryPath).ShouldBeTrue();
            byte[] importedBytes = await File.ReadAllBytesAsync(
                Path.Combine(record.ContentPath, "package.json"),
                ct);
            importedBytes.ShouldBe(manifestBytes);
            tarball.CanRead.ShouldBeTrue();
            tarball.ToArray().ShouldBe(archiveBytes);
            CacheMetadata metadata = await cache.GetMetadataAsync(ct);
            CacheMetadataEntry entry = metadata.Packages[
                PackageCacheKey.Create(reference).MetadataKey];
            entry.ArchiveSha256.ShouldBe(expectedSha256);
        }

        using DiskPackageCache reopenedCache = new DiskPackageCache(_tempDir);
        PackageRecord? reopenedRecord = await reopenedCache.GetPackageAsync(reference, ct);

        reopenedRecord.ShouldNotBeNull();
        reopenedRecord.Reference.Name.ShouldBe(reference.Name);
        reopenedRecord.Reference.Version.ShouldBe(reference.Version);
        reopenedRecord.Manifest.Author.ShouldNotBeNull();
        reopenedRecord.Manifest.Author.ShouldBe(expectedAuthor);
        reopenedRecord.ContentPath.ShouldBe(contentPath);
        bool isInstalled = await reopenedCache.IsInstalledAsync(reference, ct);
        isInstalled.ShouldBeTrue();
        reopenedCache.GetPackageContentPath(reference).ShouldBe(contentPath);
        byte[] reopenedBytes = await File.ReadAllBytesAsync(
            Path.Combine(contentPath, "package.json"),
            ct);
        reopenedBytes.ShouldBe(manifestBytes);
    }

    [Fact]
    public async Task ImportAsync_MalformedAuthor_ReturnsTypedManifestFailure()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        using MemoryStream tarball = CreateTestTarball(
            """{"name":"test.package","version":"1.0.0","author":{"name":42}}""");

        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
            () => cache.ImportAsync(
                tarball,
                new InstallCacheOptions
                {
                    Unmodified = true,
                    VerifyChecksum = false
                },
                TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidArchive);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
        JsonException jsonException = exception.InnerException.ShouldBeOfType<JsonException>();
        jsonException.Path.ShouldBe("$.author");
        Directory.Exists(Path.Combine(_tempDir, "test.package#1.0.0")).ShouldBeFalse();
    }

    [Fact]
    public async Task IsInstalledAsync_UnsafeIdentityIsRejectedBeforePathAccess()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);

        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
            () => cache.IsInstalledAsync(
                new PackageReference("../outside", "1.0.0"),
                TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPackageIdentity);
    }

    [Theory]
    [MemberData(nameof(RepositoryDateAuthorMatrix))]
    public async Task ImportAsync_RepositoryDateAuthorMatrix_PreservesBytesAndTree(
        string repositoryForm,
        bool numericDate,
        bool objectAuthor)
    {
        string identity = $"metadata.matrix.{repositoryForm}.{(numericDate ? "integer" : "string")}.{(objectAuthor ? "object" : "string")}";
        (string repositoryJson, NpmRepository expectedRepository) = repositoryForm switch
        {
            "shorthand" => ("\"github:example/repo\"", new NpmRepository(null, "github:example/repo", null)),
            "url" => ("\"https://example.test/repo.git\"", new NpmRepository(null, "https://example.test/repo.git", null)),
            "object" => ("""{"type":"git","url":"https://example.test/repo.git","directory":"src"}""",
                new NpmRepository("git", "https://example.test/repo.git", "src")),
            _ => throw new ArgumentOutOfRangeException(nameof(repositoryForm)),
        };
        string dateJson = numericDate ? "20240203123456" : "\"20240203123456\"";
        string authorJson = objectAuthor
            ? """{"name":"Example Author","email":"author@example.test","url":"https://example.test/"}"""
            : "\"Example Author <author@example.test> (https://example.test/)\"";
        string manifestJson = CreatePreservationManifest(
            identity, $"\"repository\":{repositoryJson},\"date\":{dateJson},\"author\":{authorJson}");

        await AssertMetadataArchiveRoundTripAsync(
            manifestJson,
            new PackageReference(identity, "1.0.0"),
            expectedRepository,
            "20240203123456",
            "Example Author <author@example.test> (https://example.test/)",
            import: true,
            unmodified: true);
    }

    public static IEnumerable<object?[]> RepositoryDateAuthorMatrix()
    {
        // Exactly twelve fresh-cache imports on each TFM; controls below are separate.
        foreach (string repositoryForm in new[] { "shorthand", "url", "object" })
        {
            foreach (bool numericDate in new[] { false, true })
            {
                foreach (bool objectAuthor in new[] { false, true })
                {
                    yield return [repositoryForm, numericDate, objectAuthor];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(RepositoryDateInstallControls))]
    public async Task InstallAsync_RepositoryDateForms_ValidatesAndPreservesBytes(
        string fixture,
        string? repositoryJson,
        string? dateJson,
        string? type,
        string? url,
        string? directory,
        string? expectedDate,
        bool unmodified)
    {
        await AssertMetadataControlAsync(
            fixture, repositoryJson, dateJson, type, url, directory, expectedDate, unmodified, import: false);
    }

    [Theory]
    [MemberData(nameof(RepositoryDateInstallControls))]
    public async Task ImportAsync_RepositoryDateControls_PreservesBytesAndTree(
        string fixture,
        string? repositoryJson,
        string? dateJson,
        string? type,
        string? url,
        string? directory,
        string? expectedDate,
        bool unmodified)
    {
        await AssertMetadataControlAsync(
            fixture, repositoryJson, dateJson, type, url, directory, expectedDate, unmodified, import: true);
    }

    private async Task AssertMetadataControlAsync(
        string fixture,
        string? repositoryJson,
        string? dateJson,
        string? type,
        string? url,
        string? directory,
        string? expectedDate,
        bool unmodified,
        bool import)
    {
        string identity = $"metadata.{(import ? "import" : "install")}.{fixture}.{(unmodified ? "unmodified" : "normal")}";
        List<string> properties = ["\"author\":{\"name\":\"Baseline Author\"}"];
        if (repositoryJson is not null)
            properties.Add($"\"repository\":{repositoryJson}");
        if (dateJson is not null)
            properties.Add($"\"date\":{dateJson}");
        string manifestJson = CreatePreservationManifest(identity, string.Join(",", properties));

        await AssertMetadataArchiveRoundTripAsync(
            manifestJson,
            new PackageReference(identity, "1.0.0"),
            repositoryJson is null or "null" ? null : new NpmRepository(type, url, directory),
            expectedDate,
            "Baseline Author",
            import,
            unmodified);
    }

    public static IEnumerable<object?[]> RepositoryDateInstallControls()
    {
        (string Fixture, string? Repository, string? Date, string? Type, string? Url, string? Directory, string? ExpectedDate)[] cases =
        [
            ("shorthand14", "\"github:example/repo\"", "20240203123456", null, "github:example/repo", null, "20240203123456"),
            ("url14", "\"https://example.test/repo.git\"", "\"20240203123456\"", null, "https://example.test/repo.git", null, "20240203123456"),
            ("object8", """{"type":"git","url":"raw","directory":"src"}""", "20240203", "git", "raw", "src", "20240203"),
            ("shorthand8", "\"github:example/repo\"", "\"20240203\"", null, "github:example/repo", null, "20240203"),
            ("baseline", """{"type":"git","url":"https://example.test/repo.git"}""",
                "\"2024-02-03T12:34:56+05:30\"", "git", "https://example.test/repo.git", null, "2024-02-03T12:34:56+05:30"),
            ("absent", null, null, null, null, null, null),
            ("null", "null", "null", null, null, null, null),
            ("empty", """{"type":"","url":"","directory":""}""", "\"\"", "", "", "", ""),
        ];
        foreach ((string fixture, string? repository, string? date, string? type, string? url, string? directory, string? expectedDate) in cases)
        {
            foreach (bool unmodified in new[] { false, true })
                yield return [fixture, repository, date, type, url, directory, expectedDate, unmodified];
        }
    }

    [Theory]
    [MemberData(nameof(MalformedRepositoryOrDate))]
    public async Task ImportAsync_MalformedRepositoryOrDate_ReturnsTypedManifestFailure(
        string fixture,
        string properties,
        string expectedPath)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        PackageReference prior = new PackageReference("metadata.prior", "1.0.0");
        PackageReference neighbour = new PackageReference("metadata.neighbour", "1.0.0");
        await SeedPreservationPackageAsync(cache, prior, ct);
        await SeedPreservationPackageAsync(cache, neighbour, ct);
        PackageTreeSnapshot before = SnapshotTree(_tempDir, excludeCoordination: true);

        // A missing identity must not be published; the same bad input also cannot
        // replace a valid prior identity merely because a later duplicate is valid.
        foreach (string identity in new[] { "metadata.malformed", prior.Name })
        {
            string manifestJson = fixture == "malformed-json"
                ? $"{{\"name\":\"{identity}\",\"version\":\"1.0.0\",\"repository\":{{\"url\":}}}}"
                : CreatePreservationManifest(identity, properties);
            using MemoryStream archive = CreatePreservationTarball(manifestJson);
            byte[] originalBytes = archive.ToArray();
            PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
                () => cache.ImportAsync(
                    archive,
                    new InstallCacheOptions
                    {
                        Unmodified = true,
                        OverwriteExisting = true,
                        VerifyChecksum = true,
                        ExpectedSha256Sum = Sha256(originalBytes)
                    },
                    ct));

            exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidArchive);
            exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
            exception.InnerException.ShouldBeOfType<JsonException>().Path.ShouldBe(expectedPath);
            archive.CanRead.ShouldBeTrue();
            archive.ToArray().ShouldBe(originalBytes);
            // Raw snapshots precede all recovery-capable SDK reads.
            AssertTreeSnapshot(_tempDir, before, excludeCoordination: true);
            AssertOperationArtifactsClean(_tempDir);
            Directory.Exists(Path.Combine(_tempDir, "metadata.malformed#1.0.0")).ShouldBeFalse();
        }

        (await cache.GetPackageAsync(prior, ct)).ShouldNotBeNull();
        (await cache.GetPackageAsync(neighbour, ct)).ShouldNotBeNull();
        (await cache.IsInstalledAsync(new PackageReference("metadata.malformed", "1.0.0"), ct)).ShouldBeFalse();
        AssertTreeSnapshot(_tempDir, before, excludeCoordination: true);
    }

    public static IEnumerable<object?[]> MalformedRepositoryOrDate()
    {
        foreach (string shape in new[] { "42", "1.5", "true", "false", "[]", "[null]" })
            yield return ["repository-shape:" + shape, "\"repository\":" + shape, "$.repository"];

        foreach (string member in new[] { "type", "url", "directory" })
        {
            foreach (string shape in new[] { "42", "true", "false", "{}", "[]" })
            {
                foreach (string members in new[]
                {
                    $"\"{member}\":{shape}",
                    $"\"{member}\":{shape},\"{member}\":\"valid\"",
                    $"\"{member}\":{shape},\"{member}\":null",
                    $"\"{member}\":{shape},\"{member}\":\"\"",
                    $"\"{member}\":\"valid\",\"{member}\":{shape}",
                })
                    yield return [$"repository-member:{members}", "\"repository\":{" + members + "}", "$.repository"];
            }

            foreach (string members in new[]
            {
                $"\"{member.ToUpperInvariant()}\":42,\"{member}\":\"valid\"",
                $"\"{member}\":\"valid\",\"{member.ToUpperInvariant()}\":42",
                $"\"{member}\":true,\"{member.ToUpperInvariant()}\":null",
            })
                yield return [$"repository-casing:{members}", "\"repository\":{" + members + "}", "$.repository"];
        }

        foreach (string shape in new[]
        {
            "-20240203", "-20240203123456", "-1234567", "-1234567890123",
            "202402.3", "123456789012.3", "20240203.0", "20240203123456.0",
            "20240203e0", "20240203123456E0", "2.0240203e7", "2024e003", "1234567890e+03", "2024020312e+003",
            "0", "1234567", "123456789", "1234567890123", "123456789012345",
            "true", "false", "[]", "[null]", "{}",
        })
            yield return ["date-shape:" + shape, "\"date\":" + shape, "$.date"];

        foreach ((string field, string invalid, string valid) in new[]
        {
            ("repository", """{"url":42}""", "\"github:example/repo\""),
            ("date", "20240203e0", "20240203123456"),
        })
        {
            foreach (string last in new[] { valid, "null", "\"\"" })
            {
                yield return [$"{field}-duplicate-invalid-first:{last}",
                    $"\"{field}\":{invalid},\"{field}\":{last}", $"$.{field}"];
                yield return [$"{field}-duplicate-invalid-last:{last}",
                    $"\"{field}\":{last},\"{field}\":{invalid}", $"$.{field}"];
            }
            yield return [$"{field}-top-casing-first",
                $"\"{field.ToUpperInvariant()}\":{invalid},\"{field}\":null", $"$.{field.ToUpperInvariant()}"];
            yield return [$"{field}-top-casing-last",
                $"\"{field}\":null,\"{field.ToUpperInvariant()}\":{invalid}", $"$.{field.ToUpperInvariant()}"];
        }

        yield return ["malformed-json", "", "$.repository"];
    }

    [Theory]
    [MemberData(nameof(MetadataIdentityAndArchiveFailures))]
    public async Task ImportOrInstallAsync_InvalidIdentityAndArchive_PreservesPriorAndNeighbour(
        string fixture,
        bool import,
        PackageInstallErrorCode expectedCode,
        PackageInstallStage expectedStage,
        string? expectedJsonPath)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);
        PackageReference prior = new PackageReference("metadata.prior", "1.0.0");
        PackageReference neighbour = new PackageReference("metadata.neighbour", "1.0.0");
        await SeedPreservationPackageAsync(cache, prior, ct);
        await SeedPreservationPackageAsync(cache, neighbour, ct);
        PackageTreeSnapshot before = SnapshotTree(_tempDir, excludeCoordination: true);
        using MemoryStream archive = CreateMetadataFailureArchive(fixture);
        byte[] originalBytes = archive.ToArray();
        InstallCacheOptions options = new()
        {
            Unmodified = true,
            OverwriteExisting = true,
            VerifyChecksum = true,
            ExpectedSha256Sum = Sha256(originalBytes)
        };

        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
            () => import
                ? cache.ImportAsync(archive, options, ct)
                : cache.InstallAsync(prior, archive, options, ct));

        exception.ErrorCode.ShouldBe(expectedCode);
        exception.Stage.ShouldBe(expectedStage);
        if (expectedJsonPath is not null)
            exception.InnerException.ShouldBeOfType<JsonException>().Path.ShouldBe(expectedJsonPath);
        archive.CanRead.ShouldBeTrue();
        archive.ToArray().ShouldBe(originalBytes);
        AssertTreeSnapshot(_tempDir, before, excludeCoordination: true);
        AssertOperationArtifactsClean(_tempDir);
        (await cache.GetPackageAsync(prior, ct)).ShouldNotBeNull();
        (await cache.GetPackageAsync(neighbour, ct)).ShouldNotBeNull();
        AssertTreeSnapshot(_tempDir, before, excludeCoordination: true);
    }

    public static IEnumerable<object?[]> MetadataIdentityAndArchiveFailures()
    {
        foreach (bool import in new[] { false, true })
        {
            foreach (string fixture in new[] { "empty-name", "null-name", "unsafe-name", "blank-version", "range-version" })
                yield return [fixture, import, PackageInstallErrorCode.InvalidPackageIdentity, PackageInstallStage.IdentityValidation, null];
            foreach (string fixture in new[] { "missing-name", "missing-version" })
                yield return [fixture, import, PackageInstallErrorCode.InvalidArchive, PackageInstallStage.ArchiveValidation, "$"];
            yield return ["malformed-repository", import, PackageInstallErrorCode.InvalidArchive, PackageInstallStage.ArchiveValidation, "$.repository"];
            yield return ["malformed-date", import, PackageInstallErrorCode.InvalidArchive, PackageInstallStage.ArchiveValidation, "$.date"];
            yield return ["malformed-json", import, PackageInstallErrorCode.InvalidArchive, PackageInstallStage.ArchiveValidation, "$.repository"];
            foreach (string fixture in new[] { "missing-manifest", "traversal", "link", "truncated", "not-gzip" })
                yield return [fixture, import, PackageInstallErrorCode.InvalidArchive, PackageInstallStage.ArchiveValidation, null];
        }

        foreach (string fixture in new[] { "mismatched-name", "mismatched-version" })
            yield return [fixture, false, PackageInstallErrorCode.InvalidPackageIdentity, PackageInstallStage.IdentityValidation, null];
    }

    private async Task AssertMetadataArchiveRoundTripAsync(
        string manifestJson,
        PackageReference reference,
        NpmRepository? repository,
        string? date,
        string author,
        bool import,
        bool unmodified)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MemoryStream archive = CreatePreservationTarball(manifestJson);
        byte[] archiveBytes = archive.ToArray();
        string expectedSha256 = Sha256(archiveBytes);
        PackageTreeSnapshot original = ReadArchiveTree(archiveBytes);
        InstallCacheOptions options = new()
        {
            Unmodified = unmodified,
            VerifyChecksum = true,
            ExpectedSha256Sum = expectedSha256
        };
        Directory.Exists(_tempDir).ShouldBeFalse("each matrix/control case must start cold");
        using (DiskPackageCache cache = new DiskPackageCache(_tempDir))
        {
            PackageRecord record = import
                ? await cache.ImportAsync(archive, options, ct)
                : await cache.InstallAsync(reference, archive, options, ct);
            AssertMetadataProjection(record, reference, repository, date, author);
            record.SizeBytes.ShouldBe(original.Files.Values.Sum(bytes => bytes.LongLength));
            AssertTreeSnapshot(record.DirectoryPath, original);
            archive.CanRead.ShouldBeTrue();
            archive.Position.ShouldBe(archive.Length);
            archive.ToArray().ShouldBe(archiveBytes);
            Sha256(archive.ToArray()).ShouldBe(expectedSha256);
            CacheMetadata metadata = await cache.GetMetadataAsync(ct);
            metadata.Packages.Count.ShouldBe(1);
            metadata.Packages[PackageCacheKey.Create(reference).MetadataKey].ArchiveSha256.ShouldBe(expectedSha256);
            AssertOperationArtifactsClean(_tempDir);
        }

        using DiskPackageCache reopened = new DiskPackageCache(_tempDir);
        PackageRecord? read = await reopened.GetPackageAsync(reference, ct);
        read.ShouldNotBeNull();
        AssertMetadataProjection(read, reference, repository, date, author);
        (await reopened.IsInstalledAsync(reference, ct)).ShouldBeTrue();
        (await ((IHardenedPackageCache)reopened).InspectAsync(reference, ct)).State
            .ShouldBe(HardenedPackageCacheState.Valid);
        reopened.GetPackageContentPath(reference).ShouldBe(read.ContentPath);
        PackageManifest? manifest = await reopened.ReadManifestAsync(reference, ct);
        manifest.ShouldNotBeNull();
        manifest.Repository.ShouldBe(repository);
        manifest.Date.ShouldBe(date);
        manifest.Author.ShouldBe(author);
        IReadOnlyList<PackageRecord> listed = await reopened.ListPackagesAsync(ct: ct);
        listed.Count.ShouldBe(1);
        AssertMetadataProjection(listed[0], reference, repository, date, author);
        IReadOnlyList<PackageRecord> summaries = await reopened.ListPackageSummariesAsync(ct: ct);
        summaries.Count.ShouldBe(1);
        AssertMetadataProjection(summaries[0], reference, repository, date, author);
        (await reopened.GetFileContentAsync(reference, "patient.json", ct))
            .ShouldBe(Encoding.UTF8.GetString(original.Files["package/patient.json"]));
        (await reopened.GetFileContentAsync(reference, "patient.xml", ct))
            .ShouldBe(Encoding.UTF8.GetString(original.Files["package/patient.xml"]));
        (await reopened.GetMetadataAsync(ct)).Packages[PackageCacheKey.Create(reference).MetadataKey]
            .ArchiveSha256.ShouldBe(expectedSha256);
        AssertTreeSnapshot(read.DirectoryPath, original);
        AssertOperationArtifactsClean(_tempDir);
    }

    private void AssertMetadataProjection(
        PackageRecord record,
        PackageReference reference,
        NpmRepository? repository,
        string? date,
        string author)
    {
        record.Reference.ShouldBe(reference);
        record.Manifest.Name.ShouldBe(reference.Name);
        record.Manifest.Version.ShouldBe(reference.Version);
        record.Manifest.Repository.ShouldBe(repository);
        record.Manifest.Date.ShouldBe(date);
        record.Manifest.Author.ShouldBe(author);
        record.DirectoryPath.ShouldBe(Path.Combine(_tempDir, $"{reference.Name}#{reference.Version}"));
        record.ContentPath.ShouldBe(Path.Combine(record.DirectoryPath, "package"));
    }

    private static string CreatePreservationManifest(string identity, string properties) =>
        "{\"name\":\"" + identity + "\",\"version\":\"1.0.0\"," + properties +
        ",\"fhirVersions\":[\"4.0.1\"],\"unknown-field\":{\"retained\":[1,true,null,{\"nested\":\"opaque\"}]}}";

    private static MemoryStream CreatePreservationTarball(string manifestJson) =>
        ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package/package.json", Encoding.UTF8.GetBytes(manifestJson)),
            ArbitraryTarBuilder.File("package/patient.json", """{"resourceType":"Patient","id":"json-patient","active":true}""" + "\r\n"),
            ArbitraryTarBuilder.File("package/patient.xml", """<Patient xmlns="http://hl7.org/fhir"><id value="xml-patient"/></Patient>""" + "\n"),
            ArbitraryTarBuilder.File("package/.index.json", """
                {"index-version":2,"files":[
                  {"filename":"patient.json","resourceType":"Patient","id":"json-patient"},
                  {"filename":"patient.xml","resourceType":"Patient","id":"xml-patient"}],
                 "opaque-index-extension":{"keep":true}}
                """),
            ArbitraryTarBuilder.File("README.txt", "distributed root ancillary\r\n"),
            ArbitraryTarBuilder.File("ancillary/payload.bin", [0, 128, 255, 13, 10, 0]),
            ArbitraryTarBuilder.File("ancillary/source.yaml", "opaque: [true, null]\n"),
            ArbitraryTarBuilder.Directory("package/empty/"),
            ArbitraryTarBuilder.Directory("ancillary/empty/"));

    private static async Task SeedPreservationPackageAsync(
        DiskPackageCache cache,
        PackageReference reference,
        CancellationToken ct)
    {
        using MemoryStream archive = CreatePreservationTarball(CreatePreservationManifest(
            reference.Name, "\"repository\":{\"type\":\"git\",\"url\":\"baseline\"},\"date\":\"baseline\",\"author\":\"prior\""));
        await cache.InstallAsync(
            reference,
            archive,
            new InstallCacheOptions
            {
                Unmodified = true,
                VerifyChecksum = true,
                ExpectedSha256Sum = Sha256(archive.ToArray())
            },
            ct);
    }

    private static MemoryStream CreateMetadataFailureArchive(string fixture)
    {
        const string valid = """
            {"name":"metadata.prior","version":"1.0.0","repository":"github:example/repo","date":20240203123456}
            """;
        string manifest = fixture switch
        {
            "empty-name" => valid.Replace("\"metadata.prior\"", "\"\"", StringComparison.Ordinal),
            "null-name" => valid.Replace("\"metadata.prior\"", "null", StringComparison.Ordinal),
            "unsafe-name" => valid.Replace("\"metadata.prior\"", "\"../outside\"", StringComparison.Ordinal),
            "blank-version" => valid.Replace("\"1.0.0\"", "\" \"", StringComparison.Ordinal),
            "range-version" => valid.Replace("\"1.0.0\"", "\"1.*\"", StringComparison.Ordinal),
            "missing-name" => """{"version":"1.0.0","repository":"raw","date":20240203}""",
            "missing-version" => """{"name":"metadata.prior","repository":{},"date":20240203}""",
            "mismatched-name" => valid.Replace("\"metadata.prior\"", "\"metadata.other\"", StringComparison.Ordinal),
            "mismatched-version" => valid.Replace("\"1.0.0\"", "\"2.0.0\"", StringComparison.Ordinal),
            "malformed-repository" => """{"name":"metadata.prior","version":"1.0.0","repository":{"url":42,"url":null}}""",
            "malformed-date" => """{"name":"metadata.prior","version":"1.0.0","date":20240203e0,"date":null}""",
            "malformed-json" => """{"name":"metadata.prior","version":"1.0.0","repository":{"url":}}""",
            _ => valid,
        };
        switch (fixture)
        {
            case "missing-manifest":
                return ArbitraryTarBuilder.Create(ArbitraryTarBuilder.File("package/readme.txt", "missing manifest"));
            case "traversal":
                return ArbitraryTarBuilder.Create(
                    ArbitraryTarBuilder.File("package/package.json", manifest),
                    ArbitraryTarBuilder.File("../outside.txt", "unsafe"));
            case "link":
                return ArbitraryTarBuilder.Create(
                    ArbitraryTarBuilder.File("package/package.json", manifest),
                    ArbitraryTarBuilder.SymbolicLink("package/link", "package.json"));
            case "not-gzip":
                return new MemoryStream(Encoding.UTF8.GetBytes("not a gzip archive"));
            case "truncated":
                using (MemoryStream archive = CreatePreservationTarball(manifest))
                {
                    byte[] bytes = archive.ToArray();
                    return new MemoryStream(bytes[..(bytes.Length / 2)]);
                }
            default:
                return CreatePreservationTarball(manifest);
        }
    }

    private sealed record PackageTreeSnapshot(
        IReadOnlyDictionary<string, byte[]> Files,
        IReadOnlySet<string> Directories);

    private static PackageTreeSnapshot ReadArchiveTree(byte[] archiveBytes)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        HashSet<string> directories = new(StringComparer.Ordinal);
        using MemoryStream archive = new MemoryStream(archiveBytes);
        using GZipStream gzip = new GZipStream(archive, CompressionMode.Decompress);
        using TarReader reader = new TarReader(gzip);
        while (reader.GetNextEntry() is TarEntry entry)
        {
            string path = entry.Name.TrimEnd('/');
            string[] segments = path.Split('/');
            int directoryDepth = entry.EntryType == TarEntryType.Directory ? segments.Length : segments.Length - 1;
            for (int depth = 1; depth <= directoryDepth; depth++)
                directories.Add(string.Join("/", segments.Take(depth)));
            if (entry.EntryType == TarEntryType.Directory)
                continue;
            entry.EntryType.ShouldBe(TarEntryType.RegularFile);
            using MemoryStream content = new MemoryStream();
            entry.DataStream!.CopyTo(content);
            files.Add(path, content.ToArray());
        }

        return new PackageTreeSnapshot(files, directories);
    }

    private static PackageTreeSnapshot SnapshotTree(string root, bool excludeCoordination = false)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        HashSet<string> directories = new(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!excludeCoordination || !relative.StartsWith(".fhirpkg/", StringComparison.Ordinal))
                files.Add(relative, File.ReadAllBytes(path));
        }
        foreach (string path in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!excludeCoordination || (relative != ".fhirpkg" && !relative.StartsWith(".fhirpkg/", StringComparison.Ordinal)))
                directories.Add(relative);
        }

        return new PackageTreeSnapshot(files, directories);
    }

    private static void AssertTreeSnapshot(
        string root,
        PackageTreeSnapshot expected,
        bool excludeCoordination = false)
    {
        PackageTreeSnapshot actual = SnapshotTree(root, excludeCoordination);
        actual.Files.Keys.OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(expected.Files.Keys.OrderBy(path => path, StringComparer.Ordinal));
        actual.Directories.OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(expected.Directories.OrderBy(path => path, StringComparer.Ordinal));
        foreach ((string path, byte[] bytes) in expected.Files)
            actual.Files[path].ShouldBe(bytes, path);
    }

    private static void AssertOperationArtifactsClean(string cachePath)
    {
        foreach (string name in new[] { "staging", "transactions", "backup", "quarantine" })
        {
            string path = Path.Combine(cachePath, ".fhirpkg", name);
            if (Directory.Exists(path))
                Directory.GetFileSystemEntries(path).ShouldBeEmpty();
        }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static MemoryStream CreateTestTarball(string packageJsonContent)
        => CreateTarball(("package/package.json", packageJsonContent));

    private static MemoryStream CreateTarball(
        params (string Name, string Content)[] entries)
    {
        MemoryStream memStream = new MemoryStream();
        using (GZipStream gzipStream = new GZipStream(memStream, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter tarWriter = new TarWriter(gzipStream, leaveOpen: true))
        {
            foreach ((string name, string content) in entries)
            {
                PaxTarEntry entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
                };
                tarWriter.WriteEntry(entry);
            }
        }

        memStream.Position = 0;
        return memStream;
    }

    private static async Task<PackageManifest?> ReadManifestAsync(string contentPath)
    {
        string json = await File.ReadAllTextAsync(
            Path.Combine(contentPath, "package.json"),
            TestContext.Current.CancellationToken);
        return System.Text.Json.JsonSerializer.Deserialize<PackageManifest>(json);
    }
}
