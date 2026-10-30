// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Tests.Support;
using Microsoft.Win32.SafeHandles;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Installation;

public class PackageContentAcquirerTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(
        AppContext.BaseDirectory,
        $"package-acquirer-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot))
            Directory.Delete(_cacheRoot, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AcquireAsync_SeekableSource_UsesCurrentPositionAndLeavesSourceOpen()
    {
        byte[] prefix = [1, 2, 3];
        byte[] content = Encoding.UTF8.GetBytes("package-content");
        byte[] sourceBytes = [.. prefix, .. content];
        using MemoryStream source = new MemoryStream(sourceBytes);
        source.Position = prefix.Length;
        PackageInstallLimits limits = CreateLimits(content.Length);
        string expectedSha256 = HashSha256(content);
        string expectedSha1 = HashSha1(content);

        string operationDirectory;
        await using (PackageContentAcquisition acquisition =
            await PackageContentAcquirer.AcquireAsync(
                source,
                _cacheRoot,
                limits,
                reportedContentLength: content.Length,
                expectedSha256,
                expectedSha1,
                verifyChecksums: true,
                directive: "example.package#1.0.0",
                TestContext.Current.CancellationToken))
        {
            operationDirectory = acquisition.OperationDirectory;
            acquisition.ActualLength.ShouldBe(content.Length);
            acquisition.Sha256.ShouldBe(expectedSha256);
            acquisition.Sha1.ShouldBe(expectedSha1);
            File.Exists(acquisition.ArchivePath).ShouldBeTrue();
            byte[] stagedContent = await File.ReadAllBytesAsync(
                acquisition.ArchivePath,
                TestContext.Current.CancellationToken);
            stagedContent.ShouldBe(content);
        }

        source.CanRead.ShouldBeTrue();
        source.Position.ShouldBe(source.Length);
        Directory.Exists(operationDirectory).ShouldBeFalse();
        AssertNoStagingOperations();
    }

    [Fact]
    public async Task AcquireAsync_NonSeekableSource_WithMissingLength_ReadsOnceAndLeavesOpen()
    {
        byte[] content = Encoding.UTF8.GetBytes("non-seekable-content");
        using TrackingReadStream source = new TrackingReadStream(
            new MemoryStream(content),
            canSeek: false);
        PackageInstallLimits limits = CreateLimits(content.Length);

        await using PackageContentAcquisition acquisition =
            await PackageContentAcquirer.AcquireAsync(
                source,
                _cacheRoot,
                limits,
                cancellationToken: TestContext.Current.CancellationToken);

        acquisition.ActualLength.ShouldBe(content.Length);
        source.BytesRead.ShouldBe(content.Length);
        source.WasDisposed.ShouldBeFalse();
        source.PositionWasRead.ShouldBeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task AcquireAsync_InaccurateReportedLengthWithinLimit_UsesActualLength(
        long reportedLength)
    {
        byte[] content = Encoding.UTF8.GetBytes("actual-content");
        using MemoryStream source = new MemoryStream(content);
        PackageInstallLimits limits = CreateLimits(100);

        await using PackageContentAcquisition acquisition =
            await PackageContentAcquirer.AcquireAsync(
                source,
                _cacheRoot,
                limits,
                reportedContentLength: reportedLength,
                cancellationToken: TestContext.Current.CancellationToken);

        acquisition.ActualLength.ShouldBe(content.Length);
    }

    [Fact]
    public async Task AcquireAsync_ExactCompressedLimit_Succeeds()
    {
        byte[] content = new byte[64];
        using MemoryStream source = new MemoryStream(content);

        await using PackageContentAcquisition acquisition =
            await PackageContentAcquirer.AcquireAsync(
                source,
                _cacheRoot,
                CreateLimits(content.Length),
                cancellationToken: TestContext.Current.CancellationToken);

        acquisition.ActualLength.ShouldBe(content.Length);
    }

    [Fact]
    public async Task AcquireAsync_ActualBytesOverLimit_ThrowsAndCleansOperation()
    {
        byte[] content = new byte[65];
        using TrackingReadStream source = new TrackingReadStream(
            new MemoryStream(content),
            canSeek: false);

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => PackageContentAcquirer.AcquireAsync(
                    source,
                    _cacheRoot,
                    CreateLimits(64),
                    cancellationToken: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.CompressedSizeLimitExceeded);
        exception.Stage.ShouldBe(PackageInstallStage.Acquisition);
        source.WasDisposed.ShouldBeFalse();
        AssertNoStagingOperations();
    }

    [Fact]
    public async Task AcquireAsync_ReportedLengthOverLimit_PreRejectsWithoutReading()
    {
        using TrackingReadStream source = new TrackingReadStream(
            new MemoryStream([1, 2, 3]),
            canSeek: false);

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => PackageContentAcquirer.AcquireAsync(
                    source,
                    _cacheRoot,
                    CreateLimits(3),
                    reportedContentLength: 4,
                    cancellationToken: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.CompressedSizeLimitExceeded);
        source.ReadCalls.ShouldBe(0);
        AssertNoStagingOperations();
    }

    [Fact]
    public async Task AcquireAsync_LongMaxReportedLength_IsRejectedWithoutOverflow()
    {
        using TrackingReadStream source = new TrackingReadStream(
            new MemoryStream([1]),
            canSeek: false);

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => PackageContentAcquirer.AcquireAsync(
                    source,
                    _cacheRoot,
                    CreateLimits(1),
                    reportedContentLength: long.MaxValue,
                    cancellationToken: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.CompressedSizeLimitExceeded);
        source.ReadCalls.ShouldBe(0);
        AssertNoStagingOperations();
    }

    [Fact]
    public async Task AcquireAsync_ChecksumMismatch_CleansOperation()
    {
        using MemoryStream source = new MemoryStream([1, 2, 3]);

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => PackageContentAcquirer.AcquireAsync(
                    source,
                    _cacheRoot,
                    CreateLimits(3),
                    expectedSha256: new string('0', 64),
                    verifyChecksums: true,
                    cancellationToken: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.ChecksumMismatch);
        exception.Stage.ShouldBe(PackageInstallStage.ChecksumValidation);
        source.CanRead.ShouldBeTrue();
        AssertNoStagingOperations();
    }

    [Fact]
    public async Task AcquireAsync_Sha1NotRequested_DoesNotComputeSha1()
    {
        using MemoryStream source = new MemoryStream([1, 2, 3]);

        await using PackageContentAcquisition acquisition =
            await PackageContentAcquirer.AcquireAsync(
                source,
                _cacheRoot,
                CreateLimits(3),
                cancellationToken: TestContext.Current.CancellationToken);

        acquisition.Sha256.ShouldBe(HashSha256([1, 2, 3]));
        acquisition.Sha1.ShouldBeNull();
    }

    [Fact]
    public async Task AcquireAsync_Cancellation_PropagatesAndCleansOperation()
    {
        using CancellationTokenSource cancellationSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
        using CancelOnReadStream source = new CancelOnReadStream(
            new MemoryStream(new byte[32]),
            cancellationSource);

        await Should.ThrowAsync<OperationCanceledException>(
            () => PackageContentAcquirer.AcquireAsync(
                source,
                _cacheRoot,
                CreateLimits(32),
                cancellationToken: cancellationSource.Token));

        source.WasDisposed.ShouldBeFalse();
        AssertNoStagingOperations();
    }

    [Fact]
    public async Task DiskPackageCache_StandaloneNonSeekableSource_StagesOnceAndUsesFiniteDefaults()
    {
        using MemoryStream archive = CreatePackageTarball();
        byte[] archiveBytes = archive.ToArray();
        byte[] prefix = [9, 8, 7, 6];
        MemoryStream inner = new MemoryStream([.. prefix, .. archiveBytes]);
        inner.Position = prefix.Length;
        using TrackingReadStream source = new TrackingReadStream(
            inner,
            canSeek: false);
        PackageInstallLimits limits = CreateLimits(archiveBytes.Length);
        limits.MaxExpandedBytes = 4_096;
        limits.MaxEntryBytes = 4_096;
        using DiskPackageCache cache = new DiskPackageCache(
            _cacheRoot,
            logger: null,
            timeProvider: null,
            limits);

        PackageRecord record = await cache.InstallAsync(
            new PackageReference("example.package", "1.0.0"),
            source,
            new InstallCacheOptions { VerifyChecksum = false },
            TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(record.ContentPath, "package.json")).ShouldBeTrue();
        source.BytesRead.ShouldBe(archiveBytes.Length);
        source.WasDisposed.ShouldBeFalse();
        AssertNoStagingOperations();
    }

    [Fact]
    public async Task DiskPackageCache_StandaloneConfiguredDefaultLimit_RejectsAndCleans()
    {
        using MemoryStream archive = CreatePackageTarball();
        byte[] archiveBytes = archive.ToArray();
        PackageInstallLimits limits = CreateLimits(archiveBytes.Length - 1);
        limits.MaxExpandedBytes = 4_096;
        limits.MaxEntryBytes = 4_096;
        using DiskPackageCache cache = new DiskPackageCache(
            _cacheRoot,
            logger: null,
            timeProvider: null,
            limits);

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => cache.InstallAsync(
                    new PackageReference("example.package", "1.0.0"),
                    archive,
                    new InstallCacheOptions { VerifyChecksum = false },
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.CompressedSizeLimitExceeded);
        archive.CanRead.ShouldBeTrue();
        AssertNoStagingOperations();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiskPackageCache_ExtractionLimitFailure_CleansOperationWithoutReplacingPrimary(bool import)
    {
        using MemoryStream archive = CreatePackageTarball();
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_cacheRoot);
        for (int attempt = 0; attempt < 3; attempt++)
            files.Enqueue("Unlock", ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "acquired owner unlock"));
        using DiskPackageCache cache = new(
            _cacheRoot, null, null, new PackageInstallLimits(), files.CreateContext(retries: 2));
        PackageInstallLimits limits = CreateLimits(archive.Length);
        limits.MaxExpandedBytes = 128;
        limits.MaxEntryBytes = 16;

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => import
                    ? cache.ImportAsync(
                        archive,
                        new InstallCacheOptions { VerifyChecksum = false, Limits = limits },
                        TestContext.Current.CancellationToken)
                    : cache.InstallAsync(
                        new PackageReference("example.package", "1.0.0"),
                        archive,
                        new InstallCacheOptions { VerifyChecksum = false, Limits = limits },
                        TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.EntrySizeLimitExceeded);
        files.Count("Unlock").ShouldBe(import ? 3 : 4);
        files.AsyncWaits.ShouldBe([10, 10]);
        files.Waits.ShouldBeEmpty();
        PackageCacheCoordinator.GetProcessLockEntryCount(_cacheRoot).ShouldBe(0);
        archive.CanRead.ShouldBeTrue();
        AssertNoStagingOperations();
    }

    [Fact]
    public async Task Acquire_PartialStagingWriteDoesNotRereadSourceOrDoubleHash()
    {
        byte[] content = Encoding.UTF8.GetBytes("partially-written-staging-content");
        using TrackingReadStream source = new TrackingReadStream(
            new MemoryStream(content),
            canSeek: false);
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        primitives.Enqueue<bool>("WriteAtAsync", call =>
        {
            // A physical side effect followed by a safe failure: only the same
            // retained bytes may be rewritten, never a fresh source read.
            RandomAccess.Write(call.Handle!, call.Content.Span[..5], call.Offset!.Value);
            return ScriptedFileSystemPrimitives.NativeFailure<bool>(1117, "WriteAtAsync", call.Path);
        });

        await using PackageContentAcquisition acquisition =
            await PackageContentAcquirer.AcquireAsync(
                source,
                _cacheRoot,
                CreateLimits(content.Length),
                reportedContentLength: null,
                expectedSha256: null,
                expectedSha1: HashSha1(content),
                verifyChecksums: true,
                directive: "example.package#1.0.0",
                primitives.CreateContext(),
                TestContext.Current.CancellationToken);

        acquisition.ActualLength.ShouldBe(content.Length);
        acquisition.Sha256.ShouldBe(HashSha256(content));
        acquisition.Sha1.ShouldBe(HashSha1(content));
        source.BytesRead.ShouldBe(content.Length);
        source.ReadCalls.ShouldBe(2);
        primitives.Count("WriteAtAsync").ShouldBe(2);
        primitives.AsyncWaits.ShouldBe([10]);
        primitives.Waits.ShouldBeEmpty();
        (await File.ReadAllBytesAsync(
            acquisition.ArchivePath,
            TestContext.Current.CancellationToken)).ShouldBe(content);
    }

    [Fact]
    public async Task Acquisition_ArchiveOpenCleanupAndOwnerReleaseUseAsyncWaits()
    {
        byte[] content = "retained archive"u8.ToArray();
        using MemoryStream source = new(content);
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_cacheRoot);
        FileSystemContext context = files.CreateContext(retries: 1);
        PackageCacheCoordinator coordinator = new(_cacheRoot, context, NullPackageCacheContentionObserver.Instance);
        FileStream? ownerStream = null;
        files.Set<FileStream>("OpenFile", call =>
        {
            FileSystemReplayOutcome<FileStream> opened = SystemFileSystemPrimitives.Instance.OpenFile(
                call.Path!, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                OperatingSystem.IsMacOS() ? FileShare.None : FileShare.ReadWrite, 1, FileOptions.None);
            ownerStream = opened.Value;
            return opened;
        });
        PackageContentAcquisition acquisition = await PackageContentAcquirer.AcquireAsync(
            source, _cacheRoot, CreateLimits(content.Length), null, null, null, false, "archive",
            coordinator, context, TestContext.Current.CancellationToken);
        if (OperatingSystem.IsWindows())
        {
            files.Enqueue("OpenFileHandle",
                ScriptedFileSystemPrimitives.NativeFailure<SafeFileHandle>(
                    5, "retained archive open", acquisition.ArchivePath));
        }
        else
        {
            files.Enqueue("OpenDescriptor",
                ScriptedFileSystemPrimitives.NativeFailure<int>(5, "retained archive open", acquisition.ArchivePath));
        }
        try
        {
            await using Stream archive = await acquisition.OpenArchiveReadAsync(TestContext.Current.CancellationToken);
            files.Enqueue("ReadAtAsync", ScriptedFileSystemPrimitives.NativeFailure<int>(
                5, "retained archive read", acquisition.ArchivePath));
            byte[] result = new byte[content.Length];
            await archive.ReadExactlyAsync(result, TestContext.Current.CancellationToken);
            result.ShouldBe(content);
        }
        finally
        {
            files.Enqueue("DeleteEntry", ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "archive cleanup"));
            files.Enqueue("Unlock", ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "archive owner unlock"));
            await acquisition.DisposeAsync();
        }
        await acquisition.DisposeAsync();

        Directory.Exists(acquisition.OperationDirectory).ShouldBeFalse();
        ownerStream.ShouldNotBeNull().CanRead.ShouldBeFalse();
        files.Count("Unlock").ShouldBe(2);
        files.Count("CloseFile").ShouldBe(2);
        files.Count("ReadAtAsync").ShouldBe(2);
        files.AsyncWaits.ShouldBe([10, 10, 10, 10]);
        files.Waits.ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
        source.Position.ShouldBe(content.Length);
        PackageCacheCoordinator.GetProcessLockEntryCount(_cacheRoot).ShouldBe(0);
    }

    [Fact]
    public async Task Acquisition_DisposalCleansBeforeReleasingOwner()
    {
        byte[] content = "owned acquisition"u8.ToArray();
        using TrackingReadStream source = new(new MemoryStream(content), canSeek: false);
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_cacheRoot);
        FileSystemContext context = files.CreateContext(retries: 1);
        PackageCacheCoordinator coordinator = new(
            _cacheRoot, context, NullPackageCacheContentionObserver.Instance);
        FileStream? ownerStream = null;
        files.Set<FileStream>("OpenFile", call =>
        {
            FileSystemReplayOutcome<FileStream> opened = SystemFileSystemPrimitives.Instance.OpenFile(
                call.Path!, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                OperatingSystem.IsMacOS() ? FileShare.None : FileShare.ReadWrite, 1, FileOptions.None);
            ownerStream = opened.Value;
            return opened;
        });
        await using PackageContentAcquisition acquisition = await PackageContentAcquirer.AcquireAsync(
            source, _cacheRoot, CreateLimits(content.Length), null, null, null, false, "archive",
            coordinator, context, TestContext.Current.CancellationToken);
        int ownerAcquired = files.Calls.FindIndex(call => call.Operation == "TryLock");
        int operationCreated = files.Calls.FindIndex(call =>
            call.Operation == "CreateDirectory" && call.Path == acquisition.OperationDirectory);
        int archiveOpened = files.Calls.FindIndex(call =>
            call.Operation is "OpenOwnedDescriptor" or "OpenFileHandle"
            && call.Path == acquisition.ArchivePath);
        ownerAcquired.ShouldBeGreaterThanOrEqualTo(0);
        operationCreated.ShouldBeGreaterThan(ownerAcquired);
        archiveOpened.ShouldBeGreaterThan(operationCreated);
        acquisition.ActualLength.ShouldBe(content.Length);
        acquisition.Sha256.ShouldBe(HashSha256(content));
        int cleanupAttempts = 0;
        files.Set<bool>("DeleteEntry", call =>
        {
            ownerStream.ShouldNotBeNull().CanRead.ShouldBeTrue();
            PackageCacheCoordinator.GetProcessLockEntryCount(_cacheRoot).ShouldBe(1);
            using PackageCacheLease? contender = coordinator.TryAcquireOperationOwner(acquisition.OperationId);
            contender.ShouldBeNull();
            if (cleanupAttempts++ == 0)
                return ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "DeleteEntry", call.Path);
            return SystemFileSystemPrimitives.Instance.DeleteEntry(call.Path!, call.Kind!.Value);
        });
        files.Set<bool>("Unlock", _ =>
        {
            Directory.Exists(acquisition.OperationDirectory).ShouldBeFalse();
            ownerStream.ShouldNotBeNull().CanRead.ShouldBeTrue();
            PackageCacheCoordinator.GetProcessLockEntryCount(_cacheRoot).ShouldBe(1);
            return FileSystemReplayOutcome<bool>.Complete(true);
        });

        await acquisition.DisposeAsync();
        await acquisition.DisposeAsync();

        int lastCleanup = files.Calls.FindLastIndex(call => call.Operation == "DeleteEntry");
        int ownerReleased = files.Calls.FindIndex(call => call.Operation == "Unlock");
        ownerReleased.ShouldBeGreaterThan(lastCleanup);
        Directory.Exists(acquisition.OperationDirectory).ShouldBeFalse();
        ownerStream.ShouldNotBeNull().CanRead.ShouldBeFalse();
        File.Exists(Path.Combine(
            _cacheRoot, ".fhirpkg", "locks", "operations", $"{acquisition.OperationId}.lock"))
            .ShouldBeTrue();
        files.Count("Unlock").ShouldBe(1);
        files.Count("TryLock").ShouldBe(1);
        files.AsyncWaits.ShouldBe([10]);
        files.Waits.ShouldBeEmpty();
        source.BytesRead.ShouldBe(content.Length);
        source.ReadCalls.ShouldBe(2);
        source.WasDisposed.ShouldBeFalse();
        source.PositionWasRead.ShouldBeFalse();
        PackageCacheCoordinator.GetProcessLockEntryCount(_cacheRoot).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acquisition_CleanupUsesIndependentBudgetAfterPrimaryCancellation(
        bool cleanupAndUnlockExhaust)
    {
        byte[] content = "cancelled acquisition"u8.ToArray();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        using CancelOnReadStream source = new(new MemoryStream(content), cancellation);
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_cacheRoot);
        FileSystemContext context = files.CreateContext(retries: 2);
        PackageCacheCoordinator coordinator = new(
            _cacheRoot, context, NullPackageCacheContentionObserver.Instance);
        FileStream? ownerStream = null;
        files.Set<FileStream>("OpenFile", call =>
        {
            FileSystemReplayOutcome<FileStream> opened = SystemFileSystemPrimitives.Instance.OpenFile(
                call.Path!, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                OperatingSystem.IsMacOS() ? FileShare.None : FileShare.ReadWrite, 1, FileOptions.None);
            ownerStream = opened.Value;
            return opened;
        });
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (OperatingSystem.IsWindows())
            {
                files.Enqueue("OpenFileHandle",
                    ScriptedFileSystemPrimitives.NativeFailure<SafeFileHandle>(5, "acquisition open"));
            }
            else
            {
                files.Enqueue("OpenOwnedDescriptor",
                    ScriptedFileSystemPrimitives.NativeFailure<int>(5, "acquisition open"));
            }
        }
        int cleanupAttempts = 0;
        string? cleanupPath = null;
        files.Set<bool>("DeleteEntry", call =>
        {
            cancellation.IsCancellationRequested.ShouldBeTrue();
            if (call.Kind == FileSystemEntryKind.File)
            {
                cleanupPath = call.Path;
                cleanupAttempts++;
                if (cleanupAndUnlockExhaust || cleanupAttempts <= 2)
                    return ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "DeleteEntry", call.Path);
            }
            return SystemFileSystemPrimitives.Instance.DeleteEntry(call.Path!, call.Kind!.Value);
        });
        for (int attempt = 0; attempt < (cleanupAndUnlockExhaust ? 3 : 2); attempt++)
            files.Enqueue("Unlock", ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "owner unlock"));
        int primaryWaits = 0;
        int independentWaits = 0;
        files.OnWait = () =>
        {
            ownerStream.ShouldNotBeNull().CanRead.ShouldBeTrue();
            PackageCacheCoordinator.GetProcessLockEntryCount(_cacheRoot).ShouldBe(1);
            if (cancellation.IsCancellationRequested)
                independentWaits++;
            else
                primaryWaits++;
        };

        OperationCanceledException exception = await Should.ThrowAsync<OperationCanceledException>(() =>
            PackageContentAcquirer.AcquireAsync(
                source, _cacheRoot, CreateLimits(content.Length), null, null, null, false, "archive",
                coordinator, context, cancellation.Token));

        exception.CancellationToken.ShouldBe(cancellation.Token);
        primaryWaits.ShouldBe(2);
        independentWaits.ShouldBe(4);
        cleanupAttempts.ShouldBe(3);
        cleanupPath.ShouldNotBeNull();
        File.Exists(cleanupPath).ShouldBe(cleanupAndUnlockExhaust);
        Directory.Exists(Path.GetDirectoryName(cleanupPath)).ShouldBe(cleanupAndUnlockExhaust);
        if (!cleanupAndUnlockExhaust)
            AssertNoStagingOperations();
        files.Count(OperatingSystem.IsWindows() ? "OpenFileHandle" : "OpenOwnedDescriptor").ShouldBe(3);
        files.Count("WriteAtAsync").ShouldBe(0);
        files.Count("CloseFile").ShouldBe(1);
        files.Count("Unlock").ShouldBe(3);
        files.AsyncWaits.ShouldBe([10, 10, 10, 10, 10, 10]);
        files.Waits.ShouldBeEmpty();
        ownerStream.ShouldNotBeNull().CanRead.ShouldBeFalse();
        source.WasDisposed.ShouldBeFalse();
        source.PositionWasRead.ShouldBeFalse();
        PackageCacheCoordinator.GetProcessLockEntryCount(_cacheRoot).ShouldBe(0);
    }

    [Fact]
    public async Task AcquisitionFailure_PreservesPrimaryWhenAsyncOwnerUnlockExhausts()
    {
        byte[] content = "checksum mismatch"u8.ToArray();
        using MemoryStream source = new(content);
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_cacheRoot);
        FileSystemContext context = files.CreateContext(retries: 2);
        PackageCacheCoordinator coordinator = new(_cacheRoot, context, NullPackageCacheContentionObserver.Instance);
        files.Set<bool>("Unlock", _ => ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "owner unlock"));

        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(() =>
            PackageContentAcquirer.AcquireAsync(
                source, _cacheRoot, CreateLimits(content.Length), null, "wrong", null, true, "archive",
                coordinator, context, TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.ChecksumMismatch);
        exception.Stage.ShouldBe(PackageInstallStage.ChecksumValidation);
        files.Count("Unlock").ShouldBe(3);
        files.Count("WriteAtAsync").ShouldBe(1);
        files.Count("CloseFile").ShouldBe(1);
        files.AsyncWaits.ShouldBe([10, 10]);
        files.Waits.ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
        PackageCacheCoordinator.GetProcessLockEntryCount(_cacheRoot).ShouldBe(0);
        AssertNoStagingOperations();
    }

    private static ScriptedFileSystemPrimitives SystemFiles()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(
            SystemFileSystemPrimitives.Instance.Platform);
        primitives.UseSystemFiles();
        primitives.Set<IEnumerator<string>>(
            "CreateDirectoryEnumerator",
            call => SystemFileSystemPrimitives.Instance.CreateDirectoryEnumerator(call.Path!));
        return primitives;
    }

    private static PackageInstallLimits CreateLimits(long maxCompressedBytes) =>
        new PackageInstallLimits
        {
            MaxCompressedBytes = maxCompressedBytes,
            MaxExpandedBytes = 1_024,
            MaxEntryBytes = 1_024,
            MaxArchiveEntries = 100,
            MaxArchivePathLength = 256,
            MaxArchiveDepth = 16
        };

    private static string HashSha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string HashSha1(byte[] content) =>
        Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();

    private static MemoryStream CreatePackageTarball()
    {
        MemoryStream archive = new MemoryStream();
        using (GZipStream gzip = new GZipStream(
            archive,
            CompressionMode.Compress,
            leaveOpen: true))
        using (TarWriter writer = new TarWriter(gzip, leaveOpen: true))
        {
            PaxTarEntry entry = new PaxTarEntry(
                TarEntryType.RegularFile,
                "package/package.json")
            {
                DataStream = new MemoryStream(
                    Encoding.UTF8.GetBytes(
                        """{"name":"example.package","version":"1.0.0"}"""))
            };
            writer.WriteEntry(entry);
        }

        archive.Position = 0;
        return archive;
    }

    private void AssertNoStagingOperations()
    {
        string stagingRoot = Path.Combine(_cacheRoot, ".fhirpkg", "staging");
        if (!Directory.Exists(stagingRoot))
            return;

        Directory.EnumerateFileSystemEntries(stagingRoot).ShouldBeEmpty();
    }

    private class TrackingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _canSeek;

        internal TrackingReadStream(Stream inner, bool canSeek)
        {
            _inner = inner;
            _canSeek = canSeek;
        }

        internal long BytesRead { get; private set; }

        internal int ReadCalls { get; private set; }

        internal bool WasDisposed { get; private set; }

        internal bool PositionWasRead { get; private set; }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _canSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get
            {
                PositionWasRead = true;
                if (!_canSeek)
                    throw new NotSupportedException();

                return _inner.Position;
            }
            set
            {
                if (!_canSeek)
                    throw new NotSupportedException();

                _inner.Position = value;
            }
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            int bytesRead = _inner.Read(buffer, offset, count);
            BytesRead += bytesRead;
            return bytesRead;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            int bytesRead = await _inner.ReadAsync(buffer, cancellationToken);
            BytesRead += bytesRead;
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class CancelOnReadStream : TrackingReadStream
    {
        private readonly CancellationTokenSource _cancellationSource;

        internal CancelOnReadStream(
            Stream inner,
            CancellationTokenSource cancellationSource)
            : base(inner, canSeek: false)
        {
            _cancellationSource = cancellationSource;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _cancellationSource.Cancel();
            return ValueTask.FromCanceled<int>(cancellationToken);
        }
    }
}
