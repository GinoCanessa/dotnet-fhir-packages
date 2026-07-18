// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Tests.Support;
using FhirPkg.Utilities;
using System.ComponentModel;
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

    public static TheoryData<string, string, string> OverwriteCrashPoints =>
        new()
        {
            {
                nameof(PackageCacheFaultPoint.JournalWritten),
                nameof(PackageCacheTransactionState.Prepared),
                "old"
            },
            {
                nameof(PackageCacheFaultPoint.OriginalRenamed),
                nameof(PackageCacheTransactionState.Prepared),
                "old"
            },
            {
                nameof(PackageCacheFaultPoint.JournalWritten),
                nameof(PackageCacheTransactionState.OriginalMoved),
                "old"
            },
            {
                nameof(PackageCacheFaultPoint.ReplacementPromoted),
                nameof(PackageCacheTransactionState.OriginalMoved),
                "old"
            },
            {
                nameof(PackageCacheFaultPoint.JournalWritten),
                nameof(PackageCacheTransactionState.NewPromoted),
                "new"
            },
            {
                nameof(PackageCacheFaultPoint.MetadataReplaced),
                nameof(PackageCacheTransactionState.MetadataCommitted),
                "new"
            },
            {
                nameof(PackageCacheFaultPoint.JournalWritten),
                nameof(PackageCacheTransactionState.MetadataCommitted),
                "new"
            },
            {
                nameof(PackageCacheFaultPoint.JournalWritten),
                nameof(PackageCacheTransactionState.Completed),
                "new"
            },
            {
                nameof(PackageCacheFaultPoint.ArtifactRemoved),
                nameof(PackageCacheTransactionState.Completed),
                "new"
            }
        };

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
        string expectedValue)
    {
        await InstallInitialAsync();
        PackageCacheFaultPoint point =
            Enum.Parse<PackageCacheFaultPoint>(pointName);
        PackageCacheTransactionState state =
            Enum.Parse<PackageCacheTransactionState>(stateName);
        ThrowOnceObserver observer = new(point, state);
        using (DiskPackageCache faultingCache = CreateCache(observer))
        using (MemoryStream replacement = CreateArchive("new"))
        {
            await Should.ThrowAsync<PackageCacheInjectedFaultException>(
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
        }

        using DiskPackageCache recoveredCache = CreateCache();
        string? value = await recoveredCache.GetFileContentAsync(
            s_reference,
            "value.txt",
            TestContext.Current.CancellationToken);
        CacheMetadata metadata = await recoveredCache.GetMetadataAsync(
            TestContext.Current.CancellationToken);

        value.ShouldBe(expectedValue);
        metadata.Packages[
            PackageCacheKey.Create(s_reference).MetadataKey]
            .ArchiveSha256.ShouldBe(
                expectedValue == "old" ? "old-hash" : "new-hash");
        AssertNoOperationArtifacts();
    }

    [Fact]
    public async Task CancellationAtCommitBoundary_LeavesPriorPackage()
    {
        await InstallInitialAsync();
        using CancellationTokenSource source = new();
        CancelAtPreparedObserver observer = new(source);
        using DiskPackageCache cache = CreateCache(observer);
        using MemoryStream replacement = CreateArchive("new");

        await Should.ThrowAsync<OperationCanceledException>(
            () => cache.InstallAsync(
                s_reference,
                replacement,
                new InstallCacheOptions
                {
                    VerifyChecksum = false,
                    OverwriteExisting = true
                },
                source.Token));

        using DiskPackageCache recoveredCache = CreateCache();
        (await recoveredCache.GetFileContentAsync(
            s_reference,
            "value.txt",
            TestContext.Current.CancellationToken)).ShouldBe("old");
        AssertNoOperationArtifacts();
    }

    [Fact]
    public async Task CancellationAfterFirstRename_CompletesCommit()
    {
        await InstallInitialAsync();
        using CancellationTokenSource source = new();
        RenameBarrierObserver observer = new();
        using DiskPackageCache cache = CreateCache(observer);
        using MemoryStream replacement = CreateArchive("new");
        Task<PackageRecord> installTask = cache.InstallAsync(
            s_reference,
            replacement,
            new InstallCacheOptions
            {
                VerifyChecksum = false,
                OverwriteExisting = true,
                ArchiveSha256 = "new-hash"
            },
            source.Token);

        await observer.Renamed.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        source.Cancel();
        observer.Release.TrySetResult();
        PackageRecord record = await installTask;

        record.Manifest.Version.ShouldBe("1.0.0");
        (await cache.GetFileContentAsync(
            s_reference,
            "value.txt",
            TestContext.Current.CancellationToken)).ShouldBe("new");
    }

    [Fact]
    public async Task ReaderDuringReplacement_WaitsForCoherentTarget()
    {
        await InstallInitialAsync();
        RenameBarrierObserver observer = new();
        using DiskPackageCache cache = CreateCache(observer);
        using MemoryStream replacement = CreateArchive("new");
        Task<PackageRecord> installTask = cache.InstallAsync(
            s_reference,
            replacement,
            new InstallCacheOptions
            {
                VerifyChecksum = false,
                OverwriteExisting = true
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
        (await cache.GetFileContentAsync(
            s_reference,
            "value.txt",
            TestContext.Current.CancellationToken)).ShouldBe("new");
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
        fileOperations.MutatingCalls.ShouldNotContain("DeleteFile");
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

        public ValueTask OnEventAsync(
            PackageCacheFaultEvent faultEvent,
            CancellationToken cancellationToken)
        {
            if (faultEvent.Point == _point
                && faultEvent.State == _state
                && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
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

        public void AtomicReplaceFile(
            string sourcePath,
            string destinationPath)
        {
            Before("AtomicReplaceFile");
            _inner.AtomicReplaceFile(sourcePath, destinationPath);
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
