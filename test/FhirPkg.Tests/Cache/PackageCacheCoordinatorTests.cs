// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Tests.Support;
using FhirPkg.Utilities;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Cache;

public sealed class PackageCacheCoordinatorTests
{
    [Fact]
    public async Task SameIdentity_WaitsUntilLeaseIsReleased()
    {
        using TestDirectory directory = new();
        PackageCacheCoordinator firstCoordinator =
            new(directory.Path);
        PackageCacheCoordinator secondCoordinator =
            new(directory.Path);
        PackageCacheKey cacheKey = PackageCacheKey.Create(
            new PackageReference("example.package", "1.0.0"));
        await using PackageCacheLease first =
            await firstCoordinator.AcquireIdentityAsync(
                cacheKey,
                TestContext.Current.CancellationToken);

        Task<PackageCacheLease> waiting =
            secondCoordinator.AcquireIdentityAsync(
                cacheKey,
                TestContext.Current.CancellationToken);
        await Task.Delay(
            TimeSpan.FromMilliseconds(75),
            TestContext.Current.CancellationToken);
        waiting.IsCompleted.ShouldBeFalse();

        await first.DisposeAsync();
        await using PackageCacheLease second =
            await waiting.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DifferentIdentities_AcquireConcurrently()
    {
        using TestDirectory directory = new();
        PackageCacheCoordinator coordinator =
            new(directory.Path);
        PackageCacheKey firstKey = PackageCacheKey.Create(
            new PackageReference("first.package", "1.0.0"));
        PackageCacheKey secondKey = PackageCacheKey.Create(
            new PackageReference("second.package", "1.0.0"));
        await using PackageCacheLease first =
            await coordinator.AcquireIdentityAsync(
                firstKey,
                TestContext.Current.CancellationToken);

        Task<PackageCacheLease> secondTask =
            coordinator.AcquireIdentityAsync(
                secondKey,
                TestContext.Current.CancellationToken);
        await using PackageCacheLease second =
            await secondTask.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CaseDistinctVersions_UseDistinctProcessLocks()
    {
        using TestDirectory directory = new();
        PackageCacheCoordinator coordinator =
            new(directory.Path);
        PackageCacheKey upperKey = PackageCacheKey.Create(
            new PackageReference(
                "example.package",
                "current$Main"));
        PackageCacheKey lowerKey = PackageCacheKey.Create(
            new PackageReference(
                "example.package",
                "current$main"));
        await using PackageCacheLease upper =
            await coordinator.AcquireIdentityAsync(
                upperKey,
                TestContext.Current.CancellationToken);

        await using PackageCacheLease lower =
            await coordinator.AcquireIdentityAsync(
                    lowerKey,
                    TestContext.Current.CancellationToken)
                .WaitAsync(
                    TimeSpan.FromSeconds(2),
                    TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancelledWaiter_ReleasesItsProcessSemaphore()
    {
        using TestDirectory directory = new();
        PackageCacheCoordinator coordinator =
            new(directory.Path);
        PackageCacheKey cacheKey = PackageCacheKey.Create(
            new PackageReference("example.package", "1.0.0"));
        await using PackageCacheLease owner =
            await coordinator.AcquireIdentityAsync(
                cacheKey,
                TestContext.Current.CancellationToken);
        using CancellationTokenSource cancellationSource =
            new(TimeSpan.FromMilliseconds(75));

        await Should.ThrowAsync<OperationCanceledException>(
            () => coordinator.AcquireIdentityAsync(
                cacheKey,
                cancellationSource.Token));

        await owner.DisposeAsync();
        await using (PackageCacheLease successor =
            await coordinator.AcquireIdentityAsync(
                cacheKey,
                TestContext.Current.CancellationToken))
        {
        }

        PackageCacheCoordinator.GetProcessLockEntryCount(
            directory.Path).ShouldBe(0);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task IdentityRecovery_StagingRootDisappearsAfterProbe_DoesNotAbort(
        bool asynchronous,
        int retries)
    {
        using TestDirectory directory = new();
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(directory.Path);
        FileSystemContext context = files.CreateContext(retries);
        using DiskPackageCache cache = new(
            directory.Path, null, null, new PackageInstallLimits(), context);
        cache.FileSystemContext.ShouldBeSameAs(context);
        PackageReference reference = new("recovery.package", "1.0.0");
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        string contentPath = Path.Combine(cacheKey.GetPackageDirectoryPath(cache.CacheDirectory), "package");
        Directory.CreateDirectory(contentPath);
        File.WriteAllText(
            Path.Combine(contentPath, "package.json"),
            """{"name":"recovery.package","version":"1.0.0"}""");
        string stagingRoot = Path.Combine(cache.CacheDirectory, ".fhirpkg", "staging");
        string transactionDirectory = Path.Combine(cache.CacheDirectory, ".fhirpkg", "transactions");
        string identityLockPath = Path.Combine(cache.CacheDirectory, ".fhirpkg", "locks", $"{cacheKey.LockHash}.lock");
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(transactionDirectory);
        int baseline = PackageCacheCoordinator.GetProcessLockEntryCount(cache.CacheDirectory);
        baseline.ShouldBe(0);
        List<FileStream> lockStreams = [];
        files.Set<FileStream>("OpenFile", call =>
        {
            call.Path.ShouldBe(identityLockPath);
            FileSystemReplayOutcome<FileStream> opened = SystemFileSystemPrimitives.Instance.OpenFile(
                call.Path!, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                OperatingSystem.IsMacOS() ? FileShare.None : FileShare.ReadWrite, 1, FileOptions.None);
            lockStreams.Add(opened.Value);
            return opened;
        });
        List<DirectoryNotFoundException> failures = [];
        files.Set<IEnumerator<string>>("CreateDirectoryEnumerator", call =>
        {
            if (call.Path != stagingRoot)
                return SystemFileSystemPrimitives.Instance.CreateDirectoryEnumerator(call.Path!);

            PackageCacheCoordinator.GetIdentityReferenceCount(cache.CacheDirectory, cacheKey).ShouldBe(1);
            PackageCacheCoordinator.GetProcessLockEntryCount(cache.CacheDirectory).ShouldBe(baseline + 1);
            if (failures.Count == 0)
                Directory.Delete(stagingRoot);
            Directory.Exists(stagingRoot).ShouldBeFalse();
            DirectoryNotFoundException failure = new("The staging root disappeared after journal discovery.");
            failures.Add(failure);
            return ScriptedFileSystemPrimitives.Fail<IEnumerator<string>>(
                failure, "EnumerateDirectory", call.Path, nativeError: 2);
        });
        files.Calls.Clear();

        if (asynchronous)
            (await cache.IsInstalledAsync(reference, TestContext.Current.CancellationToken)).ShouldBeTrue();
        else
            cache.GetPackageContentPath(reference).ShouldBe(contentPath);

        int stagingProbe = files.Calls.FindIndex(call => call.Operation == "Probe" && call.Path == stagingRoot);
        int journalSnapshot = files.Calls.FindIndex(call =>
            call.Operation == "CreateDirectoryEnumerator" && call.Path == transactionDirectory);
        int journalDisposal = files.Calls.FindIndex(call =>
            call.Operation == "DisposeEnumerator" && call.Path == transactionDirectory);
        int stagingSnapshot = files.Calls.FindIndex(call =>
            call.Operation == "CreateDirectoryEnumerator" && call.Path == stagingRoot);
        stagingProbe.ShouldBeGreaterThan(0);
        journalSnapshot.ShouldBeGreaterThan(stagingProbe);
        journalDisposal.ShouldBeGreaterThan(journalSnapshot);
        stagingSnapshot.ShouldBeGreaterThan(journalDisposal);
        files.Calls.Count(call => call.Operation == "Probe" && call.Path == stagingRoot).ShouldBe(1);
        files.Calls.Count(call => call.Operation == "CreateDirectoryEnumerator" && call.Path == transactionDirectory)
            .ShouldBe(1);
        files.Calls.Count(call => call.Operation == "DisposeEnumerator" && call.Path == transactionDirectory)
            .ShouldBe(1);
        files.Calls.Count(call => call.Operation == "CreateDirectoryEnumerator" && call.Path == stagingRoot)
            .ShouldBe(retries + 1);
        files.Calls.ShouldNotContain(call => call.Path == stagingRoot
            && (call.Operation == "MoveNext" || call.Operation == "DisposeEnumerator"));
        failures.Count.ShouldBe(retries + 1);
        FileSystemFailureContext.TryGetTerminal(
            failures[^1], out FileSystemFailureContext? failureContext).ShouldBeTrue();
        failureContext!.Operation.ShouldBe("EnumerateDirectory");
        failureContext.Path.ShouldBe(stagingRoot);
        failureContext.Phase.ShouldBe("SnapshotDirectory");
        failureContext.AttemptCount.ShouldBe((long)retries + 1);
        failureContext.RetriesUsed.ShouldBe((long)retries);
        failureContext.Termination.ShouldBe(FileSystemFailureTermination.Exhausted);
        failureContext.NativeErrorCode.ShouldBe(2);
        foreach (DirectoryNotFoundException earlier in failures.Take(retries))
            FileSystemFailureContext.TryGetTerminal(earlier, out _).ShouldBeFalse();
        files.Calls.Where(call => call.Operation == "OpenFile").Select(call => call.Path)
            .ShouldBe([identityLockPath]);
        files.Count("TryLock").ShouldBe(1);
        files.Count("Unlock").ShouldBe(1);
        lockStreams.ShouldHaveSingleItem().CanRead.ShouldBeFalse();
        files.Count("DeleteEntry").ShouldBe(0);
        files.Calls.ShouldNotContain(call => call.Operation == "Probe"
            && call.Path == stagingRoot && call.Flags != 0);
        files.AsyncWaits.ShouldBe(Enumerable.Repeat(10, retries).ToArray());
        files.Waits.ShouldBeEmpty();
        PackageCacheCoordinator.GetIdentityReferenceCount(cache.CacheDirectory, cacheKey).ShouldBe(0);
        PackageCacheCoordinator.GetProcessLockEntryCount(cache.CacheDirectory).ShouldBe(baseline);

        PackageCacheCoordinator coordinator = new(
            cache.CacheDirectory, context, NullPackageCacheContentionObserver.Instance);
        using CancellationTokenSource subsequent = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        subsequent.CancelAfter(TimeSpan.FromSeconds(5));
        await using (PackageCacheLease successor = await coordinator.AcquireIdentityAsync(cacheKey, subsequent.Token))
        {
            PackageCacheCoordinator.GetIdentityReferenceCount(cache.CacheDirectory, cacheKey).ShouldBe(1);
            PackageCacheCoordinator.GetProcessLockEntryCount(cache.CacheDirectory).ShouldBe(baseline + 1);
            lockStreams.Count.ShouldBe(2);
            lockStreams[1].CanRead.ShouldBeTrue();
        }

        files.Calls.Where(call => call.Operation == "OpenFile").Select(call => call.Path)
            .ShouldBe([identityLockPath, identityLockPath]);
        files.Count("TryLock").ShouldBe(2);
        files.Count("Unlock").ShouldBe(2);
        lockStreams[1].CanRead.ShouldBeFalse();
        PackageCacheCoordinator.GetIdentityReferenceCount(cache.CacheDirectory, cacheKey).ShouldBe(0);
        PackageCacheCoordinator.GetProcessLockEntryCount(cache.CacheDirectory).ShouldBe(baseline);
    }

    [Fact]
    public async Task IdentityAndGlobalLockFilesRemainPersistent()
    {
        using TestDirectory directory = new();
        PackageCacheCoordinator coordinator =
            new(directory.Path);
        PackageCacheKey cacheKey = PackageCacheKey.Create(
            new PackageReference("example.package", "1.0.0"));

        await using (PackageCacheLease identity =
            await coordinator.AcquireIdentityAsync(
                cacheKey,
                TestContext.Current.CancellationToken))
        {
        }

        await using (PackageCacheLease global =
            await coordinator.AcquireGlobalAsync(
                TestContext.Current.CancellationToken))
        {
        }

        File.Exists(Path.Combine(
            directory.Path,
            ".fhirpkg",
            "locks",
            $"{cacheKey.LockHash}.lock")).ShouldBeTrue();
        File.Exists(Path.Combine(
            directory.Path,
            ".fhirpkg",
            "locks",
            "global.lock")).ShouldBeTrue();
    }

    [Fact]
    public async Task OperationOwner_CannotBeClaimedWhileActive()
    {
        using TestDirectory directory = new();
        PackageCacheCoordinator firstCoordinator =
            new(directory.Path);
        PackageCacheCoordinator secondCoordinator =
            new(directory.Path);
        string operationId = Guid.NewGuid().ToString("N");
        await using PackageCacheLease owner =
            await firstCoordinator.AcquireOperationOwnerAsync(
                operationId,
                TestContext.Current.CancellationToken);

        secondCoordinator.TryAcquireOperationOwner(operationId)
            .ShouldBeNull();

        await owner.DisposeAsync();
        using PackageCacheLease? successor =
            secondCoordinator.TryAcquireOperationOwner(operationId);
        successor.ShouldNotBeNull();
    }

    [Fact]
    public async Task UniqueOperationOwners_DoNotRetainProcessLocks()
    {
        using TestDirectory directory = new();
        PackageCacheCoordinator coordinator =
            new(directory.Path);

        for (int index = 0; index < 64; index++)
        {
            string operationId = Guid.NewGuid().ToString("N");
            await using PackageCacheLease lease =
                await coordinator.AcquireOperationOwnerAsync(
                    operationId,
                    TestContext.Current.CancellationToken);
        }

        PackageCacheCoordinator.GetProcessLockEntryCount(
            directory.Path).ShouldBe(0);
    }

    [Fact]
    public async Task ConcurrentAcquireRelease_RetiresEntryAfterLastReference()
    {
        using TestDirectory directory = new();
        PackageCacheCoordinator coordinator =
            new(directory.Path);
        PackageCacheKey cacheKey = PackageCacheKey.Create(
            new PackageReference("race.package", "1.0.0"));
        int activeHolders = 0;
        int maximumHolders = 0;

        Task[] tasks = Enumerable.Range(0, 32)
            .Select(async _ =>
            {
                await using PackageCacheLease lease =
                    await coordinator.AcquireIdentityAsync(
                        cacheKey,
                        TestContext.Current.CancellationToken);
                int active = Interlocked.Increment(
                    ref activeHolders);
                UpdateMaximum(ref maximumHolders, active);
                await Task.Yield();
                Interlocked.Decrement(ref activeHolders);
            })
            .ToArray();

        await Task.WhenAll(tasks);

        maximumHolders.ShouldBe(1);
        PackageCacheCoordinator.GetProcessLockEntryCount(
            directory.Path).ShouldBe(0);
    }

    [Fact]
    public async Task CancellationRacingWithRelease_DoesNotLeakEntry()
    {
        using TestDirectory directory = new();
        PackageCacheCoordinator coordinator =
            new(directory.Path);
        PackageCacheKey cacheKey = PackageCacheKey.Create(
            new PackageReference("cancel.race", "1.0.0"));

        for (int index = 0; index < 32; index++)
        {
            PackageCacheLease owner =
                await coordinator.AcquireIdentityAsync(
                    cacheKey,
                    TestContext.Current.CancellationToken);
            using CancellationTokenSource cancellationSource = new();
            Task<PackageCacheLease> waiter =
                coordinator.AcquireIdentityAsync(
                    cacheKey,
                    cancellationSource.Token);

            Task cancelTask = Task.Run(
                cancellationSource.Cancel,
                TestContext.Current.CancellationToken);
            await owner.DisposeAsync();
            await cancelTask;
            try
            {
                await using PackageCacheLease acquired =
                    await waiter;
            }
            catch (OperationCanceledException)
            {
            }

            await using (PackageCacheLease successor =
                await coordinator.AcquireIdentityAsync(
                    cacheKey,
                    TestContext.Current.CancellationToken))
            {
            }
        }

        PackageCacheCoordinator.GetProcessLockEntryCount(
            directory.Path).ShouldBe(0);
    }

    [Fact]
    public async Task ListPackages_ReReadsMetadataAfterIdentityLease()
    {
        using TestDirectory directory = new();
        using DiskPackageCache cache = new(directory.Path);
        PackageReference reference =
            new("list.package", "1.0.0");
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        await using MemoryStream archive = CreateArchive(
            """{"name":"list.package","version":"1.0.0","description":"old"}""");
        await cache.InstallAsync(
            reference,
            archive,
            ct: TestContext.Current.CancellationToken);
        await cache.UpdateMetadataAsync(
            reference,
            new CacheMetadataEntry
            {
                DownloadDateTime = DateTime.UtcNow,
                SizeBytes = 111
            },
            TestContext.Current.CancellationToken);
        PackageCacheCoordinator coordinator =
            new(directory.Path);
        await using PackageCacheLease identityLease =
            await coordinator.AcquireIdentityAsync(
                cacheKey,
                TestContext.Current.CancellationToken);

        Task<IReadOnlyList<PackageRecord>> listTask =
            cache.ListPackagesAsync(
                ct: TestContext.Current.CancellationToken);
        await WaitForIdentityReferenceCountAsync(
            directory.Path,
            cacheKey,
            expectedCount: 2);

        await using (PackageCacheLease globalLease =
            await coordinator.AcquireGlobalAsync(
                TestContext.Current.CancellationToken))
        {
            string manifestPath = Path.Combine(
                cacheKey.GetPackageDirectoryPath(directory.Path),
                "package",
                "package.json");
            await File.WriteAllTextAsync(
                manifestPath,
                """{"name":"list.package","version":"1.0.0","description":"new"}""",
                TestContext.Current.CancellationToken);
            PackageCacheMetadataStore metadataStore = new(
                directory.Path,
                SystemPackageCacheFileOperations.Instance,
                NullPackageCacheFaultObserver.Instance);
            await metadataStore.SetEntryAsync(
                cacheKey,
                new CacheMetadataEntry
                {
                    DownloadDateTime = DateTime.UtcNow,
                    SizeBytes = 222
                },
                mutation: null,
                TestContext.Current.CancellationToken);
        }

        await identityLease.DisposeAsync();
        PackageRecord listed = (await listTask).ShouldHaveSingleItem();

        listed.Manifest.Description.ShouldBe("new");
        listed.SizeBytes.ShouldBe(222);
        PackageCacheCoordinator.GetProcessLockEntryCount(
            directory.Path).ShouldBe(0);
    }

    [Fact]
    public async Task IndexingList_ReReadsMetadataAfterIdentityLease()
    {
        using TestDirectory directory = new();
        using DiskPackageCache cache = new(directory.Path);
        PackageReference reference =
            new("indexing.package", "1.0.0");
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        await using MemoryStream archive = CreateArchive(
            """{"name":"indexing.package","version":"1.0.0","description":"old"}""");
        await cache.InstallAsync(
            reference,
            archive,
            ct: TestContext.Current.CancellationToken);
        await cache.UpdateMetadataAsync(
            reference,
            new CacheMetadataEntry
            {
                DownloadDateTime = DateTime.UtcNow,
                SizeBytes = 111
            },
            TestContext.Current.CancellationToken);
        PackageCacheCoordinator coordinator =
            new(directory.Path);
        await using PackageCacheLease identityLease =
            await coordinator.AcquireIdentityAsync(
                cacheKey,
                TestContext.Current.CancellationToken);
        IPackageCacheIndexStore indexStore = cache;

        Task<IReadOnlyList<PackageRecord>> listTask =
            indexStore.ListPackagesForIndexingAsync(
                cancellationToken: TestContext.Current.CancellationToken);
        await WaitForIdentityReferenceCountAsync(
            directory.Path,
            cacheKey,
            expectedCount: 2);

        await using (PackageCacheLease globalLease =
            await coordinator.AcquireGlobalAsync(
                TestContext.Current.CancellationToken))
        {
            string manifestPath = Path.Combine(
                cacheKey.GetPackageDirectoryPath(directory.Path),
                "package",
                "package.json");
            await File.WriteAllTextAsync(
                manifestPath,
                """{"name":"indexing.package","version":"1.0.0","description":"new"}""",
                TestContext.Current.CancellationToken);
            PackageCacheMetadataStore metadataStore = new(
                directory.Path,
                SystemPackageCacheFileOperations.Instance,
                NullPackageCacheFaultObserver.Instance);
            await metadataStore.SetEntryAsync(
                cacheKey,
                new CacheMetadataEntry
                {
                    DownloadDateTime = DateTime.UtcNow,
                    SizeBytes = 222
                },
                mutation: null,
                TestContext.Current.CancellationToken);
        }

        await identityLease.DisposeAsync();
        PackageRecord listed = (await listTask).ShouldHaveSingleItem();

        listed.Manifest.Description.ShouldBe("new");
        listed.SizeBytes.ShouldBe(222);
        listed.Index.ShouldBeNull();
        PackageCacheCoordinator.GetProcessLockEntryCount(
            directory.Path).ShouldBe(0);
    }

    [Fact]
    public async Task SummaryList_StaleGenerationCannotRemoveRefreshedPackage()
    {
        using TestDirectory directory = new();
        using DiskPackageCache cache = new(directory.Path);
        PackageReference reference =
            new("summary.package", "1.0.0");
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        DateTime downloadDate =
            new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
        await using MemoryStream archive = CreateArchive(
            """{"name":"summary.package","version":"1.0.0","description":"old"}""");
        await cache.InstallAsync(
            reference,
            archive,
            ct: TestContext.Current.CancellationToken);
        await cache.UpdateMetadataAsync(
            reference,
            new CacheMetadataEntry
            {
                DownloadDateTime = downloadDate,
                SizeBytes = 111
            },
            TestContext.Current.CancellationToken);
        PackageCacheCoordinator coordinator =
            new(directory.Path);
        await using PackageCacheLease identityLease =
            await coordinator.AcquireIdentityAsync(
                cacheKey,
                TestContext.Current.CancellationToken);

        Task<IReadOnlyList<PackageRecord>> listTask =
            cache.ListPackageSummariesAsync(
                ct: TestContext.Current.CancellationToken);
        await WaitForIdentityReferenceCountAsync(
            directory.Path,
            cacheKey,
            expectedCount: 2);

        await using (PackageCacheLease globalLease =
            await coordinator.AcquireGlobalAsync(
                TestContext.Current.CancellationToken))
        {
            string manifestPath = Path.Combine(
                cacheKey.GetPackageDirectoryPath(directory.Path),
                "package",
                "package.json");
            await File.WriteAllTextAsync(
                manifestPath,
                """{"name":"summary.package","version":"1.0.0","description":"new"}""",
                TestContext.Current.CancellationToken);
            PackageCacheMetadataStore metadataStore = new(
                directory.Path,
                SystemPackageCacheFileOperations.Instance,
                NullPackageCacheFaultObserver.Instance);
            await metadataStore.SetEntryAsync(
                cacheKey,
                new CacheMetadataEntry
                {
                    DownloadDateTime = downloadDate,
                    SizeBytes = 111,
                    ContentGeneration = "generation-b"
                },
                mutation: null,
                TestContext.Current.CancellationToken);
        }

        await identityLease.DisposeAsync();
        PackageRecord summary = (await listTask).ShouldHaveSingleItem();
        IPackageCacheConditionalRemoval conditionalRemoval = cache;

        bool removed = await conditionalRemoval.RemoveIfUnchangedAsync(
            summary,
            TestContext.Current.CancellationToken);
        PackageRecord? current = await cache.GetPackageAsync(
            reference,
            TestContext.Current.CancellationToken);

        summary.Manifest.Description.ShouldBe("new");
        summary.SizeBytes.ShouldBe(111);
        summary.Index.ShouldBeNull();
        removed.ShouldBeFalse();
        current.ShouldNotBeNull();
        current!.Manifest.Description.ShouldBe("new");
        current.SizeBytes.ShouldBe(111);
        current.ContentGeneration.ShouldNotBe(summary.ContentGeneration);
        PackageCacheCoordinator.GetProcessLockEntryCount(
            directory.Path).ShouldBe(0);
    }

    [Fact]
    public async Task Clear_PreservesMetadataWrittenAfterInitialSnapshot()
    {
        using TestDirectory directory = new();
        using DiskPackageCache cache = new(directory.Path);
        PackageReference snapshotReference =
            new("aaa.snapshot", "1.0.0");
        PackageCacheKey snapshotKey =
            PackageCacheKey.Create(snapshotReference);
        await cache.UpdateMetadataAsync(
            snapshotReference,
            new CacheMetadataEntry
            {
                DownloadDateTime = DateTime.UtcNow,
                SizeBytes = 100
            },
            TestContext.Current.CancellationToken);
        string metadataPath = Path.Combine(
            directory.Path,
            "packages.ini");
        await File.AppendAllTextAsync(
            metadataPath,
            $"{Environment.NewLine}[packages]{Environment.NewLine}" +
            $"legacy.package@1.0.0 = 20260717120000{Environment.NewLine}" +
            $"{Environment.NewLine}[external]{Environment.NewLine}" +
            $"keep = value{Environment.NewLine}",
            TestContext.Current.CancellationToken);
        PackageCacheCoordinator coordinator =
            new(directory.Path);
        await using PackageCacheLease identityLease =
            await coordinator.AcquireIdentityAsync(
                snapshotKey,
                TestContext.Current.CancellationToken);

        Task<int> clearTask = cache.ClearAsync(
            TestContext.Current.CancellationToken);
        await WaitForIdentityReferenceCountAsync(
            directory.Path,
            snapshotKey,
            expectedCount: 2);

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
            initialSections = IniParser.ParseFile(metadataPath);
        initialSections["packages"].ContainsKey(
            "legacy.package@1.0.0").ShouldBeFalse();
        PackageReference postSnapshotReference =
            new("zzz.post-snapshot", "1.0.0");
        PackageCacheKey postSnapshotKey =
            PackageCacheKey.Create(postSnapshotReference);
        await using MemoryStream postSnapshotArchive = CreateArchive(
            """{"name":"zzz.post-snapshot","version":"1.0.0"}""");
        await cache.InstallAsync(
            postSnapshotReference,
            postSnapshotArchive,
            ct: TestContext.Current.CancellationToken);

        await identityLease.DisposeAsync();
        (await clearTask).ShouldBe(0);
        CacheMetadata metadata = await cache.GetMetadataAsync(
            TestContext.Current.CancellationToken);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
            finalSections = IniParser.ParseFile(metadataPath);

        metadata.Packages.ContainsKey(
            snapshotKey.MetadataKey).ShouldBeFalse();
        metadata.Packages.ContainsKey(
            postSnapshotKey.MetadataKey).ShouldBeTrue();
        (await cache.IsInstalledAsync(
            postSnapshotReference,
            TestContext.Current.CancellationToken)).ShouldBeTrue();
        finalSections["external"]["keep"].ShouldBe("value");
        PackageCacheCoordinator.GetProcessLockEntryCount(
            directory.Path).ShouldBe(0);
    }

    [Fact]
    public async Task FileLockFailure_IsMappedToTypedCoordinationError()
    {
        using TestDirectory directory = new();
        string lockRoot = Path.Combine(
            directory.Path,
            ".fhirpkg",
            "locks");
        Directory.CreateDirectory(lockRoot);
        Directory.CreateDirectory(Path.Combine(lockRoot, "global.lock"));
        PackageCacheCoordinator coordinator =
            new(directory.Path);

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => coordinator.AcquireGlobalAsync(
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.CoordinationFailed);
        exception.Stage.ShouldBe(PackageInstallStage.Coordination);
    }

    [Fact]
    public void MacOpenContention_WouldBlockIsRetryable()
    {
        PackageCacheFileLock.IsMacOpenContention(
            CreateNativeIOException(35)).ShouldBeTrue();

        int[] rejectedCodes = [5, 11, 32, 33];
        foreach (int nativeError in rejectedCodes)
        {
            PackageCacheFileLock.IsMacOpenContention(
                CreateNativeIOException(nativeError)).ShouldBeFalse();
        }
    }

    [Fact]
    public void MacOpenContention_AccessDeniedIsNotRetryable()
    {
        PackageCacheFileLock.IsMacOpenContention(
            CreateNativeIOException(13)).ShouldBeFalse();
    }

    [Fact]
    public void ManagedLockContention_CurrentPlatformCodesAreClassified()
    {
        int[] acceptedCodes = OperatingSystem.IsWindows()
            ? [32, 33]
            : OperatingSystem.IsMacOS()
                ? []
                : [11, 13];
        int[] candidateCodes = [5, 11, 13, 32, 33, 35];

        foreach (int nativeError in candidateCodes)
        {
            PackageCacheFileLock.IsManagedLockContention(
                    CreateNativeIOException(nativeError))
                .ShouldBe(acceptedCodes.Contains(nativeError));
        }
    }

    [Theory]
    [InlineData(35, true)]
    [InlineData(13, false)]
    [InlineData(123456, false)]
    public void RawMacLock_ClassifiesContentionBeforeFaultScheduling(int error, bool contended)
    {
        int calls = 0;
        FileSystemReplayOutcome<bool> outcome = NativeFileSystemPrimitives.TryLockDescriptor(
            73,
            (descriptor, flags) =>
            {
                descriptor.ShouldBe(73);
                flags.ShouldBe(2 | 4);
                calls++;
                return -1;
            },
            () => error);

        calls.ShouldBe(1);
        outcome.Kind.ShouldBe(contended
            ? FileSystemReplayOutcomeKind.Contended
            : FileSystemReplayOutcomeKind.PrimitiveFailure);
        if (!contended)
        {
            FileSystemFailureContext.TryGetOrigin(
                outcome.PrimitiveFailure!.Exception, out FileSystemFailureContext? failure).ShouldBeTrue();
            failure!.NativeErrorCode.ShouldBe(error);
            outcome.PrimitiveFailure.ReplaySafety.ShouldBe(FileSystemReplaySafety.Safe);
            FileSystemFailureContext.TryGetTerminal(outcome.PrimitiveFailure.Exception, out _).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Coordination_MoreThanFiveBusyPollsDoesNotSpendFaultBudget()
    {
        using TestDirectory directory = new();
        ScriptedFileSystemPrimitives primitives = new();
        primitives.Set("Probe", ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.Directory));
        primitives.Set<FileStream>("OpenFile", _ => OpenScriptedLockFile(directory.Path));
        for (int index = 0; index < 7; index++)
            primitives.Enqueue("TryLock", FileSystemReplayOutcome<bool>.Complete(false));
        primitives.Enqueue("TryLock", FileSystemReplayOutcome<bool>.Complete(true));
        primitives.Set("Unlock", FileSystemReplayOutcome<bool>.Complete(true));
        PackageCacheCoordinator coordinator = new(
            directory.Path,
            primitives.CreateContext(retries: 2),
            NullPackageCacheContentionObserver.Instance);
        PackageCacheKey cacheKey = PackageCacheKey.Create(
            new PackageReference("busy.package", "1.0.0"));

        await using (await coordinator.AcquireIdentityAsync(
            cacheKey,
            TestContext.Current.CancellationToken))
        {
        }

        primitives.Count("TryLock").ShouldBe(8);
        primitives.AsyncWaits.ShouldBeEmpty();
        primitives.Waits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Coordination_InterleavedFaultsDoNotResetBudget()
    {
        using TestDirectory directory = new();
        ScriptedFileSystemPrimitives primitives = new();
        primitives.Set("Probe", ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.Directory));
        primitives.Set<FileStream>("OpenFile", _ => OpenScriptedLockFile(directory.Path));
        for (int index = 0; index < 3; index++)
        {
            primitives.Enqueue(
                "TryLock",
                ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "TryLock"));
            primitives.Enqueue("TryLock", FileSystemReplayOutcome<bool>.Complete(false));
        }

        PackageCacheCoordinator coordinator = new(
            directory.Path,
            primitives.CreateContext(retries: 2),
            NullPackageCacheContentionObserver.Instance);
        PackageCacheKey cacheKey = PackageCacheKey.Create(
            new PackageReference("faulty.package", "1.0.0"));

        await Should.ThrowAsync<PackageInstallException>(
            () => coordinator.AcquireIdentityAsync(
                cacheKey,
                TestContext.Current.CancellationToken));

        // Three faults against two retries: the busy polls between them never refilled it.
        primitives.Count("TryLock").ShouldBe(5);
        primitives.AsyncWaits.Count.ShouldBe(2);
        PackageCacheCoordinator.GetProcessLockEntryCount(directory.Path).ShouldBe(0);
    }

    [Fact]
    public async Task Lease_UnlockExhaustionReleasesHandleAndSemaphoreOnce()
    {
        using TestDirectory directory = new();
        ScriptedFileSystemPrimitives primitives = new();
        primitives.Set("Probe", ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.Directory));
        primitives.Set<FileStream>("OpenFile", _ => OpenScriptedLockFile(directory.Path));
        primitives.Set("TryLock", FileSystemReplayOutcome<bool>.Complete(true));
        primitives.Set(
            "Unlock",
            _ => ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "Unlock"));
        PackageCacheCoordinator coordinator = new(
            directory.Path,
            primitives.CreateContext(retries: 2),
            NullPackageCacheContentionObserver.Instance);
        PackageCacheKey cacheKey = PackageCacheKey.Create(
            new PackageReference("unlock.package", "1.0.0"));
        PackageCacheLease lease = await coordinator.AcquireIdentityAsync(
            cacheKey,
            TestContext.Current.CancellationToken);

        await Should.ThrowAsync<IOException>(async () => await lease.DisposeAsync());
        await lease.DisposeAsync();

        primitives.Count("Unlock").ShouldBe(3);
        primitives.AsyncWaits.ShouldBe([10, 10]);
        primitives.Waits.ShouldBeEmpty();
        PackageCacheCoordinator.GetProcessLockEntryCount(directory.Path).ShouldBe(0);
        await using (await new PackageCacheCoordinator(directory.Path)
            .AcquireIdentityAsync(cacheKey, TestContext.Current.CancellationToken))
        {
        }
    }

    [Fact]
    public async Task NonblockingOwner_FaultsAndUnlockUseAsyncWaitsOnOneHandle()
    {
        using TestDirectory directory = new();
        ScriptedFileSystemPrimitives files = new();
        files.Set("Probe", ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.Directory));
        FileStream? retained = null;
        files.Set<FileStream>("OpenFile", _ =>
        {
            FileSystemReplayOutcome<FileStream> opened = OpenScriptedLockFile(directory.Path);
            retained = opened.Value;
            return opened;
        });
        files.Enqueue("OpenFile", ScriptedFileSystemPrimitives.NativeFailure<FileStream>(5, "owner open"));
        files.Enqueue("TryLock", ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "owner lock"));
        files.Set("TryLock", FileSystemReplayOutcome<bool>.Complete(true));
        files.Enqueue("Unlock", ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "owner unlock"));
        files.Set("Unlock", FileSystemReplayOutcome<bool>.Complete(true));
        PackageCacheCoordinator coordinator = new(
            directory.Path, files.CreateContext(retries: 2), NullPackageCacheContentionObserver.Instance);

        PackageCacheLease? lease = await coordinator.TryAcquireOperationOwnerAsync(
            "owner", TestContext.Current.CancellationToken);

        // Open and lock spend the same episode's two retries; unlock has its own budget.
        lease.ShouldNotBeNull();
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        retained.ShouldNotBeNull().CanRead.ShouldBeFalse();
        files.Count("OpenFile").ShouldBe(2);
        files.Count("TryLock").ShouldBe(2);
        files.Count("Unlock").ShouldBe(2);
        files.AsyncWaits.ShouldBe([10, 10, 10]);
        files.Waits.ShouldBeEmpty();
        PackageCacheCoordinator.GetProcessLockEntryCount(directory.Path).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonblockingOwner_ContentionReturnsImmediatelyWithoutFaultWaits(bool processContention)
    {
        using TestDirectory directory = new();
        ScriptedFileSystemPrimitives files = new();
        files.Set("Probe", ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.Directory));
        FileStream? retained = null;
        files.Set<FileStream>("OpenFile", _ =>
        {
            FileSystemReplayOutcome<FileStream> opened = OpenScriptedLockFile(directory.Path);
            retained = opened.Value;
            return opened;
        });
        files.Set("TryLock", FileSystemReplayOutcome<bool>.Complete(processContention));
        files.Set("Unlock", FileSystemReplayOutcome<bool>.Complete(true));
        PackageCacheCoordinator coordinator = new(
            directory.Path, files.CreateContext(retries: 0), NullPackageCacheContentionObserver.Instance);
        PackageCacheLease? first = processContention
            ? await coordinator.TryAcquireOperationOwnerAsync("owner", TestContext.Current.CancellationToken)
            : null;
        try
        {
            PackageCacheLease? contended = await coordinator.TryAcquireOperationOwnerAsync(
                "owner", TestContext.Current.CancellationToken);

            contended.ShouldBeNull();
            files.Count("OpenFile").ShouldBe(1);
            files.Count("TryLock").ShouldBe(1);
            files.AsyncWaits.ShouldBeEmpty();
            files.Waits.ShouldBeEmpty();
            if (!processContention)
                retained.ShouldNotBeNull().CanRead.ShouldBeFalse();
        }
        finally
        {
            if (first is not null)
                await first.DisposeAsync();
        }
        PackageCacheCoordinator.GetProcessLockEntryCount(directory.Path).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonblockingOwner_FaultExhaustionOrCancellationReleasesRetainedHandle(bool cancel)
    {
        using TestDirectory directory = new();
        ScriptedFileSystemPrimitives files = new();
        files.Set("Probe", ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.Directory));
        FileStream? retained = null;
        files.Set<FileStream>("OpenFile", _ =>
        {
            FileSystemReplayOutcome<FileStream> opened = OpenScriptedLockFile(directory.Path);
            retained = opened.Value;
            return opened;
        });
        files.Set("TryLock", ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "owner lock"));
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        if (cancel)
            files.OnWait = cancellation.Cancel;
        PackageCacheCoordinator coordinator = new(
            directory.Path, files.CreateContext(retries: 2), NullPackageCacheContentionObserver.Instance);

