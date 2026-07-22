// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.Models;
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
    public async Task IsInstalledAsync_UnsafeIdentityIsRejectedBeforePathAccess()
    {
        using DiskPackageCache cache = new DiskPackageCache(_tempDir);

        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
            () => cache.IsInstalledAsync(
                new PackageReference("../outside", "1.0.0"),
                TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPackageIdentity);
    }

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
