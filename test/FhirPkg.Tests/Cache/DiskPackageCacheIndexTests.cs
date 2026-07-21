// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Tests.Support;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Cache;

public sealed class DiskPackageCacheIndexTests : IDisposable
{
    private static readonly PackageReference s_reference = new(
        "example.package",
        "1.0.0");

    private readonly string _cacheRoot = Path.Combine(
        AppContext.BaseDirectory,
        $"fhir-index-store-{Guid.NewGuid():N}");

    public static TheoryData<string> InvalidIndexJson =>
        new()
        {
            """{"index-version":1,"files":[]}""",
            """{"index-version":2,"files":null}""",
            """{"index-version":2,"files":[{"filename":"","resourceType":"Patient"}]}""",
            """{"index-version":2,"files":[{"filename":"patient.json","resourceType":" "}]}""",
            """{"index-version":2,"files":[{"filename":"../patient.json","resourceType":"Patient"}]}""",
            """{"index-version":2,"files":[{"filename":"patient.json","resourceType":"Patient"},{"filename":"PATIENT.json","resourceType":"Patient"}]}""",
            """{"index-version":2,"files":[{"filename":"missing.json","resourceType":"Patient"}]}"""
        };

    [Fact]
    public async Task ValidExistingIndex_PopulatesEveryPackageRecordRead()
    {
        using DiskPackageCache cache = CreateCache();
        using MemoryStream archive = CreateArchive(
            patientId: "existing",
            indexJson: CreateIndexJson("existing"));

        PackageRecord installed = await cache.InstallAsync(
            s_reference,
            archive,
            new InstallCacheOptions { VerifyChecksum = false },
            TestContext.Current.CancellationToken);
        PackageRecord? retrieved = await cache.GetPackageAsync(
            s_reference,
            TestContext.Current.CancellationToken);
        IReadOnlyList<PackageRecord> listed =
            await cache.ListPackagesAsync(
                ct: TestContext.Current.CancellationToken);
        PackageIndex? index = await cache.GetIndexAsync(
            s_reference,
            TestContext.Current.CancellationToken);

        installed.Index!.Files[0].Id.ShouldBe("existing");
        retrieved!.Index!.Files[0].Id.ShouldBe("existing");
        listed.Single().Index!.Files[0].Id.ShouldBe("existing");
        index!.Files[0].Id.ShouldBe("existing");
    }

    [Fact]
    public async Task IndexingList_DoesNotHydratePersistedIndexes()
    {
        using DiskPackageCache cache = CreateCache();
        using MemoryStream archive = CreateArchive(
            patientId: "existing",
            indexJson: CreateIndexJson("existing"));
        _ = await cache.InstallAsync(
            s_reference,
            archive,
            new InstallCacheOptions
            {
                VerifyChecksum = false,
            },
            TestContext.Current.CancellationToken);
        IPackageCacheIndexStore store = cache;

        IReadOnlyList<PackageRecord> indexingRecords =
            await store.ListPackagesForIndexingAsync(
                s_reference.Name,
                s_reference.Version,
                TestContext.Current.CancellationToken);
        IReadOnlyList<PackageRecord> publicRecords =
            await cache.ListPackagesAsync(
                s_reference.Name,
                s_reference.Version,
                TestContext.Current.CancellationToken);

        indexingRecords.Single().Index.ShouldBeNull();
        publicRecords.Single().Index.ShouldNotBeNull();
    }

