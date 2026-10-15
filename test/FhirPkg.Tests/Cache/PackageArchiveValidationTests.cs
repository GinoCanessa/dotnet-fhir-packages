// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
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
    private const string PatientFileName = "StructureDefinition-Patient.json";
    private const string PatientResource =
        """{"resourceType":"StructureDefinition","id":"Patient","url":"http://hl7.org/fhir/StructureDefinition/Patient","name":"Patient","status":"active","kind":"resource","abstract":false,"type":"Patient"}""";
    private const string OpenApiResource =
        """{"resourceType":"StructureDefinition","id":"AncillaryOpenApi","url":"http://example.org/StructureDefinition/AncillaryOpenApi","name":"AncillaryOpenApi","status":"draft","kind":"resource","abstract":false,"type":"Patient","description":"OpenAPI café"}""";
    private const string OtherResource =
        """{"resourceType":"StructureDefinition","id":"AncillaryOther","url":"http://example.org/StructureDefinition/AncillaryOther","name":"AncillaryOther","status":"draft","kind":"resource","abstract":false,"type":"Patient","description":"Other content"}""";

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

    [Theory]
    [InlineData("hl7.fhir.r4.core", "4.0.1", true, false)]
    [InlineData("hl7.fhir.r4.core", "4.0.1", false, true)]
    [InlineData("hl7.fhir.r4.core", "4.0.1", true, true)]
    [InlineData("example.package", "1.0.0", true, false)]
    [InlineData("example.package", "1.0.0", false, true)]
    [InlineData("example.package", "1.0.0", true, true)]
    public async Task InstallAsync_AncillaryRoots_AreNormalized(
        string name,
        string version,
        bool includeOpenApi,
        bool includeOther)
    {
        PackageReference reference = new PackageReference(name, version);
        string manifest =
            $$"""{"name":"{{name}}","version":"{{version}}"}""";
        ArbitraryTarEntry[] entries = CreateAncillaryEntries(
            manifest,
            includeOpenApi,
            includeOther);
        using MemoryStream archive = ArbitraryTarBuilder.Create(entries);

        PackageRecord record = await InstallSucceedsAsync(archive, reference);

        await AssertNormalizedContentAsync(record, reference, entries);
    }

    [Theory]
    [InlineData(AncillaryDirectorySpelling.Implicit, false)]
    [InlineData(AncillaryDirectorySpelling.Explicit, false)]
    [InlineData(AncillaryDirectorySpelling.ExplicitAfterImplicit, false)]
    [InlineData(AncillaryDirectorySpelling.Implicit, true)]
    [InlineData(AncillaryDirectorySpelling.Explicit, true)]
    [InlineData(AncillaryDirectorySpelling.ExplicitAfterImplicit, true)]
    public async Task InstallAsync_AncillaryDirectorySpellings_AreAcceptedPortably(
        AncillaryDirectorySpelling spelling,
        bool useBackslashes)
    {
        ArbitraryTarEntry[] files = CreateAncillaryEntries();
        ArbitraryTarEntry[] directories =
        [
            ArbitraryTarBuilder.Directory("package/"),
            ArbitraryTarBuilder.Directory("openapi/"),
            ArbitraryTarBuilder.Directory("openapi/schema/"),
            ArbitraryTarBuilder.Directory("other/"),
            ArbitraryTarBuilder.Directory("other/resources/")
        ];
        ArbitraryTarEntry[] entries = spelling switch
        {
            AncillaryDirectorySpelling.Implicit => files,
            AncillaryDirectorySpelling.Explicit => [.. directories, .. files],
            AncillaryDirectorySpelling.ExplicitAfterImplicit => [.. files, .. directories],
            _ => throw new ArgumentOutOfRangeException(nameof(spelling), spelling, null)
        };
        if (useBackslashes)
        {
            entries = entries.Select(entry => entry with
            {
                Name = entry.Name.Replace('/', '\\')
            }).ToArray();
        }
        using MemoryStream archive = ArbitraryTarBuilder.Create(entries);
        PackageReference reference = new PackageReference("example.package", "1.0.0");

        PackageRecord record = await InstallSucceedsAsync(archive, reference);

        await AssertNormalizedContentAsync(record, reference, entries);
    }

    [Theory]
    [InlineData("unknown/", true)]
    [InlineData("unknown/nested/file.json", false)]
    [InlineData("unknown.txt", false)]
    [InlineData("README.md", false)]
    [InlineData("openapi", false)]
    [InlineData("other", false)]
    [InlineData("OpenApi/", true)]
    [InlineData("Other/", true)]
    [InlineData("OpenApi/nested/file.json", false)]
    [InlineData("Other/nested/file.json", false)]
    public async Task InstallAsync_UnsupportedAncillaryLayout_IsRejectedBeforePromotion(
        string unsupportedPath,
        bool isDirectory)
    {
        string unsupportedRoot = unsupportedPath.Split('/')[0];
        ArbitraryTarEntry[] acceptedEntries = CreateAncillaryEntries(
            includeOpenApi: !string.Equals(
                unsupportedRoot,
                "openapi",
                StringComparison.OrdinalIgnoreCase),
            includeOther: !string.Equals(
                unsupportedRoot,
                "other",
                StringComparison.OrdinalIgnoreCase));
        ArbitraryTarEntry unsupportedEntry = isDirectory
            ? ArbitraryTarBuilder.Directory(unsupportedPath)
            : ArbitraryTarBuilder.File(unsupportedPath, OtherResource);
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            [.. acceptedEntries, unsupportedEntry]);

        PackageInstallException exception = await InstallFailsAsync(archive);

        AssertArchiveFailure(exception);
    }

    [Theory]
    [InlineData("openapi/package.json")]
    [InlineData("other/package.json")]
    [InlineData("openapi/nested/Package.json")]
    [InlineData("other/nested/Package.json")]
    [InlineData("openapi/PACKAGE.JSON")]
    [InlineData("other/PACKAGE.JSON")]
    public async Task InstallAsync_AncillarySecondManifest_IsRejectedBeforePromotion(
        string manifestPath)
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
        [
            .. CreateAncillaryEntries(),
            ArbitraryTarBuilder.File(manifestPath, Manifest)
        ]);

        PackageInstallException exception = await InstallFailsAsync(archive);

        AssertArchiveFailure(exception);
    }

    [Theory]
    [InlineData("openapi", "openapi", AncillaryDestinationOccupancy.File)]
    [InlineData("openapi", "openapi", AncillaryDestinationOccupancy.EmptyDirectory)]
    [InlineData("openapi", "openapi", AncillaryDestinationOccupancy.ImplicitDirectory)]
    [InlineData("openapi", "OpenApi", AncillaryDestinationOccupancy.File)]
    [InlineData("openapi", "OpenApi", AncillaryDestinationOccupancy.EmptyDirectory)]
    [InlineData("openapi", "OpenApi", AncillaryDestinationOccupancy.ImplicitDirectory)]
    [InlineData("other", "other", AncillaryDestinationOccupancy.File)]
    [InlineData("other", "other", AncillaryDestinationOccupancy.EmptyDirectory)]
    [InlineData("other", "other", AncillaryDestinationOccupancy.ImplicitDirectory)]
    [InlineData("other", "Other", AncillaryDestinationOccupancy.File)]
    [InlineData("other", "Other", AncillaryDestinationOccupancy.EmptyDirectory)]
    [InlineData("other", "Other", AncillaryDestinationOccupancy.ImplicitDirectory)]
    public async Task InstallAsync_AncillaryDestinationOccupied_IsRejectedBeforePromotion(
        string ancillaryRoot,
        string destinationSegment,
        AncillaryDestinationOccupancy occupancy)
    {
        string destinationPath = $"package/{destinationSegment}";
        ArbitraryTarEntry occupiedEntry = occupancy switch
        {
            AncillaryDestinationOccupancy.File =>
                ArbitraryTarBuilder.File(destinationPath, "occupied"),
            AncillaryDestinationOccupancy.EmptyDirectory =>
                ArbitraryTarBuilder.Directory($"{destinationPath}/"),
            AncillaryDestinationOccupancy.ImplicitDirectory =>
                ArbitraryTarBuilder.File($"{destinationPath}/existing.json", PatientResource),
            _ => throw new ArgumentOutOfRangeException(nameof(occupancy), occupancy, null)
        };
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            [.. CreateAncillaryEntries(), occupiedEntry]);

        PackageInstallException exception = await InstallFailsAsync(archive);

        AssertArchiveFailure(exception);
        exception.Message.ShouldContain("normalization destination");
        exception.Message.ShouldContain($"package/{ancillaryRoot}");
    }

    [Theory]
    [InlineData("openapi", false)]
    [InlineData("openapi", true)]
    [InlineData("other", false)]
    [InlineData("other", true)]
    public async Task ValidateAndNormalize_AncillaryConflict_DoesNotMoveAnyRoot(
        string conflictingRoot,
        bool conflictingRootFirst)
    {
        string freeRoot = conflictingRoot == "openapi" ? "other" : "openapi";
        ArbitraryTarEntry conflictingEntry = ArbitraryTarBuilder.File(
            $"{conflictingRoot}/new.json",
            OpenApiResource);
        ArbitraryTarEntry freeEntry = ArbitraryTarBuilder.File(
            $"{freeRoot}/new.json",
            OtherResource);
        ArbitraryTarEntry[] ancillaryEntries = conflictingRootFirst
            ? [conflictingEntry, freeEntry]
            : [freeEntry, conflictingEntry];
        using MemoryStream archive = ArbitraryTarBuilder.Create(
        [
            .. CreateAncillaryEntries(includeOpenApi: false, includeOther: false),
            ArbitraryTarBuilder.File(
                $"package/{conflictingRoot}/existing.json",
                PatientResource),
            .. ancillaryEntries,
            ArbitraryTarBuilder.Directory($"{freeRoot}/empty/")
        ]);
        string extractedPath = Path.Combine(_testRoot, "preflight");
        ArchiveExtractionMetrics metrics = await TarballExtractor.ExtractAsync(
            archive,
            extractedPath,
            new PackageInstallLimits(),
            "example.package#1.0.0",
            TestContext.Current.CancellationToken);
        PackageArchiveInventory? inventory = metrics.Inventory;
        inventory.ShouldNotBeNull();
        inventory.Nodes
            .Where(node => node.Depth == 1 && node.CanonicalSegment != "package")
            .Select(node => node.CanonicalSegment)
            .ShouldBe(conflictingRootFirst
                ? [conflictingRoot, freeRoot]
                : [freeRoot, conflictingRoot]);
        Dictionary<string, string?> before = await SnapshotTreeAsync(extractedPath);

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => PackageStructureNormalizer.ValidateAndNormalize(
                extractedPath,
                inventory,
                "example.package#1.0.0"));

        AssertArchiveFailure(exception);
        exception.Message.ShouldContain("normalization destination");
        exception.Message.ShouldContain($"package/{conflictingRoot}");
        Dictionary<string, string?> after = await SnapshotTreeAsync(extractedPath);
        AssertSameTree(before, after);

        PackageInstallException compatibilityException = Should.Throw<PackageInstallException>(
            () => TarballExtractor.NormalizePackageStructure(extractedPath));

        AssertArchiveFailure(compatibilityException);
        compatibilityException.Message.ShouldContain("normalization destination");
        compatibilityException.Message.ShouldContain($"package/{conflictingRoot}");
        AssertSameTree(before, await SnapshotTreeAsync(extractedPath));
    }

    [Fact]
    public async Task NormalizePackageStructure_AncillaryLayout_IsIdempotent()
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
        [
            .. CreateAncillaryEntries(),
            ArbitraryTarBuilder.Directory("openapi/empty/"),
            ArbitraryTarBuilder.Directory("other/empty/")
        ]);
        string extractedPath = Path.Combine(_testRoot, "normalize");
        await TarballExtractor.ExtractAsync(
            archive,
            extractedPath,
            TestContext.Current.CancellationToken);
        Dictionary<string, string?> before = await SnapshotTreeAsync(extractedPath);
        Dictionary<string, string?> expected = before.ToDictionary(
            entry => entry.Key == "package"
                || entry.Key.StartsWith("package/", StringComparison.Ordinal)
                    ? entry.Key
                    : $"package/{entry.Key}",
            entry => entry.Value,
            StringComparer.Ordinal);

        TarballExtractor.NormalizePackageStructure(extractedPath);

        Dictionary<string, string?> normalized = await SnapshotTreeAsync(extractedPath);
        AssertSameTree(expected, normalized);
        Directory.EnumerateFileSystemEntries(extractedPath)
            .Select(Path.GetFileName)
            .ShouldBe(["package"]);

        TarballExtractor.NormalizePackageStructure(extractedPath);

        Dictionary<string, string?> repeated = await SnapshotTreeAsync(extractedPath);
        AssertSameTree(normalized, repeated);
        Directory.Exists(Path.Combine(extractedPath, "package", "package")).ShouldBeFalse();
    }

    [Fact]
    public async Task NormalizePackageStructure_LegacyLayout_IsIdempotent()
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package.json", Manifest),
            ArbitraryTarBuilder.File("README.md", "readme"),
            ArbitraryTarBuilder.File("resources/cafe\u0301.json", OtherResource),
            ArbitraryTarBuilder.Directory("empty/"));
        string extractedPath = Path.Combine(_testRoot, "normalize-legacy");
        await TarballExtractor.ExtractAsync(
            archive,
            extractedPath,
            TestContext.Current.CancellationToken);
        Dictionary<string, string?> before = await SnapshotTreeAsync(extractedPath);
        Dictionary<string, string?> expected = before.ToDictionary(
            entry => $"package/{entry.Key}",
            entry => entry.Value,
            StringComparer.Ordinal);
        expected.Add("package", null);

        TarballExtractor.NormalizePackageStructure(extractedPath);

        Dictionary<string, string?> normalized = await SnapshotTreeAsync(extractedPath);
        AssertSameTree(expected, normalized);
        Directory.EnumerateFileSystemEntries(extractedPath)
            .Select(Path.GetFileName)
            .ShouldBe(["package"]);

        TarballExtractor.NormalizePackageStructure(extractedPath);

        AssertSameTree(normalized, await SnapshotTreeAsync(extractedPath));
        Directory.Exists(Path.Combine(extractedPath, "package", "package")).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidateAndNormalizePackageStructure_PreservesOriginalLayoutAndNormalizedPaths(
        bool standardLayout)
    {
        string prefix = standardLayout ? "package/" : "";
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File($"{prefix}package.json", Manifest),
            ArbitraryTarBuilder.File($"{prefix}resource.json", PatientResource));
        string extractedPath = Path.Combine(_testRoot, "compatibility");
        ArchiveExtractionMetrics metrics = await TarballExtractor.ExtractAsync(
            archive,
            extractedPath,
            new PackageInstallLimits(),
            "example.package#1.0.0",
            TestContext.Current.CancellationToken);
        PackageArchiveInventory? inventory = metrics.Inventory;
        inventory.ShouldNotBeNull();
        Dictionary<string, string?> before = await SnapshotTreeAsync(extractedPath);
        Dictionary<string, string?> expected = standardLayout
            ? before
            : before.ToDictionary(
                entry => $"package/{entry.Key}",
                entry => entry.Value,
                StringComparer.Ordinal);
        if (!standardLayout)
            expected.Add("package", null);

        PackageArchiveLayoutResult result = TarballExtractor.ValidateAndNormalizePackageStructure(
            extractedPath,
            inventory,
            "example.package#1.0.0");

        result.Layout.ShouldBe(
            standardLayout ? PackageArchiveLayout.Standard : PackageArchiveLayout.Legacy);
        result.ContentPath.ShouldBe(Path.Combine(extractedPath, "package"));
        result.ManifestPath.ShouldBe(Path.Combine(extractedPath, "package", "package.json"));
        AssertSameTree(expected, await SnapshotTreeAsync(extractedPath));
    }

    [Theory]
    [InlineData("unknown/", true)]
    [InlineData("unknown/nested/file.json", false)]
    [InlineData("unknown.txt", false)]
    [InlineData("README.md", false)]
    [InlineData("openapi", false)]
    [InlineData("other", false)]
    [InlineData("OpenApi/", true)]
    [InlineData("Other/", true)]
    [InlineData("OpenApi/nested/file.json", false)]
    [InlineData("Other/nested/file.json", false)]
    public async Task NormalizePackageStructure_UnsupportedAncillaryLayout_DoesNotMutate(
        string unsupportedPath,
        bool isDirectory)
    {
        string unsupportedRoot = unsupportedPath.Split('/')[0];
        ArbitraryTarEntry unsupportedEntry = isDirectory
            ? ArbitraryTarBuilder.Directory(unsupportedPath)
            : ArbitraryTarBuilder.File(unsupportedPath, OtherResource);
        using MemoryStream archive = ArbitraryTarBuilder.Create(
        [
            .. CreateAncillaryEntries(
                includeOpenApi: !string.Equals(
                    unsupportedRoot,
                    "openapi",
                    StringComparison.OrdinalIgnoreCase),
                includeOther: !string.Equals(
                    unsupportedRoot,
                    "other",
                    StringComparison.OrdinalIgnoreCase)),
            unsupportedEntry
        ]);

        await AssertNormalizationFailsWithoutMutationAsync(archive);
    }

    [Theory]
    [InlineData("openapi/package.json")]
    [InlineData("other/package.json")]
    [InlineData("openapi/nested/Package.json")]
    [InlineData("other/nested/Package.json")]
    [InlineData("openapi/PACKAGE.JSON")]
    [InlineData("other/PACKAGE.JSON")]
    public async Task NormalizePackageStructure_AncillarySecondManifest_DoesNotMutate(
        string manifestPath)
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
        [
            .. CreateAncillaryEntries(),
            ArbitraryTarBuilder.File(manifestPath, Manifest)
        ]);

        await AssertNormalizationFailsWithoutMutationAsync(archive);
    }

    [Theory]
    [InlineData(InvalidLayout.MixedStandardRoot)]
    [InlineData(InvalidLayout.LegacyWithPackageTree)]
    [InlineData(InvalidLayout.WrapperRoot)]
    [InlineData(InvalidLayout.SecondManifest)]
    [InlineData(InvalidLayout.MissingManifest)]
    [InlineData(InvalidLayout.ManifestWrongCase)]
    public async Task NormalizePackageStructure_InvalidLayout_DoesNotMutate(
        InvalidLayout invalidLayout)
    {
        using MemoryStream archive = CreateInvalidLayoutArchive(invalidLayout);

        await AssertNormalizationFailsWithoutMutationAsync(archive);
    }

    [Theory]
    [InlineData("openapi", UnsafeAncillaryEntry.Traversal)]
    [InlineData("openapi", UnsafeAncillaryEntry.HardLink)]
    [InlineData("openapi", UnsafeAncillaryEntry.SymbolicLink)]
    [InlineData("openapi", UnsafeAncillaryEntry.Duplicate)]
    [InlineData("openapi", UnsafeAncillaryEntry.CaseCollision)]
    [InlineData("openapi", UnsafeAncillaryEntry.UnicodeNormalizationCollision)]
    [InlineData("other", UnsafeAncillaryEntry.Traversal)]
    [InlineData("other", UnsafeAncillaryEntry.HardLink)]
    [InlineData("other", UnsafeAncillaryEntry.SymbolicLink)]
    [InlineData("other", UnsafeAncillaryEntry.Duplicate)]
    [InlineData("other", UnsafeAncillaryEntry.CaseCollision)]
    [InlineData("other", UnsafeAncillaryEntry.UnicodeNormalizationCollision)]
    public async Task InstallAsync_UnsafeAncillaryEntries_AreRejectedBeforePromotion(
        string ancillaryRoot,
        UnsafeAncillaryEntry unsafeEntry)
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
        [
            .. CreateAncillaryEntries(),
            .. CreateUnsafeAncillaryEntries(ancillaryRoot, unsafeEntry)
        ]);

        PackageInstallException exception = await InstallFailsAsync(archive);

        AssertArchiveFailure(exception);
        Directory.EnumerateFiles(_testRoot, "escaped.txt", SearchOption.AllDirectories)
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData(PackageInstallErrorCode.EntrySizeLimitExceeded)]
    [InlineData(PackageInstallErrorCode.ExpandedSizeLimitExceeded)]
    [InlineData(PackageInstallErrorCode.ArchiveEntryCountLimitExceeded)]
    [InlineData(PackageInstallErrorCode.ArchiveDepthLimitExceeded)]
    public async Task InstallAsync_AncillaryEntries_RespectTightenedLimits(
        PackageInstallErrorCode expectedCode)
    {
        ArbitraryTarEntry[] entries = CreateAncillaryEntries();
        long expandedBytes = entries.Sum(entry => (long)entry.Content!.Length);
        long largestEntryBytes = entries.Max(entry => (long)entry.Content!.Length);
        long largestPackageEntryBytes = entries
            .Where(entry => entry.Name.StartsWith("package/", StringComparison.Ordinal))
            .Max(entry => (long)entry.Content!.Length);
        largestEntryBytes.ShouldBeGreaterThan(largestPackageEntryBytes);
        PackageInstallLimits limits = expectedCode switch
        {
            PackageInstallErrorCode.EntrySizeLimitExceeded => new PackageInstallLimits
            {
                MaxEntryBytes = largestPackageEntryBytes
            },
            PackageInstallErrorCode.ExpandedSizeLimitExceeded => new PackageInstallLimits
            {
                MaxExpandedBytes = expandedBytes - 1,
                MaxEntryBytes = largestEntryBytes
            },
            PackageInstallErrorCode.ArchiveEntryCountLimitExceeded => new PackageInstallLimits
            {
                MaxArchiveEntries = entries.Length - 1
            },
            PackageInstallErrorCode.ArchiveDepthLimitExceeded => new PackageInstallLimits
            {
                MaxArchiveDepth = 2
            },
            _ => throw new ArgumentOutOfRangeException(nameof(expectedCode), expectedCode, null)
        };
        using MemoryStream acceptedArchive = ArbitraryTarBuilder.Create(entries);
        await InstallSucceedsAsync(acceptedArchive);
        using MemoryStream limitedArchive = ArbitraryTarBuilder.Create(entries);

        PackageInstallException exception = await InstallFailsAsync(
            limitedArchive,
            limits: limits);

        exception.ErrorCode.ShouldBe(expectedCode);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
    }

    [Theory]
    [InlineData("hl7.fhir.r4.core", "4.0.1", "other.package", "4.0.1")]
    [InlineData("hl7.fhir.r4.core", "4.0.1", "hl7.fhir.r4.core", "4.0.2")]
    [InlineData("example.package", "1.0.0", "other.package", "1.0.0")]
    [InlineData("example.package", "1.0.0", "example.package", "2.0.0")]
    public async Task InstallAsync_AncillaryManifestIdentityMismatch_IsRejectedBeforePromotion(
        string expectedName,
        string expectedVersion,
        string manifestName,
        string manifestVersion)
    {
        PackageReference reference = new PackageReference(expectedName, expectedVersion);
        string manifest =
            $$"""{"name":"{{manifestName}}","version":"{{manifestVersion}}"}""";
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            CreateAncillaryEntries(manifest));

        PackageInstallException exception = await InstallFailsAsync(archive, reference);

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPackageIdentity);
        exception.Stage.ShouldBe(PackageInstallStage.IdentityValidation);
    }

    [Theory]
    [InlineData("openapi", true)]
    [InlineData("other", true)]
    [InlineData("openapi", false)]
    [InlineData("other", false)]
    public async Task InstallAsync_InvalidAncillaryReplacement_PreservesPriorGeneration(
        string ancillaryRoot,
        bool destinationConflict)
    {
        string cacheRoot = Path.Combine(_testRoot, "replacement-cache");
        PackageReference reference = new PackageReference("example.package", "1.0.0");
        PackageRecord priorRecord;
        CacheMetadataEntry priorMetadata;
        Dictionary<string, string?> priorTree;
        using (DiskPackageCache cache = CreateCache(cacheRoot))
        using (MemoryStream original = ArbitraryTarBuilder.Create(
        [
            .. CreateAncillaryEntries(includeOpenApi: false, includeOther: false),
            ArbitraryTarBuilder.File("package/prior-only.txt", "prior generation")
        ]))
        {
            string originalHash = Convert.ToHexString(SHA256.HashData(original.ToArray()))
                .ToLowerInvariant();
            await InstallSucceedsAsync(
                original,
                reference,
                existingCache: cache);
            // Compare persisted generations, including second-precision metadata timestamps.
            PackageRecord? persistedRecord = await cache.GetPackageAsync(
                reference,
                TestContext.Current.CancellationToken);
            persistedRecord.ShouldNotBeNull();
            priorRecord = persistedRecord;
            CacheMetadata metadata = await cache.GetMetadataAsync(
                TestContext.Current.CancellationToken);
            priorMetadata = metadata.Packages[PackageCacheKey.Create(reference).MetadataKey];
            priorMetadata.ArchiveSha256.ShouldBe(originalHash);
            priorMetadata.ContentGeneration.ShouldNotBeNullOrEmpty();
            priorTree = await SnapshotTreeAsync(priorRecord.DirectoryPath);

            ArbitraryTarEntry[] invalidEntries = destinationConflict
                ? [ArbitraryTarBuilder.File($"package/{ancillaryRoot}/existing.json", OtherResource)]
                : CreateUnsafeAncillaryEntries(ancillaryRoot, UnsafeAncillaryEntry.Traversal);
            ArbitraryTarEntry[] replacementEntries = CreateAncillaryEntries()
                .Select(entry => entry.Name == $"package/{PatientFileName}"
                    ? ArbitraryTarBuilder.File(
                        entry.Name,
                        PatientResource.Replace("\"active\"", "\"draft\""))
                    : entry)
                .ToArray();
            using MemoryStream replacement = ArbitraryTarBuilder.Create(
                [.. replacementEntries, .. invalidEntries]);

            PackageInstallException exception = await InstallFailsAsync(
                replacement,
                reference,
                existingCache: cache,
                overwriteExisting: true);

            AssertArchiveFailure(exception);
            if (destinationConflict)
                exception.Message.ShouldContain("normalization destination");
            replacement.Position.ShouldBe(replacement.Length);
            (await cache.GetFileContentAsync(
                reference,
                PatientFileName,
                TestContext.Current.CancellationToken)).ShouldBe(PatientResource);
            CacheMetadata retainedMetadata = await cache.GetMetadataAsync(
                TestContext.Current.CancellationToken);
            retainedMetadata.Packages[PackageCacheKey.Create(reference).MetadataKey]
                .ShouldBe(priorMetadata);
            AssertSameTree(priorTree, await SnapshotTreeAsync(priorRecord.DirectoryPath));
        }

        using DiskPackageCache reopened = CreateCache(cacheRoot);
        PackageRecord? retained = await reopened.GetPackageAsync(
            reference,
            TestContext.Current.CancellationToken);
        retained.ShouldNotBeNull();
        retained.Reference.ShouldBe(reference);
        retained.Manifest.Name.ShouldBe(reference.Name);
        retained.Manifest.Version.ShouldBe(reference.Version);
        retained.ContentPath.ShouldBe(priorRecord.ContentPath);
        retained.ContentGeneration.ShouldBe(priorRecord.ContentGeneration);
        retained.SizeBytes.ShouldBe(priorRecord.SizeBytes);
        (await reopened.GetFileContentAsync(
            reference,
            PatientFileName,
            TestContext.Current.CancellationToken)).ShouldBe(PatientResource);
        (await reopened.GetFileContentAsync(
            reference,
            "prior-only.txt",
            TestContext.Current.CancellationToken)).ShouldBe("prior generation");
        CacheMetadata reopenedMetadata = await reopened.GetMetadataAsync(
            TestContext.Current.CancellationToken);
        CacheMetadataEntry retainedEntry =
            reopenedMetadata.Packages[PackageCacheKey.Create(reference).MetadataKey];
        retainedEntry.ArchiveSha256.ShouldBe(priorMetadata.ArchiveSha256);
        retainedEntry.ContentGeneration.ShouldBe(priorMetadata.ContentGeneration);
        retainedEntry.ShouldBe(priorMetadata);
        AssertSameTree(priorTree, await SnapshotTreeAsync(retained.DirectoryPath));
        File.Exists(Path.Combine(cacheRoot, "escaped.txt")).ShouldBeFalse();
        AssertNoStagingOperations(cacheRoot);
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
        MemoryStream archive,
        PackageReference? requestedReference = null,
        PackageInstallLimits? limits = null,
        DiskPackageCache? existingCache = null)
    {
        using DiskPackageCache? ownedCache = existingCache is null
            ? CreateCache(Path.Combine(_testRoot, Guid.NewGuid().ToString("N")))
            : null;
        DiskPackageCache cache = existingCache ?? ownedCache!;
        try
        {
            return await cache.InstallAsync(
                requestedReference ?? new PackageReference("example.package", "1.0.0"),
                archive,
                new InstallCacheOptions
                {
                    VerifyChecksum = false,
                    Limits = limits
                },
                TestContext.Current.CancellationToken);
        }
        catch (PackageInstallException exception)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"[{exception.ErrorCode}/{exception.Stage}] {exception.Message}");
            throw;
        }
    }

    private async Task<PackageInstallException> InstallFailsAsync(
        MemoryStream archive,
        PackageReference? requestedReference = null,
        PackageInstallLimits? limits = null,
        DiskPackageCache? existingCache = null,
        bool overwriteExisting = false)
    {
        using DiskPackageCache? ownedCache = existingCache is null
            ? CreateCache(Path.Combine(_testRoot, Guid.NewGuid().ToString("N")))
            : null;
        DiskPackageCache cache = existingCache ?? ownedCache!;
        string cacheRoot = cache.CacheDirectory;
        PackageReference reference = requestedReference ?? new PackageReference(
            "example.package",
            "1.0.0");
        string? priorContentPath = cache.GetPackageContentPath(reference);

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => cache.InstallAsync(
                    reference,
                    archive,
                    new InstallCacheOptions
                    {
                        VerifyChecksum = false,
                        Limits = limits,
                        OverwriteExisting = overwriteExisting
                    },
                    TestContext.Current.CancellationToken));

        // Check cleanup before cache reads can perform recovery.
        AssertNoStagingOperations(cacheRoot);
        if (overwriteExisting)
        {
            cache.GetPackageContentPath(reference).ShouldBe(priorContentPath);
        }
        else
        {
            Directory.Exists(Path.Combine(cacheRoot, reference.CacheDirectoryName))
                .ShouldBeFalse();
            cache.GetPackageContentPath(reference).ShouldBeNull();
            (await cache.GetPackageAsync(
                reference,
                TestContext.Current.CancellationToken)).ShouldBeNull();
        }
        AssertNoStagingOperations(cacheRoot);
        return exception;
    }

    private static DiskPackageCache CreateCache(
        string cacheRoot,
        PackageInstallLimits? limits = null) =>
        new DiskPackageCache(
            cacheRoot,
            logger: null,
            timeProvider: null,
            limits ?? new PackageInstallLimits());

    private static ArbitraryTarEntry[] CreateAncillaryEntries(
        string manifest = Manifest,
        bool includeOpenApi = true,
        bool includeOther = true)
    {
        List<ArbitraryTarEntry> entries =
        [
            ArbitraryTarBuilder.File("package/package.json", manifest),
            ArbitraryTarBuilder.File($"package/{PatientFileName}", PatientResource)
        ];
        if (includeOpenApi)
        {
            entries.Add(ArbitraryTarBuilder.File(
                "openapi/schema/StructureDefinition-OpenApi.json",
                OpenApiResource));
        }
        if (includeOther)
        {
            entries.Add(ArbitraryTarBuilder.File(
                "other/resources/StructureDefinition-Other.json",
                OtherResource));
        }

        return entries.ToArray();
    }

    private static ArbitraryTarEntry[] CreateUnsafeAncillaryEntries(
        string ancillaryRoot,
        UnsafeAncillaryEntry unsafeEntry) =>
        unsafeEntry switch
        {
            UnsafeAncillaryEntry.Traversal =>
            [
                ArbitraryTarBuilder.File(
                    $"{ancillaryRoot}/../../../../../escaped.txt",
                    "escaped")
            ],
            UnsafeAncillaryEntry.HardLink =>
            [
                ArbitraryTarBuilder.HardLink(
                    $"{ancillaryRoot}/link",
                    "package/package.json")
            ],
            UnsafeAncillaryEntry.SymbolicLink =>
            [
                ArbitraryTarBuilder.SymbolicLink(
                    $"{ancillaryRoot}/link",
                    "../package/package.json")
            ],
            UnsafeAncillaryEntry.Duplicate =>
            [
                ArbitraryTarBuilder.File($"{ancillaryRoot}/duplicate.json", OpenApiResource),
                ArbitraryTarBuilder.File($"{ancillaryRoot}/duplicate.json", OtherResource)
            ],
            UnsafeAncillaryEntry.CaseCollision =>
            [
                ArbitraryTarBuilder.File($"{ancillaryRoot}/File.json", OpenApiResource),
                ArbitraryTarBuilder.File($"{ancillaryRoot}/file.json", OtherResource)
            ],
            UnsafeAncillaryEntry.UnicodeNormalizationCollision =>
            [
                ArbitraryTarBuilder.File($"{ancillaryRoot}/café.json", OpenApiResource),
                ArbitraryTarBuilder.File($"{ancillaryRoot}/cafe\u0301.json", OtherResource)
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(unsafeEntry), unsafeEntry, null)
        };

    private async Task AssertNormalizationFailsWithoutMutationAsync(MemoryStream archive)
    {
        string extractedPath = Path.Combine(_testRoot, "normalize-rejected");
        await TarballExtractor.ExtractAsync(
            archive,
            extractedPath,
            TestContext.Current.CancellationToken);
        Dictionary<string, string?> before = await SnapshotTreeAsync(extractedPath);

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => TarballExtractor.NormalizePackageStructure(extractedPath));

        AssertArchiveFailure(exception);
        AssertSameTree(before, await SnapshotTreeAsync(extractedPath));
    }

    private static async Task<Dictionary<string, string?>> SnapshotTreeAsync(string root)
    {
        Dictionary<string, string?> snapshot =
            new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFileSystemEntries(
            root,
            "*",
            SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
            snapshot.Add(
                relativePath,
                Directory.Exists(path)
                    ? null
                    : Convert.ToBase64String(await File.ReadAllBytesAsync(
                        path,
                        TestContext.Current.CancellationToken)));
        }

        return snapshot;
    }

    private static void AssertSameTree(
        Dictionary<string, string?> expected,
        Dictionary<string, string?> actual) =>
        actual.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ShouldBe(expected.OrderBy(entry => entry.Key, StringComparer.Ordinal));

    private static async Task AssertNormalizedContentAsync(
        PackageRecord record,
        PackageReference reference,
        ArbitraryTarEntry[] entries)
    {
        record.Reference.ShouldBe(reference);
        record.Manifest.Name.ShouldBe(reference.Name);
        record.Manifest.Version.ShouldBe(reference.Version);
        Path.GetFileName(record.DirectoryPath).ShouldBe(reference.CacheDirectoryName);
        record.ContentPath.ShouldBe(Path.Combine(record.DirectoryPath, "package"));
        Directory.EnumerateFileSystemEntries(record.DirectoryPath)
            .Select(Path.GetFileName)
            .ShouldBe(["package"]);

        ArbitraryTarEntry[] files = entries
            .Where(entry => entry.EntryType == TarEntryType.RegularFile)
            .ToArray();
        long expectedSize = files.Sum(entry => (long)entry.Content!.Length);
        record.SizeBytes.ShouldBe(expectedSize);
        Directory.EnumerateFiles(record.ContentPath, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length)
            .ShouldBe(expectedSize);

        using DiskPackageCache reopened = CreateCache(
            Path.GetDirectoryName(record.DirectoryPath)!);
        PackageRecord? reopenedRecord = await reopened.GetPackageAsync(
            reference,
            TestContext.Current.CancellationToken);
        reopenedRecord.ShouldNotBeNull();
        reopenedRecord.Reference.ShouldBe(reference);
        reopenedRecord.Manifest.Name.ShouldBe(reference.Name);
        reopenedRecord.Manifest.Version.ShouldBe(reference.Version);
        reopenedRecord.ContentPath.ShouldBe(record.ContentPath);
        reopenedRecord.SizeBytes.ShouldBe(expectedSize);

        foreach (ArbitraryTarEntry file in files)
        {
            string relativePath = file.Name.Replace('\\', '/');
            if (relativePath.StartsWith("package/", StringComparison.Ordinal))
                relativePath = relativePath["package/".Length..];

            byte[] content = await File.ReadAllBytesAsync(
                Path.Combine(record.ContentPath, relativePath),
                TestContext.Current.CancellationToken);
            content.ShouldBe(file.Content!);
            (await reopened.GetFileContentAsync(
                reference,
                relativePath,
                TestContext.Current.CancellationToken))
                .ShouldBe(Encoding.UTF8.GetString(file.Content!));
        }
    }

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

    public enum AncillaryDirectorySpelling
    {
        Implicit,
        Explicit,
        ExplicitAfterImplicit
    }

    public enum AncillaryDestinationOccupancy
    {
        File,
        EmptyDirectory,
        ImplicitDirectory
    }

    public enum UnsafeAncillaryEntry
    {
        Traversal,
        HardLink,
        SymbolicLink,
        Duplicate,
        CaseCollision,
        UnicodeNormalizationCollision
    }
}
