// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.Models;
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
    public async Task SameFileMutation_WaitsUntilLeaseIsReleased()
    {
        using TestDirectory directory = new();
        PackageCacheCoordinator firstCoordinator =
            new(directory.Path);
        PackageCacheCoordinator secondCoordinator =
            new(directory.Path);
        string filePath = Path.Combine(
            directory.Path,
            "fhirpkg.lock.json");
        await using PackageCacheLease first =
            await firstCoordinator.AcquireFileMutationAsync(
                filePath,
                TestContext.Current.CancellationToken);

        Task<PackageCacheLease> waiting =
            secondCoordinator.AcquireFileMutationAsync(
                filePath,
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
    public async Task FileMutationsUseOneProcessCoordinationDomain()
    {
        using TestDirectory directory = new();
        PackageCacheCoordinator firstCoordinator =
            new(directory.Path);
        PackageCacheCoordinator secondCoordinator =
            new(directory.Path);
        await using PackageCacheLease first =
            await firstCoordinator.AcquireFileMutationAsync(
                Path.Combine(
                    directory.Path,
                    "first.lock.json"),
                TestContext.Current.CancellationToken);

        Task<PackageCacheLease> waiting =
            secondCoordinator.AcquireFileMutationAsync(
                Path.Combine(
                    directory.Path,
                    "second.lock.json"),
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
