// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
using FhirPkg.Tests.Support;
using FhirPkg.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;
using RecordingFileOperations = FhirPkg.Tests.Cache.DiskPackageCacheIndexTests.RecordingFileOperations;

namespace FhirPkg.Tests;

public sealed class FhirPackageManagerIndexingTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(
        AppContext.BaseDirectory,
        $"fhir-manager-resources-{Guid.NewGuid():N}");

    public static TheoryData<bool, bool, string, string> IndexModes
    {
        get
        {
            TheoryData<bool, bool, string, string> cases = [];
            foreach (bool unmodified in new[] { false, true })
            {
                foreach (bool rootLayout in new[] { false, true })
                {
                    foreach (string operation in new[] { "eager", "lazy", "forced" })
                    {
                        foreach (string state in new[] { "valid", "missing", "invalid", "directory" })
                            cases.Add(unmodified, rootLayout, operation, state);
                    }
                }
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(IndexModes))]
    public async Task ResourceIndex_ModeMatrix_SearchesReadsAndPreservesPayload(
        bool unmodified,
        bool rootLayout,
        string operation,
        string state)
    {
        PackageReference reference = new("example.package", "1.0.0");
        string prefix = rootLayout ? string.Empty : "package/";
        List<ArbitraryTarEntry> entries =
        [
            ArbitraryTarBuilder.File(
                $"{prefix}package.json",
                """{"name":"example.package","version":"1.0.0"}"""),
            ArbitraryTarBuilder.File(
                $"{prefix}patient.json",
                CreateResourceJson("Patient", "generated", "https://example.test/generated")),
            ArbitraryTarBuilder.File("other/.index.json", "opaque ancillary index"),
            ArbitraryTarBuilder.File("other/package.json", "opaque ancillary manifest"),
            ArbitraryTarBuilder.Directory("empty/"),
        ];
        if (!rootLayout)
            entries.Add(ArbitraryTarBuilder.File(".index.json", "opaque root index"));
        if (state is "valid" or "invalid")
        {
            entries.Add(ArbitraryTarBuilder.File(
                $"{prefix}.index.json",
                state == "valid"
                    ? """
                      { "files" : [ { "filename" : "patient.json", "resourceType" : "Patient",
                        "id" : "publisher", "url" : "https://example.test/publisher" } ],
                        "index-version" : 2, "publisher-extension" : "retain me" }
                      """
                    : "unusable publisher index"));
        }
        else if (state == "directory")
        {
            entries.Add(ArbitraryTarBuilder.File(
                $"{prefix}.index.json/payload.txt",
                "occupied index directory"));
        }

        RecordingFileOperations fileOperations = new();
        using DiskPackageCache cache = CreateCache(fileOperations);
        RecordingLogger logger = new();
        using FhirPackageManager manager = CreateManager(
            cache,
            new PackageIndexer(NullLogger<PackageIndexer>.Instance),
            memoryCache: new MemoryResourceCache(10),
            options: new FhirPackageManagerOptions { Unmodified = unmodified },
            logger: logger);
        IFhirPackageManager compatibilitySurface = manager;
        using MemoryStream archive = ArbitraryTarBuilder.Create([.. entries]);
        PackageRecord installed = operation == "eager"
            ? await manager.InstallAsync(
                reference, archive, options: null, TestContext.Current.CancellationToken)
            : await cache.InstallAsync(
                reference,
                archive,
                new InstallCacheOptions { Unmodified = unmodified },
                TestContext.Current.CancellationToken);
        string indexPath = Path.Combine(installed.ContentPath, ".index.json");
        bool persistenceFails = !unmodified && state == "directory";
        if (persistenceFails)
        {
            if (operation == "eager")
            {
                installed.Index.ShouldBeNull();
                logger.Warnings.ShouldContain(message =>
                    message.Contains("was installed", StringComparison.Ordinal));
            }
            else if (operation == "lazy")
            {
                await Should.ThrowAsync<IOException>(() => manager.FindResourcesAsync(
                    new ResourceSearchCriteria { PackageScope = reference.FhirDirective },
                    TestContext.Current.CancellationToken));
            }
            else
            {
                await Should.ThrowAsync<IOException>(() => compatibilitySurface.IndexPackageAsync(
                    reference,
                    new IndexingOptions { ForceReindex = true },
                    TestContext.Current.CancellationToken));
            }

            (await cache.IsInstalledAsync(reference, TestContext.Current.CancellationToken))
                .ShouldBeTrue();
            Directory.Exists(indexPath).ShouldBeTrue();
            fileOperations.DeletedPaths.ShouldNotContain(indexPath);
        }
        else
        {
            if (operation == "forced")
            {
                PackageIndex? forced = await compatibilitySurface.IndexPackageAsync(
                    reference,
                    new IndexingOptions { ForceReindex = true },
                    TestContext.Current.CancellationToken);
                forced!.Files.Single().Id.ShouldBe("generated");
            }

            string expectedId = state == "valid" && operation != "forced"
                ? "publisher"
                : "generated";
            IReadOnlyList<ResourceInfo> resources = await manager.FindResourcesAsync(
                new ResourceSearchCriteria { PackageScope = reference.FhirDirective },
                TestContext.Current.CancellationToken);
            ResourceInfo resource = resources.ShouldHaveSingleItem();
            resource.Id.ShouldBe(expectedId);
            resource.PackageName.ShouldBe(reference.Name);
            resource.PackageVersion.ShouldBe(reference.Version);
            resource.FilePath.ShouldBe("patient.json");
            (await compatibilitySurface.FindByCanonicalUrlAsync(
                $"https://example.test/{expectedId}",
                reference.FhirDirective,
                TestContext.Current.CancellationToken)).ShouldBe(resource);
            (await compatibilitySurface.FindByResourceTypeAsync(
                "Patient", reference.FhirDirective, TestContext.Current.CancellationToken))
                .ShouldHaveSingleItem().ShouldBe(resource);
            (await compatibilitySurface.ReadResourceAsync(
                resource, TestContext.Current.CancellationToken))!["id"]!
                .GetValue<string>().ShouldBe("generated");
            logger.Warnings.ShouldBeEmpty();
        }

        bool generatedOnDisk = !unmodified && !persistenceFails
            && (state != "valid" || operation == "forced");
        Dictionary<string, byte[]> expectedFiles = entries
            .Where(entry => entry.Content is not null)
            .ToDictionary(
                entry => Path.Combine(
                    installed.DirectoryPath,
                    rootLayout && !unmodified ? "package" : string.Empty,
                    entry.Name.Replace('/', Path.DirectorySeparatorChar)),
                entry => entry.Content!,
                StringComparer.Ordinal);
        foreach ((string path, byte[] bytes) in expectedFiles)
        {
            if (!generatedOnDisk || path != indexPath)
                (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken))
                    .ShouldBe(bytes);
        }
        Directory.GetFiles(installed.DirectoryPath, "*", SearchOption.AllDirectories)
            .ShouldBe(
                generatedOnDisk
                    ? expectedFiles.Keys.Append(indexPath).Distinct()
                    : expectedFiles.Keys,
                ignoreOrder: true);
        Directory.Exists(Path.Combine(
            installed.DirectoryPath,
            rootLayout && !unmodified ? "package" : string.Empty,
            "empty")).ShouldBeTrue();
        if (unmodified)
            fileOperations.AssertNoPayloadWrites(installed.DirectoryPath);
        PackageIndex? onDisk = await cache.GetIndexAsync(
            reference, TestContext.Current.CancellationToken);
        if (generatedOnDisk)
            onDisk!.Files.Single().Id.ShouldBe("generated");
        else if (state == "valid")
            onDisk!.Files.Single().Id.ShouldBe("publisher");
        else
            onDisk.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unmodified_IsRuntimeOnly_NormalReopenMayPersist(bool rootLayout)
    {
        PackageReference reference = new("@scope/example.package", "1.0.0");
        using DiskPackageCache installingCache = CreateCache();
        PackageRecord installed = await InstallPackageAsync(
            installingCache,
            reference,
            "patient.json",
            "Patient",
            "runtime",
            "https://example.test/runtime",
            rootLayout);
        string indexPath = Path.Combine(installed.ContentPath, ".index.json");
        byte[] metadataBefore = await File.ReadAllBytesAsync(
            Path.Combine(_cacheRoot, "packages.ini"), TestContext.Current.CancellationToken);
        for (int restart = 0; restart < 3; restart++)
        {
            bool unmodified = restart < 2;
            RecordingFileOperations fileOperations = new();
            using DiskPackageCache cache = CreateCache(fileOperations);
            using FhirPackageManager manager = CreateManager(
                cache,
                new PackageIndexer(NullLogger<PackageIndexer>.Instance),
                options: new FhirPackageManagerOptions { Unmodified = unmodified });
            IFhirPackageManager compatibilitySurface = manager;

            ResourceInfo resource = (await compatibilitySurface.FindResourcesAsync(
                new ResourceSearchCriteria { PackageScope = reference.FhirDirective },
                TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
            resource.PackageName.ShouldBe(reference.Name);
            resource.PackageVersion.ShouldBe(reference.Version);
            resource.FilePath.ShouldBe("patient.json");
            (await compatibilitySurface.ReadResourceAsync(
                resource, TestContext.Current.CancellationToken))!["id"]!
                .GetValue<string>().ShouldBe("runtime");
            File.Exists(indexPath).ShouldBe(!unmodified);
            if (unmodified)
                fileOperations.AssertNoPayloadWrites(installed.DirectoryPath);
            else
                fileOperations.AtomicDestinations.ShouldContain(indexPath);
            (await File.ReadAllBytesAsync(
                Path.Combine(_cacheRoot, "packages.ini"), TestContext.Current.CancellationToken))
                .ShouldBe(metadataBefore);
        }
    }

    [Fact]
    public async Task FhirPackageManagerResource_ExplicitIndexPersistsSearchesAndReads()
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        using DiskPackageCache cache = CreateCache();
        await InstallPackageAsync(
            cache,
            reference,
            "patient.json",
            "Patient",
            "patient-one",
            "https://example.test/Patient/one");
        using FhirPackageManager manager =
            CreateManager(
                cache,
                new PackageIndexer(
                    NullLogger<PackageIndexer>.Instance),
                new MemoryResourceCache(10));

        PackageIndex? index =
            await manager.IndexPackageAsync(
                reference,
                cancellationToken:
                    TestContext.Current.CancellationToken);
        ResourceInfo? canonical =
            await manager.FindByCanonicalUrlAsync(
                "https://example.test/Patient/one",
                reference.FhirDirective,
                TestContext.Current.CancellationToken);
        IReadOnlyList<ResourceInfo> patients =
            await manager.FindByResourceTypeAsync(
                "Patient",
                reference.FhirDirective,
                TestContext.Current.CancellationToken);
        JsonNode? resource =
            await manager.ReadResourceAsync(
                canonical!,
                TestContext.Current.CancellationToken);

        index.ShouldNotBeNull();
        index.Files.Single().Id.ShouldBe("patient-one");
        File.Exists(GetIndexPath(reference)).ShouldBeTrue();
        canonical.ShouldNotBeNull();
        patients.ShouldHaveSingleItem();
        resource!["id"]!.GetValue<string>()
            .ShouldBe("patient-one");

        IFhirPackageManager compatibilitySurface = manager;
        ResourceInfo? compatibilityResult =
            await compatibilitySurface.FindByCanonicalUrlAsync(
                "https://example.test/Patient/one",
                reference.FhirDirective,
                TestContext.Current.CancellationToken);
        compatibilityResult.ShouldBe(canonical);
    }

    [Fact]
    public async Task FhirPackageManagerResource_LazySearchIndexesOnlyRequestedScope()
    {
        PackageReference first = new(
            "example.package",
            "1.0.0");
        PackageReference second = new(
            "example.package",
            "2.0.0");
        using DiskPackageCache cache = CreateCache();
        await InstallPackageAsync(
            cache,
            first,
            "patient.json",
            "Patient",
            "patient-one",
            "https://example.test/Patient/one");
        await InstallPackageAsync(
            cache,
            second,
            "patient.json",
            "Patient",
            "patient-two",
            "https://example.test/Patient/two");
        using FhirPackageManager manager =
            CreateManager(
                cache,
                new PackageIndexer(
                    NullLogger<PackageIndexer>.Instance),
                new MemoryResourceCache(10));

        IReadOnlyList<ResourceInfo> resources =
            await manager.FindByResourceTypeAsync(
                "Patient",
                second.FhirDirective,
                TestContext.Current.CancellationToken);

        resources.ShouldHaveSingleItem();
        resources[0].Id.ShouldBe("patient-two");
        resources[0].PackageVersion.ShouldBe("2.0.0");
        File.Exists(GetIndexPath(second)).ShouldBeTrue();
        File.Exists(GetIndexPath(first)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceIndex_ForceReindexInvalidatesResourceCache(bool unmodified)
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        using DiskPackageCache cache = CreateCache();
        PackageRecord installed =
            await InstallPackageAsync(
                cache,
                reference,
                "patient.json",
                "Patient",
                "before",
                "https://example.test/Patient/resource");
        using FhirPackageManager manager =
            CreateManager(
                cache,
                new PackageIndexer(
                    NullLogger<PackageIndexer>.Instance),
                new MemoryResourceCache(10),
                new FhirPackageManagerOptions { Unmodified = unmodified });
        _ = await manager.IndexPackageAsync(
            reference,
            cancellationToken:
                TestContext.Current.CancellationToken);
        ResourceInfo resource =
            (await manager.FindByCanonicalUrlAsync(
                "https://example.test/Patient/resource",
                reference.FhirDirective,
                TestContext.Current.CancellationToken))!;
        (await manager.ReadResourceAsync(
            resource,
            TestContext.Current.CancellationToken))!["id"]!
            .GetValue<string>()
            .ShouldBe("before");
        await File.WriteAllTextAsync(
            Path.Combine(
                installed.ContentPath,
                "patient.json"),
            CreateResourceJson(
                "Patient",
                "after",
                "https://example.test/Patient/resource"),
            TestContext.Current.CancellationToken);

        PackageIndex? reindexed =
            await manager.IndexPackageAsync(
                reference,
                new IndexingOptions
                {
                    ForceReindex = true,
                },
                TestContext.Current.CancellationToken);
        PackageIndex? persisted =
            await cache.GetIndexAsync(
                reference,
                TestContext.Current.CancellationToken);
        JsonNode? refreshed =
            await manager.ReadResourceAsync(
                resource,
                TestContext.Current.CancellationToken);

        reindexed!.Files.Single().Id.ShouldBe("after");
        if (unmodified)
            persisted.ShouldBeNull();
        else
            persisted!.Files.Single().Id.ShouldBe("after");
        (await manager.FindByCanonicalUrlAsync(
            "https://example.test/Patient/resource",
            reference.FhirDirective,
            TestContext.Current.CancellationToken))!.Id.ShouldBe("after");
        refreshed!["id"]!.GetValue<string>()
            .ShouldBe("after");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ResourceCache_MutationInvalidatesOnlyAffectedPackage(
        bool unmodified,
        bool rootLayout)
    {
        PackageReference first = new(
            "first.package",
            "1.0.0");
        PackageReference second = new(
            "second.package",
            "1.0.0");
        using DiskPackageCache cache = CreateCache();
        using DiskPackageCache mutatingCache = CreateCache();
        _ = await InstallPackageAsync(
            cache,
            first,
            "resource.json",
            "Patient",
            "first-old",
            "https://example.test/first",
            rootLayout);
        PackageRecord secondRecord =
            await InstallPackageAsync(
                cache,
                second,
                "resource.json",
                "Patient",
                "second",
                "https://example.test/second");
        MemoryResourceCache memoryCache = new(10);
        using FhirPackageManager manager =
            CreateManager(
                cache,
                new PackageIndexer(
                    NullLogger<PackageIndexer>.Instance),
                memoryCache,
                new FhirPackageManagerOptions { Unmodified = unmodified });
        ResourceInfo firstResource =
            (await manager.FindByCanonicalUrlAsync(
                "https://example.test/first",
                first.FhirDirective,
                TestContext.Current.CancellationToken))!;
        ResourceInfo secondResource =
            (await manager.FindByCanonicalUrlAsync(
                "https://example.test/second",
                second.FhirDirective,
                TestContext.Current.CancellationToken))!;
        _ = await manager.ReadResourceAsync(
            firstResource,
            TestContext.Current.CancellationToken);
        _ = await manager.ReadResourceAsync(
            secondResource,
            TestContext.Current.CancellationToken);

        using MemoryStream replacement = CreateArchive(
            first,
            "resource.json",
            "Patient",
            "first-new",
            "https://example.test/first",
            rootLayout);
        _ = await mutatingCache.InstallAsync(
            first,
            replacement,
            new InstallCacheOptions
            {
                OverwriteExisting = true,
                VerifyChecksum = false,
                Unmodified = rootLayout,
            },
            TestContext.Current.CancellationToken);
        ResourceInfo refreshedInfo =
            (await manager.FindByCanonicalUrlAsync(
                "https://example.test/first",
                first.FhirDirective,
                TestContext.Current.CancellationToken))!;
        File.Delete(
            Path.Combine(
                secondRecord.ContentPath,
                "resource.json"));

        JsonNode? refreshed =
            await manager.ReadResourceAsync(
                refreshedInfo,
                TestContext.Current.CancellationToken);
        JsonNode? retained =
            await manager.ReadResourceAsync(
                secondResource,
                TestContext.Current.CancellationToken);

        refreshed!["id"]!.GetValue<string>()
            .ShouldBe("first-new");
        retained!["id"]!.GetValue<string>()
            .ShouldBe("second");
        refreshedInfo.Id.ShouldBe("first-new");
        memoryCache.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ResourceCache_LegacyWriterCannotMaskReplacementGeneration()
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        using DiskPackageCache cache = CreateCache();
        using DiskPackageCache mutatingCache = CreateCache();
        await InstallPackageAsync(
            cache,
            reference,
            "patient.json",
            "Patient",
            "before",
            "https://example.test/Patient/resource");
        string metadataKey =
            reference.FhirDirective;
        string oldGeneration =
            (await cache.GetMetadataAsync(
                TestContext.Current.CancellationToken))
            .Packages[metadataKey]
            .ContentGeneration!;
        MemoryResourceCache memoryCache = new(10);
        using FhirPackageManager manager =
            CreateManager(
                cache,
                new PackageIndexer(
                    NullLogger<PackageIndexer>.Instance),
                memoryCache);
        ResourceInfo resource =
            (await manager.FindByCanonicalUrlAsync(
                "https://example.test/Patient/resource",
                reference.FhirDirective,
                TestContext.Current.CancellationToken))!;
        _ = await manager.ReadResourceAsync(
            resource,
            TestContext.Current.CancellationToken);
        using MemoryStream replacement = CreateArchive(
            reference,
            "patient.json",
            "Patient",
            "after",
            "https://example.test/Patient/resource");
        _ = await mutatingCache.InstallAsync(
            reference,
            replacement,
            new InstallCacheOptions
            {
                OverwriteExisting = true,
                VerifyChecksum = false,
            },
            TestContext.Current.CancellationToken);

        string metadataPath =
            Path.Combine(
                _cacheRoot,
                "packages.ini");
        IReadOnlyDictionary<
            string,
            IReadOnlyDictionary<string, string>> parsed =
            await IniParser.ParseFileAsync(
                metadataPath,
                TestContext.Current.CancellationToken);
        Dictionary<
            string,
            IReadOnlyDictionary<string, string>> rewritten =
            parsed.ToDictionary(
                section => section.Key,
                section =>
                    (IReadOnlyDictionary<string, string>)
                    new Dictionary<string, string>(
                        section.Value,
                        StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> generations =
            new(
                rewritten["package-content-generations"],
                StringComparer.OrdinalIgnoreCase)
            {
                [metadataKey] = oldGeneration,
            };
        rewritten["package-content-generations"] =
            generations;
        await IniParser.WriteFileAsync(
            metadataPath,
            rewritten,
            TestContext.Current.CancellationToken);

        JsonNode? refreshed =
            await manager.ReadResourceAsync(
                resource,
                TestContext.Current.CancellationToken);

        refreshed!["id"]!.GetValue<string>()
            .ShouldBe("after");
    }

    [Fact]
    public async Task ResourceIndex_CrossInstanceRemovalReconcilesSearch()
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        using DiskPackageCache cache = CreateCache();
        using DiskPackageCache mutatingCache = CreateCache();
        await InstallPackageAsync(
            cache,
            reference,
            "patient.json",
            "Patient",
            "patient",
            "https://example.test/Patient/resource");
        using FhirPackageManager manager =
            CreateManager(
                cache,
                new PackageIndexer(
                    NullLogger<PackageIndexer>.Instance));
        (await manager.FindByCanonicalUrlAsync(
            "https://example.test/Patient/resource",
            reference.FhirDirective,
            TestContext.Current.CancellationToken))
            .ShouldNotBeNull();

        (await mutatingCache.RemoveAsync(
            reference,
            TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        (await manager.FindByCanonicalUrlAsync(
            "https://example.test/Patient/resource",
            reference.FhirDirective,
            TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceIndex_ConcurrentOverwriteCannotResurrectRegistration(bool unmodified)
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        using DiskPackageCache cache = CreateCache();
        await InstallPackageAsync(
            cache,
            reference,
            "patient.json",
            "Patient",
            "before",
            "https://example.test/before");
        BlockingManagedIndexer indexer = new();
        using FhirPackageManager manager =
            CreateManager(
                cache,
                indexer,
                options: new FhirPackageManagerOptions { Unmodified = unmodified });
        Task<PackageIndex?> indexingTask =
            manager.IndexPackageAsync(
                reference,
                new IndexingOptions
                {
                    ForceReindex = true,
                },
                TestContext.Current.CancellationToken);
        await indexer.Entered;
        using MemoryStream replacement = CreateArchive(
            reference,
            "patient.json",
            "Patient",
            "after",
            "https://example.test/after");
        Task<PackageRecord> overwriteTask =
            cache.InstallAsync(
                reference,
                replacement,
                new InstallCacheOptions
                {
                    OverwriteExisting = true,
                    VerifyChecksum = false,
                },
                TestContext.Current.CancellationToken);
        await Task.Delay(
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken);
        overwriteTask.IsCompleted.ShouldBeFalse();

        indexer.Release();
        (await indexingTask).ShouldNotBeNull();
        _ = await overwriteTask;

        indexer.Events.ShouldBe(
        [
            "register",
            "unregister",
        ]);
        indexer.IsRegistered.ShouldBeFalse();
    }

    [Fact]
    public async Task ResourceCache_ZeroCapacityBypassesMemoryCaching()
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        using DiskPackageCache cache = CreateCache();
        PackageRecord installed =
            await InstallPackageAsync(
                cache,
                reference,
                "patient.json",
                "Patient",
                "before",
                "https://example.test/Patient/resource");
        using FhirPackageManager manager =
            CreateManager(
                cache,
                new PackageIndexer(
                    NullLogger<PackageIndexer>.Instance),
                memoryCache: null,
                new FhirPackageManagerOptions
                {
                    ResourceCacheSize = 0,
                });
        ResourceInfo resource =
            (await manager.FindByCanonicalUrlAsync(
                "https://example.test/Patient/resource",
                reference.FhirDirective,
                TestContext.Current.CancellationToken))!;
        JsonNode? first =
            await manager.ReadResourceAsync(
                resource,
                TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(
                installed.ContentPath,
                "patient.json"),
            CreateResourceJson(
                "Patient",
                "after",
                "https://example.test/Patient/resource"),
            TestContext.Current.CancellationToken);

        JsonNode? second =
            await manager.ReadResourceAsync(
                resource,
                TestContext.Current.CancellationToken);

        first!["id"]!.GetValue<string>().ShouldBe("before");
        second!["id"]!.GetValue<string>().ShouldBe("after");
    }

    [Fact]
    public async Task ResourceCache_CustomCacheWithoutGenerationBypassesMemoryCaching()
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        Mock<IPackageCache> cache = new();
        cache.SetupSequence(value => value.GetFileContentAsync(
                reference,
                "patient.json",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                CreateResourceJson(
                    "Patient",
                    "before",
                    "https://example.test/Patient/resource"))
            .ReturnsAsync(
                CreateResourceJson(
                    "Patient",
                    "after",
                    "https://example.test/Patient/resource"));
        using FhirPackageManager manager =
            CreateManager(
                cache.Object,
                new PackageIndexer(
                    NullLogger<PackageIndexer>.Instance),
                new MemoryResourceCache(10));
        ResourceInfo resource = new()
        {
            ResourceType = "Patient",
            PackageName = reference.Name,
            PackageVersion = reference.Version,
            FilePath = "patient.json",
        };

        JsonNode? first =
            await manager.ReadResourceAsync(
                resource,
                TestContext.Current.CancellationToken);
        JsonNode? second =
            await manager.ReadResourceAsync(
                resource,
                TestContext.Current.CancellationToken);

        first!["id"]!.GetValue<string>()
            .ShouldBe("before");
        second!["id"]!.GetValue<string>()
            .ShouldBe("after");
        cache.Verify(value => value.GetFileContentAsync(
            reference,
            "patient.json",
            It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ResourceCache_DisposeUnsubscribesFromMutations()
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        using DiskPackageCache cache = CreateCache();
        await InstallPackageAsync(
            cache,
            reference,
            "patient.json",
            "Patient",
            "before",
            "https://example.test/Patient/resource");
        MemoryResourceCache memoryCache = new(10);
        FhirPackageManager manager =
            CreateManager(
                cache,
                new PackageIndexer(
                    NullLogger<PackageIndexer>.Instance),
                memoryCache);
        ResourceInfo resource =
            (await manager.FindByCanonicalUrlAsync(
                "https://example.test/Patient/resource",
                reference.FhirDirective,
                TestContext.Current.CancellationToken))!;
        _ = await manager.ReadResourceAsync(
            resource,
            TestContext.Current.CancellationToken);
        manager.Dispose();

        using MemoryStream replacement = CreateArchive(
            reference,
            "patient.json",
            "Patient",
            "after",
            "https://example.test/Patient/resource");
        _ = await cache.InstallAsync(
            reference,
            replacement,
            new InstallCacheOptions
            {
                OverwriteExisting = true,
                VerifyChecksum = false,
            },
            TestContext.Current.CancellationToken);

        memoryCache.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ResourceIndex_RestartLoadsPersistedIndexWithoutRegeneration()
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        using DiskPackageCache cache = CreateCache();
        PackageRecord installed =
            await InstallPackageAsync(
                cache,
                reference,
                "patient.json",
                "Patient",
                "persisted",
                "https://example.test/persisted");
        using (FhirPackageManager firstManager =
               CreateManager(
                   cache,
                   new PackageIndexer(
                       NullLogger<PackageIndexer>.Instance)))
        {
            _ = await firstManager.IndexPackageAsync(
                reference,
                cancellationToken:
                    TestContext.Current.CancellationToken);
        }

        await File.WriteAllTextAsync(
            Path.Combine(
                installed.ContentPath,
                "patient.json"),
            CreateResourceJson(
                "Patient",
                "changed",
                "https://example.test/changed"),
            TestContext.Current.CancellationToken);
        using FhirPackageManager restartedManager =
            CreateManager(
                cache,
                new PackageIndexer(
                    NullLogger<PackageIndexer>.Instance));

        ResourceInfo? persisted =
            await restartedManager.FindByCanonicalUrlAsync(
                "https://example.test/persisted",
                reference.FhirDirective,
                TestContext.Current.CancellationToken);
        ResourceInfo? changed =
            await restartedManager.FindByCanonicalUrlAsync(
                "https://example.test/changed",
                reference.FhirDirective,
                TestContext.Current.CancellationToken);

        persisted.ShouldNotBeNull();
        persisted.Id.ShouldBe("persisted");
        changed.ShouldBeNull();
    }

    [Fact]
    public async Task FhirPackageManagerResource_StandaloneManagerPersistsScopedIndexes()
    {
        PackageReference reference = new(
            "@scope/example.package",
            "1.0.0");
        using DiskPackageCache installingCache =
            CreateCache();
        await InstallPackageAsync(
            installingCache,
            reference,
            "patient.json",
            "Patient",
            "scoped",
            "https://example.test/scoped");
        FhirPackageManagerOptions options = new()
        {
            CachePath = _cacheRoot,
            IncludeCiBuilds = false,
            IncludeHl7WebsiteFallback = false,
            Registries = [],
        };
        using FhirPackageManager manager = new(options);

        ResourceInfo? resource =
            await manager.FindByCanonicalUrlAsync(
                "https://example.test/scoped",
                reference.FhirDirective,
                TestContext.Current.CancellationToken);

        resource.ShouldNotBeNull();
        resource.PackageName.ShouldBe(
            "@scope/example.package");
        File.Exists(GetIndexPath(reference)).ShouldBeTrue();

        (await manager.RemoveAsync(
            reference.FhirDirective,
            TestContext.Current.CancellationToken))
            .ShouldBeTrue();
        (await manager.FindByCanonicalUrlAsync(
            "https://example.test/scoped",
            reference.FhirDirective,
            TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task FhirPackageManagerResource_IndexFailurePropagatesAndCanRetry()
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        PackageRecord package = new()
        {
            Reference = reference,
            DirectoryPath = _cacheRoot,
            ContentPath = _cacheRoot,
            Manifest = new PackageManifest
            {
                Name = reference.Name,
                Version = reference.Version!,
            },
        };
        Mock<IPackageCache> cache = new();
        cache.Setup(value => value.GetPackageAsync(
                reference,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(package);
        RetryingPackageIndexer indexer = new();
        using FhirPackageManager manager =
            CreateManager(
                cache.Object,
                indexer);

        await Should.ThrowAsync<InvalidOperationException>(
            () => manager.IndexPackageAsync(
                reference,
                cancellationToken:
                    TestContext.Current.CancellationToken));
        PackageIndex? retried =
            await manager.IndexPackageAsync(
                reference,
                cancellationToken:
                    TestContext.Current.CancellationToken);

        retried.ShouldNotBeNull();
        indexer.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task FhirPackageManagerResource_EagerIndexFailurePreservesInstallationSuccess()
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        ResolvedDirective resolved = new()
        {
            Reference = reference,
            TarballUri =
                new Uri("https://example.test/package.tgz"),
        };
        PackageRecord installed = new()
        {
            Reference = reference,
            DirectoryPath = _cacheRoot,
            ContentPath = _cacheRoot,
            Manifest = new PackageManifest
            {
                Name = reference.Name,
                Version = reference.Version!,
            },
        };
        Mock<IHardenedPackageCache> cache = new();
        cache.Setup(value => value.InspectAsync(
                reference,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HardenedPackageCacheInspection
            {
                State = HardenedPackageCacheState.Missing,
            });
        cache.Setup(value => value.InstallAsync(
                reference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(installed);
        Mock<IRegistryClient> registry = new();
        registry.Setup(value => value.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);
        registry.Setup(value => value.DownloadAsync(
                resolved,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1, 2, 3]),
                ContentType = "application/gzip",
            });
        AlwaysFailingPackageIndexer indexer = new();
        using FhirPackageManager manager =
            CreateManager(
                cache.Object,
                indexer,
                registry.Object);

        PackageRecord? result =
            await manager.InstallAsync(
                reference.FhirDirective,
                cancellationToken:
                    TestContext.Current.CancellationToken);

        result.ShouldBe(installed);
        indexer.CallCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FhirPackageManagerIndexing_EagerIndexReturnsCurrentGeneration(bool unmodified)
    {
        PackageReference reference = new(
            "example.package",
            "1.0.0");
        using DiskPackageCache cache = CreateCache();
        using DiskPackageCache mutatingCache = CreateCache();
        using FhirPackageManager manager =
            CreateManager(
                cache,
                new PackageIndexer(
                    NullLogger<PackageIndexer>.Instance),
                options: new FhirPackageManagerOptions { Unmodified = unmodified });
        using MemoryStream initial = CreateArchive(
            reference,
            "patient.json",
            "Patient",
            "before",
            "https://example.test/Patient/resource");
        InlineProgress progress = new(report =>
        {
            if (report.Phase
                    != PackageProgressPhase.Indexing)
            {
                return;
            }

            using MemoryStream replacement =
                CreateArchive(
                    reference,
                    "patient.json",
                    "Patient",
                    "after",
                    "https://example.test/Patient/resource");
            _ = mutatingCache.InstallAsync(
                    reference,
                    replacement,
                    new InstallCacheOptions
                    {
                        OverwriteExisting = true,
                        VerifyChecksum = false,
                    },
                    TestContext.Current.CancellationToken)
                .GetAwaiter()
                .GetResult();
        });

        PackageRecord installed =
            await manager.InstallAsync(
                reference,
                initial,
                new PackageSourceInstallOptions
                {
                    Progress = progress,
                },
                TestContext.Current.CancellationToken);

        installed.Index!.Files.Single().Id
            .ShouldBe("after");
    }

    [Fact]
    public async Task InterfaceCompatibility_UnsupportedManagerFailsClearly()
    {
        Mock<IFhirPackageManager> manager = new();

        NotSupportedException exception =
            await Should.ThrowAsync<NotSupportedException>(
                () => manager.Object.FindResourcesAsync(
                    new ResourceSearchCriteria(),
                    TestContext.Current.CancellationToken));

        exception.Message.ShouldContain(
            "indexed resource operations");
    }

    [Fact]
    public void InterfaceCompatibility_DependencyInjectionUsesSameSingleton()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddFhirPackageManagement(options =>
        {
            options.CachePath = _cacheRoot;
            options.Registries.Clear();
            options.IncludeCiBuilds = false;
            options.IncludeHl7WebsiteFallback = false;
        });
        using ServiceProvider provider =
            services.BuildServiceProvider();

        IFhirPackageManager manager =
            provider.GetRequiredService<IFhirPackageManager>();
        IFhirPackageResourceManager resourceManager =
            provider.GetRequiredService<IFhirPackageResourceManager>();

        ReferenceEquals(manager, resourceManager)
            .ShouldBeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot))
            Directory.Delete(_cacheRoot, recursive: true);
    }

    private DiskPackageCache CreateCache(IPackageCacheFileOperations? fileOperations = null) =>
        new(
            _cacheRoot,
            logger: null,
            timeProvider: null,
            new PackageInstallLimits(),
            fileOperations ?? SystemPackageCacheFileOperations.Instance,
            NullPackageCacheFaultObserver.Instance);

    private FhirPackageManager CreateManager(
        IPackageCache cache,
        IPackageIndexer indexer,
        IRegistryClient? registry = null,
        MemoryResourceCache? memoryCache = null,
        FhirPackageManagerOptions? options = null,
        ILogger<FhirPackageManager>? logger = null) =>
        new(
            cache,
            registry ?? new Mock<IRegistryClient>().Object,
            new Mock<IVersionResolver>().Object,
            new Mock<IDependencyResolver>().Object,
            indexer,
            options ?? new FhirPackageManagerOptions(),
            logger ?? NullLogger<FhirPackageManager>.Instance,
            memoryCache);

    private FhirPackageManager CreateManager(
        IPackageCache cache,
        IPackageIndexer indexer,
        MemoryResourceCache? memoryCache,
        FhirPackageManagerOptions? options = null) =>
        CreateManager(
            cache,
            indexer,
            registry: null,
            memoryCache,
            options);

    private async Task<PackageRecord> InstallPackageAsync(
        DiskPackageCache cache,
        PackageReference reference,
        string fileName,
        string resourceType,
        string id,
        string canonicalUrl,
        bool rootLayout = false)
    {
        using MemoryStream archive = CreateArchive(
            reference,
            fileName,
            resourceType,
            id,
            canonicalUrl,
            rootLayout);
        return await cache.InstallAsync(
            reference,
            archive,
            new InstallCacheOptions
            {
                VerifyChecksum = false,
                Unmodified = rootLayout,
            },
            TestContext.Current.CancellationToken);
    }

    private MemoryStream CreateArchive(
        PackageReference reference,
        string fileName,
        string resourceType,
        string id,
        string canonicalUrl,
        bool rootLayout = false) =>
        ArbitraryTarBuilder.Create(
        [
            ArbitraryTarBuilder.File(
                rootLayout ? "package.json" : "package/package.json",
                $$"""{"name":"{{reference.Name}}","version":"{{reference.Version}}"}"""),
            ArbitraryTarBuilder.File(
                rootLayout ? fileName : $"package/{fileName}",
                CreateResourceJson(
                    resourceType,
                    id,
                    canonicalUrl)),
        ]);

    private static string CreateResourceJson(
        string resourceType,
        string id,
        string canonicalUrl) =>
        $$"""{"resourceType":"{{resourceType}}","id":"{{id}}","url":"{{canonicalUrl}}"}""";

    private string GetIndexPath(
        PackageReference reference) =>
        Path.Combine(
            PackageCacheKey.Create(reference)
                .GetPackageDirectoryPath(_cacheRoot),
            "package",
            ".index.json");

    private sealed class RetryingPackageIndexer :
        IPackageIndexer
    {
        internal int CallCount { get; private set; }

        public Task<PackageIndex> IndexPackageAsync(
            string packageContentPath,
            IndexingOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 1)
            {
                throw new InvalidOperationException(
                    "Transient indexing failure.");
            }

            return Task.FromResult(
                CreateIndex());
        }

        public IReadOnlyList<ResourceInfo> FindResources(
            ResourceSearchCriteria criteria) =>
            [];

        public ResourceInfo? FindByCanonicalUrl(
            string canonicalUrl) =>
            null;

        public IReadOnlyList<ResourceInfo> FindByResourceType(
            string resourceType,
            string? packageScope = null) =>
            [];
    }

    private sealed class InlineProgress(
        Action<PackageProgress> report) :
        IProgress<PackageProgress>
    {
        public void Report(
            PackageProgress value) =>
            report(value);
    }

    private sealed class RecordingLogger : ILogger<FhirPackageManager>
    {
        internal List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class AlwaysFailingPackageIndexer :
        IPackageIndexer
    {
        internal int CallCount { get; private set; }

        public Task<PackageIndex> IndexPackageAsync(
            string packageContentPath,
            IndexingOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException(
                "Indexing failed.");
        }

        public IReadOnlyList<ResourceInfo> FindResources(
            ResourceSearchCriteria criteria) =>
            [];

        public ResourceInfo? FindByCanonicalUrl(
            string canonicalUrl) =>
            null;

        public IReadOnlyList<ResourceInfo> FindByResourceType(
            string resourceType,
            string? packageScope = null) =>
            [];
    }

    private sealed class BlockingManagedIndexer :
        IPackageIndexer,
        IManagedPackageIndexer
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _events = new();
        private int _registered;

        internal Task Entered => _entered.Task;

        internal IReadOnlyList<string> Events =>
            [.. _events];

        internal bool IsRegistered =>
            Volatile.Read(ref _registered) == 1;

        internal void Release() =>
            _release.TrySetResult();

        public Task<PackageIndex> IndexPackageAsync(
            string packageContentPath,
            IndexingOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateIndex());

        public async Task<PackageIndex> BuildIndexAsync(
            PackageRecord package,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(
                cancellationToken);
            return CreateIndex();
        }

        public void RegisterPersistedIndex(
            PackageReference reference,
            PackageIndex index)
        {
            Volatile.Write(
                ref _registered,
                1);
            _events.Enqueue("register");
        }

        public bool Unregister(
            PackageReference reference)
        {
            bool removed =
                Interlocked.Exchange(
                    ref _registered,
                    0) == 1;
            if (removed)
                _events.Enqueue("unregister");
            return removed;
        }

        public void Clear() =>
            Volatile.Write(
                ref _registered,
                0);

        public IReadOnlyList<ResourceInfo> FindManagedResources(
            ResourceSearchCriteria criteria) =>
            [];

        public IReadOnlyList<ResourceInfo> FindManagedByResourceType(
            string resourceType,
            string? packageScope = null) =>
            [];

        public IReadOnlyList<ResourceInfo> FindResources(
            ResourceSearchCriteria criteria) =>
            [];

        public ResourceInfo? FindByCanonicalUrl(
            string canonicalUrl) =>
            null;

        public IReadOnlyList<ResourceInfo> FindByResourceType(
            string resourceType,
            string? packageScope = null) =>
            [];
    }

    private static PackageIndex CreateIndex() =>
        new()
        {
            Files =
            [
                new ResourceIndexEntry
                {
                    Filename = "patient.json",
                    ResourceType = "Patient",
                    Id = "patient",
                },
            ],
        };
}
