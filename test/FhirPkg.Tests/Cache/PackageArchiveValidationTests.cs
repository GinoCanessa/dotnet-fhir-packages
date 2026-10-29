// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.IO;
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

    public static TheoryData<bool, bool, bool> LayoutModes =>
        new()
        {
            { false, false, false },
            { false, false, true },
            { false, true, false },
            { false, true, true },
            { true, false, false },
            { true, false, true },
            { true, true, false },
            { true, true, true }
        };

    public static IEnumerable<object[]> IncidentalPackageCases()
    {
        foreach (string shape in new[]
        {
            "file",
            "empty-directory",
            "subtree",
            "wrong-case-manifest",
            "wrong-case-directory",
            "manifest-directory"
        })
        {
            foreach (bool import in new[] { false, true })
            {
                foreach (bool unmodified in new[] { false, true })
                    yield return [shape, import, unmodified];
            }
        }
    }

    public static IEnumerable<object[]> SameHashInstallCases()
    {
        foreach (string version in new[] { "1.0.0", "current", "current$main", "dev" })
        {
            foreach (bool legacyLayout in new[] { false, true })
            {
                foreach (bool initiallyUnmodified in new[] { false, true })
                {
                    foreach (bool unmodified in new[] { false, true })
                        yield return [version, legacyLayout, initiallyUnmodified, unmodified];
                }
            }
        }
    }

    public static IEnumerable<object[]> InvalidLayoutCases()
    {
        foreach (InvalidLayout layout in Enum.GetValues<InvalidLayout>())
        {
            foreach (bool import in new[] { false, true })
            {
                foreach (bool unmodified in new[] { false, true })
                    yield return [layout, import, unmodified];
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void InstallCacheOptions_Unmodified_DefaultsToFalse()
    {
        new InstallCacheOptions().Unmodified.ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(LayoutModes))]
    public async Task InstallAndImport_LayoutModes_PreserveCompleteArchive(
        bool import,
        bool legacyLayout,
        bool unmodified)
    {
        ArbitraryTarEntry[] entries = CreatePreservationEntries(legacyLayout);
        using MemoryStream archive = ArbitraryTarBuilder.Create(entries);
        PackageReference reference = new("example.package", "1.0.0");

        PackageRecord record = await InstallSucceedsAsync(
            archive,
            reference,
            import: import,
            unmodified: unmodified);

        archive.CanRead.ShouldBeTrue();
        archive.Position.ShouldBe(archive.Length);
        record.Index.ShouldNotBeNull();
        record.Index.Files.ShouldHaveSingleItem().Filename.ShouldBe(PatientFileName);
        await AssertInstalledContentAsync(
            record,
            reference,
            entries,
            legacyLayout,
            unmodified);

        string archiveRoot = legacyLayout && !unmodified
            ? record.ContentPath
            : record.DirectoryPath;
        string relativeReference = await File.ReadAllTextAsync(
            Path.Combine(archiveRoot, "references.txt"),
            TestContext.Current.CancellationToken);
        (await File.ReadAllTextAsync(
            Path.Combine(archiveRoot, relativeReference),
            TestContext.Current.CancellationToken)).ShouldBe(PatientResource);
        string ancillaryReference = await File.ReadAllTextAsync(
            Path.Combine(archiveRoot, "openapi", "reference.txt"),
            TestContext.Current.CancellationToken);
        (await File.ReadAllBytesAsync(
            Path.Combine(archiveRoot, "openapi", ancillaryReference),
            TestContext.Current.CancellationToken))
            .ShouldBe(entries.Single(entry => entry.Name == "other/data.bin").Content!);
    }

    [Theory]
    [MemberData(nameof(IncidentalPackageCases))]
    public async Task InstallAndImport_LegacyIncidentalPackageEntry_IsPreserved(
        string shape,
        bool import,
        bool unmodified)
    {
        ArbitraryTarEntry incidental = shape switch
        {
            "file" => ArbitraryTarBuilder.File("package", "incidental file"),
            "empty-directory" => ArbitraryTarBuilder.Directory("package/"),
            "subtree" => ArbitraryTarBuilder.File(
                "package/nested/package.json", "{opaque"),
            "wrong-case-manifest" => ArbitraryTarBuilder.File(
                "package/Package.json", "{opaque"),
            "wrong-case-directory" => ArbitraryTarBuilder.File(
                "Package/package.json", "{opaque"),
            "manifest-directory" => ArbitraryTarBuilder.Directory(
                "package/package.json/"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };
        ArbitraryTarEntry[] entries =
        [
            ArbitraryTarBuilder.File("package.json", Manifest),
            ArbitraryTarBuilder.File(PatientFileName, PatientResource),
            incidental
        ];
        using MemoryStream archive = ArbitraryTarBuilder.Create(entries);
        PackageReference reference = new("example.package", "1.0.0");

        PackageRecord record = await InstallSucceedsAsync(
            archive,
            reference,
            import: import,
            unmodified: unmodified);

        await AssertInstalledContentAsync(
            record,
            reference,
            entries,
            legacyLayout: true,
            unmodified: unmodified);
    }

    [Theory]
    [MemberData(nameof(SameHashInstallCases))]
    public async Task InstallAsync_SameHashOverwrite_OnlyOrdinaryStandardContentMaySkip(
        string version,
        bool legacyLayout,
        bool initiallyUnmodified,
        bool unmodified)
    {
        PackageReference reference = new("example.package", version);
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        ArbitraryTarEntry[] entries = CreatePreservationEntries(legacyLayout);
        using MemoryStream archive = ArbitraryTarBuilder.Create(entries);
        byte[] archiveBytes = archive.ToArray();
        string cacheRoot = Path.Combine(_testRoot, "same-hash");
        using DiskPackageCache cache = CreateCache(cacheRoot);
        PackageRecord prior = await InstallSucceedsAsync(
            archive,
            reference,
            existingCache: cache,
            unmodified: initiallyUnmodified);
        await File.WriteAllTextAsync(
            Path.Combine(prior.ContentPath, "local-only.txt"),
            "not distributed",
            TestContext.Current.CancellationToken);
        Dictionary<string, string?> priorTree = await SnapshotTreeAsync(prior.DirectoryPath);
        CacheMetadataEntry priorMetadata = (await cache.GetMetadataAsync(
            TestContext.Current.CancellationToken)).Packages[cacheKey.MetadataKey];
        using MemoryStream replacement = new(archiveBytes);
        InstallCacheOptions options = new()
        {
            OverwriteExisting = true,
            SkipIfArchiveUnchanged = true,
            Unmodified = unmodified,
            SourcePublicationDate = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)
        };

        PackageRecord record = await cache.InstallAsync(
            reference,
            replacement,
            options,
            TestContext.Current.CancellationToken);

        AssertNoStagingOperations(cacheRoot);
        replacement.CanRead.ShouldBeTrue();
        replacement.Position.ShouldBe(replacement.Length);
        CacheMetadataEntry metadata = (await cache.GetMetadataAsync(
            TestContext.Current.CancellationToken)).Packages[cacheKey.MetadataKey];
        metadata.ArchiveSha256.ShouldBe(priorMetadata.ArchiveSha256);
        metadata.SizeBytes.ShouldBe(priorMetadata.SizeBytes);
        metadata.SourcePublicationDate.ShouldBe(options.SourcePublicationDate);
        bool shouldSkip = !unmodified && (!legacyLayout || !initiallyUnmodified);
        if (shouldSkip)
        {
            options.InstallOutcome.Effect.ShouldBe(PackageCacheInstallEffect.Unchanged);
            metadata.ContentGeneration.ShouldBe(priorMetadata.ContentGeneration);
            record.ContentPath.ShouldBe(prior.ContentPath);
            AssertSameTree(priorTree, await SnapshotTreeAsync(record.DirectoryPath));
        }
        else
        {
            options.InstallOutcome.Effect.ShouldBe(PackageCacheInstallEffect.Replaced);
            metadata.ContentGeneration.ShouldNotBe(priorMetadata.ContentGeneration);
            await AssertInstalledContentAsync(
                record,
                reference,
                entries,
                legacyLayout,
                unmodified,
                manifestVersion: "1.0.0");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAsync_SameHashLegacyOverwrite_ReconstructsRequestedMode(
        bool initiallyUnmodified)
    {
        ArbitraryTarEntry[] entries = CreatePreservationEntries(legacyLayout: true);
        using MemoryStream original = ArbitraryTarBuilder.Create(entries);
        string cacheRoot = Path.Combine(_testRoot, "import-overwrite");
        using DiskPackageCache cache = CreateCache(cacheRoot);
        PackageRecord prior = await InstallSucceedsAsync(
            original,
            existingCache: cache,
            import: true,
            unmodified: initiallyUnmodified);
        CacheMetadataEntry priorMetadata = (await cache.GetMetadataAsync(
            TestContext.Current.CancellationToken))
            .Packages[PackageCacheKey.Create(prior.Reference).MetadataKey];
        using MemoryStream replacement = new(original.ToArray());

        PackageRecord record = await cache.ImportAsync(
            replacement,
            new InstallCacheOptions
            {
                OverwriteExisting = true,
                SkipIfArchiveUnchanged = true,
                Unmodified = !initiallyUnmodified
            },
            TestContext.Current.CancellationToken);

        AssertNoStagingOperations(cacheRoot);
        replacement.CanRead.ShouldBeTrue();
        CacheMetadataEntry metadata = (await cache.GetMetadataAsync(
            TestContext.Current.CancellationToken))
            .Packages[PackageCacheKey.Create(record.Reference).MetadataKey];
        metadata.ArchiveSha256.ShouldBe(priorMetadata.ArchiveSha256);
        metadata.ContentGeneration.ShouldNotBe(priorMetadata.ContentGeneration);
        await AssertInstalledContentAsync(
            record,
            prior.Reference,
            entries,
            legacyLayout: true,
            unmodified: !initiallyUnmodified);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAsync_CacheHit_DoesNotConvertExistingGeneration(
        bool initiallyUnmodified)
    {
        ArbitraryTarEntry[] entries = CreatePreservationEntries(legacyLayout: true);
        using MemoryStream original = ArbitraryTarBuilder.Create(entries);
        string cacheRoot = Path.Combine(_testRoot, "import-hit");
        using DiskPackageCache cache = CreateCache(cacheRoot);
        PackageRecord prior = await InstallSucceedsAsync(
            original,
            existingCache: cache,
            import: true,
            unmodified: initiallyUnmodified);
        byte[] priorMetadata = await File.ReadAllBytesAsync(
            Path.Combine(cacheRoot, "packages.ini"),
            TestContext.Current.CancellationToken);
        using MemoryStream replacement = new(original.ToArray());

        PackageRecord record = await cache.ImportAsync(
            replacement,
            new InstallCacheOptions { Unmodified = !initiallyUnmodified },
            TestContext.Current.CancellationToken);

        AssertNoStagingOperations(cacheRoot);
        replacement.CanRead.ShouldBeTrue();
        replacement.Position.ShouldBe(replacement.Length);
        record.ContentPath.ShouldBe(prior.ContentPath);
        (await File.ReadAllBytesAsync(
            Path.Combine(cacheRoot, "packages.ini"),
            TestContext.Current.CancellationToken)).ShouldBe(priorMetadata);
        await AssertInstalledContentAsync(
            record,
            prior.Reference,
            entries,
            legacyLayout: true,
            unmodified: initiallyUnmodified);
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
    public async Task InstallAsync_LegacyRootLayout_IsValidatedThenWrapped()
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
    public async Task InstallAsync_AncillaryRoots_ArePreserved(
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

        await AssertInstalledContentAsync(record, reference, entries);
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

        await AssertInstalledContentAsync(record, reference, entries);
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
    public async Task InstallAsync_ArbitraryAncillaryLayout_IsPreserved(
        string ancillaryPath,
        bool isDirectory)
    {
        string ancillaryRoot = ancillaryPath.Split('/')[0];
        ArbitraryTarEntry[] acceptedEntries = CreateAncillaryEntries(
            includeOpenApi: !string.Equals(
                ancillaryRoot,
                "openapi",
                StringComparison.OrdinalIgnoreCase),
            includeOther: !string.Equals(
                ancillaryRoot,
                "other",
                StringComparison.OrdinalIgnoreCase));
        ArbitraryTarEntry ancillaryEntry = isDirectory
            ? ArbitraryTarBuilder.Directory(ancillaryPath)
            : ArbitraryTarBuilder.File(ancillaryPath, OtherResource);
        ArbitraryTarEntry[] entries = [.. acceptedEntries, ancillaryEntry];
        using MemoryStream archive = ArbitraryTarBuilder.Create(entries);
        PackageReference reference = new("example.package", "1.0.0");

        PackageRecord record = await InstallSucceedsAsync(archive, reference);

        await AssertInstalledContentAsync(record, reference, entries);
    }

    [Theory]
    [InlineData("openapi/package.json")]
    [InlineData("other/package.json")]
    [InlineData("openapi/nested/Package.json")]
    [InlineData("other/nested/Package.json")]
    [InlineData("openapi/PACKAGE.JSON")]
    [InlineData("other/PACKAGE.JSON")]
    public async Task InstallAsync_AncillaryManifest_IsOpaquePreservedPayload(
        string manifestPath)
    {
        ArbitraryTarEntry[] entries =
        [
            .. CreateAncillaryEntries(),
            ArbitraryTarBuilder.File(manifestPath, "{not package manifest JSON")
        ];
        using MemoryStream archive = ArbitraryTarBuilder.Create(entries);
        PackageReference reference = new("example.package", "1.0.0");

        PackageRecord record = await InstallSucceedsAsync(archive, reference);

        await AssertInstalledContentAsync(record, reference, entries);
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
    public async Task InstallAsync_CoincidentAncillaryAndContentPaths_ArePreserved(
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
        ArbitraryTarEntry[] entries =
        [
            .. CreateAncillaryEntries(
                includeOpenApi: ancillaryRoot == "openapi",
                includeOther: ancillaryRoot == "other"),
            occupiedEntry
        ];
        using MemoryStream archive = ArbitraryTarBuilder.Create(entries);
        PackageReference reference = new("example.package", "1.0.0");

        PackageRecord record = await InstallSucceedsAsync(archive, reference);

        await AssertInstalledContentAsync(record, reference, entries);
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

    [Fact]
    public async Task Normalize_FaultAfterMoveDoesNotMoveCompletedEntryAgain()
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package.json", Manifest),
            ArbitraryTarBuilder.File("README.md", "readme"));
        string extractedPath = Path.Combine(_testRoot, "normalize-fault");
        await TarballExtractor.ExtractAsync(
            archive,
            extractedPath,
            TestContext.Current.CancellationToken);
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(
            FileSystemPlatform.Linux);
        primitives.UseSystemFiles();
        primitives.EnableDirectorySynchronization();
        primitives.Set<IEnumerator<string>>(
            "CreateDirectoryEnumerator",
            call => SystemFileSystemPrimitives.Instance.CreateDirectoryEnumerator(call.Path!));
        // The rename commits, then the durability barrier fails once: replay must
        // finish the barrier without renaming the completed entry again.
        primitives.Enqueue<FileSystemDirectorySyncOutcome>("SynchronizeDescriptor", call =>
            ScriptedFileSystemPrimitives.NativeFailure<FileSystemDirectorySyncOutcome>(
                5, "fsync", call.Path));
        FileSystemContext context = primitives.CreateContext();

        PackageArchiveInventory inventory = PackageArchiveInventory.FromFileSystem(
            extractedPath,
            "example.package#1.0.0",
            context);
        TarballExtractor.ValidateAndNormalizePackageStructure(
            extractedPath,
            inventory,
            "example.package#1.0.0",
            context);

        File.Exists(Path.Combine(extractedPath, "package", "package.json")).ShouldBeTrue();
        File.Exists(Path.Combine(extractedPath, "package", "README.md")).ShouldBeTrue();
        primitives.Count("MovePath").ShouldBe(2);
    }

    [Fact]
    public async Task Inventory_RelativeRootIsResolvedOnceThroughRetainedContext()
    {
        string extractedPath = Path.Combine(_testRoot, "relative-inventory");
        Directory.CreateDirectory(Path.Combine(extractedPath, "package"));
        Directory.CreateDirectory(Path.Combine(extractedPath, "other", "empty"));
        await File.WriteAllTextAsync(
            Path.Combine(extractedPath, "package", "package.json"),
            Manifest,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(extractedPath, "other", "Mixed.Name.json"),
            OtherResource,
            TestContext.Current.CancellationToken);
        const string relativeRoot = "relative-inventory";
        ScriptedFileSystemPrimitives primitives = new(SystemFileSystemPrimitives.Instance.Platform);
        primitives.UseSystemFiles();
        primitives.Set<IEnumerator<string>>("CreateDirectoryEnumerator",
            call => SystemFileSystemPrimitives.Instance.CreateDirectoryEnumerator(call.Path!));
        primitives.Set<string>("GetFullPath", call =>
        {
            call.Path.ShouldBe(relativeRoot);
            return FileSystemReplayOutcome<string>.Complete(extractedPath);
        });
        for (int attempt = 0; attempt < 2; attempt++)
        {
            primitives.Enqueue("GetFullPath",
                ScriptedFileSystemPrimitives.NativeFailure<string>(5, "GetFullPath", relativeRoot));
        }
        FileSystemContext context = primitives.CreateContext(retries: 2);

        PackageArchiveInventory inventory = PackageArchiveInventory.FromFileSystem(
            relativeRoot, "example.package#1.0.0", context);

        inventory.Nodes.Select(node => node.ExactSegment)
            .OrderBy(segment => segment, StringComparer.Ordinal)
            .ShouldBe(["Mixed.Name.json", "empty", "other", "package", "package.json"]);
        PackageArchiveLayoutResult layout = PackageArchiveContentLocator.Locate(
            relativeRoot, inventory, "example.package#1.0.0");
        layout.Layout.ShouldBe(PackageArchiveLayout.Standard);
        layout.ContentPath.ShouldBe(Path.Combine(relativeRoot, "package"));
        primitives.Count("GetFullPath").ShouldBe(3);
        string[] expectedDirectories =
        [
            extractedPath,
            Path.Combine(extractedPath, "other"),
            Path.Combine(extractedPath, "other", "empty"),
            Path.Combine(extractedPath, "package"),
        ];
        primitives.Calls.Where(call => call.Operation == "CreateDirectoryEnumerator")
            .Select(call => call.Path!)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(expectedDirectories.OrderBy(path => path, StringComparer.Ordinal));
        primitives.Waits.ShouldBe([10, 10]);
        primitives.AsyncWaits.ShouldBeEmpty();
    }

    [Fact]
    public void Inventory_RelativeRootExhaustionDoesNotEnumerate()
    {
        const string relativeRoot = "relative-inventory";
        ScriptedFileSystemPrimitives primitives = new(SystemFileSystemPrimitives.Instance.Platform);
        IOException[] failures = Enumerable.Range(0, 3)
            .Select(attempt => new IOException($"Inventory root failure {attempt}."))
            .ToArray();
        foreach (IOException failure in failures)
        {
            primitives.Enqueue("GetFullPath",
                ScriptedFileSystemPrimitives.Fail<string>(failure, "GetFullPath", relativeRoot));
        }
        FileSystemContext context = primitives.CreateContext(retries: 2);

        IOException exception = Should.Throw<IOException>(() =>
            PackageArchiveInventory.FromFileSystem(relativeRoot, "example.package#1.0.0", context));

        exception.ShouldBeSameAs(failures[2]);
        FileSystemFailureContext.FormatMessage(exception).ShouldContain("attempts 3, retries 2, termination Exhausted");
        primitives.Count("GetFullPath").ShouldBe(3);
        primitives.Count("CreateDirectoryEnumerator").ShouldBe(0);
        primitives.Waits.ShouldBe([10, 10]);
        primitives.AsyncWaits.ShouldBeEmpty();
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
    [InlineData(InvalidLayout.WrapperRoot)]
    [InlineData(InvalidLayout.MissingManifest)]
    [InlineData(InvalidLayout.ManifestWrongCase)]
    [InlineData(InvalidLayout.PackageDirectoryWrongCase)]
    [InlineData(InvalidLayout.RootManifestWrongCase)]
    [InlineData(InvalidLayout.ManifestDirectory)]
    [InlineData(InvalidLayout.RootManifestDirectory)]
    [InlineData(InvalidLayout.DualManifest)]
    public async Task NormalizePackageStructure_InvalidLayout_DoesNotMutate(
        InvalidLayout invalidLayout)
    {
        using MemoryStream archive = CreateInvalidLayoutArchive(invalidLayout);

        await AssertNormalizationFailsWithoutMutationAsync(archive);
    }

    [Theory]
    [InlineData(CompatibilityLayout.StandardRootFile)]
    [InlineData(CompatibilityLayout.LegacyWithPackageTree)]
    [InlineData(CompatibilityLayout.NestedManifest)]
    public async Task NormalizePackageStructure_CompatibilityOnlyRestrictions_DoNotMutate(
        CompatibilityLayout layout)
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            CreateCompatibilityEntries(layout));

        await AssertNormalizationFailsWithoutMutationAsync(archive);
    }

    [Theory]
    [InlineData(CompatibilityLayout.StandardRootFile)]
    [InlineData(CompatibilityLayout.LegacyWithPackageTree)]
    [InlineData(CompatibilityLayout.NestedManifest)]
    public async Task InstallAsync_LayoutsRejectedOnlyByExplicitNormalizer_ArePreserved(
        CompatibilityLayout layout)
    {
        ArbitraryTarEntry[] entries = CreateCompatibilityEntries(layout);
        using MemoryStream archive = ArbitraryTarBuilder.Create(entries);
        PackageReference reference = new("example.package", "1.0.0");

        PackageRecord record = await InstallSucceedsAsync(archive, reference);

        await AssertInstalledContentAsync(
            record,
            reference,
            entries,
            legacyLayout: layout == CompatibilityLayout.LegacyWithPackageTree);
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
    [MemberData(nameof(LayoutModes))]
    public async Task InstallAndImport_InvalidReplacement_PreservesCompletePriorGeneration(
        bool import,
        bool legacyLayout,
        bool unmodified)
    {
        string cacheRoot = Path.Combine(_testRoot, "replacement-cache");
        PackageReference reference = new PackageReference("example.package", "1.0.0");
        PackageRecord priorRecord;
        CacheMetadataEntry priorMetadata;
        Dictionary<string, string?> priorTree;
        byte[] priorMetadataBytes;
        ArbitraryTarEntry[] originalEntries = CreatePreservationEntries(legacyLayout);
        using (DiskPackageCache cache = CreateCache(cacheRoot))
        using (MemoryStream original = ArbitraryTarBuilder.Create(originalEntries))
        {
            string originalHash = Convert.ToHexString(SHA256.HashData(original.ToArray()))
                .ToLowerInvariant();
            await InstallSucceedsAsync(
                original,
                reference,
                existingCache: cache,
                import: import,
                unmodified: unmodified);
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
            priorMetadataBytes = await File.ReadAllBytesAsync(
                Path.Combine(cacheRoot, "packages.ini"),
                TestContext.Current.CancellationToken);

            foreach (string failure in new[] { "traversal", "dual", "malformed", "identity" })
            {
                string prefix = legacyLayout ? "" : "package/";
                string manifest = failure switch
                {
                    "malformed" => "{not-json",
                    "identity" => """{"name":" ","version":"1.0.0"}""",
                    _ => Manifest
                };
                List<ArbitraryTarEntry> replacementEntries =
                [
                    ArbitraryTarBuilder.File($"{prefix}package.json", manifest),
                    ArbitraryTarBuilder.File(
                        $"{prefix}{PatientFileName}",
                        PatientResource.Replace("\"active\"", "\"draft\"")),
                    ArbitraryTarBuilder.File("replacement-only.txt", "new generation"),
                    ArbitraryTarBuilder.File("openapi/new.txt", "new ancillary"),
                    ArbitraryTarBuilder.File("nested/package.json", Manifest)
                ];
                if (failure == "dual")
                {
                    replacementEntries.Add(ArbitraryTarBuilder.File(
                        legacyLayout ? "package/package.json" : "package.json",
                        Manifest));
                }
                else if (failure == "traversal")
                {
                    replacementEntries.AddRange(CreateUnsafeAncillaryEntries(
                        "other",
                        UnsafeAncillaryEntry.Traversal));
                }
                using MemoryStream replacement = ArbitraryTarBuilder.Create(
                    replacementEntries.ToArray());
                InstallCacheOptions options = new()
                {
                    OverwriteExisting = true,
                    Unmodified = unmodified
                };

                PackageInstallException exception =
                    await Should.ThrowAsync<PackageInstallException>(
                        () => import
                            ? cache.ImportAsync(
                                replacement,
                                options,
                                TestContext.Current.CancellationToken)
                            : cache.InstallAsync(
                                reference,
                                replacement,
                                options,
                                TestContext.Current.CancellationToken));

                // Inspect raw state before any cache reader could recover a transaction.
                AssertNoStagingOperations(cacheRoot);
                string transactionsRoot = Path.Combine(cacheRoot, ".fhirpkg", "transactions");
                if (Directory.Exists(transactionsRoot))
                    Directory.EnumerateFileSystemEntries(transactionsRoot).ShouldBeEmpty();
                AssertSameTree(priorTree, await SnapshotTreeAsync(priorRecord.DirectoryPath));
                (await File.ReadAllBytesAsync(
                    Path.Combine(cacheRoot, "packages.ini"),
                    TestContext.Current.CancellationToken)).ShouldBe(priorMetadataBytes);
                if (failure == "identity")
                {
                    exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPackageIdentity);
                    exception.Stage.ShouldBe(PackageInstallStage.IdentityValidation);
                }
                else
                {
                    AssertArchiveFailure(exception);
                }
                replacement.CanRead.ShouldBeTrue();
                replacement.Position.ShouldBe(replacement.Length);
                CacheMetadata retainedMetadata = await cache.GetMetadataAsync(
                    TestContext.Current.CancellationToken);
                retainedMetadata.Packages[PackageCacheKey.Create(reference).MetadataKey]
                    .ShouldBe(priorMetadata);
            }
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
        CacheMetadata reopenedMetadata = await reopened.GetMetadataAsync(
            TestContext.Current.CancellationToken);
        CacheMetadataEntry retainedEntry =
            reopenedMetadata.Packages[PackageCacheKey.Create(reference).MetadataKey];
        retainedEntry.ArchiveSha256.ShouldBe(priorMetadata.ArchiveSha256);
        retainedEntry.ContentGeneration.ShouldBe(priorMetadata.ContentGeneration);
        retainedEntry.ShouldBe(priorMetadata);
        AssertSameTree(priorTree, await SnapshotTreeAsync(retained.DirectoryPath));
        await AssertInstalledContentAsync(
            retained,
            reference,
            originalEntries,
            legacyLayout,
            unmodified);
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
    [MemberData(nameof(InvalidLayoutCases))]
    public async Task InstallAndImport_AmbiguousOrInvalidLayout_IsRejectedBeforePromotion(
        InvalidLayout invalidLayout,
        bool import,
        bool unmodified)
    {
        using MemoryStream archive = CreateInvalidLayoutArchive(invalidLayout);

        PackageInstallException exception = await InstallFailsAsync(
            archive,
            import: import,
            unmodified: unmodified);

        AssertArchiveFailure(exception);
    }

    [Theory]
    [InlineData("other.package", "1.0.0", false, false)]
    [InlineData("other.package", "1.0.0", false, true)]
    [InlineData("other.package", "1.0.0", true, false)]
    [InlineData("other.package", "1.0.0", true, true)]
    [InlineData("example.package", "2.0.0", false, false)]
    [InlineData("example.package", "2.0.0", false, true)]
    [InlineData("example.package", "2.0.0", true, false)]
    [InlineData("example.package", "2.0.0", true, true)]
    public async Task InstallAsync_ManifestIdentityMismatch_IsRejectedBeforePromotion(
        string name,
        string version,
        bool legacyLayout,
        bool unmodified)
    {
        string manifest =
            $$"""{"name":"{{name}}","version":"{{version}}"}""";
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File(
                legacyLayout ? "package.json" : "package/package.json",
                manifest),
            ArbitraryTarBuilder.File("nested/package.json", Manifest));

        PackageInstallException exception = await InstallFailsAsync(
            archive,
            unmodified: unmodified);

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPackageIdentity);
        exception.Stage.ShouldBe(PackageInstallStage.IdentityValidation);
    }

    [Theory]
    [MemberData(nameof(LayoutModes))]
    public async Task InstallAndImport_InvalidAuthoritativeManifest_DoesNotUseNestedManifest(
        bool import,
        bool legacyLayout,
        bool unmodified)
    {
        (string Manifest, PackageInstallErrorCode ErrorCode)[] cases =
        [
            ("{not-json", PackageInstallErrorCode.InvalidArchive),
            ("null", PackageInstallErrorCode.InvalidArchive),
            ("[]", PackageInstallErrorCode.InvalidArchive),
            ("{}", PackageInstallErrorCode.InvalidArchive),
            ("""{"name":"","version":"1.0.0"}""",
                PackageInstallErrorCode.InvalidPackageIdentity),
            ("""{"name":"example.package","version":"current"}""",
                PackageInstallErrorCode.InvalidPackageIdentity)
        ];
        foreach ((string manifest, PackageInstallErrorCode expectedCode) in cases)
        {
            using MemoryStream archive = ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.File(
                    legacyLayout ? "package.json" : "package/package.json",
                    manifest),
                ArbitraryTarBuilder.File("other/package.json", Manifest),
                ArbitraryTarBuilder.File("nested/package.json", Manifest));

            PackageInstallException exception = await InstallFailsAsync(
                archive,
                import: import,
                unmodified: unmodified);

            exception.ErrorCode.ShouldBe(expectedCode);
            exception.Stage.ShouldBe(expectedCode == PackageInstallErrorCode.InvalidArchive
                ? PackageInstallStage.ArchiveValidation
                : PackageInstallStage.IdentityValidation);
        }
    }

    [Theory]
    [MemberData(nameof(LayoutModes))]
    public async Task InstallAndImport_CancellationBeforePreparation_PreservesPriorTreeAndMetadata(
        bool import,
        bool legacyLayout,
        bool unmodified)
    {
        string cacheRoot = Path.Combine(_testRoot, "cancelled-replacement");
        using DiskPackageCache cache = CreateCache(cacheRoot);
        using MemoryStream original = ArbitraryTarBuilder.Create(
            CreatePreservationEntries(legacyLayout));
        PackageRecord prior = await InstallSucceedsAsync(
            original,
            existingCache: cache,
            import: import,
            unmodified: unmodified);
        Dictionary<string, string?> priorTree = await SnapshotTreeAsync(prior.DirectoryPath);
        byte[] priorMetadata = await File.ReadAllBytesAsync(
            Path.Combine(cacheRoot, "packages.ini"),
            TestContext.Current.CancellationToken);
        using CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using MemoryStream replacement = new(original.ToArray());
        InstallCacheOptions options = new()
        {
            OverwriteExisting = true,
            Unmodified = unmodified,
            Progress = new CancelOnValidationProgress(source)
        };

        await Should.ThrowAsync<OperationCanceledException>(
            () => import
                ? cache.ImportAsync(replacement, options, source.Token)
                : cache.InstallAsync(prior.Reference, replacement, options, source.Token));

        AssertNoStagingOperations(cacheRoot);
        replacement.CanRead.ShouldBeTrue();
        replacement.Position.ShouldBe(replacement.Length);
        AssertSameTree(priorTree, await SnapshotTreeAsync(prior.DirectoryPath));
        (await File.ReadAllBytesAsync(
            Path.Combine(cacheRoot, "packages.ini"),
            TestContext.Current.CancellationToken)).ShouldBe(priorMetadata);
    }

    private async Task<PackageRecord> InstallSucceedsAsync(
        MemoryStream archive,
        PackageReference? requestedReference = null,
        PackageInstallLimits? limits = null,
        DiskPackageCache? existingCache = null,
        bool import = false,
        bool unmodified = false)
    {
        using DiskPackageCache? ownedCache = existingCache is null
            ? CreateCache(Path.Combine(_testRoot, Guid.NewGuid().ToString("N")))
            : null;
        DiskPackageCache cache = existingCache ?? ownedCache!;
        try
        {
            InstallCacheOptions options = new()
            {
                VerifyChecksum = false,
                Limits = limits,
                Unmodified = unmodified
            };
            PackageRecord record = import
                ? await cache.ImportAsync(
                    archive,
                    options,
                    TestContext.Current.CancellationToken)
                : await cache.InstallAsync(
                    requestedReference ?? new PackageReference("example.package", "1.0.0"),
                    archive,
                    options,
                    TestContext.Current.CancellationToken);
            AssertNoStagingOperations(cache.CacheDirectory);
            return record;
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
        bool overwriteExisting = false,
        bool import = false,
        bool unmodified = false)
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
        InstallCacheOptions options = new()
        {
            VerifyChecksum = false,
            Limits = limits,
            OverwriteExisting = overwriteExisting,
            Unmodified = unmodified
        };

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => import
                    ? cache.ImportAsync(
                        archive,
                        options,
                        TestContext.Current.CancellationToken)
                    : cache.InstallAsync(
                        reference,
                        archive,
                        options,
                        TestContext.Current.CancellationToken));

        // Check cleanup before cache reads can perform recovery.
        AssertNoStagingOperations(cacheRoot);
        if (overwriteExisting)
        {
            cache.GetPackageContentPath(reference).ShouldBe(priorContentPath);
        }
        else
        {
            Directory.Exists(PackageCacheKey.Create(reference).GetPackageDirectoryPath(cacheRoot))
                .ShouldBeFalse();
            cache.GetPackageContentPath(reference).ShouldBeNull();
            (await cache.GetPackageAsync(
                reference,
                TestContext.Current.CancellationToken)).ShouldBeNull();
        }
        AssertNoStagingOperations(cacheRoot);
        archive.CanRead.ShouldBeTrue();
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

    private static ArbitraryTarEntry[] CreatePreservationEntries(bool legacyLayout)
    {
        string prefix = legacyLayout ? "" : "package/";
        return
        [
            ArbitraryTarBuilder.File($"{prefix}package.json", Manifest),
            ArbitraryTarBuilder.File($"{prefix}{PatientFileName}", PatientResource),
            ArbitraryTarBuilder.File(
                $"{prefix}.index.json",
                $$"""
                { "index-version": 2, "files": [
                  { "filename": "{{PatientFileName}}", "resourceType": "StructureDefinition",
                    "id": "Patient", "url": "http://hl7.org/fhir/StructureDefinition/Patient" }
                ] }
                """),
            ArbitraryTarBuilder.File("README.md", "distributed root readme"),
            ArbitraryTarBuilder.File("package/README.md", "different package readme"),
            ArbitraryTarBuilder.File("references.txt", $"{prefix}{PatientFileName}"),
            ArbitraryTarBuilder.File("openapi/reference.txt", "../other/data.bin"),
            ArbitraryTarBuilder.File("openapi/schema/model.json", OpenApiResource),
            ArbitraryTarBuilder.File("package/openapi/content.txt", "not a move destination"),
            ArbitraryTarBuilder.Directory("package/other/empty/"),
            ArbitraryTarBuilder.File(
                legacyLayout ? "package/Package.json" : "package/nested/package.json",
                "{opaque malformed manifest"),
            ArbitraryTarBuilder.File("other/nested/package.json", "{also opaque"),
            ArbitraryTarBuilder.File("other/Package.json", "not JSON"),
            ArbitraryTarBuilder.File("other/.index.json", "{opaque ancillary index"),
            ArbitraryTarBuilder.File(
                "other/data.bin",
                Enumerable.Range(0, 256).Select(value => (byte)value).ToArray()),
            ArbitraryTarBuilder.Directory("other/empty/"),
            ArbitraryTarBuilder.File(".fhirpkg/transactions/payload.json", "{payload, not journal"),
            ArbitraryTarBuilder.File(".fhirpkg/packages.ini", "distributed nested bookkeeping name"),
            ArbitraryTarBuilder.File("packages.ini", "[packages]\npayload = not cache metadata\n"),
            ArbitraryTarBuilder.File("docs\\nested\\notes.txt", "UTF-8 café; relative paths stay intact"),
            ArbitraryTarBuilder.Directory("docs/nested/"),
            ArbitraryTarBuilder.Directory("package/"),
            ArbitraryTarBuilder.Directory("empty/"),
            ArbitraryTarBuilder.Directory("explicit/"),
            ArbitraryTarBuilder.File("explicit/file.txt", "explicit parent"),
            ArbitraryTarBuilder.File("empty-file.bin", [])
        ];
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
        Dictionary<string, string?> actual)
    {
        actual.Count.ShouldBe(expected.Count);
        actual.Count(entry => entry.Value is null)
            .ShouldBe(expected.Count(entry => entry.Value is null));
        actual.Count(entry => entry.Value is not null)
            .ShouldBe(expected.Count(entry => entry.Value is not null));
        actual.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ShouldBe(expected.OrderBy(entry => entry.Key, StringComparer.Ordinal));
    }

    private static Dictionary<string, string?> ExpectedTree(
        ArbitraryTarEntry[] entries,
        bool wrapLegacyRoot)
    {
        Dictionary<string, string?> expected = new(StringComparer.Ordinal);
        if (wrapLegacyRoot)
            expected.Add("package", null);
        foreach (ArbitraryTarEntry entry in entries)
        {
            string relativePath = entry.Name.Replace('\\', '/').TrimEnd('/');
            if (wrapLegacyRoot)
                relativePath = $"package/{relativePath}";
            string[] segments = relativePath.Split('/');
            for (int depth = 1; depth < segments.Length; depth++)
                expected.TryAdd(string.Join("/", segments.Take(depth)), null);

            if (entry.EntryType == TarEntryType.Directory)
                expected.TryAdd(relativePath, null);
            else
                expected.Add(relativePath, Convert.ToBase64String(entry.Content!));
        }

        return expected;
    }

    private static async Task AssertInstalledContentAsync(
        PackageRecord record,
        PackageReference reference,
        ArbitraryTarEntry[] entries,
        bool legacyLayout = false,
        bool unmodified = false,
        string? manifestVersion = null)
    {
        record.Reference.ShouldBe(reference);
        record.Manifest.Name.ShouldBe(reference.Name);
        record.Manifest.Version.ShouldBe(manifestVersion ?? reference.Version);
        Path.GetFileName(record.DirectoryPath)
            .ShouldBe(Path.GetFileName(PackageCacheKey.Create(reference).RelativePath));
        record.ContentPath.ShouldBe(legacyLayout && unmodified
            ? record.DirectoryPath
            : Path.Combine(record.DirectoryPath, "package"));
        Dictionary<string, string?> expected = ExpectedTree(
            entries,
            wrapLegacyRoot: legacyLayout && !unmodified);
        AssertSameTree(expected, await SnapshotTreeAsync(record.DirectoryPath));

        ArbitraryTarEntry[] files = entries
            .Where(entry => entry.EntryType == TarEntryType.RegularFile)
            .ToArray();
        long expectedSize = files.Sum(entry => (long)entry.Content!.Length);
        record.SizeBytes.ShouldBe(expectedSize);
        Directory.EnumerateFiles(record.DirectoryPath, "*", SearchOption.AllDirectories)
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
        reopenedRecord.Manifest.Version.ShouldBe(manifestVersion ?? reference.Version);
        reopenedRecord.DirectoryPath.ShouldBe(record.DirectoryPath);
        reopenedRecord.ContentPath.ShouldBe(record.ContentPath);
        reopenedRecord.SizeBytes.ShouldBe(expectedSize);
        reopened.GetPackageContentPath(reference).ShouldBe(record.ContentPath);
        CacheMetadata metadata = await reopened.GetMetadataAsync(
            TestContext.Current.CancellationToken);
        metadata.Packages[PackageCacheKey.Create(reference).MetadataKey]
            .SizeBytes.ShouldBe(expectedSize);

        foreach (ArbitraryTarEntry file in files)
        {
            string relativePath = file.Name.Replace('\\', '/');
            string archiveRoot = legacyLayout && !unmodified
                ? record.ContentPath
                : record.DirectoryPath;
            byte[] content = await File.ReadAllBytesAsync(
                Path.Combine(archiveRoot, relativePath),
                TestContext.Current.CancellationToken);
            content.ShouldBe(file.Content!);

            if (!legacyLayout)
            {
                if (!relativePath.StartsWith("package/", StringComparison.Ordinal))
                    continue;
                relativePath = relativePath["package/".Length..];
            }

            (await reopened.GetFileContentAsync(
                reference,
                relativePath,
                TestContext.Current.CancellationToken))
                .ShouldBe(Encoding.UTF8.GetString(file.Content!));
        }
        AssertSameTree(expected, await SnapshotTreeAsync(record.DirectoryPath));
    }

    private static MemoryStream CreateInvalidLayoutArchive(
        InvalidLayout invalidLayout) =>
        invalidLayout switch
        {
            InvalidLayout.WrapperRoot => ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.File("wrapper/package.json", Manifest)),
            InvalidLayout.MissingManifest => ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.File("package/file.txt", "missing")),
            InvalidLayout.ManifestWrongCase => ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.File("package/Package.json", Manifest)),
            InvalidLayout.PackageDirectoryWrongCase => ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.File("Package/package.json", Manifest)),
            InvalidLayout.RootManifestWrongCase => ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.File("Package.json", Manifest)),
            InvalidLayout.ManifestDirectory => ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.Directory("package/package.json/"),
                ArbitraryTarBuilder.File("other/package.json", Manifest)),
            InvalidLayout.RootManifestDirectory => ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.Directory("package.json/"),
                ArbitraryTarBuilder.File("other/package.json", Manifest)),
            InvalidLayout.DualManifest => ArbitraryTarBuilder.Create(
                ArbitraryTarBuilder.File("package/package.json", Manifest),
                ArbitraryTarBuilder.File("package.json", Manifest)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(invalidLayout),
                invalidLayout,
                null)
        };

    private static ArbitraryTarEntry[] CreateCompatibilityEntries(
        CompatibilityLayout layout) =>
        layout switch
        {
            CompatibilityLayout.StandardRootFile =>
            [
                ArbitraryTarBuilder.File("package/package.json", Manifest),
                ArbitraryTarBuilder.File("README.md", "root file")
            ],
            CompatibilityLayout.LegacyWithPackageTree =>
            [
                ArbitraryTarBuilder.File("package.json", Manifest),
                ArbitraryTarBuilder.File("package/file.txt", "incidental subtree")
            ],
            CompatibilityLayout.NestedManifest =>
            [
                ArbitraryTarBuilder.File("package/package.json", Manifest),
                ArbitraryTarBuilder.File("package/nested/package.json", "{opaque")
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, null)
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
        WrapperRoot,
        MissingManifest,
        ManifestWrongCase,
        PackageDirectoryWrongCase,
        RootManifestWrongCase,
        ManifestDirectory,
        RootManifestDirectory,
        DualManifest
    }

    public enum CompatibilityLayout
    {
        StandardRootFile,
        LegacyWithPackageTree,
        NestedManifest
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

    private sealed class CancelOnValidationProgress(CancellationTokenSource source) :
        IProgress<PackageProgress>
    {
        public void Report(PackageProgress value)
        {
            if (value.Phase == PackageProgressPhase.Validating)
                source.Cancel();
        }
    }
}
