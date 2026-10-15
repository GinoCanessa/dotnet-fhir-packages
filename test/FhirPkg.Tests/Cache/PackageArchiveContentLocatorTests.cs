// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.Tests.Support;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Cache;

public class PackageArchiveContentLocatorTests : IDisposable
{
    private const string Directive = "example.package#1.0.0";
    private const string Manifest =
        """{"name":"example.package","version":"1.0.0"}""";

    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        $"archive-locator-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Locate_AuthoritativeLayoutWithOpaqueAncillaryPayload_LeavesTreeUnchanged(
        bool standardLayout,
        bool useBackslashes)
    {
        ArbitraryTarEntry[] entries =
        [
            ArbitraryTarBuilder.File(
                standardLayout ? "package/package.json" : "package.json",
                Manifest),
            ArbitraryTarBuilder.File("README.md", "readme"),
            ArbitraryTarBuilder.File("root.bin", [0, 255, 128, 13, 10]),
            ArbitraryTarBuilder.File("packages.ini", "distributed payload"),
            ArbitraryTarBuilder.File(".fhirpkg/state.bin", [1, 0, 254]),
            ArbitraryTarBuilder.File("source/package.json", "{ malformed ancillary"),
            ArbitraryTarBuilder.File("source/nested/Package.json", "not JSON"),
            ArbitraryTarBuilder.File("source/upper/PACKAGE.JSON", "opaque"),
            ArbitraryTarBuilder.File("package/nested/package.json", "[]"),
            ArbitraryTarBuilder.File("wrapper/package/package.json", Manifest),
            ArbitraryTarBuilder.Directory("source/empty/"),
            ArbitraryTarBuilder.File("OpenApi/specification.json", "{"),
            ArbitraryTarBuilder.File("other/non-resource.txt", "other")
        ];
        if (useBackslashes)
        {
            entries = entries.Select(entry => entry with
            {
                Name = entry.Name.Replace('/', '\\')
            }).ToArray();
        }

        await AssertLocatedWithoutMutationAsync(
            entries,
            standardLayout ? PackageArchiveLayout.Standard : PackageArchiveLayout.Legacy);
    }

    [Theory]
    [InlineData("package/openapi", false)]
    [InlineData("package/openapi/", true)]
    [InlineData("package/openapi/existing.json", false)]
    [InlineData("package/OpenApi", false)]
    [InlineData("package/OpenApi/", true)]
    [InlineData("package/OpenApi/existing.json", false)]
    [InlineData("package/other", false)]
    [InlineData("package/other/", true)]
    [InlineData("package/other/existing.json", false)]
    [InlineData("package/Other", false)]
    [InlineData("package/Other/", true)]
    [InlineData("package/Other/existing.json", false)]
    public async Task Locate_StandardLayout_HypotheticalMoveDestinations_AreIgnored(
        string occupiedPath,
        bool isDirectory)
    {
        ArbitraryTarEntry occupiedEntry = isDirectory
            ? ArbitraryTarBuilder.Directory(occupiedPath)
            : ArbitraryTarBuilder.File(occupiedPath, "existing");

        await AssertLocatedWithoutMutationAsync(
            [
                ArbitraryTarBuilder.File("package/package.json", Manifest),
                ArbitraryTarBuilder.File("openapi/new.json", "openapi"),
                ArbitraryTarBuilder.File("other/new.json", "other"),
                occupiedEntry
            ],
            PackageArchiveLayout.Standard);
    }

    [Theory]
    [InlineData("package", false)]
    [InlineData("Package", false)]
    [InlineData("package/", true)]
    [InlineData("package/README.md", false)]
    [InlineData("package/nested/package.json", false)]
    [InlineData("package/Package.json", false)]
    [InlineData("package/PACKAGE.JSON", false)]
    [InlineData("package/package.json/", true)]
    [InlineData("package/package.json/child.json", false)]
    [InlineData("Package/package.json", false)]
    [InlineData("PACKAGE/package.json", false)]
    public async Task Locate_RootManifestWithIncidentalPackageEntry_UsesRootWithoutMutation(
        string incidentalPath,
        bool isDirectory)
    {
        ArbitraryTarEntry incidentalEntry = isDirectory
            ? ArbitraryTarBuilder.Directory(incidentalPath)
            : ArbitraryTarBuilder.File(incidentalPath, "not a required manifest");

        await AssertLocatedWithoutMutationAsync(
            [
                ArbitraryTarBuilder.File("package.json", Manifest),
                ArbitraryTarBuilder.File("source/package.json", "{"),
                incidentalEntry
            ],
            PackageArchiveLayout.Legacy);
    }

    [Theory]
    [InlineData("Package.json", false)]
    [InlineData("PACKAGE.JSON", false)]
    [InlineData("package.json/", true)]
    [InlineData("package.json/child.json", false)]
    public async Task Locate_StandardManifestWithNonAuthoritativeRootEntry_UsesStandardWithoutMutation(
        string incidentalPath,
        bool isDirectory)
    {
        ArbitraryTarEntry incidentalEntry = isDirectory
            ? ArbitraryTarBuilder.Directory(incidentalPath)
            : ArbitraryTarBuilder.File(incidentalPath, "not a required manifest");

        await AssertLocatedWithoutMutationAsync(
            [
                ArbitraryTarBuilder.File("package/package.json", Manifest),
                incidentalEntry
            ],
            PackageArchiveLayout.Standard);
    }

    [Theory]
    [InlineData(false, "{ malformed")]
    [InlineData(true, "{ malformed")]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    [InlineData(false, """{"name":"other.package","version":"2.0.0"}""")]
    [InlineData(true, """{"name":"other.package","version":"2.0.0"}""")]
    public async Task Locate_RequiredManifestContents_AreNotParsedOrReplacedByNestedManifest(
        bool standardLayout,
        string manifest)
    {
        await AssertLocatedWithoutMutationAsync(
            [
                ArbitraryTarBuilder.File(
                    standardLayout ? "package/package.json" : "package.json",
                    manifest),
                ArbitraryTarBuilder.File("source/package.json", Manifest),
                ArbitraryTarBuilder.File("package/nested/package.json", Manifest)
            ],
            standardLayout ? PackageArchiveLayout.Standard : PackageArchiveLayout.Legacy);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Locate_TwoAuthoritativeRegularManifests_IsRejectedWithoutMutation(
        bool identicalManifests,
        bool standardFirst)
    {
        ArbitraryTarEntry standard = ArbitraryTarBuilder.File(
            "package/package.json",
            Manifest);
        ArbitraryTarEntry legacy = ArbitraryTarBuilder.File(
            "package.json",
            identicalManifests ? Manifest : "{ malformed");

        await AssertRejectedWithoutMutationAsync(
            standardFirst ? [standard, legacy] : [legacy, standard]);
    }

    [Fact]
    public async Task Locate_EmptyInventory_IsRejectedWithoutMutation()
    {
        Directory.CreateDirectory(_testRoot);
        PackageArchiveInventory inventory = new PackageArchiveInventory(
            new PackageInstallLimits());
        Dictionary<string, string?> before = await SnapshotTreeAsync(_testRoot);

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => PackageArchiveContentLocator.Locate(
                _testRoot,
                inventory,
                Directive));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidArchive);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
        exception.Directive.ShouldBe(Directive);
        AssertSameTree(before, await SnapshotTreeAsync(_testRoot));
    }

    [Theory]
    [InlineData("README.md", false)]
    [InlineData("package", false)]
    [InlineData("package/", true)]
    [InlineData("package/resource.json", false)]
    [InlineData("wrapper/package.json", false)]
    [InlineData("wrapper/package/package.json", false)]
    [InlineData("package/nested/package.json", false)]
    [InlineData("package.json/", true)]
    [InlineData("package.json/child.json", false)]
    [InlineData("package/package.json/", true)]
    [InlineData("package/package.json/child.json", false)]
    [InlineData("Package.json", false)]
    [InlineData("PACKAGE.JSON", false)]
    [InlineData("Package/package.json", false)]
    [InlineData("PACKAGE/package.json", false)]
    [InlineData("package/Package.json", false)]
    [InlineData("package/PACKAGE.JSON", false)]
    public async Task Locate_MissingExactRegularAuthority_IsRejectedWithoutMutation(
        string entryPath,
        bool isDirectory)
    {
        ArbitraryTarEntry entry = isDirectory
            ? ArbitraryTarBuilder.Directory(entryPath)
            : ArbitraryTarBuilder.File(entryPath, Manifest);

        await AssertRejectedWithoutMutationAsync(
            [
                entry,
                ArbitraryTarBuilder.File("ancillary/deep/package.json", Manifest),
                ArbitraryTarBuilder.Directory("ancillary/empty/")
            ]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Locate_InventoryOnly_DoesNotRequireOrCreateAnExtractedDirectory(
        bool standardLayout)
    {
        PackageArchiveInventory inventory = new PackageArchiveInventory(
            new PackageInstallLimits());
        inventory.Add(
            PortableArchivePath.Create(
                standardLayout ? "package/package.json" : "package.json",
                isDirectory: false),
            PackageArchiveEntryKind.RegularFile,
            Directive);
        Directory.Exists(_testRoot).ShouldBeFalse();

        PackageArchiveLayoutResult result = PackageArchiveContentLocator.Locate(
            _testRoot,
            inventory,
            Directive);

        string expectedContentPath = standardLayout
            ? Path.Combine(_testRoot, "package")
            : _testRoot;
        result.Layout.ShouldBe(
            standardLayout ? PackageArchiveLayout.Standard : PackageArchiveLayout.Legacy);
        result.ContentPath.ShouldBe(expectedContentPath);
        result.ManifestPath.ShouldBe(Path.Combine(expectedContentPath, "package.json"));
        Directory.Exists(_testRoot).ShouldBeFalse();
    }

    private async Task AssertLocatedWithoutMutationAsync(
        ArbitraryTarEntry[] entries,
        PackageArchiveLayout expectedLayout)
    {
        (string extractedPath, PackageArchiveInventory inventory) = await ExtractAsync(entries);
        Dictionary<string, string?> before = await SnapshotTreeAsync(extractedPath);

        PackageArchiveLayoutResult result = PackageArchiveContentLocator.Locate(
            extractedPath,
            inventory,
            Directive);

        string expectedContentPath = expectedLayout == PackageArchiveLayout.Standard
            ? Path.Combine(extractedPath, "package")
            : extractedPath;
        result.Layout.ShouldBe(expectedLayout);
        result.ContentPath.ShouldBe(expectedContentPath);
        result.ManifestPath.ShouldBe(Path.Combine(expectedContentPath, "package.json"));
        AssertSameTree(before, await SnapshotTreeAsync(extractedPath));
    }

    private async Task AssertRejectedWithoutMutationAsync(ArbitraryTarEntry[] entries)
    {
        (string extractedPath, PackageArchiveInventory inventory) = await ExtractAsync(entries);
        Dictionary<string, string?> before = await SnapshotTreeAsync(extractedPath);

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => PackageArchiveContentLocator.Locate(
                extractedPath,
                inventory,
                Directive));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidArchive);
        exception.Stage.ShouldBe(PackageInstallStage.ArchiveValidation);
        exception.Directive.ShouldBe(Directive);
        AssertSameTree(before, await SnapshotTreeAsync(extractedPath));
    }

    private async Task<(string ExtractedPath, PackageArchiveInventory Inventory)> ExtractAsync(
        ArbitraryTarEntry[] entries)
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(entries);
        string extractedPath = Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
        ArchiveExtractionMetrics metrics = await TarballExtractor.ExtractAsync(
            archive,
            extractedPath,
            new PackageInstallLimits(),
            Directive,
            TestContext.Current.CancellationToken);
        PackageArchiveInventory? inventory = metrics.Inventory;
        inventory.ShouldNotBeNull();
        return (extractedPath, inventory);
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
}