        if (cancel)
        {
            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await coordinator.TryAcquireOperationOwnerAsync("owner", cancellation.Token));
        }
        else
        {
            PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(async () =>
                await coordinator.TryAcquireOperationOwnerAsync("owner", cancellation.Token));
            exception.ErrorCode.ShouldBe(PackageInstallErrorCode.CoordinationFailed);
            FileSystemFailureContext.TryGetTerminal(exception, out FileSystemFailureContext? failure).ShouldBeTrue();
            failure!.RetriesUsed.ShouldBe(2);
        }

        retained.ShouldNotBeNull().CanRead.ShouldBeFalse();
        files.Count("OpenFile").ShouldBe(1);
        files.Count("TryLock").ShouldBe(cancel ? 1 : 3);
        files.Count("Unlock").ShouldBe(0);
        files.AsyncWaits.ShouldBe(cancel ? [10] : [10, 10]);
        files.Waits.ShouldBeEmpty();
        PackageCacheCoordinator.GetProcessLockEntryCount(directory.Path).ShouldBe(0);
    }

    private static FileSystemReplayOutcome<FileStream> OpenScriptedLockFile(string root)
    {
        string path = Path.Combine(root, $"scripted-{Guid.NewGuid():N}.lock");
        return FileSystemReplayOutcome<FileStream>.Complete(
            new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete,
                1,
                FileOptions.DeleteOnClose));
    }

    private sealed class TestDirectory : IDisposable
    {
        internal TestDirectory()
        {
            Path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "phase5-coordinator-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private static void UpdateMaximum(
        ref int maximum,
        int candidate)
    {
        int current = Volatile.Read(ref maximum);
        while (candidate > current)
        {
            int observed = Interlocked.CompareExchange(
                ref maximum,
                candidate,
                current);
            if (observed == current)
                return;

            current = observed;
        }
    }

    private static IOException CreateNativeIOException(
        int nativeError) =>
        new(
            "Synthetic native I/O failure.",
            unchecked((int)0x80070000) | nativeError);

    private static async Task WaitForIdentityReferenceCountAsync(
        string cacheRoot,
        PackageCacheKey cacheKey,
        int expectedCount)
    {
        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(5));
        while (PackageCacheCoordinator.GetIdentityReferenceCount(
                cacheRoot,
                cacheKey) < expectedCount)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                timeoutSource.Token);
        }
    }

    private static MemoryStream CreateArchive(string packageJson)
    {
        MemoryStream archive = new();
        using (GZipStream gzip = new(
            archive,
            CompressionLevel.Fastest,
            leaveOpen: true))
        using (TarWriter writer = new(
            gzip,
            TarEntryFormat.Pax,
            leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(
                TarEntryType.Directory,
                "package/"));
            byte[] manifest = Encoding.UTF8.GetBytes(packageJson);
            writer.WriteEntry(new PaxTarEntry(
                TarEntryType.RegularFile,
                "package/package.json")
            {
                DataStream = new MemoryStream(manifest)
            });
        }

        archive.Position = 0;
        return archive;
    }
}