    [Theory]
    [MemberData(nameof(InvalidIndexJson))]
    public async Task InvalidExistingIndex_IsDerivativeAbsence(
        string indexJson)
    {
        using DiskPackageCache cache = CreateCache();
        using MemoryStream archive = CreateArchive(
            patientId: "installed",
            indexJson);

        PackageRecord installed = await cache.InstallAsync(
            s_reference,
            archive,
            new InstallCacheOptions { VerifyChecksum = false },
            TestContext.Current.CancellationToken);

        installed.Index.ShouldBeNull();
        (await cache.GetPackageAsync(
            s_reference,
            TestContext.Current.CancellationToken))!.Index.ShouldBeNull();
        (await cache.GetIndexAsync(
            s_reference,
            TestContext.Current.CancellationToken)).ShouldBeNull();
        (await cache.IsInstalledAsync(
            s_reference,
            TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task GetOrCreateIndex_GeneratesAndPersistsAtomically()
    {
        RecordingFileOperations fileOperations = new();
        using DiskPackageCache cache = CreateCache(fileOperations);
        await InstallWithoutIndexAsync(cache, "generated");
        fileOperations.AtomicDestinations.Clear();
        fileOperations.WrittenPaths.Clear();
        IPackageCacheIndexStore store = cache;

        PackageIndex? index = await store.GetOrCreateIndexAsync(
            s_reference,
            forceReindex: false,
            (record, _) =>
            {
                record.Index.ShouldBeNull();
                return Task.FromResult(CreateIndex("generated"));
            },
            TestContext.Current.CancellationToken);

        string indexPath = GetIndexPath();
        index!.Files[0].Id.ShouldBe("generated");
        File.Exists(indexPath).ShouldBeTrue();
        fileOperations.AtomicDestinations.ShouldContain(indexPath);
        fileOperations.WrittenPaths.ShouldContain(
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
        Directory.GetFiles(
            Path.GetDirectoryName(indexPath)!,
            "*.tmp",
            SearchOption.TopDirectoryOnly).ShouldBeEmpty();
        PackageIndex? persisted =
            JsonSerializer.Deserialize<PackageIndex>(
                await File.ReadAllBytesAsync(
                    indexPath,
                    TestContext.Current.CancellationToken));
        persisted!.Files[0].Id.ShouldBe("generated");
    }

    [Fact]
    public async Task GetOrCreateIndex_ForceReindexReplacesValidIndex()
    {
        using DiskPackageCache cache = CreateCache();
        await InstallWithoutIndexAsync(cache, "resource");
        IPackageCacheIndexStore store = cache;
        await store.GetOrCreateIndexAsync(
            s_reference,
            forceReindex: false,
            (_, _) => Task.FromResult(CreateIndex("first")),
            TestContext.Current.CancellationToken);
        int skippedGeneratorCalls = 0;

        PackageIndex? retained = await store.GetOrCreateIndexAsync(
            s_reference,
            forceReindex: false,
            (_, _) =>
            {
                skippedGeneratorCalls++;
                return Task.FromResult(CreateIndex("unexpected"));
            },
            TestContext.Current.CancellationToken);
        PackageIndex? replaced = await store.GetOrCreateIndexAsync(
            s_reference,
            forceReindex: true,
            (_, _) => Task.FromResult(CreateIndex("second")),
            TestContext.Current.CancellationToken);

        skippedGeneratorCalls.ShouldBe(0);
        retained!.Files[0].Id.ShouldBe("first");
        replaced!.Files[0].Id.ShouldBe("second");
        (await cache.GetIndexAsync(
            s_reference,
            TestContext.Current.CancellationToken))!
            .Files[0].Id.ShouldBe("second");
    }

    [Fact]
    public async Task GetOrCreateIndex_CallbackFailurePreservesPriorBytes()
    {
        using DiskPackageCache cache = CreateCache();
        await InstallWithoutIndexAsync(cache, "resource");
        IPackageCacheIndexStore store = cache;
        await store.GetOrCreateIndexAsync(
            s_reference,
            forceReindex: false,
            (_, _) => Task.FromResult(CreateIndex("first")),
            TestContext.Current.CancellationToken);
        byte[] priorBytes = await File.ReadAllBytesAsync(
            GetIndexPath(),
            TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.GetOrCreateIndexAsync(
                s_reference,
                forceReindex: true,
                (_, _) => throw new InvalidOperationException("generation failed"),
                TestContext.Current.CancellationToken));

        byte[] retainedBytes = await File.ReadAllBytesAsync(
            GetIndexPath(),
            TestContext.Current.CancellationToken);
        retainedBytes.ShouldBe(priorBytes);
    }

    [Theory]
    [InlineData("overwrite")]
    [InlineData("remove")]
    public async Task GetOrCreateIndex_SerializesAgainstPackageMutation(
        string mutation)
    {
        using DiskPackageCache generatingCache = CreateCache();
        using DiskPackageCache mutatingCache = CreateCache();
        await InstallWithoutIndexAsync(generatingCache, "old");
        IPackageCacheIndexStore store = generatingCache;
        TaskCompletionSource generatorEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseGenerator = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<PackageIndex?> generationTask =
            store.GetOrCreateIndexAsync(
                s_reference,
                forceReindex: true,
                async (_, _) =>
                {
                    generatorEntered.TrySetResult();
                    await releaseGenerator.Task;
                    return CreateIndex("old");
                },
                TestContext.Current.CancellationToken);
        await generatorEntered.Task;

        Task mutationTask;
        if (mutation == "overwrite")
        {
            MemoryStream replacement = CreateArchive(
                patientId: "new",
                indexJson: null);
            mutationTask = OverwriteAsync(mutatingCache, replacement);
        }
        else
        {
            mutationTask = mutatingCache.RemoveAsync(
                s_reference,
                TestContext.Current.CancellationToken);
        }

        await Task.Delay(
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken);
        mutationTask.IsCompleted.ShouldBeFalse();

        releaseGenerator.TrySetResult();
        (await generationTask).ShouldNotBeNull();
        await mutationTask;

        PackageRecord? finalRecord = await generatingCache.GetPackageAsync(
            s_reference,
            TestContext.Current.CancellationToken);
        if (mutation == "overwrite")
        {
            finalRecord.ShouldNotBeNull();
            finalRecord!.Index.ShouldBeNull();
            (await generatingCache.GetFileContentAsync(
                s_reference,
                "patient.json",
                TestContext.Current.CancellationToken))!
                .ShouldContain("\"id\":\"new\"");
        }
        else
        {
            finalRecord.ShouldBeNull();
        }
    }

    [Fact]
    public async Task GetIndexAsync_OversizedIndexIsDerivativeAbsence()
    {
        using (DiskPackageCache installingCache = CreateCache())
            await InstallWithoutIndexAsync(installingCache, "resource");
        await File.WriteAllTextAsync(
            GetIndexPath(),
            new string(' ', 128),
            TestContext.Current.CancellationToken);
        PackageInstallLimits limits = new()
        {
            MaxEntryBytes = 64
        };
        using DiskPackageCache boundedCache = new(
            _cacheRoot,
            logger: null,
            timeProvider: null,
            limits);

        (await boundedCache.GetIndexAsync(
            s_reference,
            TestContext.Current.CancellationToken)).ShouldBeNull();
        (await boundedCache.GetPackageAsync(
            s_reference,
            TestContext.Current.CancellationToken))!.Index.ShouldBeNull();
    }

    [Fact]
    public async Task MetadataUpdate_CannotReplaceContentGeneration()
    {
        using DiskPackageCache cache = CreateCache();
        await InstallWithoutIndexAsync(
            cache,
            "resource");
        CacheMetadataEntry original =
            (await cache.GetMetadataAsync(
                TestContext.Current.CancellationToken))
            .Packages[s_reference.FhirDirective];
        using MemoryStream replacement = CreateArchive(
            patientId: "replacement",
            indexJson: null);
        _ = await cache.InstallAsync(
            s_reference,
            replacement,
            new InstallCacheOptions
            {
                OverwriteExisting = true,
                VerifyChecksum = false,
            },
            TestContext.Current.CancellationToken);
        CacheMetadataEntry replacementMetadata =
            (await cache.GetMetadataAsync(
                TestContext.Current.CancellationToken))
            .Packages[s_reference.FhirDirective];
        replacementMetadata.ContentGeneration
            .ShouldNotBe(original.ContentGeneration);

        await cache.UpdateMetadataAsync(
            s_reference,
            original with
            {
                SizeBytes = 123,
            },
            TestContext.Current.CancellationToken);

        CacheMetadataEntry updated =
            (await cache.GetMetadataAsync(
                TestContext.Current.CancellationToken))
            .Packages[s_reference.FhirDirective];
        updated.ContentGeneration.ShouldBe(
            replacementMetadata.ContentGeneration);
        updated.SizeBytes.ShouldBe(123);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot))
            Directory.Delete(_cacheRoot, recursive: true);
    }

    private DiskPackageCache CreateCache(
        IPackageCacheFileOperations? fileOperations = null) =>
        new(
            _cacheRoot,
            logger: null,
            timeProvider: null,
            new PackageInstallLimits(),
            fileOperations ?? SystemPackageCacheFileOperations.Instance,
            NullPackageCacheFaultObserver.Instance);

    private async Task InstallWithoutIndexAsync(
        DiskPackageCache cache,
        string patientId)
    {
        using MemoryStream archive = CreateArchive(
            patientId,
            indexJson: null);
        await cache.InstallAsync(
            s_reference,
            archive,
            new InstallCacheOptions { VerifyChecksum = false },
            TestContext.Current.CancellationToken);
    }

    private static async Task OverwriteAsync(
        DiskPackageCache cache,
        MemoryStream replacement)
    {
        using (replacement)
        {
            await cache.InstallAsync(
                s_reference,
                replacement,
                new InstallCacheOptions
                {
                    VerifyChecksum = false,
                    OverwriteExisting = true
                },
                TestContext.Current.CancellationToken);
        }
    }

    private string GetIndexPath() =>
        Path.Combine(
            PackageCacheKey.Create(s_reference)
                .GetPackageDirectoryPath(_cacheRoot),
            "package",
            ".index.json");

    private static MemoryStream CreateArchive(
        string patientId,
        string? indexJson)
    {
        List<ArbitraryTarEntry> entries =
        [
            ArbitraryTarBuilder.File(
                "package/package.json",
                """{"name":"example.package","version":"1.0.0"}"""),
            ArbitraryTarBuilder.File(
                "package/patient.json",
                $$"""{"resourceType":"Patient","id":"{{patientId}}"}""")
        ];
        if (indexJson is not null)
        {
            entries.Add(
                ArbitraryTarBuilder.File(
                    "package/.index.json",
                    indexJson));
        }

        return ArbitraryTarBuilder.Create([.. entries]);
    }

    private static string CreateIndexJson(string id) =>
        JsonSerializer.Serialize(CreateIndex(id));

    private static PackageIndex CreateIndex(string id) =>
        new()
        {
            IndexVersion = 2,
            Files =
            [
                new ResourceIndexEntry
                {
                    Filename = "patient.json",
                    ResourceType = "Patient",
                    Id = id,
                    Url = $"https://example/{id}"
                }
            ]
        };

    private sealed class RecordingFileOperations :
        IPackageCacheFileOperations
    {
        private readonly IPackageCacheFileOperations _inner =
            SystemPackageCacheFileOperations.Instance;

        internal List<string> AtomicDestinations { get; } = [];

        internal List<string> WrittenPaths { get; } = [];

        public bool DirectoryExists(string path) =>
            _inner.DirectoryExists(path);

        public bool FileExists(string path) =>
            _inner.FileExists(path);

        public void CreateDirectory(string path) =>
            _inner.CreateDirectory(path);

        public void MoveDirectory(
            string sourcePath,
            string destinationPath) =>
            _inner.MoveDirectory(sourcePath, destinationPath);

        public void MoveFile(
            string sourcePath,
            string destinationPath) =>
            _inner.MoveFile(sourcePath, destinationPath);

        public void DeleteDirectory(string path, bool recursive) =>
            _inner.DeleteDirectory(path, recursive);

        public void DeleteFile(string path) =>
            _inner.DeleteFile(path);

        public async ValueTask WriteFileAndFlushAsync(
            string path,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            WrittenPaths.Add(path);
            await _inner.WriteFileAndFlushAsync(
                    path,
                    content,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public void AtomicReplaceFile(
            string sourcePath,
            string destinationPath)
        {
            AtomicDestinations.Add(destinationPath);
            _inner.AtomicReplaceFile(
                sourcePath,
                destinationPath);
        }

        public void SynchronizeDirectory(string directoryPath) =>
            _inner.SynchronizeDirectory(directoryPath);

        public PackageCacheArtifactKind GetArtifactKind(string path) =>
            _inner.GetArtifactKind(path);

        public bool ArtifactExists(
            string path,
            PackageCacheArtifactKind artifactKind) =>
            _inner.ArtifactExists(path, artifactKind);

        public void MoveArtifact(
            string sourcePath,
            string destinationPath,
            PackageCacheArtifactKind artifactKind) =>
            _inner.MoveArtifact(
                sourcePath,
                destinationPath,
                artifactKind);

        public void DeleteArtifact(
            string path,
            PackageCacheArtifactKind artifactKind) =>
            _inner.DeleteArtifact(path, artifactKind);
    }
}
