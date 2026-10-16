// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Tests.Support;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Cache;

[Collection("EnvironmentVariable")]
public class TarballExtractorLimitTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        $"tarball-limits-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ExtractAsync_ExactLimits_ReturnsMeasuredMetrics()
    {
        byte[] content = Encoding.UTF8.GetBytes("data");
        const string entryName = "a/b.txt";
        using MemoryStream archive = CreateArchive((entryName, content));
        PackageInstallLimits limits = CreateLimits();
        limits.MaxExpandedBytes = content.Length;
        limits.MaxEntryBytes = content.Length;
        limits.MaxArchiveEntries = 1;
        limits.MaxArchivePathLength = entryName.Length;
        limits.MaxArchiveDepth = 2;

        ArchiveExtractionMetrics metrics = await TarballExtractor.ExtractAsync(
            archive,
            GetDestination(),
            limits,
            "example.package#1.0.0",
            TestContext.Current.CancellationToken);

        metrics.ExpandedBytes.ShouldBe(content.Length);
        metrics.LargestEntryBytes.ShouldBe(content.Length);
        metrics.EntryCount.ShouldBe(1);
        metrics.MaximumPathLength.ShouldBe(entryName.Length);
        metrics.MaximumDepth.ShouldBe(2);
        metrics.MetadataBytes.ShouldBeGreaterThan(0);
        metrics.MetadataEntryCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExtractAsync_UnicodeNamesAndBytes_ArePreserved(
        bool explicitParent,
        bool useBackslashes)
    {
        const string directoryName = "soUrce-e\u0301";
        const string emptyDirectoryName = "empty-e\u0301";
        const string fileName = "cafe\u0301-\u0394-\u65e5\U0001f600.bin";
        const string otherFileName = "résumé-\u0958.txt";
        byte[] content = [0, 255, 128, 13, 10, 1, 2, 3];
        byte[] otherContent = Encoding.UTF8.GetBytes("Unicode café \u65e5\U0001f600");
        List<ArbitraryTarEntry> entries =
        [
            ArbitraryTarBuilder.Directory($"package/{emptyDirectoryName}/"),
            ArbitraryTarBuilder.File($"package/{directoryName}/{fileName}", content),
            ArbitraryTarBuilder.File($"package/{otherFileName}", otherContent)
        ];
        if (explicitParent)
            entries.Insert(0, ArbitraryTarBuilder.Directory($"package/{directoryName}/"));

        ArbitraryTarEntry[] archiveEntries = entries.Select(entry => useBackslashes
            ? entry with { Name = entry.Name.Replace('/', '\\') }
            : entry).ToArray();
        using MemoryStream archive = ArbitraryTarBuilder.Create(archiveEntries);
        string destination = GetDestination();

        await TarballExtractor.ExtractAsync(
            archive,
            destination,
            CreateLimits(),
            directive: null,
            TestContext.Current.CancellationToken);

        Dictionary<string, byte[]> expectedFiles = new Dictionary<string, byte[]>(
            StringComparer.Ordinal)
        {
            [$"package/{directoryName}/{fileName}"] = content,
            [$"package/{otherFileName}"] = otherContent
        };
        string[] expectedDirectories =
        [
            "package",
            $"package/{directoryName}",
            $"package/{emptyDirectoryName}"
        ];
        await AssertNativeTreeAsync(destination, expectedFiles, expectedDirectories);
    }

    [Theory]
    [InlineData("a", 255, "", 255)]
    [InlineData("é", 127, "a", 128)]
    [InlineData("e\u0301", 85, "", 170)]
    public async Task ExtractAsync_ExactComponentAtPortableLimit_WritesUnchanged(
        string repeatedText,
        int repeatCount,
        string suffix,
        int expectedUnits)
    {
        string component = string.Concat(Enumerable.Repeat(repeatedText, repeatCount)) + suffix;
        component.Length.ShouldBe(expectedUnits);
        Encoding.UTF8.GetByteCount(component).ShouldBe(255);
        string entryName = $"folder/{component}";
        byte[] content = [0, 128, 255];
        using MemoryStream archive = CreateArchive((entryName, content));
        PackageInstallLimits limits = CreateLimits();
        limits.MaxArchivePathLength = entryName.Length;
        string destination = GetDestination();

        ArchiveExtractionMetrics metrics = await TarballExtractor.ExtractAsync(
            archive,
            destination,
            limits,
            directive: null,
            TestContext.Current.CancellationToken);

        await AssertNativeTreeAsync(
            destination,
            new Dictionary<string, byte[]> { [entryName] = content },
            ["folder"]);
        metrics.MaximumPathLength.ShouldBe(entryName.Length);
    }

    [Theory]
    [InlineData("a", 256)]
    [InlineData("é", 128)]
    public async Task ExtractAsync_ComponentOneOverPortableLimit_RejectsBeforeTargetWrite(
        string repeatedText,
        int repeatCount)
    {
        string component = string.Concat(Enumerable.Repeat(repeatedText, repeatCount));
        Encoding.UTF8.GetByteCount(component).ShouldBe(256);
        string entryName = $"folder/{component}";
        using MemoryStream archive = CreateArchive((entryName, [1]));
        PackageInstallLimits limits = CreateLimits();
        limits.MaxArchivePathLength = entryName.Length;

        await AssertPathLengthFailureBeforeWriteAsync(archive, limits);
    }

    [Theory]
    [InlineData("e\u0301", 85, "a", 171, 256)]
    [InlineData("e\u0301", 127, "", 254, 381)]
    [InlineData("a", 253, "\u03b9\u0308\u0301", 256, 259)]
    public async Task ExtractAsync_ExactComponentOverLimitWithCanonicalWithinLimit_RejectsBeforeTargetWrite(
        string repeatedText,
        int repeatCount,
        string suffix,
        int expectedUnits,
        int expectedBytes)
    {
        string component = string.Concat(Enumerable.Repeat(repeatedText, repeatCount)) + suffix;
        component.Length.ShouldBe(expectedUnits);
        Encoding.UTF8.GetByteCount(component).ShouldBe(expectedBytes);
        string entryName = $"folder/{component}/file.txt";
        PortableArchivePath path = PortableArchivePath.Create(entryName, isDirectory: false);
        path.Segments[1].Length.ShouldBeLessThanOrEqualTo(255);
        Encoding.UTF8.GetByteCount(path.Segments[1]).ShouldBeLessThanOrEqualTo(255);
        using MemoryStream archive = CreateArchive((entryName, [1]));
        PackageInstallLimits limits = CreateLimits();
        limits.MaxArchivePathLength = path.ExactSpelling.Length;

        await AssertPathLengthFailureBeforeWriteAsync(archive, limits);
    }

    [Fact]
    public async Task ExtractAsync_ExactDirectoryComponentOneByteOverLimit_RejectsBeforeTargetWrite()
    {
        string component = string.Concat(Enumerable.Repeat("e\u0301", 85)) + "a";
        string entryName = $"folder/{component}/";
        PortableArchivePath path = PortableArchivePath.Create(entryName, isDirectory: true);
        Encoding.UTF8.GetByteCount(path.ExactSegments[1]).ShouldBe(256);
        Encoding.UTF8.GetByteCount(path.Segments[1]).ShouldBeLessThanOrEqualTo(255);
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.Directory(entryName));
        PackageInstallLimits limits = CreateLimits();
        limits.MaxArchivePathLength = path.ExactSpelling.Length;

        await AssertPathLengthFailureBeforeWriteAsync(archive, limits);
    }

    [Theory]
    [InlineData("folder/cafe\u0301.txt")]
    [InlineData("folder/\u0958.txt")]
    public async Task ExtractAsync_DifferentSpellingLengths_AtLimitReportsGreaterLength(
        string entryName)
    {
        string canonicalPath = entryName.Normalize(NormalizationForm.FormC);
        int maximumLength = Math.Max(canonicalPath.Length, entryName.Length);
        maximumLength.ShouldBe(Math.Min(canonicalPath.Length, entryName.Length) + 1);
        using MemoryStream archive = CreateArchive(
            (entryName, [1, 2, 3]),
            ("a.txt", [4]));
        PackageInstallLimits limits = CreateLimits();
        limits.MaxArchivePathLength = maximumLength;
        string destination = GetDestination();

        ArchiveExtractionMetrics metrics = await TarballExtractor.ExtractAsync(
            archive,
            destination,
            limits,
            directive: null,
            TestContext.Current.CancellationToken);

        metrics.MaximumPathLength.ShouldBe(maximumLength);
        metrics.EntryCount.ShouldBe(2);
        metrics.ExpandedBytes.ShouldBe(4);
        metrics.LargestEntryBytes.ShouldBe(3);
        await AssertNativeTreeAsync(
            destination,
            new Dictionary<string, byte[]>
            {
                [entryName] = [1, 2, 3],
                ["a.txt"] = [4],
            },
            ["folder"]);
    }

    [Theory]
    [InlineData("folder/cafe\u0301.txt")]
    [InlineData("folder/\u0958.txt")]
    public async Task ExtractAsync_OneSpellingOverConfiguredLimit_RejectsBeforeTargetWrite(
        string entryName)
    {
        string canonicalPath = entryName.Normalize(NormalizationForm.FormC);
        int minimumLength = Math.Min(canonicalPath.Length, entryName.Length);
        minimumLength.ShouldBe(Math.Max(canonicalPath.Length, entryName.Length) - 1);
        using MemoryStream archive = CreateArchive((entryName, [1]));
        PackageInstallLimits limits = CreateLimits();
        limits.MaxArchivePathLength = minimumLength;

        await AssertPathLengthFailureBeforeWriteAsync(archive, limits);
    }

    [Fact]
    public async Task ExtractAsync_ExpandedBytesOneOverLimit_UsesExpandedErrorCode()
    {
        using MemoryStream archive = CreateArchive(
            ("a.txt", new byte[3]),
            ("b.txt", new byte[3]));
        PackageInstallLimits limits = CreateLimits();
        limits.MaxExpandedBytes = 5;
        limits.MaxEntryBytes = 3;

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => TarballExtractor.ExtractAsync(
                    archive,
                    GetDestination(),
                    limits,
                    directive: null,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.ExpandedSizeLimitExceeded);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
    }

    [Fact]
    public async Task ExtractAsync_EntryBytesOneOverLimit_UsesEntryErrorCode()
    {
        using MemoryStream archive = CreateArchive(("a.txt", new byte[5]));
        PackageInstallLimits limits = CreateLimits();
        limits.MaxExpandedBytes = 10;
        limits.MaxEntryBytes = 4;

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => TarballExtractor.ExtractAsync(
                    archive,
                    GetDestination(),
                    limits,
                    directive: null,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.EntrySizeLimitExceeded);
    }

    [Fact]
    public async Task ExtractAsync_EntryCountOneOverLimit_UsesCountErrorCode()
    {
        using MemoryStream archive = CreateArchive(
            ("a.txt", [1]),
            ("b.txt", [2]));
        PackageInstallLimits limits = CreateLimits();
        limits.MaxArchiveEntries = 1;

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => TarballExtractor.ExtractAsync(
                    archive,
                    GetDestination(),
                    limits,
                    directive: null,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.ArchiveEntryCountLimitExceeded);
    }

    [Fact]
    public async Task ExtractAsync_NormalizedPathOneOverLimit_UsesPathErrorCode()
    {
        const string entryName = "folder/file.txt";
        const string normalizedName = entryName;
        using MemoryStream archive = CreateArchive((entryName, new byte[1]));
        PackageInstallLimits limits = CreateLimits();
        limits.MaxArchivePathLength = normalizedName.Length - 1;

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => TarballExtractor.ExtractAsync(
                    archive,
                    GetDestination(),
                    limits,
                    directive: null,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.ArchivePathLengthLimitExceeded);
    }

    [Fact]
    public async Task ExtractAsync_NormalizedDepthOneOverLimit_UsesDepthErrorCode()
    {
        using MemoryStream archive = CreateArchive(
            ("a/b/c.txt", new byte[1]));
        PackageInstallLimits limits = CreateLimits();
        limits.MaxArchiveDepth = 2;

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => TarballExtractor.ExtractAsync(
                    archive,
                    GetDestination(),
                    limits,
                    directive: null,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.ArchiveDepthLimitExceeded);
    }

    [Fact]
    public async Task ExtractAsync_ConsumesCurrentPositionAndLeavesSourceOpen()
    {
        using MemoryStream archive = CreateArchive(("file.txt", [1, 2, 3]));
        byte[] archiveBytes = archive.ToArray();
        byte[] prefix = [4, 5, 6];
        using MemoryStream source = new MemoryStream([.. prefix, .. archiveBytes]);
        source.Position = prefix.Length;

        ArchiveExtractionMetrics metrics = await TarballExtractor.ExtractAsync(
            source,
            GetDestination(),
            CreateLimits(),
            directive: null,
            TestContext.Current.CancellationToken);

        metrics.ExpandedBytes.ShouldBe(3);
        source.CanRead.ShouldBeTrue();
        source.Position.ShouldBe(source.Length);
    }

    [Fact]
    public async Task ExtractAsync_Cancellation_PropagatesWithoutWrapping()
    {
        using MemoryStream archive = CreateArchive(("file.txt", new byte[128]));
        using CancellationTokenSource cancellationSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
        cancellationSource.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => TarballExtractor.ExtractAsync(
                archive,
                GetDestination(),
                CreateLimits(),
                directive: null,
                cancellationSource.Token));
    }

    [Fact]
    public async Task ExtractAsync_OverflowingLimits_AreRejectedAsInvalidPolicy()
    {
        using MemoryStream archive = CreateArchive(("file.txt", [1]));
        PackageInstallLimits limits = new PackageInstallLimits
        {
            MaxCompressedBytes = long.MaxValue,
            MaxExpandedBytes = long.MaxValue,
            MaxEntryBytes = long.MaxValue,
            MaxArchiveEntries = 2,
            MaxArchivePathLength = 256,
            MaxArchiveDepth = 16
        };

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => TarballExtractor.ExtractAsync(
                    archive,
                    GetDestination(),
                    limits,
                    directive: null,
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
        exception.Stage.ShouldBe(PackageInstallStage.PolicyValidation);
    }

    [Fact]
    public async Task DiskPackageCache_OversizedPaxMetadata_RejectsBeforeParserAndCleansOperation()
    {
        using MemoryStream archive = CreateRawMetadataArchive(
            (byte)'x',
            5 * 1024 * 1024);
        archive.Length.ShouldBeLessThan(20_000);
        string cacheRoot = Path.Combine(_testRoot, "pax-cache");
        using DiskPackageCache cache = new DiskPackageCache(cacheRoot);
        PackageReference reference = new PackageReference(
            "example.package",
            "1.0.0");
        PackageInstallLimits limits = CreateMetadataLimits();

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => cache.InstallAsync(
                    reference,
                    archive,
                    new InstallCacheOptions
                    {
                        VerifyChecksum = false,
                        Limits = limits
                    },
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.EntrySizeLimitExceeded);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
        cache.GetPackageContentPath(reference).ShouldBeNull();
        AssertNoStagingOperations(cacheRoot);
    }

    [Fact]
    public async Task DiskPackageCache_OversizedGnuLongPath_RejectsBeforeParserAndCleansOperation()
    {
        using MemoryStream archive = CreateRawMetadataArchive(
            (byte)'L',
            5 * 1024 * 1024);
        archive.Length.ShouldBeLessThan(20_000);
        string cacheRoot = Path.Combine(_testRoot, "gnu-cache");
        using DiskPackageCache cache = new DiskPackageCache(cacheRoot);
        PackageReference reference = new PackageReference(
            "example.package",
            "1.0.0");
        PackageInstallLimits limits = CreateMetadataLimits();

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => cache.InstallAsync(
                    reference,
                    archive,
                    new InstallCacheOptions
                    {
                        VerifyChecksum = false,
                        Limits = limits
                    },
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.ArchivePathLengthLimitExceeded);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
        cache.GetPackageContentPath(reference).ShouldBeNull();
        AssertNoStagingOperations(cacheRoot);
    }

    [Fact]
    public async Task ExtractAsync_TruncatedTar_MapsParserFailureToInvalidArchive()
    {
        using MemoryStream archive = CreateTruncatedArchive();

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => TarballExtractor.ExtractAsync(
                    archive,
                    GetDestination(),
                    CreateLimits(),
                    "example.package#1.0.0",
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidArchive);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
        exception.InnerException.ShouldBeOfType<EndOfStreamException>();
    }

    [Fact]
    public async Task ExtractAsync_GnuSparseEntry_MapsUnsupportedFormatToInvalidArchive()
    {
        using MemoryStream archive = CreateRawArchive(
            (byte)'S',
            declaredSize: 0,
            payloadBytes: 0,
            completeArchive: true);

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => TarballExtractor.ExtractAsync(
                    archive,
                    GetDestination(),
                    CreateLimits(),
                    "example.package#1.0.0",
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidArchive);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
    }

    [Fact]
    public async Task ExtractAsync_DestinationIoFailure_IsNotMappedToInvalidArchive()
    {
        using MemoryStream archive = CreateArchive(("file.txt", [1]));
        string blockerPath = Path.Combine(_testRoot, "destination-file");
        Directory.CreateDirectory(_testRoot);
        await File.WriteAllTextAsync(
            blockerPath,
            "not a directory",
            TestContext.Current.CancellationToken);
        string destination = Path.Combine(blockerPath, "child");

        await Should.ThrowAsync<IOException>(
            () => TarballExtractor.ExtractAsync(
                archive,
                destination,
                CreateLimits(),
                directive: null,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExtractAsync_PaxSizeOverrideFramesFollowingEntry()
    {
        using MemoryStream archive = CreatePaxSizeOverrideArchive(
            "600",
            rawSize: 0,
            actualPayloadBytes: 600);
        PackageInstallLimits limits = CreateLimits();
        limits.MaxExpandedBytes = 600;
        limits.MaxEntryBytes = 600;
        string destination = GetDestination();

        ArchiveExtractionMetrics metrics = await TarballExtractor.ExtractAsync(
            archive,
            destination,
            limits,
            "example.package#1.0.0",
            TestContext.Current.CancellationToken);

        metrics.ExpandedBytes.ShouldBe(600);
        metrics.LargestEntryBytes.ShouldBe(600);
        metrics.EntryCount.ShouldBe(1);
        metrics.MetadataEntryCount.ShouldBe(1);
        new FileInfo(Path.Combine(destination, "file.bin")).Length.ShouldBe(600);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    [InlineData("9223372036854775808")]
    public async Task ExtractAsync_InvalidPaxSizeOverride_MapsToInvalidArchive(
        string sizeValue)
    {
        using MemoryStream archive = CreatePaxSizeOverrideArchive(
            sizeValue,
            rawSize: 0,
            actualPayloadBytes: 0);

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => TarballExtractor.ExtractAsync(
                    archive,
                    GetDestination(),
                    CreateLimits(),
                    "example.package#1.0.0",
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidArchive);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
        exception.InnerException.ShouldBeOfType<InvalidDataException>();
    }

    private async Task AssertNativeTreeAsync(
        string destination,
        IReadOnlyDictionary<string, byte[]> expectedFiles,
        IReadOnlyCollection<string> expectedDirectories)
    {
        string control = Path.Combine(_testRoot, $"native-{Guid.NewGuid():N}");
        foreach (string directory in expectedDirectories)
            Directory.CreateDirectory(Path.Combine(control, directory));
        foreach ((string path, byte[] content) in expectedFiles)
        {
            string controlPath = Path.Combine(control, path);
            await File.WriteAllBytesAsync(
                controlPath, content, TestContext.Current.CancellationToken);
            (await File.ReadAllBytesAsync(controlPath, TestContext.Current.CancellationToken))
                .ShouldBe(content);
        }

        string[] controlFiles = Directory.GetFiles(control, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(control, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        string[] controlDirectories = Directory.GetDirectories(control, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(control, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (!controlFiles.SequenceEqual(expectedFiles.Keys.OrderBy(path => path, StringComparer.Ordinal))
            || !controlDirectories.SequenceEqual(
                expectedDirectories.OrderBy(path => path, StringComparer.Ordinal)))
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                "Native filesystem filename limitation: direct writes did not retain exact " +
                "Unicode spelling. Extraction must match the independently written names, " +
                "without normalizing either comparison. Requested files: " +
                string.Join(", ", expectedFiles.Keys) + "; native files: " +
                string.Join(", ", controlFiles) + "; requested directories: " +
                string.Join(", ", expectedDirectories) + "; native directories: " +
                string.Join(", ", controlDirectories));
        }

        Directory.GetFiles(destination, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(destination, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal).ShouldBe(controlFiles);
        Directory.GetDirectories(destination, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(destination, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal).ShouldBe(controlDirectories);
        foreach (string path in controlFiles)
        {
            (await File.ReadAllBytesAsync(
                Path.Combine(destination, path), TestContext.Current.CancellationToken))
                .ShouldBe(await File.ReadAllBytesAsync(
                    Path.Combine(control, path), TestContext.Current.CancellationToken));
        }
    }

    private async Task AssertPathLengthFailureBeforeWriteAsync(
        MemoryStream archive,
        PackageInstallLimits limits)
    {
        string destination = GetDestination();
        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
            () => TarballExtractor.ExtractAsync(
                archive,
                destination,
                limits,
                "example.package#1.0.0",
                TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.ArchivePathLengthLimitExceeded);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
        exception.Directive.ShouldBe("example.package#1.0.0");
        Directory.EnumerateFileSystemEntries(destination).ShouldBeEmpty();
    }

    private string GetDestination() =>
        Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));

    private static PackageInstallLimits CreateLimits() =>
        new PackageInstallLimits
        {
            MaxCompressedBytes = 1_024,
            MaxExpandedBytes = 1_024,
            MaxEntryBytes = 1_024,
            MaxArchiveEntries = 100,
            MaxArchivePathLength = 256,
            MaxArchiveDepth = 16
        };

    private static PackageInstallLimits CreateMetadataLimits() =>
        new PackageInstallLimits
        {
            MaxCompressedBytes = 100_000,
            MaxExpandedBytes = 1_024,
            MaxEntryBytes = 1_024,
            MaxArchiveEntries = 100,
            MaxArchivePathLength = 1_024,
            MaxArchiveDepth = 16
        };

    private static MemoryStream CreateArchive(
        params (string Name, byte[] Content)[] entries)
    {
        MemoryStream archive = new MemoryStream();
        using (GZipStream gzip = new GZipStream(
            archive,
            CompressionMode.Compress,
            leaveOpen: true))
        using (TarWriter writer = new TarWriter(gzip, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                PaxTarEntry entry = new PaxTarEntry(
                    TarEntryType.RegularFile,
                    name)
                {
                    DataStream = new MemoryStream(content)
                };
                writer.WriteEntry(entry);
            }
        }

        archive.Position = 0;
        return archive;
    }

    private static MemoryStream CreateRawMetadataArchive(
        byte entryType,
        int metadataBytes) =>
        CreateRawArchive(
            entryType,
            metadataBytes,
            metadataBytes,
            completeArchive: true);

    private static MemoryStream CreateTruncatedArchive() =>
        CreateRawArchive(
            (byte)'0',
            declaredSize: 1_024,
            payloadBytes: 10,
            completeArchive: false);

    private static MemoryStream CreatePaxSizeOverrideArchive(
        string sizeValue,
        int rawSize,
        int actualPayloadBytes)
    {
        byte[] paxRecord = CreatePaxRecord("size", sizeValue);
        MemoryStream archive = new MemoryStream();
        using (GZipStream gzip = new GZipStream(
            archive,
            CompressionMode.Compress,
            leaveOpen: true))
        {
            WriteTarHeader(
                gzip,
                "PaxHeaders/file.bin",
                paxRecord.Length,
                (byte)'x');
            gzip.Write(paxRecord);
            WriteTarPadding(gzip, paxRecord.Length);

            WriteTarHeader(gzip, "file.bin", rawSize, (byte)'0');
            WriteRepeatedBytes(gzip, actualPayloadBytes, (byte)'b');
            WriteTarPadding(gzip, actualPayloadBytes);
            WriteRepeatedBytes(gzip, 1_024, 0);
        }

        archive.Position = 0;
        return archive;
    }

    private static MemoryStream CreateRawArchive(
        byte entryType,
        int declaredSize,
        int payloadBytes,
        bool completeArchive)
    {
        MemoryStream archive = new MemoryStream();
        using (GZipStream gzip = new GZipStream(
            archive,
            CompressionMode.Compress,
            leaveOpen: true))
        {
            WriteTarHeader(
                gzip,
                "metadata",
                declaredSize,
                entryType);
            WriteRepeatedBytes(gzip, payloadBytes, (byte)'a');

            if (completeArchive)
            {
                int paddingBytes = declaredSize % 512 == 0
                    ? 0
                    : 512 - (declaredSize % 512);
                WriteRepeatedBytes(gzip, paddingBytes, 0);
                WriteRepeatedBytes(gzip, 1_024, 0);
            }
        }

        archive.Position = 0;
        return archive;
    }

    private static void WriteTarHeader(
        Stream destination,
        string name,
        long size,
        byte entryType)
    {
        byte[] header = new byte[512];
        WriteAscii(header, 0, 100, name);
        WriteOctal(header, 100, 8, 420);
        WriteOctal(header, 108, 8, 0);
        WriteOctal(header, 116, 8, 0);
        WriteOctal(header, 124, 12, size);
        WriteOctal(header, 136, 12, 0);
        header.AsSpan(148, 8).Fill((byte)' ');
        header[156] = entryType;
        WriteAscii(header, 257, 6, "ustar");
        WriteAscii(header, 263, 2, "00");

        int checksum = 0;
        foreach (byte value in header)
            checksum += value;

        string checksumText = Convert.ToString(checksum, 8)!.PadLeft(6, '0');
        WriteAscii(header, 148, 6, checksumText);
        header[154] = 0;
        header[155] = (byte)' ';
        destination.Write(header);
    }

    private static void WriteAscii(
        byte[] destination,
        int offset,
        int fieldLength,
        string value)
    {
        byte[] encoded = Encoding.ASCII.GetBytes(value);
        int bytesToCopy = Math.Min(fieldLength, encoded.Length);
        encoded.AsSpan(0, bytesToCopy).CopyTo(
            destination.AsSpan(offset, bytesToCopy));
    }

    private static void WriteOctal(
        byte[] destination,
        int offset,
        int fieldLength,
        long value)
    {
        string octal = Convert.ToString(value, 8)!;
        string field = octal.PadLeft(fieldLength - 1, '0');
        WriteAscii(destination, offset, fieldLength - 1, field);
        destination[offset + fieldLength - 1] = 0;
    }

    private static void WriteRepeatedBytes(
        Stream destination,
        int count,
        byte value)
    {
        byte[] buffer = new byte[8_192];
        if (value != 0)
            buffer.AsSpan().Fill(value);

        int remaining = count;
        while (remaining > 0)
        {
            int bytesToWrite = Math.Min(buffer.Length, remaining);
            destination.Write(buffer, 0, bytesToWrite);
            remaining -= bytesToWrite;
        }
    }

    private static byte[] CreatePaxRecord(string keyword, string value)
    {
        string body = $"{keyword}={value}\n";
        int bodyLength = Encoding.UTF8.GetByteCount(body);
        int recordLength = bodyLength + 2;

        while (true)
        {
            string lengthText = recordLength.ToString(
                CultureInfo.InvariantCulture);
            int calculatedLength = Encoding.ASCII.GetByteCount(lengthText)
                + 1
                + bodyLength;
            if (calculatedLength == recordLength)
            {
                return Encoding.UTF8.GetBytes(
                    $"{lengthText} {body}");
            }

            recordLength = calculatedLength;
        }
    }

    private static void WriteTarPadding(Stream destination, int dataLength)
    {
        int paddingBytes = dataLength % 512 == 0
            ? 0
            : 512 - (dataLength % 512);
        WriteRepeatedBytes(destination, paddingBytes, 0);
    }

    private static void AssertNoStagingOperations(string cacheRoot)
    {
        string stagingRoot = Path.Combine(cacheRoot, ".fhirpkg", "staging");
        if (!Directory.Exists(stagingRoot))
            return;

        Directory.EnumerateFileSystemEntries(stagingRoot).ShouldBeEmpty();
    }
}
