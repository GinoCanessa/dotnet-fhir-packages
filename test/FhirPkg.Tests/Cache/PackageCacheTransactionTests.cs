// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Tests.Support;
using FhirPkg.Utilities;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Cache;

public sealed class PackageCacheTransactionTests : IDisposable
{
    private static readonly PackageReference s_reference = new(
        "example.package",
        "1.0.0");

    private readonly string _cacheRoot = Path.Combine(
        AppContext.BaseDirectory,
        $"fhir-transaction-{Guid.NewGuid():N}");

    public static TheoryData<string, string, string, bool, bool> OverwriteCrashPoints
    {
        get
        {
            (PackageCacheFaultPoint Point, PackageCacheTransactionState State, string Value)[] points =
            [
                (PackageCacheFaultPoint.JournalWritten, PackageCacheTransactionState.Prepared, "old"),
                (PackageCacheFaultPoint.OriginalRenamed, PackageCacheTransactionState.Prepared, "old"),
                (PackageCacheFaultPoint.JournalWritten, PackageCacheTransactionState.OriginalMoved, "old"),
                (PackageCacheFaultPoint.ReplacementPromoted, PackageCacheTransactionState.OriginalMoved, "old"),
                (PackageCacheFaultPoint.JournalWritten, PackageCacheTransactionState.NewPromoted, "new"),
                (PackageCacheFaultPoint.MetadataReplaced, PackageCacheTransactionState.MetadataCommitted, "new"),
                (PackageCacheFaultPoint.JournalWritten, PackageCacheTransactionState.MetadataCommitted, "new"),
                (PackageCacheFaultPoint.JournalWritten, PackageCacheTransactionState.Completed, "new"),
                (PackageCacheFaultPoint.ArtifactRemoved, PackageCacheTransactionState.Completed, "new"),
            ];
            TheoryData<string, string, string, bool, bool> cases = [];
            foreach (bool oldRawRoot in new[] { false, true })
            {
                foreach (bool newRawRoot in new[] { false, true })
                {
                    foreach ((PackageCacheFaultPoint point, PackageCacheTransactionState state, string value) in points)
                        cases.Add(point.ToString(), state.ToString(), value, oldRawRoot, newRawRoot);
                }
            }

            return cases;
        }
    }

    public static TheoryData<bool, string, int, bool> LegacyWrapperFailures
    {
        get
        {
            TheoryData<bool, string, int, bool> cases = [];
            foreach (bool import in new[] { false, true })
            {
                foreach (bool abandoned in new[] { false, true })
                {
                    cases.Add(import, "MoveDirectory", 1, abandoned);
                    cases.Add(import, "CreateDirectory", 1, abandoned);
                    cases.Add(import, "MoveDirectory", 2, abandoned);
                }
            }

            return cases;
        }
    }

    [Fact]
    public async Task NewInstall_CommitsContentMetadataAndCleansArtifacts()
    {
        using DiskPackageCache cache = CreateCache();
        using MemoryStream archive = CreateArchive("new");

        PackageRecord record = await cache.InstallAsync(
            s_reference,
            archive,
            new InstallCacheOptions
            {
                VerifyChecksum = false,
                ArchiveSha256 = "new-hash"
            },
            TestContext.Current.CancellationToken);
        CacheMetadata metadata = await cache.GetMetadataAsync(
            TestContext.Current.CancellationToken);

        record.Manifest.Version.ShouldBe("1.0.0");
        (await cache.GetFileContentAsync(
            s_reference,
            "value.txt",
            TestContext.Current.CancellationToken)).ShouldBe("new");
        metadata.Packages[
            PackageCacheKey.Create(s_reference).MetadataKey]
            .ArchiveSha256.ShouldBe("new-hash");
        AssertNoOperationArtifacts();
    }

    [Theory]
    [InlineData(
        nameof(PackageCacheFaultPoint.JournalWritten),
        nameof(PackageCacheTransactionState.Prepared),
        false)]
    [InlineData(
        nameof(PackageCacheFaultPoint.ReplacementPromoted),
        nameof(PackageCacheTransactionState.OriginalMoved),
        false)]
    public async Task NewInstall_InjectedCrash_RecoversDeterministically(
        string pointName,
        string stateName,
        bool expectedInstalled)
    {
        ThrowOnceObserver observer = new(
            Enum.Parse<PackageCacheFaultPoint>(pointName),
            Enum.Parse<PackageCacheTransactionState>(stateName));
        using (DiskPackageCache faultingCache = CreateCache(observer))
        using (MemoryStream archive = CreateArchive("new"))
        {
            await Should.ThrowAsync<PackageCacheInjectedFaultException>(
                () => faultingCache.InstallAsync(
                    s_reference,
                    archive,
                    new InstallCacheOptions
                    {
                        VerifyChecksum = false,
                        ArchiveSha256 = "new-hash"
                    },
                    TestContext.Current.CancellationToken));
        }

        using DiskPackageCache recoveredCache = CreateCache();
        (await recoveredCache.IsInstalledAsync(
            s_reference,
            TestContext.Current.CancellationToken))
            .ShouldBe(expectedInstalled);
        if (expectedInstalled)
        {
            (await recoveredCache.GetFileContentAsync(
                s_reference,
                "value.txt",
                TestContext.Current.CancellationToken)).ShouldBe("new");
        }

        AssertNoOperationArtifacts();
    }

