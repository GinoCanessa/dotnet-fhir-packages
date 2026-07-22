// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Tests.Support;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Cache;

public class PackageArchiveValidationTests : IDisposable
{
    private const string Manifest =
        """{"name":"example.package","version":"1.0.0"}""";

    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        $"archive-validation-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task InstallAsync_StandardLayout_IsAccepted()
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.Directory("package/"),
            ArbitraryTarBuilder.File("package/package.json", Manifest),
            ArbitraryTarBuilder.File("package/resource.json", "{}"));

        PackageRecord record = await InstallSucceedsAsync(archive);

        File.Exists(Path.Combine(record.ContentPath, "package.json")).ShouldBeTrue();
        File.Exists(Path.Combine(record.ContentPath, "resource.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task InstallAsync_LegacyRootLayout_IsValidatedThenNormalized()
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package.json", Manifest),
            ArbitraryTarBuilder.File("README.md", "readme"),
            ArbitraryTarBuilder.File("resources/example.json", "{}"));

        PackageRecord record = await InstallSucceedsAsync(archive);

        Path.GetFileName(record.ContentPath).ShouldBe("package");
        File.Exists(Path.Combine(record.ContentPath, "package.json")).ShouldBeTrue();
        File.Exists(Path.Combine(record.ContentPath, "README.md")).ShouldBeTrue();
        File.Exists(Path.Combine(
            record.ContentPath,
            "resources",
            "example.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task InstallAsync_BackslashStandardLayout_IsAcceptedPortably()
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package\\package.json", Manifest),
            ArbitraryTarBuilder.File("package\\nested\\file.txt", "content"));

        PackageRecord record = await InstallSucceedsAsync(archive);

        File.Exists(Path.Combine(record.ContentPath, "package.json")).ShouldBeTrue();
        File.Exists(Path.Combine(
            record.ContentPath,
            "nested",
            "file.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task InstallAsync_ExplicitDirectoryAfterImplicitParent_IsAccepted()
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package/sub/file.txt", "content"),
            ArbitraryTarBuilder.Directory("package/sub/"),
            ArbitraryTarBuilder.File("package/package.json", Manifest));

        PackageRecord record = await InstallSucceedsAsync(archive);

        File.Exists(Path.Combine(
            record.ContentPath,
            "sub",
            "file.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task InstallAsync_DuplicateNormalizedPath_IsRejectedBeforePromotion()
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package/package.json", Manifest),
            ArbitraryTarBuilder.File("package/file.txt", "first"),
            ArbitraryTarBuilder.File("package/file.txt", "second"));

        PackageInstallException exception = await InstallFailsAsync(archive);

        AssertArchiveFailure(exception);
    }

    [Fact]
    public async Task InstallAsync_CaseCollision_IsRejectedBeforePromotion()
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package/package.json", Manifest),
            ArbitraryTarBuilder.File("package/Foo.txt", "first"),
            ArbitraryTarBuilder.File("package/foo.txt", "second"));

        PackageInstallException exception = await InstallFailsAsync(archive);

        AssertArchiveFailure(exception);
    }

    [Fact]
    public async Task InstallAsync_UnicodeNormalizationCollision_IsRejectedBeforePromotion()
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package/package.json", Manifest),
            ArbitraryTarBuilder.File("package/café.txt", "first"),
            ArbitraryTarBuilder.File("package/cafe\u0301.txt", "second"));

        PackageInstallException exception = await InstallFailsAsync(archive);

        AssertArchiveFailure(exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallAsync_AncestorFileDirectoryConflict_IsRejectedBeforePromotion(
        bool descendantFirst)
    {
        ArbitraryTarEntry ancestor = ArbitraryTarBuilder.File(
            "package/conflict",
            "file");
        ArbitraryTarEntry descendant = ArbitraryTarBuilder.File(
            "package/conflict/nested.txt",
            "nested");
        ArbitraryTarEntry[] conflictingEntries = descendantFirst
            ? [descendant, ancestor]
            : [ancestor, descendant];
        ArbitraryTarEntry[] entries =
        [
            ArbitraryTarBuilder.File("package/package.json", Manifest),
            .. conflictingEntries
        ];
        using MemoryStream archive = ArbitraryTarBuilder.Create(entries);

        PackageInstallException exception = await InstallFailsAsync(archive);

        AssertArchiveFailure(exception);
    }

    [Fact]
    public async Task InstallAsync_ImplicitParentCaseCollision_IsRejectedBeforePromotion()
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package/package.json", Manifest),
            ArbitraryTarBuilder.File("package/Foo/a.txt", "first"),
            ArbitraryTarBuilder.File("package/foo/b.txt", "second"));

        PackageInstallException exception = await InstallFailsAsync(archive);

        AssertArchiveFailure(exception);
    }

    [Fact]
    public async Task InstallAsync_DirectFileDirectoryConflict_IsRejectedBeforePromotion()
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package/package.json", Manifest),
            ArbitraryTarBuilder.Directory("package/conflict/"),
            ArbitraryTarBuilder.File("package/conflict", "file"));

        PackageInstallException exception = await InstallFailsAsync(archive);

        AssertArchiveFailure(exception);
    }

    [Fact]
    public async Task InstallAsync_ComponentOverPortableLimit_IsArchivePathFailure()
    {
        string oversizedComponent = new string(
            'a',
            PortableArchivePath.MaximumComponentLength + 1);
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package/package.json", Manifest),
            ArbitraryTarBuilder.File(
                $"package/{oversizedComponent}",
                "content"));

        PackageInstallException exception = await InstallFailsAsync(archive);

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.ArchivePathLengthLimitExceeded);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
    }

    [Fact]
    public void Inventory_DeepUniqueEntries_StopAtDerivedNodeBudget()
    {
        PackageInstallLimits limits = new PackageInstallLimits();
        PackageArchiveInventory inventory = new PackageArchiveInventory(limits);
        inventory.NodeBudget.ShouldBe(100_032);
        inventory.PathByteBudget.ShouldBe(64L * 1024 * 1024);
        string sharedSuffix = string.Join(
            '/',
            Enumerable.Range(0, 30).Select(index => $"d{index}"));

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () =>
            {
                for (int index = 0;
                    index < limits.MaxArchiveEntries;
                    index++)
                {
                    PortableArchivePath path = PortableArchivePath.Create(
                        $"root{index}/{sharedSuffix}/file",
                        isDirectory: false);
                    inventory.Add(
                        path,
                        PackageArchiveEntryKind.RegularFile,
                        directive: null);
                }
            });

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.ArchiveEntryCountLimitExceeded);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
        inventory.NodeCount.ShouldBe(inventory.NodeBudget);
        inventory.NodeCount.ShouldBeLessThan(
            (long)limits.MaxArchiveEntries * limits.MaxArchiveDepth);
    }

    [Fact]
    public void Inventory_PathBytesStopAtDerivedPathBudget()
    {
        PackageInstallLimits limits = new PackageInstallLimits
        {
            MaxCompressedBytes = 1,
            MaxExpandedBytes = 1,
            MaxEntryBytes = 1,
            MaxArchiveEntries = 100,
            MaxArchivePathLength = 255,
            MaxArchiveDepth = 1
        };
        PackageArchiveInventory inventory = new PackageArchiveInventory(limits);
        for (int index = 0; index < 4; index++)
        {
            string component = string.Concat(
                index.ToString("D3"),
                new string('a', 252));
            inventory.Add(
                PortableArchivePath.Create(component, isDirectory: false),
                PackageArchiveEntryKind.RegularFile,
                directive: null);
        }

        string overBudgetComponent = string.Concat(
            "004",
            new string('a', 252));
        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => inventory.Add(
                PortableArchivePath.Create(
                    overBudgetComponent,
                    isDirectory: false),
                PackageArchiveEntryKind.RegularFile,
                directive: null));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.ArchivePathLengthLimitExceeded);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
        inventory.PathBytes.ShouldBe(inventory.PathByteBudget);
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    public async Task InstallAsync_LinkEntry_IsRejectedBeforePromotion(
        TarEntryType entryType)
    {
        ArbitraryTarEntry link = entryType == TarEntryType.SymbolicLink
            ? ArbitraryTarBuilder.SymbolicLink(
                "package/link",
                "package/package.json")
            : ArbitraryTarBuilder.HardLink(
                "package/link",
                "package/package.json");
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package/package.json", Manifest),
            link);

        PackageInstallException exception = await InstallFailsAsync(archive);

        AssertArchiveFailure(exception);
    }

    [Theory]
    [InlineData(InvalidLayout.MixedStandardRoot)]
    [InlineData(InvalidLayout.LegacyWithPackageTree)]
    [InlineData(InvalidLayout.WrapperRoot)]
    [InlineData(InvalidLayout.SecondManifest)]
    [InlineData(InvalidLayout.MissingManifest)]
    [InlineData(InvalidLayout.ManifestWrongCase)]
    public async Task InstallAsync_AmbiguousOrInvalidLayout_IsRejectedBeforePromotion(
        InvalidLayout invalidLayout)
    {
        using MemoryStream archive = CreateInvalidLayoutArchive(invalidLayout);

        PackageInstallException exception = await InstallFailsAsync(archive);

        AssertArchiveFailure(exception);
    }

    [Theory]
    [InlineData("other.package", "1.0.0")]
    [InlineData("example.package", "2.0.0")]
    public async Task InstallAsync_ManifestIdentityMismatch_IsRejectedBeforePromotion(
        string name,
        string version)
    {
        string manifest =
            $$"""{"name":"{{name}}","version":"{{version}}"}""";
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package/package.json", manifest));

        PackageInstallException exception = await InstallFailsAsync(archive);

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPackageIdentity);
        exception.Stage.ShouldBe(PackageInstallStage.IdentityValidation);
    }

    private async Task<PackageRecord> InstallSucceedsAsync(
        MemoryStream archive)
    {
        string cacheRoot = Path.Combine(
            _testRoot,
            Guid.NewGuid().ToString("N"));
        using DiskPackageCache cache = CreateCache(cacheRoot);
        return await cache.InstallAsync(
            new PackageReference("example.package", "1.0.0"),
            archive,
            new InstallCacheOptions { VerifyChecksum = false },
            TestContext.Current.CancellationToken);
    }

    private async Task<PackageInstallException> InstallFailsAsync(
        MemoryStream archive)
    {
        string cacheRoot = Path.Combine(
            _testRoot,
            Guid.NewGuid().ToString("N"));
        using DiskPackageCache cache = CreateCache(cacheRoot);
        PackageReference reference = new PackageReference(
            "example.package",
            "1.0.0");

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => cache.InstallAsync(
                    reference,
                    archive,
                    new InstallCacheOptions { VerifyChecksum = false },
                    TestContext.Current.CancellationToken));

        cache.GetPackageContentPath(reference).ShouldBeNull();
        AssertNoStagingOperations(cacheRoot);
        return exception;
    }

    private static DiskPackageCache CreateCache(string cacheRoot) =>
        new DiskPackageCache(
            cacheRoot,
            logger: null,
            timeProvider: null,
            new PackageInstallLimits());

    private static MemoryStream CreateInvalidLayoutArchive(
        InvalidLayout invalidLayout) =>
        invalidLayout switch
        {
            InvalidLayout.MixedStandardRoot => ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.File("package/package.json", Manifest),
                ArbitraryTarBuilder.File("README.md", "mixed")),
            InvalidLayout.LegacyWithPackageTree => ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.File("package.json", Manifest),
                ArbitraryTarBuilder.File("package/file.txt", "mixed")),
            InvalidLayout.WrapperRoot => ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.File("wrapper/package.json", Manifest)),
            InvalidLayout.SecondManifest => ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.File("package/package.json", Manifest),
                ArbitraryTarBuilder.File(
                    "package/nested/package.json",
                    Manifest)),
            InvalidLayout.MissingManifest => ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.File("package/file.txt", "missing")),
            InvalidLayout.ManifestWrongCase => ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.File("package/Package.json", Manifest)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(invalidLayout),
                invalidLayout,
                null)
        };

    private static void AssertArchiveFailure(
        PackageInstallException exception)
    {
        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidArchive);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
    }

    private static void AssertNoStagingOperations(string cacheRoot)
    {
        string stagingRoot = Path.Combine(cacheRoot, ".fhirpkg", "staging");
        if (!Directory.Exists(stagingRoot))
            return;

        Directory.EnumerateFileSystemEntries(stagingRoot).ShouldBeEmpty();
    }

    public enum InvalidLayout
    {
        MixedStandardRoot,
        LegacyWithPackageTree,
        WrapperRoot,
        SecondManifest,
        MissingManifest,
        ManifestWrongCase
    }
}
