// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
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

    public static TheoryData<bool, bool, string, bool> IndexModes
    {
        get
        {
            TheoryData<bool, bool, string, bool> cases = [];
            foreach (bool persistIndex in new[] { false, true })
            {
                foreach (bool rootLayout in new[] { false, true })
                {
                    foreach (string state in new[] { "valid", "missing", "invalid", "directory" })
                    {
                        cases.Add(persistIndex, rootLayout, state, false);
                        cases.Add(persistIndex, rootLayout, state, true);
                    }
                }
            }

            return cases;
        }
    }

    [Fact]
    public async Task GetIndex_MissingOptionalIndex_DoesNotOpenOrSpendFaultRetries()
    {
        string indexPath = GetIndexPath();
        string contentPath = Path.GetDirectoryName(indexPath)!;
        Directory.CreateDirectory(contentPath);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, "package.json"),
            """{"name":"example.package","version":"1.0.0"}""",
            TestContext.Current.CancellationToken);
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_cacheRoot);
        FileSystemContext context = files.CreateContext(retries: 2);
        using DiskPackageCache cache = new(
            _cacheRoot, null, null, new PackageInstallLimits(), context);
        files.Calls.Clear();

        PackageIndex? index = await cache.GetIndexAsync(
            s_reference, TestContext.Current.CancellationToken);

        index.ShouldBeNull();
        File.Exists(indexPath).ShouldBeFalse();
        FileSystemPrimitiveCall probe = files.Calls
            .Where(call => call.Path == indexPath)
            .ShouldHaveSingleItem();
        probe.Operation.ShouldBe("Probe");
        probe.Flags.ShouldBe(0);
        files.Waits.ShouldBeEmpty();
        files.AsyncWaits.ShouldBeEmpty();
    }

    [Fact]
    public async Task InternalReadPort_ProtectsIndexLengthAndReadsWithItsContext()
    {
        Directory.CreateDirectory(_cacheRoot);
        string path = Path.Combine(_cacheRoot, ".index.json");
        byte[] content = """{"index-version":2,"files":[]}"""u8.ToArray();
        File.WriteAllBytes(path, content);
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(SystemFileSystemPrimitives.Instance.Platform);
        primitives.UseSystemFiles();
        FileSystemContext context = primitives.CreateContext();
        SystemPackageCacheFileOperations operations = new SystemPackageCacheFileOperations(context);
        await using Stream stream = await operations.OpenReadAsync(path, TestContext.Current.CancellationToken);
        stream.ShouldBeOfType<PositionedFileStream>().Context.ShouldBeSameAs(context);
        primitives.Enqueue("GetHandleLength", ScriptedFileSystemPrimitives.NativeFailure<long>(5, "index length", path));
        primitives.Enqueue<int>("ReadAtAsync", call =>
        {
            RandomAccess.Read(call.Handle!, call.Buffer.Span[..2], call.Offset!.Value).ShouldBe(2);
            return ScriptedFileSystemPrimitives.NativeFailure<int>(1117, "index read", path);
        });

        stream.Length.ShouldBe(content.Length);
        byte[] result = new byte[content.Length];
        (await stream.ReadAsync(result.AsMemory(), TestContext.Current.CancellationToken)).ShouldBe(content.Length);

        result.ShouldBe(content);
        primitives.Count("GetHandleLength").ShouldBe(2);
        primitives.Count("ReadAtAsync").ShouldBe(2);
        primitives.Count("PrepareBorrowedRead").ShouldBe(0);
        primitives.Waits.ShouldBe([10]);
        primitives.AsyncWaits.ShouldBe([10]);
    }

    [Theory]
    [MemberData(nameof(IndexModes))]
    public async Task GetOrCreateIndex_PersistenceMatrix_PreservesPublisherPayload(
        bool persistIndex,
        bool rootLayout,
        string state,
        bool forceReindex)
    {
        const string filename = "patient-e\u0301.json";
        const string resourceJson = """{"resourceType":"Patient","id":"generated"}""";
        string controlDirectory = Path.Combine(_cacheRoot, ".native-control");
        Directory.CreateDirectory(controlDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(controlDirectory, filename),
            Encoding.UTF8.GetBytes(resourceJson),
            TestContext.Current.CancellationToken);
        string controlFile = Directory.GetFiles(controlDirectory).Single();
        string nativeFilename = Path.GetFileName(controlFile);
        (await File.ReadAllBytesAsync(controlFile, TestContext.Current.CancellationToken))
            .ShouldBe(Encoding.UTF8.GetBytes(resourceJson));
        if (!string.Equals(nativeFilename, filename, StringComparison.Ordinal))
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Native filesystem filename limitation: direct write of '{filename}' " +
                $"returned '{nativeFilename}'. Extraction and generated indexes must use " +
                "that visible spelling; the publisher index bytes must remain untouched.");
        }
        string prefix = rootLayout ? string.Empty : "package/";
        string publisherIndex =
            "{\r\n \"files\" : [ { \"filename\" : \"" + filename
            + "\", \"resourceType\" : \"Patient\", \"id\" : \"publisher\" } ],\r\n"
            + " \"index-version\" : 2, \"publisher-extension\" : \"retain me\"\r\n}\r\n";
        List<ArbitraryTarEntry> entries =
        [
            ArbitraryTarBuilder.File(
                $"{prefix}package.json",
                """{"name":"example.package","version":"1.0.0"}"""),
            ArbitraryTarBuilder.File(
                $"{prefix}{filename}",
                resourceJson),
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
                state == "valid" ? publisherIndex : "invalid publisher index"));
        }
        else if (state == "directory")
        {
            entries.Add(ArbitraryTarBuilder.File(
                $"{prefix}.index.json/payload.txt",
                "this directory must never be deleted"));
        }

        RecordingFileOperations fileOperations = new();
        using DiskPackageCache cache = CreateCache(fileOperations);
        using MemoryStream archive = ArbitraryTarBuilder.Create([.. entries]);
        PackageRecord installed = await cache.InstallAsync(
            s_reference,
            archive,
            new InstallCacheOptions { Unmodified = rootLayout },
            TestContext.Current.CancellationToken);
        string indexPath = Path.Combine(installed.ContentPath, ".index.json");
        Dictionary<string, byte[]> before = ReadPayload(installed.DirectoryPath);
        string[] directoriesBefore = Directory.GetDirectories(
            installed.DirectoryPath, "*", SearchOption.AllDirectories);
        Directory.GetFiles(installed.ContentPath)
            .Select(Path.GetFileName)
            .ShouldContain(nativeFilename);
        (await File.ReadAllBytesAsync(
            Path.Combine(installed.ContentPath, nativeFilename),
            TestContext.Current.CancellationToken)).ShouldBe(Encoding.UTF8.GetBytes(resourceJson));
        fileOperations.Clear();
        IPackageCacheIndexStore store = cache;
        IManagedPackageIndexer builder =
            new PackageIndexer(NullLogger<PackageIndexer>.Instance);
        int generatorCalls = 0;
        PackageRecord? readyPackage = null;
        PackageIndex? readyIndex = null;

        Task<PackageIndex?> indexing = store.GetOrCreateIndexAsync(
            s_reference,
            forceReindex,
            persistIndex,
            async (package, cancellationToken) =>
            {
                generatorCalls++;
                package.Reference.ShouldBe(s_reference);
                package.ContentPath.ShouldBe(installed.ContentPath);
                return await builder.BuildIndexAsync(package, cancellationToken);
            },
            TestContext.Current.CancellationToken,
            (package, index) =>
            {
                readyPackage = package;
                readyIndex = index;
            });
        bool reusesPublisherIndex = state == "valid" && !forceReindex;
        if (persistIndex && state == "directory")
        {
            await Should.ThrowAsync<IOException>(() => indexing);
            readyPackage.ShouldBeNull();
            readyIndex.ShouldBeNull();
            fileOperations.AtomicDestinations.ShouldContain(indexPath);
            fileOperations.DeletedPaths.ShouldNotContain(indexPath);
        }
        else
        {
            PackageIndex? index = await indexing;
            index.ShouldNotBeNull();
            index.Files.Single().Id.ShouldBe(
                reusesPublisherIndex ? "publisher" : "generated");
            index.Files.Single().Filename.ShouldBe(
                reusesPublisherIndex ? filename : nativeFilename);
            readyPackage.ShouldNotBeNull();
            readyPackage.Index.ShouldBeSameAs(index);
            readyIndex.ShouldBeSameAs(index);
            if (persistIndex && !reusesPublisherIndex)
                fileOperations.AtomicDestinations.ShouldContain(indexPath);
        }

        generatorCalls.ShouldBe(reusesPublisherIndex ? 0 : 1);
        if (!persistIndex || reusesPublisherIndex)
        {
            fileOperations.AssertNoPayloadWrites(installed.DirectoryPath);
            fileOperations.ReadPaths.Count(path => path == indexPath)
                .ShouldBe(state == "missing" ? 0 : 1);
            if (state == "valid")
            {
                (await File.ReadAllBytesAsync(indexPath, TestContext.Current.CancellationToken))
                    .ShouldBe(Encoding.UTF8.GetBytes(publisherIndex));
            }
        }
        Directory.GetDirectories(
                installed.DirectoryPath, "*", SearchOption.AllDirectories)
            .ShouldBe(directoriesBefore, ignoreOrder: true);
        Dictionary<string, byte[]> after = ReadPayload(installed.DirectoryPath);
        bool changedIndex = persistIndex && !reusesPublisherIndex && state != "directory";
        foreach ((string path, byte[] content) in before)
        {
            if (!changedIndex || path != indexPath)
                after[path].ShouldBe(content);
        }
        after.Keys.ShouldBe(
            changedIndex ? before.Keys.Append(indexPath).Distinct() : before.Keys,
            ignoreOrder: true);

        PackageIndex? onDisk = await cache.GetIndexAsync(
            s_reference,
            TestContext.Current.CancellationToken);
        if (changedIndex)
            onDisk!.Files.Single().Id.ShouldBe("generated");
        else if (state == "valid")
            onDisk!.Files.Single().Id.ShouldBe("publisher");
        else
            onDisk.ShouldBeNull();
    }

    [Theory]
    [InlineData(false, "structure")]
    [InlineData(true, "structure")]
    [InlineData(false, "missing-file")]
    [InlineData(true, "missing-file")]
    [InlineData(false, "size")]
    [InlineData(true, "size")]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    public async Task GetOrCreateIndex_InvalidGeneratedIndex_IsNeverPublished(
        bool persistIndex,
        string failure)
    {
        RecordingFileOperations fileOperations = new();
        using DiskPackageCache cache = CreateCache(
            fileOperations,
            new PackageInstallLimits { MaxEntryBytes = 512 });
        using MemoryStream archive = CreateArchive("resource", CreateIndexJson("publisher"));
        PackageRecord installed = await cache.InstallAsync(
            s_reference,
            archive,
            new InstallCacheOptions(),
            TestContext.Current.CancellationToken);
        byte[] before = await File.ReadAllBytesAsync(
            GetIndexPath(), TestContext.Current.CancellationToken);
        fileOperations.Clear();
        PackageIndex generated = failure switch
        {
            "structure" => new PackageIndex { IndexVersion = 1, Files = [] },
            "missing-file" => new PackageIndex
            {
                Files =
                [
                    new ResourceIndexEntry
                    {
                        Filename = "missing.json",
                        ResourceType = "Patient",
                    },
                ],
            },
            "size" => CreateIndex(new string('x', 2048)),
            "null" => null!,
            _ => throw new InvalidOperationException(),
        };
        IPackageCacheIndexStore store = cache;
        bool published = false;

        await Should.ThrowAsync<InvalidDataException>(() => store.GetOrCreateIndexAsync(
            s_reference,
            forceReindex: true,
            persistIndex,
            (_, _) => Task.FromResult(generated),
            TestContext.Current.CancellationToken,
            (_, _) => published = true));

        published.ShouldBeFalse();
        fileOperations.AssertNoPayloadWrites(installed.DirectoryPath);
        (await File.ReadAllBytesAsync(GetIndexPath(), TestContext.Current.CancellationToken))
            .ShouldBe(before);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("changed-target")]
    public async Task GetOrCreateIndex_NoPersistence_RevalidatesBeforePublishing(string failure)
    {
        RecordingFileOperations fileOperations = new();
        using DiskPackageCache cache = CreateCache(fileOperations);
        await InstallWithoutIndexAsync(cache, "resource");
        using CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        fileOperations.Clear();
        IPackageCacheIndexStore store = cache;
        bool published = false;
        string target = PackageCacheKey.Create(s_reference).GetPackageDirectoryPath(_cacheRoot);

        Task<PackageIndex?> indexing = store.GetOrCreateIndexAsync(
            s_reference,
            forceReindex: true,
            persistIndex: false,
            async (package, cancellationToken) =>
            {
                if (failure == "cancel")
                    cancellation.Cancel();
                else
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(package.ContentPath, "package.json"),
                        """{"name":"different.package","version":"1.0.0"}""",
                        cancellationToken);
                }
                return CreateIndex("generated");
            },
            cancellation.Token,
            (_, _) => published = true);

        if (failure == "cancel")
            await Should.ThrowAsync<OperationCanceledException>(() => indexing);
        else
            await Should.ThrowAsync<IOException>(() => indexing);
        published.ShouldBeFalse();
        fileOperations.AssertNoPayloadWrites(target);
        File.Exists(GetIndexPath()).ShouldBeFalse();
    }

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

    [Fact]
    public async Task SummaryList_DoesNotHydratePersistedIndexes()
    {
        RecordingFileOperations fileOperations = new();
        using DiskPackageCache cache = CreateCache(fileOperations);
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
        fileOperations.ReadPaths.Clear();

        IReadOnlyList<PackageRecord> summaryRecords =
            await cache.ListPackageSummariesAsync(
                s_reference.Name,
                s_reference.Version,
                TestContext.Current.CancellationToken);
        bool summaryReadIndex = fileOperations.ReadPaths.Any(
            path => string.Equals(
                path,
                GetIndexPath(),
                StringComparison.Ordinal));
        fileOperations.ReadPaths.Clear();
        IReadOnlyList<PackageRecord> publicRecords =
            await cache.ListPackagesAsync(
                s_reference.Name,
                s_reference.Version,
                TestContext.Current.CancellationToken);
        bool hydratedReadIndex = fileOperations.ReadPaths.Any(
            path => string.Equals(
                path,
                GetIndexPath(),
                StringComparison.Ordinal));

        summaryRecords.Single().Index.ShouldBeNull();
        summaryReadIndex.ShouldBeFalse();
        publicRecords.Single().Index.ShouldNotBeNull();
        hydratedReadIndex.ShouldBeTrue();
    }

    [Fact]
    public async Task SummaryList_MatchesHydratedListExceptIndex()
    {
        using DiskPackageCache cache = CreateCache();
        PackageReference laterReference =
            new("zeta.package", "2.0.0");
        PackageReference earlierReference =
            new("alpha.package", "1.0.0");
        await InstallPackageAsync(
            cache,
            laterReference,
            "zeta",
            "5.0.0",
            new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc),
            222);
        await InstallPackageAsync(
            cache,
            earlierReference,
            "alpha",
            "4.0.1",
            new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc),
            111);
        PackageReference invalidReference =
            new("invalid.package", "1.0.0");
        string invalidContentPath = Path.Combine(
            PackageCacheKey.Create(invalidReference)
                .GetPackageDirectoryPath(_cacheRoot),
            "package");
        Directory.CreateDirectory(invalidContentPath);
        await File.WriteAllTextAsync(
            Path.Combine(invalidContentPath, "package.json"),
            """{"name":"different.package","version":"1.0.0"}""",
            TestContext.Current.CancellationToken);

        IReadOnlyList<PackageRecord> hydrated =
            await cache.ListPackagesAsync(
                ct: TestContext.Current.CancellationToken);
        IReadOnlyList<PackageRecord> summaries =
            await cache.ListPackageSummariesAsync(
                ct: TestContext.Current.CancellationToken);
        IReadOnlyList<PackageRecord> filtered =
            await cache.ListPackageSummariesAsync(
                "zeta",
                "2.0.0",
                TestContext.Current.CancellationToken);

        hydrated.Select(record => record.Reference).ShouldBe(
        [
            earlierReference,
            laterReference
        ]);
        summaries.Select(record => record.Reference).ShouldBe(
            hydrated.Select(record => record.Reference));
        filtered.Single().Reference.ShouldBe(laterReference);

        foreach ((PackageRecord hydratedRecord, PackageRecord summaryRecord)
            in hydrated.Zip(summaries))
        {
            ReferenceEquals(hydratedRecord, summaryRecord).ShouldBeFalse();
            summaryRecord.Reference.ShouldBe(hydratedRecord.Reference);
            summaryRecord.DirectoryPath.ShouldBe(
                hydratedRecord.DirectoryPath);
            summaryRecord.ContentPath.ShouldBe(hydratedRecord.ContentPath);
            summaryRecord.Manifest.Name.ShouldBe(
                hydratedRecord.Manifest.Name);
            summaryRecord.Manifest.Version.ShouldBe(
                hydratedRecord.Manifest.Version);
            summaryRecord.Manifest.Description.ShouldBe(
                hydratedRecord.Manifest.Description);
            summaryRecord.Manifest.FhirVersions.ShouldBe(
                hydratedRecord.Manifest.FhirVersions);
            summaryRecord.InstalledAt.ShouldBe(
                hydratedRecord.InstalledAt);
            summaryRecord.SizeBytes.ShouldBe(hydratedRecord.SizeBytes);
            summaryRecord.ContentGeneration.ShouldBe(
                hydratedRecord.ContentGeneration);
            summaryRecord.Index.ShouldBeNull();
            hydratedRecord.Index.ShouldNotBeNull();
        }
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
            persistIndex: true,
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
            persistIndex: true,
            (_, _) => Task.FromResult(CreateIndex("first")),
            TestContext.Current.CancellationToken);
        int skippedGeneratorCalls = 0;

        PackageIndex? retained = await store.GetOrCreateIndexAsync(
            s_reference,
            forceReindex: false,
            persistIndex: true,
            (_, _) =>
            {
                skippedGeneratorCalls++;
                return Task.FromResult(CreateIndex("unexpected"));
            },
            TestContext.Current.CancellationToken);
        PackageIndex? replaced = await store.GetOrCreateIndexAsync(
            s_reference,
            forceReindex: true,
            persistIndex: true,
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
            persistIndex: true,
            (_, _) => Task.FromResult(CreateIndex("first")),
            TestContext.Current.CancellationToken);
        byte[] priorBytes = await File.ReadAllBytesAsync(
            GetIndexPath(),
            TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.GetOrCreateIndexAsync(
                s_reference,
                forceReindex: true,
                persistIndex: true,
                (_, _) => throw new InvalidOperationException("generation failed"),
                TestContext.Current.CancellationToken));

        byte[] retainedBytes = await File.ReadAllBytesAsync(
            GetIndexPath(),
            TestContext.Current.CancellationToken);
        retainedBytes.ShouldBe(priorBytes);
    }

    [Theory]
    [InlineData("overwrite", true)]
    [InlineData("overwrite", false)]
    [InlineData("remove", true)]
    [InlineData("remove", false)]
    public async Task GetOrCreateIndex_SerializesAgainstPackageMutation(
        string mutation,
        bool persistIndex)
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
                persistIndex,
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
        IPackageCacheFileOperations? fileOperations = null,
        PackageInstallLimits? limits = null) =>
        new(
            _cacheRoot,
            logger: null,
            timeProvider: null,
            limits ?? new PackageInstallLimits(),
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

    private static async Task InstallPackageAsync(
        DiskPackageCache cache,
        PackageReference reference,
        string patientId,
        string fhirVersion,
        DateTime installedAt,
        long sizeBytes)
    {
        using MemoryStream archive = CreateArchive(
            reference,
            patientId,
            CreateIndexJson(patientId),
            fhirVersion);
        _ = await cache.InstallAsync(
            reference,
            archive,
            new InstallCacheOptions { VerifyChecksum = false },
            TestContext.Current.CancellationToken);
        await cache.UpdateMetadataAsync(
            reference,
            new CacheMetadataEntry
            {
                DownloadDateTime = installedAt,
                SizeBytes = sizeBytes
            },
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
        string? indexJson) =>
        CreateArchive(
            s_reference,
            patientId,
            indexJson,
            "4.0.1");

    private static MemoryStream CreateArchive(
        PackageReference reference,
        string patientId,
        string? indexJson,
        string fhirVersion)
    {
        PackageManifest manifest = new()
        {
            Name = reference.Name,
            Version = reference.Version!,
            Description = $"{reference.Name} description",
            FhirVersions = [fhirVersion]
        };
        List<ArbitraryTarEntry> entries =
        [
            ArbitraryTarBuilder.File(
                "package/package.json",
                manifest.Serialize()),
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

    private static Dictionary<string, byte[]> ReadPayload(string directoryPath) =>
        Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);

    internal sealed class RecordingFileOperations :
        IPackageCacheFileOperations
    {
        private readonly IPackageCacheFileOperations _inner =
            SystemPackageCacheFileOperations.Instance;

        internal ConcurrentQueue<string> AtomicDestinations { get; } = new();

        internal ConcurrentQueue<string> ReadPaths { get; } = new();

        internal ConcurrentQueue<string> WrittenPaths { get; } = new();

        internal ConcurrentQueue<string> DeletedPaths { get; } = new();

        internal void Clear()
        {
            AtomicDestinations.Clear();
            ReadPaths.Clear();
            WrittenPaths.Clear();
            DeletedPaths.Clear();
        }

        internal void AssertNoPayloadWrites(string packageDirectory)
        {
            string prefix = packageDirectory + Path.DirectorySeparatorChar;
            WrittenPaths.ShouldNotContain(path => path.StartsWith(prefix, StringComparison.Ordinal));
            AtomicDestinations.ShouldNotContain(path => path.StartsWith(prefix, StringComparison.Ordinal));
            AtomicDestinations.ShouldNotContain(path => Path.GetFileName(path) == ".index.json");
            DeletedPaths.ShouldNotContain(path =>
                path == packageDirectory || path.StartsWith(prefix, StringComparison.Ordinal));
        }

        public bool DirectoryExists(string path) =>
            _inner.DirectoryExists(path);

        public bool FileExists(string path) =>
            _inner.FileExists(path);

        public Stream OpenRead(string path)
        {
            ReadPaths.Enqueue(path);
            return _inner.OpenRead(path);
        }

        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
        {
            ReadPaths.Enqueue(path);
            return _inner.OpenReadAsync(path, cancellationToken);
        }

        public ValueTask<string> GetFullPathAsync(string path, CancellationToken cancellationToken) =>
            _inner.GetFullPathAsync(path, cancellationToken);

        public void CreateDirectory(string path) =>
            _inner.CreateDirectory(path);

        public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
            _inner.CreateDirectoryAsync(path, cancellationToken);

        public void MoveDirectory(
            string sourcePath,
            string destinationPath) =>
            _inner.MoveDirectory(sourcePath, destinationPath);

        public void MoveFile(
            string sourcePath,
            string destinationPath) =>
            _inner.MoveFile(sourcePath, destinationPath);

        public void DeleteDirectory(string path, bool recursive)
        {
            DeletedPaths.Enqueue(path);
            _inner.DeleteDirectory(path, recursive);
        }

        public void DeleteFile(string path)
        {
            DeletedPaths.Enqueue(path);
            _inner.DeleteFile(path);
        }

        public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken)
        {
            DeletedPaths.Enqueue(path);
            return _inner.DeleteFileAsync(path, cancellationToken);
        }

        public async ValueTask WriteFileAndFlushAsync(
            string path,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            WrittenPaths.Enqueue(path);
            await _inner.WriteFileAndFlushAsync(
                    path,
                    content,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public ValueTask AtomicReplaceFileAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            AtomicReplaceFileAsync(new DurableFileReplacement(sourcePath, destinationPath), cancellationToken);

        public async ValueTask AtomicReplaceFileAsync(
            DurableFileReplacement replacement,
            CancellationToken cancellationToken)
        {
            AtomicDestinations.Enqueue(replacement.DestinationPath);
            await _inner.AtomicReplaceFileAsync(
                    replacement,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public void SynchronizeDirectory(string directoryPath) =>
            _inner.SynchronizeDirectory(directoryPath);

        public ValueTask SynchronizeDirectoryAsync(string directoryPath, CancellationToken cancellationToken) =>
            _inner.SynchronizeDirectoryAsync(directoryPath, cancellationToken);

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
            PackageCacheArtifactKind artifactKind)
        {
            DeletedPaths.Enqueue(path);
            _inner.DeleteArtifact(path, artifactKind);
        }
    }
}