    [Fact]
    public async Task Recovery_RemovesArchiveFromAbandonedOperationDirectory()
    {
        using MemoryStream source = CreateArchive("new");
        await using PackageContentAcquisition acquisition =
            await PackageContentAcquirer.AcquireAsync(
                source,
                _cacheRoot,
                new PackageInstallLimits(),
                verifyChecksums: false,
                directive: s_reference.FhirDirective,
                cancellationToken:
                    TestContext.Current.CancellationToken);
        ThrowOnceObserver observer = new(
            PackageCacheFaultPoint.JournalWritten,
            PackageCacheTransactionState.Prepared);
        using (DiskPackageCache faultingCache = CreateCache(observer))
        using (MemoryStream unusedSource = new())
        {
            await Should.ThrowAsync<PackageCacheInjectedFaultException>(
                () => faultingCache.InstallAsync(
                    s_reference,
                    unusedSource,
                    new InstallCacheOptions
                    {
                        VerifyChecksum = false,
                        AcquiredContent = acquisition
                    },
                    TestContext.Current.CancellationToken));
        }

        File.Exists(acquisition.ArchivePath).ShouldBeTrue();
        using DiskPackageCache recoveredCache = CreateCache();
        (await recoveredCache.IsInstalledAsync(
            s_reference,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
        Directory.Exists(acquisition.OperationDirectory).ShouldBeFalse();
        AssertNoOperationArtifacts();
    }

    [Theory]
    [MemberData(nameof(OverwriteCrashPoints))]
    public async Task Overwrite_InjectedCrash_RecoversCoherentState(
        string pointName,
        string stateName,
        string expectedValue,
        bool oldRawRoot,
        bool newRawRoot)
    {
        CacheMetadataEntry original = await InstallInitialGenerationAsync(oldRawRoot);
        PackageCacheFaultPoint point =
            Enum.Parse<PackageCacheFaultPoint>(pointName);
        PackageCacheTransactionState state =
            Enum.Parse<PackageCacheTransactionState>(stateName);
        ThrowOnceObserver observer = new(point, state);
        using (DiskPackageCache faultingCache = CreateCache(observer))
        using (MemoryStream replacement = CreateGenerationArchive("new", newRawRoot))
        {
            await Should.ThrowAsync<PackageCacheInjectedFaultException>(
                async () =>
                {
                    try
                    {
                        await faultingCache.InstallAsync(
                            s_reference,
                            replacement,
                            new InstallCacheOptions
                            {
                                VerifyChecksum = false,
                                OverwriteExisting = true,
                                Unmodified = newRawRoot,
                                ArchiveSha256 = "new-hash",
                                SourcePublicationDate = GenerationPublicationDate("new")
                            },
                            TestContext.Current.CancellationToken);
                    }
                    catch (Exception exception) when (exception is not PackageCacheInjectedFaultException)
                    {
                        try
                        {
                            ITestOutputHelper? output = TestContext.Current.TestOutputHelper;
                            if (output is not null)
                            {
                                output.WriteLine(
                                    $"Unexpected installation exception in {nameof(Overwrite_InjectedCrash_RecoversCoherentState)}: " +
                                    $"(pointName: \"{pointName}\", stateName: \"{stateName}\", " +
                                    $"expectedValue: \"{expectedValue}\", oldRawRoot: {oldRawRoot}, newRawRoot: {newRawRoot})");
                                output.WriteLine(
                                    $"UTC: {DateTimeOffset.UtcNow:O}; ProcessId: {Environment.ProcessId}; " +
                                    $"Runtime: {RuntimeInformation.FrameworkDescription}");
                                output.WriteLine($"CacheRoot: {_cacheRoot}");
                                if (observer.OperationId is string operationId)
                                    output.WriteLine($"OperationId: {operationId}");
                                output.WriteLine(exception.ToString());
                                WriteExceptionDetails(output, exception, "Exception");
                            }
                        }
                        catch (Exception)
                        {
                            // Best-effort diagnostics must not replace the installation exception.
                        }

                        throw;
                    }
                });
        }

        using DiskPackageCache recoveredCache = CreateCache();
        CacheMetadataEntry recovered = await AssertGenerationAsync(
            recoveredCache,
            expectedValue,
            expectedValue == "old" ? oldRawRoot : newRawRoot,
            expectedValue == "old" ? original.ContentGeneration! : observer.OperationId!);
        if (expectedValue == "old")
            recovered.ShouldBe(original);
        else
            recovered.ContentGeneration.ShouldNotBe(original.ContentGeneration);
        AssertNoOperationArtifacts();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationAtCommitBoundary_LeavesPriorPackage(
        bool oldRawRoot,
        bool newRawRoot)
    {
        CacheMetadataEntry original = await InstallInitialGenerationAsync(oldRawRoot);
        byte[] metadataBefore = File.ReadAllBytes(Path.Combine(_cacheRoot, "packages.ini"));
        using CancellationTokenSource source = new();
        CancelAtPreparedObserver observer = new(source);
        using DiskPackageCache cache = CreateCache(observer);
        using MemoryStream replacement = CreateGenerationArchive("new", newRawRoot);

        await Should.ThrowAsync<OperationCanceledException>(
            () => cache.InstallAsync(
                s_reference,
                replacement,
                new InstallCacheOptions
                {
                    VerifyChecksum = false,
                    OverwriteExisting = true,
                    Unmodified = newRawRoot,
                    ArchiveSha256 = "new-hash",
                    SourcePublicationDate = GenerationPublicationDate("new")
                },
                source.Token));

        AssertGenerationTree(
            PackageCacheKey.Create(s_reference).GetPackageDirectoryPath(_cacheRoot),
            "old",
            oldRawRoot);
        File.ReadAllBytes(Path.Combine(_cacheRoot, "packages.ini")).ShouldBe(metadataBefore);
        using DiskPackageCache recoveredCache = CreateCache();
        (await AssertGenerationAsync(
            recoveredCache, "old", oldRawRoot, original.ContentGeneration!)).ShouldBe(original);
        AssertNoOperationArtifacts();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationAfterFirstRename_CompletesCommit(
        bool oldRawRoot,
        bool newRawRoot)
    {
        CacheMetadataEntry original = await InstallInitialGenerationAsync(oldRawRoot);
        using CancellationTokenSource source = new();
        RenameBarrierObserver observer = new();
        using DiskPackageCache cache = CreateCache(observer);
        using MemoryStream replacement = CreateGenerationArchive("new", newRawRoot);
        Task<PackageRecord> installTask = cache.InstallAsync(
            s_reference,
            replacement,
            new InstallCacheOptions
            {
                VerifyChecksum = false,
                OverwriteExisting = true,
                Unmodified = newRawRoot,
                ArchiveSha256 = "new-hash",
                SourcePublicationDate = GenerationPublicationDate("new")
            },
            source.Token);

        await observer.Renamed.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        source.Cancel();
        observer.Release.TrySetResult();
        PackageRecord record = await installTask;

        record.Manifest.Version.ShouldBe("1.0.0");
        CacheMetadataEntry committed = await AssertGenerationAsync(
            cache, "new", newRawRoot, observer.OperationId!);
        committed.ContentGeneration.ShouldNotBe(original.ContentGeneration);
        AssertNoOperationArtifacts();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReaderDuringReplacement_WaitsForCoherentTarget(
        bool oldRawRoot,
        bool newRawRoot)
    {
        CacheMetadataEntry original = await InstallInitialGenerationAsync(oldRawRoot);
        RenameBarrierObserver observer = new();
        using DiskPackageCache cache = CreateCache(observer);
        using MemoryStream replacement = CreateGenerationArchive("new", newRawRoot);
        Task<PackageRecord> installTask = cache.InstallAsync(
            s_reference,
            replacement,
            new InstallCacheOptions
            {
                VerifyChecksum = false,
                OverwriteExisting = true,
                Unmodified = newRawRoot,
                ArchiveSha256 = "new-hash",
                SourcePublicationDate = GenerationPublicationDate("new")
            },
            TestContext.Current.CancellationToken);
        await observer.Renamed.Task.WaitAsync(
            TestContext.Current.CancellationToken);

        Task<PackageRecord?> readTask = cache.GetPackageAsync(
            s_reference,
            TestContext.Current.CancellationToken);
        await Task.Delay(
            TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken);
        readTask.IsCompleted.ShouldBeFalse();

        observer.Release.TrySetResult();
        await installTask;
        PackageRecord? record = await readTask;
        record.ShouldNotBeNull();
        record.ContentPath.ShouldBe(newRawRoot
            ? record.DirectoryPath
            : Path.Combine(record.DirectoryPath, "package"));
        record.ContentGeneration.ShouldNotBeNull().ShouldStartWith($"{observer.OperationId}\0");
        AssertGenerationTree(record.DirectoryPath, "new", newRawRoot);
        CacheMetadataEntry committed = await AssertGenerationAsync(
            cache, "new", newRawRoot, observer.OperationId!);
        committed.ContentGeneration.ShouldNotBe(original.ContentGeneration);
        AssertNoOperationArtifacts();
    }

    [Theory]
    [MemberData(nameof(LegacyWrapperFailures))]
    public async Task LegacyWrappingFailure_LeavesPriorGenerationAndCleansOperation(
        bool import,
        string operationName,
        int callIndex,
        bool abandoned)
    {
        bool oldRawRoot = import;
        CacheMetadataEntry original = await InstallInitialGenerationAsync(oldRawRoot);
        string target = PackageCacheKey.Create(s_reference).GetPackageDirectoryPath(_cacheRoot);
        string metadataPath = Path.Combine(_cacheRoot, "packages.ini");
        byte[] metadataBefore = File.ReadAllBytes(metadataPath);
        using MemoryStream replacement = CreateGenerationArchive("new", rawRoot: true);
        await using PackageContentAcquisition? acquisition = abandoned
            ? await PackageContentAcquirer.AcquireAsync(
                replacement,
                _cacheRoot,
                new PackageInstallLimits(),
                cancellationToken: TestContext.Current.CancellationToken)
            : null;
        FaultingFileOperations fileOperations = new(
            new FileOperationFault(operationName, callIndex));
        using DiskPackageCache faultingCache = CreateCache(fileOperations: fileOperations);
        InstallCacheOptions options = new()
        {
            OverwriteExisting = true,
            AcquiredContent = acquisition,
        };

        await Should.ThrowAsync<IOException>(() => import
            ? faultingCache.ImportAsync(replacement, options, TestContext.Current.CancellationToken)
            : faultingCache.InstallAsync(
                s_reference, replacement, options, TestContext.Current.CancellationToken));

        // Inspect raw state first: an SDK read could recover or remove the evidence.
        replacement.CanRead.ShouldBeTrue();
        AssertGenerationTree(target, "old", oldRawRoot);
        File.ReadAllBytes(metadataPath).ShouldBe(metadataBefore);
        fileOperations.MutatingCalls.ShouldNotContain("WriteFileAndFlush");
        fileOperations.MutatingCalls.ShouldNotContain("MoveArtifact");
        foreach (string name in new[] { "transactions", "backup", "quarantine" })
        {
            string path = Path.Combine(_cacheRoot, ".fhirpkg", name);
            if (Directory.Exists(path))
                Directory.GetFileSystemEntries(path).ShouldBeEmpty();
        }

        if (abandoned)
        {
            acquisition.ShouldNotBeNull();
            File.Exists(acquisition.ArchivePath).ShouldBeTrue();
            string expanded = Path.Combine(acquisition.OperationDirectory, "expanded");
            string[] legacy = Directory.GetDirectories(acquisition.OperationDirectory, "legacy-*");
            if (operationName == "MoveDirectory" && callIndex == 1)
            {
                legacy.ShouldBeEmpty();
                AssertGenerationTree(expanded, "new", rawRoot: true);
            }
            else
            {
                AssertGenerationTree(legacy.ShouldHaveSingleItem(), "new", rawRoot: true);
                if (operationName == "CreateDirectory")
                    Directory.Exists(expanded).ShouldBeFalse();
                else
                    Directory.GetFileSystemEntries(expanded).ShouldBeEmpty();
            }
        }
        else
        {
            AssertNoOperationArtifacts();
        }

        using DiskPackageCache recoveredCache = CreateCache();
        (await AssertGenerationAsync(
            recoveredCache, "old", oldRawRoot, original.ContentGeneration!)).ShouldBe(original);
        if (acquisition is not null)
            Directory.Exists(acquisition.OperationDirectory).ShouldBeFalse();
        AssertNoOperationArtifacts();
    }

    [Fact]
    public async Task Clear_PreservesUnknownMetadataAndIgnoresHiddenArtifacts()
    {
        await InstallInitialAsync();
        string metadataPath = Path.Combine(_cacheRoot, "packages.ini");
        await File.AppendAllTextAsync(
            metadataPath,
            $"{Environment.NewLine}[custom]{Environment.NewLine}value = keep{Environment.NewLine}",
            TestContext.Current.CancellationToken);
        string hiddenContent = Path.Combine(
            _cacheRoot,
            ".fhirpkg",
            "backup",
            "fake#1.0.0",
            "package");
        Directory.CreateDirectory(hiddenContent);
        await File.WriteAllTextAsync(
            Path.Combine(hiddenContent, "package.json"),
            """{"name":"fake","version":"1.0.0"}""",
            TestContext.Current.CancellationToken);
        using DiskPackageCache cache = CreateCache();

        (await cache.ListPackagesAsync(
            ct: TestContext.Current.CancellationToken)).Count.ShouldBe(1);
        (await cache.ClearAsync(
            TestContext.Current.CancellationToken)).ShouldBe(1);
        (await cache.ListPackagesAsync(
            ct: TestContext.Current.CancellationToken)).ShouldBeEmpty();
        string metadata = await File.ReadAllTextAsync(
            metadataPath,
            TestContext.Current.CancellationToken);
        metadata.ShouldContain("[custom]");
        metadata.ShouldContain("value = keep");
        Directory.Exists(hiddenContent).ShouldBeTrue();
    }

    [Fact]
    public async Task Remove_MetadataBeforeJournalCrash_RecoversCompletedRemoval()
    {
        await InstallInitialAsync();
        ThrowOnceObserver observer = new(
            PackageCacheFaultPoint.MetadataReplaced,
            PackageCacheTransactionState.MetadataCommitted);
        using (DiskPackageCache faultingCache = CreateCache(observer))
        {
            await Should.ThrowAsync<PackageCacheInjectedFaultException>(
                () => faultingCache.RemoveAsync(
                    s_reference,
                    TestContext.Current.CancellationToken));
        }

        using DiskPackageCache recoveredCache = CreateCache();
        (await recoveredCache.IsInstalledAsync(
            s_reference,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
        CacheMetadata metadata = await recoveredCache.GetMetadataAsync(
            TestContext.Current.CancellationToken);
        metadata.Packages.ContainsKey(
            PackageCacheKey.Create(s_reference).MetadataKey).ShouldBeFalse();
        AssertNoOperationArtifacts();
    }

    [Fact]
    public async Task RemoveRecovery_TargetReappeared_RollsBackInsteadOfForward()
    {
        await InstallInitialAsync();
        ThrowOnceObserver observer = new(
            PackageCacheFaultPoint.MetadataReplaced,
            PackageCacheTransactionState.MetadataCommitted);
        using (DiskPackageCache faultingCache = CreateCache(observer))
        {
            await Should.ThrowAsync<PackageCacheInjectedFaultException>(
                () => faultingCache.RemoveAsync(
                    s_reference,
                    TestContext.Current.CancellationToken));
        }

        string targetPath = PackageCacheKey.Create(s_reference)
            .GetPackageDirectoryPath(_cacheRoot);
        string contentPath = Path.Combine(targetPath, "package");
        Directory.CreateDirectory(contentPath);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, "package.json"),
            """{"name":"example.package","version":"1.0.0"}""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, "value.txt"),
            "unexpected",
            TestContext.Current.CancellationToken);

        using DiskPackageCache recoveredCache = CreateCache();
        (await recoveredCache.GetFileContentAsync(
            s_reference,
            "value.txt",
            TestContext.Current.CancellationToken)).ShouldBe("old");
        AssertNoOperationArtifacts();
    }

    [Fact]
    public async Task Repair_MetadataFailure_RestoresCorruptTarget()
    {
        string contentPath = Path.Combine(
            PackageCacheKey.Create(s_reference)
                .GetPackageDirectoryPath(_cacheRoot),
            "package");
        Directory.CreateDirectory(contentPath);
        string manifestPath = Path.Combine(contentPath, "package.json");
        await File.WriteAllTextAsync(
            manifestPath,
            "{original-corrupt-json",
            TestContext.Current.CancellationToken);
        string metadataPath = Path.Combine(_cacheRoot, "packages.ini");
        Directory.CreateDirectory(metadataPath);
        using DiskPackageCache cache = CreateCache();
        using MemoryStream replacement = CreateArchive("new");

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => cache.InstallAsync(
                    s_reference,
                    replacement,
                    new InstallCacheOptions { VerifyChecksum = false },
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.CommitFailed);
        (await File.ReadAllTextAsync(
            manifestPath,
            TestContext.Current.CancellationToken))
            .ShouldBe("{original-corrupt-json");
        PackageCacheInspection inspection = await cache.InspectAsync(
            s_reference,
            TestContext.Current.CancellationToken);
        inspection.State.ShouldBe(PackageCacheInspectionState.Corrupt);
    }

    [Theory]
    [InlineData(nameof(PackageCacheTransactionState.RollbackStarted))]
    [InlineData(nameof(PackageCacheTransactionState.RolledBack))]
    public async Task RollbackTransitionCrash_RecoversPriorGeneration(
        string stateName)
    {
        await InstallInitialAsync();
        string metadataPath = Path.Combine(_cacheRoot, "packages.ini");
        File.Delete(metadataPath);
        Directory.CreateDirectory(metadataPath);
        ThrowOnceObserver observer = new(
            PackageCacheFaultPoint.JournalWritten,
            Enum.Parse<PackageCacheTransactionState>(stateName));
        using (DiskPackageCache faultingCache = CreateCache(observer))
        using (MemoryStream replacement = CreateArchive("new"))
        {
            PackageInstallException exception =
                await Should.ThrowAsync<PackageInstallException>(
                    () => faultingCache.InstallAsync(
                        s_reference,
                        replacement,
                        new InstallCacheOptions
                        {
                            VerifyChecksum = false,
                            OverwriteExisting = true,
                            ArchiveSha256 = "new-hash"
                        },
                        TestContext.Current.CancellationToken));
            exception.ErrorCode.ShouldBe(
                PackageInstallErrorCode.CommitFailed);
        }

        using DiskPackageCache recoveredCache = CreateCache();
        (await recoveredCache.GetFileContentAsync(
            s_reference,
            "value.txt",
            TestContext.Current.CancellationToken)).ShouldBe("old");
        AssertNoOperationArtifacts();
    }

    [Fact]
    public async Task RelativeDirectoryLink_RepairRollbackRestoresLink()
    {
        if (OperatingSystem.IsWindows())
            return;

        string targetPath = PackageCacheKey.Create(s_reference)
            .GetPackageDirectoryPath(_cacheRoot);
        string linkTargetPath = Path.Combine(_cacheRoot, "link-target");
        Directory.CreateDirectory(linkTargetPath);
        await File.WriteAllTextAsync(
            Path.Combine(linkTargetPath, "marker.txt"),
            "linked",
            TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(targetPath, "link-target");
        string metadataPath = Path.Combine(_cacheRoot, "packages.ini");
        Directory.CreateDirectory(metadataPath);
        using DiskPackageCache cache = CreateCache();
        using MemoryStream replacement = CreateArchive("new");

        await Should.ThrowAsync<PackageInstallException>(
            () => cache.InstallAsync(
                s_reference,
                replacement,
                new InstallCacheOptions { VerifyChecksum = false },
                TestContext.Current.CancellationToken));

        DirectoryInfo restoredLink = new(targetPath);
        restoredLink.LinkTarget.ShouldBe("link-target");
        File.Exists(Path.Combine(targetPath, "marker.txt")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("new", "MoveDirectory", 1)]
    [InlineData("new", "AtomicReplaceFile", 4)]
    [InlineData("overwrite", "MoveDirectory", 1)]
    [InlineData("overwrite", "MoveArtifact", 1)]
    [InlineData("overwrite", "AtomicReplaceFile", 4)]
    [InlineData("repair", "MoveDirectory", 1)]
    [InlineData("repair", "MoveArtifact", 1)]
    [InlineData("repair", "AtomicReplaceFile", 4)]
    [InlineData("remove", "AtomicReplaceFile", 3)]
    [InlineData("remove", "MoveArtifact", 1)]
    public async Task FileOperationFailure_RestoresAuthoritativeState(
        string scenario,
        string operationName,
        int operationCallIndex)
    {
        if (scenario is "overwrite" or "remove")
            await InstallInitialAsync();
        else if (scenario == "repair")
            await CreateCorruptTargetAsync();

        FaultingFileOperations fileOperations = new(
            new FileOperationFault(
                operationName,
                operationCallIndex));
        using DiskPackageCache cache = CreateCache(
            fileOperations: fileOperations);

        if (scenario == "remove")
        {
            PackageInstallException exception =
                await Should.ThrowAsync<PackageInstallException>(
                    () => cache.RemoveAsync(
                        s_reference,
                        TestContext.Current.CancellationToken));
            exception.ErrorCode.ShouldBe(
                PackageInstallErrorCode.CommitFailed);
        }
        else
        {
            using MemoryStream archive = CreateArchive("new");
            PackageInstallException exception =
                await Should.ThrowAsync<PackageInstallException>(
                    () => cache.InstallAsync(
                        s_reference,
                        archive,
                        new InstallCacheOptions
                        {
                            VerifyChecksum = false,
                            OverwriteExisting = scenario == "overwrite"
                        },
                        TestContext.Current.CancellationToken));
            exception.ErrorCode.ShouldBe(
                PackageInstallErrorCode.CommitFailed);
        }

        using DiskPackageCache recoveredCache = CreateCache();
        if (scenario == "new")
        {
            (await recoveredCache.IsInstalledAsync(
                s_reference,
                TestContext.Current.CancellationToken)).ShouldBeFalse();
        }
        else if (scenario == "repair")
        {
            PackageCacheInspection inspection =
                await recoveredCache.InspectAsync(
                    s_reference,
                    TestContext.Current.CancellationToken);
            inspection.State.ShouldBe(
                PackageCacheInspectionState.Corrupt);
        }
        else
        {
            (await recoveredCache.GetFileContentAsync(
                s_reference,
                "value.txt",
                TestContext.Current.CancellationToken)).ShouldBe("old");
        }

        AssertNoOperationArtifacts();
    }

    [Fact]
    public async Task RollbackMoveFailure_PreservesBackupForRecovery()
    {
        await InstallInitialAsync();
        FaultingFileOperations fileOperations = new(
            new FileOperationFault("AtomicReplaceFile", 4),
            new FileOperationFault("MoveArtifact", 2));
        using (DiskPackageCache cache = CreateCache(
            fileOperations: fileOperations))
        using (MemoryStream replacement = CreateArchive("new"))
        {
            await Should.ThrowAsync<PackageInstallException>(
                () => cache.InstallAsync(
                    s_reference,
                    replacement,
                    new InstallCacheOptions
                    {
                        VerifyChecksum = false,
                        OverwriteExisting = true
                    },
                    TestContext.Current.CancellationToken));
        }

        string backupRoot = Path.Combine(
            _cacheRoot,
            ".fhirpkg",
            "backup");
        Directory.EnumerateFileSystemEntries(backupRoot)
            .ShouldHaveSingleItem();

        using DiskPackageCache recoveredCache = CreateCache();
        (await recoveredCache.GetFileContentAsync(
            s_reference,
            "value.txt",
            TestContext.Current.CancellationToken)).ShouldBe("old");
        AssertNoOperationArtifacts();
    }

    [Fact]
    public async Task DurableFileWriter_OrdersFlushReplaceAndDirectorySync()
    {
        string path = Path.Combine(_cacheRoot, "ordered.ini");
        FaultingFileOperations fileOperations = new();
        Dictionary<string, IReadOnlyDictionary<string, string>> sections =
            new()
            {
                ["section"] = new Dictionary<string, string>
                {
                    ["value"] = "new"
                }
            };

        await IniParser.WriteFileAtomicallyAsync(
            path,
            sections,
            fileOperations,
            TestContext.Current.CancellationToken);

        fileOperations.MutatingCalls.ShouldBe(
        [
            "CreateDirectory",
            "WriteFileAndFlush",
            "AtomicReplaceFile",
            "SynchronizeDirectory"
        ]);
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public async Task JournalAndMetadataWrites_UseDurableWriteTriplets()
    {
        FaultingFileOperations fileOperations = new();
        using DiskPackageCache cache = CreateCache(
            fileOperations: fileOperations);
        using MemoryStream archive = CreateArchive("new");

        await cache.InstallAsync(
            s_reference,
            archive,
            new InstallCacheOptions { VerifyChecksum = false },
            TestContext.Current.CancellationToken);

        int replacements = 0;
        for (int index = 0;
            index < fileOperations.MutatingCalls.Count;
            index++)
        {
            if (fileOperations.MutatingCalls[index]
                != "AtomicReplaceFile")
            {
                continue;
            }

            replacements++;
            fileOperations.MutatingCalls[index - 1]
                .ShouldBe("WriteFileAndFlush");
            fileOperations.MutatingCalls[index + 1]
                .ShouldBe("SynchronizeDirectory");
        }

        replacements.ShouldBeGreaterThanOrEqualTo(6);
    }

    [Fact]
    public void WindowsPathRename_FailureRetainsPathsFlagsAndNativeError()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(_cacheRoot);
        string sourcePath = Path.Combine(_cacheRoot, "missing-source");
        string destinationPath = Path.Combine(_cacheRoot, "destination");

        IOException exception = Should.Throw<IOException>(() =>
            SystemPackageCacheFileOperations.Instance.MoveDirectory(
                sourcePath,
                destinationPath));

        AssertWindowsRenameFailure(exception, sourcePath, destinationPath);
        Directory.Exists(sourcePath).ShouldBeFalse();
        Directory.Exists(destinationPath).ShouldBeFalse();
    }

    [Fact]
    public void WindowsPathRename_NonDeleteSharedHandleBlocksMoveUntilReleased()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string sourcePath = Path.Combine(_cacheRoot, "source");
        string destinationPath = Path.Combine(_cacheRoot, "destination");
        Directory.CreateDirectory(Path.Combine(sourcePath, "empty"));
        File.WriteAllText(Path.Combine(sourcePath, "value.txt"), "payload");

        // Open the directory itself: a child file handle need not block an
        // ancestor rename. Successful open, move, disposal and move are ordered
        // on this thread; no timing window or assumed native error is needed.
        SafeFileHandle blocker = OpenDirectoryWithoutDeleteSharing(sourcePath);
        using (blocker)
        {
            blocker.IsInvalid.ShouldBeFalse();
            blocker.IsClosed.ShouldBeFalse();
            IOException exception = Should.Throw<IOException>(() =>
                SystemPackageCacheFileOperations.Instance.MoveDirectory(
                    sourcePath,
                    destinationPath));

            AssertWindowsRenameFailure(exception, sourcePath, destinationPath);
            Directory.Exists(sourcePath).ShouldBeTrue();
            Directory.Exists(destinationPath).ShouldBeFalse();
            File.ReadAllText(Path.Combine(sourcePath, "value.txt")).ShouldBe("payload");
        }

        blocker.IsClosed.ShouldBeTrue();
        SystemPackageCacheFileOperations.Instance.MoveDirectory(
            sourcePath,
            destinationPath);

        Directory.Exists(sourcePath).ShouldBeFalse();
        Directory.Exists(Path.Combine(destinationPath, "empty")).ShouldBeTrue();
        File.ReadAllText(Path.Combine(destinationPath, "value.txt")).ShouldBe("payload");
        TestContext.Current.TestOutputHelper?.WriteLine(
            "The identical MoveDirectory succeeded after disposing the owned directory handle. " +
            "This establishes a mechanism, not ownership of the reported intermittent failure.");
    }

    [Fact]
    public async Task WindowsFileReplace_RetriesTransientErrorsAsynchronously()
    {
        TaskCompletionSource delayStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseDelay =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        int delayCalls = 0;

        Task replaceTask = PackageCacheNativeFileSystem
            .ReplaceWindowsFileDurablyAsync(
                "source",
                "destination",
                (string sourcePath, string destinationPath, uint flags) =>
                {
                    sourcePath.ShouldBe("source");
                    destinationPath.ShouldBe("destination");
                    flags.ShouldBe(0x00000009u);
                    attempts++;
                    return attempts == 2;
                },
                () => 32,
                async (
                    TimeSpan delay,
                    CancellationToken cancellationToken) =>
                {
                    delayCalls++;
                    delay.ShouldBe(TimeSpan.FromMilliseconds(10));
                    delayStarted.TrySetResult();
                    await releaseDelay.Task.WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                },
                CancellationToken.None)
            .AsTask();

        await delayStarted.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        attempts.ShouldBe(1);
        delayCalls.ShouldBe(1);
        replaceTask.IsCompleted.ShouldBeFalse();

        releaseDelay.TrySetResult();
        await replaceTask;
        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task WindowsFileReplace_NonTransientErrorFailsImmediately()
    {
        int attempts = 0;
        int delayCalls = 0;

        IOException exception = await Should.ThrowAsync<IOException>(
            () => PackageCacheNativeFileSystem
                .ReplaceWindowsFileDurablyAsync(
                    "source",
                    "destination",
                    (string sourcePath, string destinationPath, uint flags) =>
                    {
                        sourcePath.ShouldBe("source");
                        destinationPath.ShouldBe("destination");
                        flags.ShouldBe(0x00000009u);
                        attempts++;
                        return false;
                    },
                    () => 87,
                    (TimeSpan delay, CancellationToken cancellationToken) =>
                    {
                        delayCalls++;
                        return ValueTask.CompletedTask;
                    },
                    CancellationToken.None)
                .AsTask());

        attempts.ShouldBe(1);
        delayCalls.ShouldBe(0);
        Win32Exception nativeException =
            exception.InnerException.ShouldBeOfType<Win32Exception>();
        nativeException.NativeErrorCode.ShouldBe(87);
    }

    [Fact]
    public async Task WindowsFileReplace_ExhaustionReportsLastNativeError()
    {
        int attempts = 0;
        int delayCalls = 0;

        IOException exception = await Should.ThrowAsync<IOException>(
            () => PackageCacheNativeFileSystem
                .ReplaceWindowsFileDurablyAsync(
                    "source",
                    "destination",
                    (string sourcePath, string destinationPath, uint flags) =>
                    {
                        sourcePath.ShouldBe("source");
                        destinationPath.ShouldBe("destination");
                        flags.ShouldBe(0x00000009u);
                        attempts++;
                        return false;
                    },
                    () => attempts == 20 ? 33 : 5,
                    (TimeSpan delay, CancellationToken cancellationToken) =>
                    {
                        delayCalls++;
                        delay.ShouldBe(TimeSpan.FromMilliseconds(10));
                        return ValueTask.CompletedTask;
                    },
                    CancellationToken.None)
                .AsTask());

        attempts.ShouldBe(20);
        delayCalls.ShouldBe(19);
        Win32Exception nativeException =
            exception.InnerException.ShouldBeOfType<Win32Exception>();
        nativeException.NativeErrorCode.ShouldBe(33);
    }

    [Fact]
    public async Task WindowsFileReplace_CancellationStopsBeforeCommit()
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        int attempts = 0;
        int delayCalls = 0;

        await Should.ThrowAsync<OperationCanceledException>(
            () => PackageCacheNativeFileSystem
                .ReplaceWindowsFileDurablyAsync(
                    "source",
                    "destination",
                    (string sourcePath, string destinationPath, uint flags) =>
                    {
                        sourcePath.ShouldBe("source");
                        destinationPath.ShouldBe("destination");
                        flags.ShouldBe(0x00000009u);
                        attempts++;
                        return false;
                    },
                    () => 32,
                    (TimeSpan delay, CancellationToken cancellationToken) =>
                    {
                        delayCalls++;
                        delay.ShouldBe(TimeSpan.FromMilliseconds(10));
                        cancellationToken.IsCancellationRequested
                            .ShouldBeTrue();
                        return new ValueTask(
                            Task.Delay(
                                delay,
                                cancellationToken));
                    },
                    source.Token)
                .AsTask());

        attempts.ShouldBe(1);
        delayCalls.ShouldBe(1);
    }

    [Fact]
    public async Task JournalRead_WindowsSharingViolationIsRetried()
    {
        if (!OperatingSystem.IsWindows())
            return;

        PackageCacheKey cacheKey = PackageCacheKey.Create(s_reference);
        string operationId = Guid.NewGuid().ToString("N");
        PackageCacheJournalStore store = new(
            _cacheRoot,
            SystemPackageCacheFileOperations.Instance,
            NullPackageCacheFaultObserver.Instance);
        PackageCacheTransactionJournal journal = new()
        {
            OperationId = operationId,
            Operation = PackageCacheTransactionOperation.Install,
            State = PackageCacheTransactionState.Prepared,
            CanonicalIdentity = cacheKey.CanonicalIdentity,
            PackageName = s_reference.Name,
            PackageVersion = s_reference.Version!,
            PackageScope = s_reference.Scope,
            TargetRelativePath =
                cacheKey.RelativePath.Replace('\\', '/'),
            StagingRelativePath =
                $".fhirpkg/staging/{operationId}/expanded",
            ArtifactRelativePath = null,
            OriginalState = PackageCacheInspectionState.Missing,
            OriginalArtifactKind = PackageCacheArtifactKind.Missing,
            IntendedMetadata = new CacheMetadataEntry
            {
                DownloadDateTime = DateTime.UtcNow,
                ContentGeneration = operationId
            },
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await store.WriteAsync(
            journal,
            TestContext.Current.CancellationToken);
        string journalPath = store.GetJournalPath(cacheKey);
        Task<PackageCacheTransactionJournal?> readTask;
        using (FileStream blocker = new(
            journalPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            readTask = store.ReadAsync(
                cacheKey,
                TestContext.Current.CancellationToken);
            await Task.Delay(
                TimeSpan.FromMilliseconds(75),
                TestContext.Current.CancellationToken);
            readTask.IsCompleted.ShouldBeFalse();
        }

        PackageCacheTransactionJournal? result =
            await readTask.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
        result.ShouldNotBeNull();
        result!.OperationId.ShouldBe(operationId);
    }

    [Fact]
    public async Task DurableFileWriter_ConcurrentReaderAllowsAtomicReplacement()
    {
        Directory.CreateDirectory(_cacheRoot);
        string path = Path.Combine(_cacheRoot, "replace.bin");
        await File.WriteAllTextAsync(
            path,
            "old",
            TestContext.Current.CancellationToken);
        FileStream reader = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.Asynchronous);
        try
        {
            Task releaseReaderTask = ReleaseReaderAsync();

            Task replaceTask = DurableFileWriter.WriteAsync(
                path,
                "new"u8.ToArray(),
                SystemPackageCacheFileOperations.Instance,
                TestContext.Current.CancellationToken);

            await Task.WhenAll(releaseReaderTask, replaceTask);
        }
        finally
        {
            await reader.DisposeAsync();
        }

        string content = await File.ReadAllTextAsync(
            path,
            TestContext.Current.CancellationToken);
        content.ShouldBe("new");

        async Task ReleaseReaderAsync()
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(50),
                TestContext.Current.CancellationToken);
            await reader.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("AtomicReplaceFile", "old")]
    [InlineData("SynchronizeDirectory", "new")]
    public async Task DurableFileWriter_FailureHasNoDeleteMoveFallback(
        string operationName,
        string expectedContent)
    {
        Directory.CreateDirectory(_cacheRoot);
        string path = Path.Combine(_cacheRoot, "failure.ini");
        await File.WriteAllTextAsync(
            path,
            "old",
            TestContext.Current.CancellationToken);
        FaultingFileOperations fileOperations = new(
            new FileOperationFault(operationName, 1));
        Dictionary<string, IReadOnlyDictionary<string, string>> sections =
            new()
            {
                ["section"] = new Dictionary<string, string>
                {
                    ["value"] = "new"
                }
            };

        await Should.ThrowAsync<IOException>(
            () => IniParser.WriteFileAtomicallyAsync(
                path,
                sections,
                fileOperations,
                TestContext.Current.CancellationToken));

        string content = await File.ReadAllTextAsync(
            path,
            TestContext.Current.CancellationToken);
        if (expectedContent == "old")
            content.ShouldBe("old");
        else
            content.ShouldContain("value = new");
        fileOperations.DeletedFiles.ShouldAllBe(
            deletedPath =>
                !Path.GetFullPath(deletedPath).Equals(
                    Path.GetFullPath(path),
                    StringComparison.OrdinalIgnoreCase));
        fileOperations.MutatingCalls.ShouldNotContain("MoveFile");
    }

    [Fact]
    public void MacDirectorySync_UsesFullSyncWhenSupported()
    {
        List<string> calls = [];

        PackageCacheDirectorySyncOutcome outcome =
            PackageCacheNativeFileSystem.SynchronizeMacDirectory(
                () =>
                {
                    calls.Add("fcntl");
                    return 0;
                },
                () =>
                {
                    calls.Add("fsync");
                    return 0;
                },
                () => 0);

        outcome.ShouldBe(PackageCacheDirectorySyncOutcome.Full);
        calls.ShouldBe(["fcntl"]);
    }

    [Fact]
    public void MacDirectorySync_FallsBackWhenFullSyncIsUnsupported()
    {
        List<string> calls = [];

        PackageCacheDirectorySyncOutcome outcome =
            PackageCacheNativeFileSystem.SynchronizeMacDirectory(
                () =>
                {
                    calls.Add("fcntl");
                    return -1;
                },
                () =>
                {
                    calls.Add("fsync");
                    return 0;
                },
                () => 22);

        outcome.ShouldBe(PackageCacheDirectorySyncOutcome.Standard);
        calls.ShouldBe(["fcntl", "fsync"]);
    }

    [Fact]
    public void MacDirectorySync_FallsBackWhenFullSyncReturnsEnotty()
    {
        List<string> calls = [];

        PackageCacheDirectorySyncOutcome outcome =
            PackageCacheNativeFileSystem.SynchronizeMacDirectory(
                () =>
                {
                    calls.Add("fcntl");
                    return -1;
                },
                () =>
                {
                    calls.Add("fsync");
                    return 0;
                },
                () => 25);

        outcome.ShouldBe(PackageCacheDirectorySyncOutcome.Standard);
        calls.ShouldBe(["fcntl", "fsync"]);
    }

    [Fact]
    public void MacDirectorySync_ReportsDocumentedUnsupportedLimitation()
    {
        Queue<int> errors = new([45, 22]);

        PackageCacheDirectorySyncOutcome outcome =
            PackageCacheNativeFileSystem.SynchronizeMacDirectory(
                () => -1,
                () => -1,
                errors.Dequeue);

        outcome.ShouldBe(PackageCacheDirectorySyncOutcome.Unsupported);
    }

    [Fact]
    public void MacDirectorySync_SurfacesRealFullSyncFailure()
    {
        IOException exception = Should.Throw<IOException>(
            () => PackageCacheNativeFileSystem.SynchronizeMacDirectory(
                () => -1,
                () => 0,
                () => 5));

        Win32Exception nativeException =
            exception.InnerException.ShouldBeOfType<Win32Exception>();
        nativeException.NativeErrorCode.ShouldBe(5);
    }

    [Fact]
    public void MacDirectorySync_SurfacesRealFallbackFailure()
    {
        Queue<int> errors = new([22, 5]);

        IOException exception = Should.Throw<IOException>(
            () => PackageCacheNativeFileSystem.SynchronizeMacDirectory(
                () => -1,
                () => -1,
                errors.Dequeue));

        Win32Exception nativeException =
            exception.InnerException.ShouldBeOfType<Win32Exception>();
        nativeException.NativeErrorCode.ShouldBe(5);
    }

    [Fact]
    public void MacDirectorySync_DoesNotIgnoreFallbackEnotty()
    {
        Queue<int> errors = new([22, 25]);

        IOException exception = Should.Throw<IOException>(
            () => PackageCacheNativeFileSystem.SynchronizeMacDirectory(
                () => -1,
                () => -1,
                errors.Dequeue));

        Win32Exception nativeException =
            exception.InnerException.ShouldBeOfType<Win32Exception>();
        nativeException.NativeErrorCode.ShouldBe(25);
    }

    [Theory]
    [InlineData(0, 13, 0)]
    [InlineData(-1, 2, 1)]
    [InlineData(-1, 20, 1)]
    [InlineData(-1, 38, 2)]
    [InlineData(-1, 13, 3)]
    public void LinuxStatxResult_MapsPlatformOutcomes(
        int nativeResult,
        int error,
        int expected)
    {
        PackageCacheNativeFileSystem.ClassifyLinuxStatxResult(
                nativeResult,
                error)
            .ShouldBe((PackageCacheLinuxStatxOutcome)expected);
    }

    [Fact]
    public void LinuxStatx_DispatchesToFixedAbiNoFollowInspection()
    {
        string filePath = Path.Combine(_cacheRoot, "statx-file");
        if (!OperatingSystem.IsLinux())
        {
            Should.Throw<PlatformNotSupportedException>(
                () =>
                {
                    _ = PackageCacheNativeFileSystem
                        .TryGetLinuxPathModeNoFollow(
                            filePath,
                            out int ignoredMode);
                });
            return;
        }

        Directory.CreateDirectory(_cacheRoot);
        File.WriteAllText(filePath, "content");
        string linkPath = Path.Combine(_cacheRoot, "statx-link");
        File.CreateSymbolicLink(linkPath, Path.GetFileName(filePath));

        PackageCacheNativeFileSystem.TryGetLinuxPathModeNoFollow(
                filePath,
                out int fileMode)
            .ShouldBeTrue();
        (fileMode & 0xF000).ShouldBe(0x8000);
        PackageCacheNativeFileSystem.TryGetLinuxPathModeNoFollow(
                linkPath,
                out int linkMode)
            .ShouldBeTrue();
        (linkMode & 0xF000).ShouldBe(0xA000);

        using FileStream stream = File.OpenRead(filePath);
        int descriptor = stream.SafeFileHandle
            .DangerousGetHandle()
            .ToInt32();
        int descriptorMode =
            PackageCacheNativeFileSystem.GetLinuxDescriptorMode(
                descriptor);
        (descriptorMode & 0xF000).ShouldBe(0x8000);
    }

    [Fact]
    public void LinuxManifestOpen_UsesDirectSafeFlagsWithoutProcfs()
    {
        string? openedPath = null;
        int openedFlags = -1;
        uint openedMode = uint.MaxValue;
        int inspectedDescriptor = -1;
        int duplicateAttempts = 0;
        List<int> closedDescriptors = [];

        int descriptor = PackageCacheRegularFile
            .OpenValidatedLinuxDescriptor(
                "package.json",
                (string path, int flags, uint mode) =>
                {
                    openedPath = path;
                    openedFlags = flags;
                    openedMode = mode;
                    return 73;
                },
                (int value) =>
                {
                    inspectedDescriptor = value;
                    return 0x8000;
                },
                (int value, int command, int minimum) =>
                {
                    duplicateAttempts++;
                    return 75;
                },
                (int value) =>
                {
                    closedDescriptors.Add(value);
                    return 0;
                });

        descriptor.ShouldBe(73);
        openedPath.ShouldBe("package.json");
        openedMode.ShouldBe(0u);
        inspectedDescriptor.ShouldBe(descriptor);
        duplicateAttempts.ShouldBe(0);
        openedFlags.ShouldBe(
            PackageCacheRegularFile.LinuxManifestOpenFlags);
        (openedFlags & 0x00000003).ShouldBe(0);
        (openedFlags & 0x00000800).ShouldBe(0x00000800);
        (openedFlags & 0x00020000).ShouldBe(0x00020000);
        (openedFlags & 0x00080000).ShouldBe(0x00080000);
        (openedFlags & 0x00200000).ShouldBe(0);
        closedDescriptors.ShouldBeEmpty();
    }

    [Fact]
    public void LinuxManifestOpen_ClosesRejectedSpecialDescriptor()
    {
        List<int> closedDescriptors = [];

        Should.Throw<IOException>(
            () => PackageCacheRegularFile
                .OpenValidatedLinuxDescriptor(
                    "package.json",
                    (string path, int flags, uint mode) => 74,
                    (int descriptor) => 0x1000,
                    (int descriptor, int command, int minimum) => 75,
                    (int descriptor) =>
                    {
                        closedDescriptors.Add(descriptor);
                        return 0;
                    }));

        closedDescriptors.ShouldBe([74]);
    }

    [Fact]
    public void LinuxManifestOpen_DuplicatesZeroDescriptorAndTransfersDuplicate()
    {
        int duplicatedSource = -1;
        int duplicateCommand = -1;
        int duplicateMinimum = -1;
        List<int> inspectedDescriptors = [];
        List<int> closedDescriptors = [];

        int descriptor = PackageCacheRegularFile
            .OpenValidatedLinuxDescriptor(
                "package.json",
                (string path, int flags, uint mode) => 0,
                (int value) =>
                {
                    inspectedDescriptors.Add(value);
                    return 0x8000;
                },
                (int value, int command, int minimum) =>
                {
                    duplicatedSource = value;
                    duplicateCommand = command;
                    duplicateMinimum = minimum;
                    return 4;
                },
                (int value) =>
                {
                    closedDescriptors.Add(value);
                    return 0;
                });

        descriptor.ShouldBe(4);
        inspectedDescriptors.ShouldBe([0]);
        duplicatedSource.ShouldBe(0);
        duplicateCommand.ShouldBe(
            PackageCacheRegularFile
                .LinuxDuplicateFileDescriptorCloseOnExec);
        duplicateMinimum.ShouldBe(
            PackageCacheRegularFile.MinimumTransferDescriptor);
        closedDescriptors.ShouldBe([0]);
    }

    [Fact]
    public void LinuxManifestOpen_DuplicateFailureClosesZeroDescriptor()
    {
        List<int> closedDescriptors = [];

        Should.Throw<IOException>(
            () => PackageCacheRegularFile
                .OpenValidatedLinuxDescriptor(
                    "package.json",
                    (string path, int flags, uint mode) => 0,
                    (int descriptor) => 0x8000,
                    (int descriptor, int command, int minimum) => -1,
                    (int descriptor) =>
                    {
                        closedDescriptors.Add(descriptor);
                        return 0;
                    }));

        closedDescriptors.ShouldBe([0]);
    }

    [Fact]
    public void LinuxManifestOpen_CloseFailureReleasesDuplicate()
    {
        List<int> closedDescriptors = [];

        Should.Throw<IOException>(
            () => PackageCacheRegularFile
                .OpenValidatedLinuxDescriptor(
                    "package.json",
                    (string path, int flags, uint mode) => 0,
                    (int descriptor) => 0x8000,
                    (int descriptor, int command, int minimum) => 4,
                    (int descriptor) =>
                    {
                        closedDescriptors.Add(descriptor);
                        return descriptor == 0 ? -1 : 0;
                    }));

        closedDescriptors.ShouldBe([0, 4]);
    }

    [Fact]
    public void MacManifestTransfer_DuplicatesZeroWithDarwinCommand()
    {
        int duplicateCommand = -1;
        int duplicateMinimum = -1;
        List<int> closedDescriptors = [];

        int descriptor = PackageCacheRegularFile
            .GuardDescriptorForTransfer(
                0,
                PackageCacheRegularFile
                    .DarwinDuplicateFileDescriptorCloseOnExec,
                (int value, int command, int minimum) =>
                {
                    value.ShouldBe(0);
                    duplicateCommand = command;
                    duplicateMinimum = minimum;
                    return 4;
                },
                (int value) =>
                {
                    closedDescriptors.Add(value);
                    return 0;
                });

        descriptor.ShouldBe(4);
        duplicateCommand.ShouldBe(67);
        duplicateMinimum.ShouldBe(
            PackageCacheRegularFile.MinimumTransferDescriptor);
        closedDescriptors.ShouldBe([0]);
    }

    [Fact]
    public void MacManifestTransfer_DuplicateFailureClosesZeroDescriptor()
    {
        List<int> closedDescriptors = [];

        Should.Throw<IOException>(
            () => PackageCacheRegularFile.GuardDescriptorForTransfer(
                0,
                PackageCacheRegularFile
                    .DarwinDuplicateFileDescriptorCloseOnExec,
                (int descriptor, int command, int minimum) => -1,
                (int descriptor) =>
                {
                    closedDescriptors.Add(descriptor);
                    return 0;
                }));

        closedDescriptors.ShouldBe([0]);
    }

    [Fact]
    public void MacManifestTransfer_HandleConstructorFailureClosesDuplicate()
    {
        List<int> closedDescriptors = [];
        int descriptor = PackageCacheRegularFile
            .GuardDescriptorForTransfer(
                0,
                PackageCacheRegularFile
                    .DarwinDuplicateFileDescriptorCloseOnExec,
                (int value, int command, int minimum) => 4,
                (int value) =>
                {
                    closedDescriptors.Add(value);
                    return 0;
                });

        Should.Throw<InvalidOperationException>(
            () => PackageCacheRegularFile
                .TransferDescriptorOwnership<object, string>(
                    descriptor,
                    (int value) =>
                        throw new InvalidOperationException(
                            "Handle construction failed."),
                    (object handle) => "unused",
                    (object handle) =>
                        throw new InvalidOperationException(
                            "No handle was constructed."),
                    (int value) =>
                    {
                        closedDescriptors.Add(value);
                        return 0;
                    }));

        closedDescriptors.ShouldBe([0, 4]);
    }

    [Fact]
    public void MacManifestTransfer_StreamConstructorFailureDisposesHandle()
    {
        List<int> rawClosedDescriptors = [];
        List<int> disposedHandleDescriptors = [];
        int descriptor = PackageCacheRegularFile
            .GuardDescriptorForTransfer(
                0,
                PackageCacheRegularFile
                    .DarwinDuplicateFileDescriptorCloseOnExec,
                (int value, int command, int minimum) => 4,
                (int value) =>
                {
                    rawClosedDescriptors.Add(value);
                    return 0;
                });
        int adoptedDescriptor = -1;

        Should.Throw<InvalidOperationException>(
            () => PackageCacheRegularFile
                .TransferDescriptorOwnership<object, string>(
                    descriptor,
                    (int value) =>
                    {
                        adoptedDescriptor = value;
                        return new object();
                    },
                    (object handle) =>
                        throw new InvalidOperationException(
                            "Stream construction failed."),
                    (object handle) =>
                        disposedHandleDescriptors.Add(
                            adoptedDescriptor),
                    (int value) =>
                    {
                        rawClosedDescriptors.Add(value);
                        return 0;
                    }));

        rawClosedDescriptors.ShouldBe([0]);
        disposedHandleDescriptors.ShouldBe([4]);
    }

    [Fact]
    public void LinuxManifestOpen_TransfersDescriptorOwnershipToStream()
    {
        if (!OperatingSystem.IsLinux())
            return;

        Directory.CreateDirectory(_cacheRoot);
        string path = Path.Combine(_cacheRoot, "owned-manifest.json");
        File.WriteAllText(path, "{}");

        FileStream stream = PackageCacheRegularFile.OpenRead(path);
        SafeFileHandle handle = stream.SafeFileHandle;
        stream.ReadByte().ShouldBe((int)'{');
        stream.Dispose();

        handle.IsClosed.ShouldBeTrue();
    }

    [Fact]
    public void MacManifestOpen_TransfersDescriptorOwnershipToStream()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        Directory.CreateDirectory(_cacheRoot);
        string path = Path.Combine(_cacheRoot, "mac-owned-manifest.json");
        File.WriteAllText(path, "{}");

        FileStream stream = PackageCacheRegularFile.OpenRead(path);
        SafeFileHandle handle = stream.SafeFileHandle;
        stream.ReadByte().ShouldBe((int)'{');
        stream.Dispose();

        handle.IsClosed.ShouldBeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot))
            Directory.Delete(_cacheRoot, recursive: true);

        GC.SuppressFinalize(this);
    }

    private static void AssertWindowsRenameFailure(
        IOException exception,
        string sourcePath,
        string destinationPath)
    {
        Win32Exception nativeException =
            exception.InnerException.ShouldBeOfType<Win32Exception>();
        nativeException.NativeErrorCode.ShouldNotBe(0);
        exception.Data["SourcePath"].ShouldBe(sourcePath);
        exception.Data["DestinationPath"].ShouldBe(destinationPath);
        exception.Data["Operation"].ShouldBe("MoveFileExW");
        exception.Data["NativeFlags"].ShouldBe(0x00000008u);
        exception.Data["NativeErrorCode"].ShouldBe(nativeException.NativeErrorCode);
        exception.Message.ShouldContain(sourcePath);
        exception.Message.ShouldContain(destinationPath);
        exception.Message.ShouldContain("MoveFileExW");
        exception.Message.ShouldContain("0x00000008");
        exception.Message.ShouldContain($"NativeErrorCode: {nativeException.NativeErrorCode}");

        ITestOutputHelper? output = TestContext.Current.TestOutputHelper;
        if (output is not null)
        {
            output.WriteLine(
                $"UTC: {DateTimeOffset.UtcNow:O}; ProcessId: {Environment.ProcessId}; " +
                $"Runtime: {RuntimeInformation.FrameworkDescription}");
            output.WriteLine(exception.ToString());
            WriteExceptionDetails(output, exception, "Exception");
        }
    }

    private static SafeFileHandle OpenDirectoryWithoutDeleteSharing(string path)
    {
        const uint GenericRead = 0x80000000;
        const uint OpenExisting = 3;
        const uint FileFlagBackupSemantics = 0x02000000;
        SafeFileHandle handle = CreateFile(
            path,
            GenericRead,
            FileShare.ReadWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(
                "The test directory handle could not be opened.",
                new Win32Exception(error));
        }

        return handle;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    private static void WriteExceptionDetails(
        ITestOutputHelper output,
        Exception exception,
        string path)
    {
        output.WriteLine(
            $"{path}: {exception.GetType().FullName}; HResult: {exception.HResult} (0x{exception.HResult:X8})");
        if (exception is PackageInstallException installException)
        {
            output.WriteLine(
                $"{path}: ErrorCode: {installException.ErrorCode}; Stage: {installException.Stage}; " +
                $"Directive: {installException.Directive ?? "<null>"}");
        }
        if (exception is Win32Exception win32Exception)
            output.WriteLine($"{path}: NativeErrorCode: {win32Exception.NativeErrorCode}");
        foreach (System.Collections.DictionaryEntry entry in exception.Data)
            output.WriteLine($"{path}.Data[{entry.Key}]: {entry.Value ?? "<null>"}");

        if (exception is AggregateException aggregateException)
        {
            for (int index = 0; index < aggregateException.InnerExceptions.Count; index++)
            {
                WriteExceptionDetails(
                    output,
                    aggregateException.InnerExceptions[index],
                    $"{path}.InnerExceptions[{index}]");
            }
        }
        else if (exception.InnerException is Exception innerException)
        {
            WriteExceptionDetails(output, innerException, $"{path}.InnerException");
        }
    }

    private async Task CreateCorruptTargetAsync()
    {
        string contentPath = Path.Combine(
            PackageCacheKey.Create(s_reference)
                .GetPackageDirectoryPath(_cacheRoot),
            "package");
        Directory.CreateDirectory(contentPath);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, "package.json"),
            "{corrupt",
            TestContext.Current.CancellationToken);
    }

    private async Task InstallInitialAsync()
    {
        using DiskPackageCache cache = CreateCache();
        using MemoryStream archive = CreateArchive("old");
        await cache.InstallAsync(
            s_reference,
            archive,
            new InstallCacheOptions
            {
                VerifyChecksum = false,
                ArchiveSha256 = "old-hash"
            },
            TestContext.Current.CancellationToken);
    }

    private async Task<CacheMetadataEntry> InstallInitialGenerationAsync(bool rawRoot)
    {
        using DiskPackageCache cache = CreateCache();
        using MemoryStream archive = CreateGenerationArchive("old", rawRoot);
        await cache.InstallAsync(
            s_reference,
            archive,
            new InstallCacheOptions
            {
                Unmodified = rawRoot,
                ArchiveSha256 = "old-hash",
                SourcePublicationDate = GenerationPublicationDate("old"),
            },
            TestContext.Current.CancellationToken);
        CacheMetadata metadata = await cache.GetMetadataAsync(TestContext.Current.CancellationToken);
        return await AssertGenerationAsync(
            cache, "old", rawRoot, metadata.Packages.ShouldHaveSingleItem().Value.ContentGeneration!);
    }

    private static DateTimeOffset GenerationPublicationDate(string value) =>
        new(2026, 10, value == "old" ? 1 : 2, 0, 0, 0, TimeSpan.Zero);

    private static ArbitraryTarEntry[] GenerationEntries(string value, bool rawRoot)
    {
        string prefix = rawRoot ? string.Empty : "package/";
        return
        [
            ArbitraryTarBuilder.File(
                $"{prefix}package.json",
                $$"""{"name":"example.package","version":"1.0.0","description":"{{value}}"}"""),
            ArbitraryTarBuilder.File($"{prefix}value.txt", value),
            ArbitraryTarBuilder.File(
                $"{prefix}content/.index.json",
                $$"""{"index-version":2,"files":[{"filename":"value.txt","resourceType":"Binary","id":"{{value}}"}]}"""),
            ArbitraryTarBuilder.File($"{prefix}content/{value}-only.bin",
                value == "old" ? [0, 128, 255] : [255, 0, 1, 2, 3]),
            ArbitraryTarBuilder.Directory($"{prefix}content/empty-{value}/"),
            ArbitraryTarBuilder.File("ROOT.txt", $"{value} root payload"),
            ArbitraryTarBuilder.File("packages.ini", $"{value} distributed metadata"),
            ArbitraryTarBuilder.File(".fhirpkg/sentinel.txt", $"{value} distributed hidden payload"),
            ArbitraryTarBuilder.File("other/sentinel.txt", $"{value} sibling"),
            ArbitraryTarBuilder.File($"other/{value}-only.txt", value),
            ArbitraryTarBuilder.File("other/.index.json", $"{value} ancillary index"),
            ArbitraryTarBuilder.File("other/package.json", $"{value} opaque manifest"),
            ArbitraryTarBuilder.File("package/other/sentinel.txt", $"{value} content subtree"),
            ArbitraryTarBuilder.File("package/other/.index.json", $"{value} content ancillary index"),
            ArbitraryTarBuilder.Directory($"empty-{value}/"),
        ];
    }

    private static MemoryStream CreateGenerationArchive(string value, bool rawRoot) =>
        ArbitraryTarBuilder.Create(GenerationEntries(value, rawRoot));

    private static void AssertGenerationTree(string target, string value, bool rawRoot)
    {
        ArbitraryTarEntry[] entries = GenerationEntries(value, rawRoot);
        Dictionary<string, byte[]> expectedFiles = entries
            .Where(entry => entry.Content is not null)
            .ToDictionary(entry => entry.Name, entry => entry.Content!, StringComparer.Ordinal);
        HashSet<string> expectedDirectories = new(StringComparer.Ordinal);
        foreach (ArbitraryTarEntry entry in entries)
        {
            string[] segments = entry.Name.TrimEnd('/').Split('/');
            int depth = entry.Content is null ? segments.Length : segments.Length - 1;
            for (int index = 1; index <= depth; index++)
                expectedDirectories.Add(string.Join("/", segments.Take(index)));
        }

        Directory.GetDirectories(target, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(target, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(expectedDirectories.OrderBy(path => path, StringComparer.Ordinal));
        string[] files = Directory.GetFiles(target, "*", SearchOption.AllDirectories);
        files.Select(path => Path.GetRelativePath(target, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(expectedFiles.Keys.OrderBy(path => path, StringComparer.Ordinal));
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(target, file).Replace('\\', '/');
            File.ReadAllBytes(file).ShouldBe(expectedFiles[relative], relative);
        }
    }

    private static async Task<CacheMetadataEntry> AssertGenerationAsync(
        DiskPackageCache cache,
        string value,
        bool rawRoot,
        string expectedGeneration)
    {
        PackageRecord record = (await cache.GetPackageAsync(
            s_reference, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        record.Reference.ShouldBe(s_reference);
        record.Manifest.Description.ShouldBe(value);
        record.ContentPath.ShouldBe(rawRoot
            ? record.DirectoryPath
            : Path.Combine(record.DirectoryPath, "package"));
        record.ContentGeneration.ShouldNotBeNull().ShouldStartWith($"{expectedGeneration}\0");
        AssertGenerationTree(record.DirectoryPath, value, rawRoot);
        CacheMetadata metadata = await cache.GetMetadataAsync(TestContext.Current.CancellationToken);
        CacheMetadataEntry entry = metadata.Packages.ShouldHaveSingleItem().Value;
        metadata.Packages.Keys.ShouldHaveSingleItem().ShouldBe(
            PackageCacheKey.Create(s_reference).MetadataKey);
        entry.ArchiveSha256.ShouldBe($"{value}-hash");
        entry.SourcePublicationDate.ShouldBe(GenerationPublicationDate(value));
        entry.ContentGeneration.ShouldBe(expectedGeneration);
        entry.ContentGeneration.ShouldNotBeNullOrWhiteSpace();
        entry.SizeBytes.ShouldBe(GenerationEntries(value, rawRoot)
            .Sum(member => member.Content?.LongLength ?? 0));
        record.SizeBytes.ShouldBe(entry.SizeBytes);
        record.InstalledAt.ShouldBe(new DateTimeOffset(entry.DownloadDateTime));
        return entry;
    }

    private DiskPackageCache CreateCache(
        IPackageCacheFaultObserver? observer = null,
        IPackageCacheFileOperations? fileOperations = null) =>
        new(
            _cacheRoot,
            logger: null,
            timeProvider: null,
            new PackageInstallLimits(),
            fileOperations ?? SystemPackageCacheFileOperations.Instance,
            observer ?? NullPackageCacheFaultObserver.Instance);

    private static MemoryStream CreateArchive(string value) =>
        ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File(
                "package/package.json",
                """{"name":"example.package","version":"1.0.0"}"""),
            ArbitraryTarBuilder.File("package/value.txt", value));

    private void AssertNoOperationArtifacts()
    {
        string operationsRoot = Path.Combine(_cacheRoot, ".fhirpkg");
        if (!Directory.Exists(operationsRoot))
            return;

        string[] directoryNames =
            ["staging", "transactions", "backup", "quarantine"];
        foreach (string directoryName in directoryNames)
        {
            string directory = Path.Combine(
                operationsRoot,
                directoryName);
            if (!Directory.Exists(directory))
                continue;

            Directory.EnumerateFileSystemEntries(
                    directory,
                    "*",
                    SearchOption.AllDirectories)
                .ShouldBeEmpty();
        }
    }

    private sealed class ThrowOnceObserver : IPackageCacheFaultObserver
    {
        private readonly PackageCacheFaultPoint _point;
        private readonly PackageCacheTransactionState _state;
        private int _thrown;

        internal ThrowOnceObserver(
            PackageCacheFaultPoint point,
            PackageCacheTransactionState state)
        {
            _point = point;
            _state = state;
        }

        internal string? OperationId { get; private set; }

        public ValueTask OnEventAsync(
            PackageCacheFaultEvent faultEvent,
            CancellationToken cancellationToken)
        {
            if (faultEvent.Point == _point
                && faultEvent.State == _state
                && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                OperationId = faultEvent.OperationId;
                throw new PackageCacheInjectedFaultException(
                    $"Injected fault at {_point}/{_state}.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancelAtPreparedObserver :
        IPackageCacheFaultObserver
    {
        private readonly CancellationTokenSource _source;

        internal CancelAtPreparedObserver(CancellationTokenSource source)
        {
            _source = source;
        }

        public ValueTask OnEventAsync(
            PackageCacheFaultEvent faultEvent,
            CancellationToken cancellationToken)
        {
            if (faultEvent.Point == PackageCacheFaultPoint.JournalWritten
                && faultEvent.State
                    == PackageCacheTransactionState.Prepared)
            {
                _source.Cancel();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RenameBarrierObserver :
        IPackageCacheFaultObserver
    {
        internal string? OperationId { get; private set; }

        internal TaskCompletionSource Renamed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask OnEventAsync(
            PackageCacheFaultEvent faultEvent,
            CancellationToken cancellationToken)
        {
            if (faultEvent.Point != PackageCacheFaultPoint.OriginalRenamed)
                return;

            OperationId = faultEvent.OperationId;
            Renamed.TrySetResult();
            await Release.Task.ConfigureAwait(false);
        }
    }

    private sealed record FileOperationFault(
        string OperationName,
        int CallIndex);

    private sealed class FaultingFileOperations :
        IPackageCacheFileOperations
    {
        private readonly IPackageCacheFileOperations _inner =
            SystemPackageCacheFileOperations.Instance;
        private readonly IReadOnlyList<FileOperationFault> _faults;
        private readonly Dictionary<string, int> _occurrences =
            new(StringComparer.Ordinal);
        private readonly HashSet<FileOperationFault> _fired = [];

        internal FaultingFileOperations(
            params FileOperationFault[] faults)
        {
            _faults = faults;
        }

        internal List<string> MutatingCalls { get; } = [];

        internal List<string> DeletedFiles { get; } = [];

        public bool DirectoryExists(string path)
        {
            Before("DirectoryExists", isMutating: false);
            return _inner.DirectoryExists(path);
        }

        public bool FileExists(string path)
        {
            Before("FileExists", isMutating: false);
            return _inner.FileExists(path);
        }

        public FileStream OpenRead(string path)
        {
            Before("OpenRead", isMutating: false);
            return _inner.OpenRead(path);
        }

        public void CreateDirectory(string path)
        {
            Before("CreateDirectory");
            _inner.CreateDirectory(path);
        }

        public void MoveDirectory(
            string sourcePath,
            string destinationPath)
        {
            Before("MoveDirectory");
            _inner.MoveDirectory(sourcePath, destinationPath);
        }

        public void MoveFile(
            string sourcePath,
            string destinationPath)
        {
            Before("MoveFile");
            _inner.MoveFile(sourcePath, destinationPath);
        }

        public void DeleteDirectory(string path, bool recursive)
        {
            Before("DeleteDirectory");
            _inner.DeleteDirectory(path, recursive);
        }

        public void DeleteFile(string path)
        {
            Before("DeleteFile");
            DeletedFiles.Add(path);
            _inner.DeleteFile(path);
        }

        public async ValueTask WriteFileAndFlushAsync(
            string path,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            Before("WriteFileAndFlush");
            await _inner.WriteFileAndFlushAsync(
                    path,
                    content,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async ValueTask AtomicReplaceFileAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            Before("AtomicReplaceFile");
            await _inner.AtomicReplaceFileAsync(
                    sourcePath,
                    destinationPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public void SynchronizeDirectory(string directoryPath)
        {
            Before("SynchronizeDirectory");
            _inner.SynchronizeDirectory(directoryPath);
        }

        public PackageCacheArtifactKind GetArtifactKind(string path)
        {
            Before("GetArtifactKind", isMutating: false);
            return _inner.GetArtifactKind(path);
        }

        public bool ArtifactExists(
            string path,
            PackageCacheArtifactKind artifactKind)
        {
            Before("ArtifactExists", isMutating: false);
            return _inner.ArtifactExists(path, artifactKind);
        }

        public void MoveArtifact(
            string sourcePath,
            string destinationPath,
            PackageCacheArtifactKind artifactKind)
        {
            Before("MoveArtifact");
            _inner.MoveArtifact(
                sourcePath,
                destinationPath,
                artifactKind);
        }

        public void DeleteArtifact(
            string path,
            PackageCacheArtifactKind artifactKind)
        {
            Before("DeleteArtifact");
            _inner.DeleteArtifact(path, artifactKind);
        }

        private void Before(
            string operationName,
            bool isMutating = true)
        {
            if (isMutating)
                MutatingCalls.Add(operationName);
            int occurrence = _occurrences.TryGetValue(
                operationName,
                out int prior)
                ? prior + 1
                : 1;
            _occurrences[operationName] = occurrence;
            foreach (FileOperationFault fault in _faults)
            {
                if (!_fired.Contains(fault)
                    && string.Equals(
                        fault.OperationName,
                        operationName,
                        StringComparison.Ordinal)
                    && fault.CallIndex == occurrence)
                {
                    _fired.Add(fault);
                    throw new IOException(
                        $"Injected file-operation failure at " +
                        $"{operationName} occurrence {occurrence}.");
                }
            }
        }
    }
}
