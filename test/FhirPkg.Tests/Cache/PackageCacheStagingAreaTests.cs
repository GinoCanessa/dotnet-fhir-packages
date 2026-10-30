// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Tests.Support;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Cache;

public sealed class PackageCacheStagingAreaTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(
        AppContext.BaseDirectory,
        $"staging-area-{Guid.NewGuid():N}");

    [Fact]
    public void Paths_PreserveExistingLayout()
    {
        ScriptedFileSystemPrimitives files = new();
        FileSystemContext context = files.CreateContext();
        string operationId = Guid.NewGuid().ToString("N");
        string[] roots =
        [
            _cacheRoot,
            _cacheRoot + Path.DirectorySeparatorChar,
            Path.Combine("relative", "..", "cache")
        ];

        foreach (string cacheRoot in roots)
        {
            PackageCacheStagingArea stagingArea = new(cacheRoot, context);
            string stagingRoot = Path.Combine(cacheRoot, ".fhirpkg", "staging");
            string operationDirectory = Path.Combine(stagingRoot, operationId);

            stagingArea.Context.ShouldBeSameAs(context);
            stagingArea.StagingRoot.ShouldBe(stagingRoot);
            stagingArea.GetOperationDirectory(operationId).ShouldBe(operationDirectory);
            stagingArea.GetArchivePath(operationId)
                .ShouldBe(Path.Combine(operationDirectory, "archive.tgz"));
            Guid.TryParseExact(Path.GetFileName(operationDirectory), "N", out Guid _)
                .ShouldBeTrue();
        }

        files.Calls.ShouldBeEmpty();
        Directory.Exists(_cacheRoot).ShouldBeFalse();
    }

    [Fact]
    public async Task CleanupAbandoned_PreservesJournalProtectedActiveAndNonDirectoryEntries()
    {
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_cacheRoot);
        FileSystemContext context = files.CreateContext(retries: 1);
        PackageCacheStagingArea stagingArea = new(_cacheRoot, context);
        PackageCacheCoordinator coordinator = new(
            _cacheRoot, context, NullPackageCacheContentionObserver.Instance);
        PackageCacheCommitter committer = CreateCommitter(context, out PackageCacheJournalStore journalStore);
        string journalId = new('1', 32);
        string protectedId = new('2', 32);
        string activeId = new('3', 32);
        string fileId = new('4', 32);
        string directoryLinkId = new('5', 32);
        string fileLinkId = new('6', 32);
        string upperId = new('B', 32);
        string lowerId = new('a', 32);
        string malformedId = "not-an-operation";
        string[] retainedDirectories =
        [
            journalId, protectedId, activeId, directoryLinkId, fileLinkId, malformedId
        ];
        foreach (string operationId in retainedDirectories)
            Directory.CreateDirectory(stagingArea.GetOperationDirectory(operationId));
        Directory.CreateDirectory(stagingArea.GetOperationDirectory(upperId));
        Directory.CreateDirectory(stagingArea.GetOperationDirectory(lowerId));
        string filePath = stagingArea.GetOperationDirectory(fileId);
        File.WriteAllText(filePath, "foreign file");
        string journalPath = await WriteJournalAsync(journalStore, journalId);
        string journalContent = File.ReadAllText(journalPath);
        PackageCacheLease activeOwner = await coordinator.AcquireOperationOwnerAsync(
            activeId, TestContext.Current.CancellationToken);
        string directoryLinkPath = stagingArea.GetOperationDirectory(directoryLinkId);
        string fileLinkPath = stagingArea.GetOperationDirectory(fileLinkId);
        files.Set<FileSystemProbeResult>("Probe", call =>
        {
            if (call.Path == directoryLinkPath || call.Path == fileLinkPath)
            {
                call.Flags.ShouldBe(0);
                return ScriptedFileSystemPrimitives.Entry(call.Path == directoryLinkPath
                    ? FileSystemEntryKind.DirectorySymbolicLink
                    : FileSystemEntryKind.SymbolicLink);
            }
            return SystemFileSystemPrimitives.Instance.Probe(call.Path!, call.Flags == 1);
        });
        string[] operationDirectories =
        [
            stagingArea.GetOperationDirectory(lowerId),
            fileLinkPath,
            stagingArea.GetOperationDirectory(protectedId),
            stagingArea.GetOperationDirectory(upperId),
            stagingArea.GetOperationDirectory(malformedId),
            filePath,
            stagingArea.GetOperationDirectory(activeId),
            directoryLinkPath,
            stagingArea.GetOperationDirectory(journalId)
        ];
        bool stagingSnapshotCreated = false;
        files.Set<IEnumerator<string>>("CreateDirectoryEnumerator", call =>
        {
            if (call.Path == stagingArea.StagingRoot && !stagingSnapshotCreated)
            {
                stagingSnapshotCreated = true;
                return FileSystemReplayOutcome<IEnumerator<string>>.Complete(
                    ((IEnumerable<string>)operationDirectories).GetEnumerator());
            }
            return SystemFileSystemPrimitives.Instance.CreateDirectoryEnumerator(call.Path!);
        });
        files.Calls.Clear();

        try
        {
            await stagingArea.CleanupAbandonedAsync(
                coordinator, committer, TestContext.Current.CancellationToken, protectedId);

            files.Calls[0].Operation.ShouldBe("Probe");
            files.Calls[0].Path.ShouldBe(stagingArea.StagingRoot);
            int journalSnapshot = files.Calls.FindIndex(call =>
                call.Operation == "CreateDirectoryEnumerator"
                && call.Path == Path.GetDirectoryName(journalPath));
            int stagingSnapshot = files.Calls.FindIndex(call =>
                call.Operation == "CreateDirectoryEnumerator" && call.Path == stagingArea.StagingRoot);
            journalSnapshot.ShouldBeGreaterThan(0);
            stagingSnapshot.ShouldBeGreaterThan(journalSnapshot);
            string[] probedCandidates = files.Calls
                .Where(call => call.Operation == "Probe"
                    && operationDirectories.Contains(call.Path!, StringComparer.Ordinal))
                .Select(call => call.Path!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            probedCandidates.ShouldBe(
            [
                stagingArea.GetOperationDirectory(activeId),
                filePath,
                directoryLinkPath,
                fileLinkPath,
                stagingArea.GetOperationDirectory(upperId),
                stagingArea.GetOperationDirectory(lowerId)
            ]);
            files.Calls.Where(call => call.Operation == "OpenFile")
                .Select(call => call.Path)
                .ShouldBe([GetOwnerLockPath(upperId), GetOwnerLockPath(lowerId)]);
            files.Calls.Where(call => call.Operation == "DeleteEntry")
                .Select(call => call.Path)
                .ShouldBe(
                [
                    stagingArea.GetOperationDirectory(upperId),
                    stagingArea.GetOperationDirectory(lowerId)
                ]);
            foreach (string operationId in retainedDirectories)
                Directory.Exists(stagingArea.GetOperationDirectory(operationId)).ShouldBeTrue();
            Directory.Exists(stagingArea.GetOperationDirectory(upperId)).ShouldBeFalse();
            Directory.Exists(stagingArea.GetOperationDirectory(lowerId)).ShouldBeFalse();
            File.ReadAllText(filePath).ShouldBe("foreign file");
            File.ReadAllText(journalPath).ShouldBe(journalContent);
            File.Exists(GetOwnerLockPath(upperId)).ShouldBeTrue();
            File.Exists(GetOwnerLockPath(lowerId)).ShouldBeTrue();
            files.Count("Unlock").ShouldBe(2);
            files.AsyncWaits.ShouldBeEmpty();
            files.Waits.ShouldBeEmpty();
            PackageCacheCoordinator.GetProcessLockEntryCount(_cacheRoot).ShouldBe(1);
        }
        finally
        {
            await activeOwner.DisposeAsync();
        }

        PackageCacheCoordinator.GetProcessLockEntryCount(_cacheRoot).ShouldBe(0);
    }

    [Fact]
    public async Task CleanupAbandoned_ReclaimsGuidDirectoryOnlyWhileOwnerHeld()
    {
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_cacheRoot);
        FileSystemContext context = files.CreateContext(retries: 1);
        PackageCacheStagingArea stagingArea = new(_cacheRoot, context);
        PackageCacheCoordinator coordinator = new(
            _cacheRoot, context, NullPackageCacheContentionObserver.Instance);
        PackageCacheCommitter committer = CreateCommitter(context, out PackageCacheJournalStore _);
        string operationId = Guid.NewGuid().ToString("N");
        string operationDirectory = stagingArea.GetOperationDirectory(operationId);
        string nestedDirectory = Path.Combine(operationDirectory, "expanded");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(Path.Combine(nestedDirectory, "partial.json"), "partial");
        FileStream? ownerStream = null;
        bool ownerHeld = false;
        files.Set<FileStream>("OpenFile", call =>
        {
            call.Path.ShouldBe(GetOwnerLockPath(operationId));
            FileSystemReplayOutcome<FileStream> opened = OpenOwnerFile(call.Path!);
            ownerStream = opened.Value;
            return opened;
        });
        files.Set<bool>("TryLock", _ =>
        {
            ownerHeld = true;
            return FileSystemReplayOutcome<bool>.Complete(true);
        });
        bool failedDelete = false;
        files.Set<bool>("DeleteEntry", call =>
        {
            ownerHeld.ShouldBeTrue();
            ownerStream.ShouldNotBeNull().CanRead.ShouldBeTrue();
            PackageCacheCoordinator.GetProcessLockEntryCount(_cacheRoot).ShouldBe(1);
            using PackageCacheLease? contender = coordinator.TryAcquireOperationOwner(operationId);
            contender.ShouldBeNull();
            if (!failedDelete)
            {
                failedDelete = true;
                return ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "DeleteEntry", call.Path);
            }
            return SystemFileSystemPrimitives.Instance.DeleteEntry(call.Path!, call.Kind!.Value);
        });
        files.OnWait = () => ownerHeld.ShouldBeTrue();
        files.Set<bool>("Unlock", _ =>
        {
            ownerHeld.ShouldBeTrue();
            Directory.Exists(operationDirectory).ShouldBeFalse();
            Directory.Exists(stagingArea.StagingRoot).ShouldBeFalse();
            ownerStream.ShouldNotBeNull().CanRead.ShouldBeTrue();
            ownerHeld = false;
            return FileSystemReplayOutcome<bool>.Complete(true);
        });

        await stagingArea.CleanupAbandonedAsync(
            coordinator, committer, TestContext.Current.CancellationToken);

        Directory.Exists(operationDirectory).ShouldBeFalse();
        Directory.Exists(stagingArea.StagingRoot).ShouldBeFalse();
        File.Exists(GetOwnerLockPath(operationId)).ShouldBeTrue();
        ownerHeld.ShouldBeFalse();
        ownerStream.ShouldNotBeNull().CanRead.ShouldBeFalse();
        files.Count("OpenFile").ShouldBe(1);
        files.Count("TryLock").ShouldBe(1);
        files.Count("Unlock").ShouldBe(1);
        files.AsyncWaits.ShouldBe([10]);
        files.Waits.ShouldBeEmpty();
        PackageCacheCoordinator.GetProcessLockEntryCount(_cacheRoot).ShouldBe(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task CleanupAbandoned_RootDisappearsAtEnumeratorCreation_HasNoCandidates(int retries)
    {
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_cacheRoot);
        PackageCacheStagingArea stagingArea = CreateAbandonedDiscovery(
            files, retries, out PackageCacheCoordinator coordinator, out PackageCacheCommitter committer);
        List<TrackingEnumerator> enumerators = [];
        List<DirectoryNotFoundException> failures = [];
        ScriptMissingStagingSnapshots(
            files, stagingArea.StagingRoot, null, enumerators, failures,
            onFailure: attempt =>
            {
                if (attempt == 1)
                    Directory.Delete(stagingArea.StagingRoot);
                Directory.Exists(stagingArea.StagingRoot).ShouldBeFalse();
            });

        await stagingArea.CleanupAbandonedAsync(
            coordinator, committer, TestContext.Current.CancellationToken);

        AssertDiscoveryOrder(files, stagingArea.StagingRoot);
        AssertSnapshotCalls(
            files, stagingArea.StagingRoot, retries + 1, advances: 0, currents: 0, disposals: 0);
        enumerators.ShouldBeEmpty();
        failures.Count.ShouldBe(retries + 1);
        AssertTerminalSnapshotFailure(
            failures[^1], "EnumerateDirectory", stagingArea.StagingRoot, retries, nativeError: 2);
        foreach (DirectoryNotFoundException earlier in failures.Take(retries))
            FileSystemFailureContext.TryGetTerminal(earlier, out _).ShouldBeFalse();
        files.Calls.Where(call => call.Operation == "Probe").Select(call => call.Path)
            .ShouldBe([stagingArea.StagingRoot, GetTransactionDirectory()]);
        files.AsyncWaits.ShouldBe(Enumerable.Repeat(10, retries).ToArray());
        files.Waits.ShouldBeEmpty();
        AssertNoOwnerOrDelete(files);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    public async Task CleanupAbandoned_RootDisappearsDuringMoveNext_DiscardsPartialSnapshot(
        int retries,
        bool secondaryDisposalFailure)
    {
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_cacheRoot);
        PackageCacheStagingArea stagingArea = CreateAbandonedDiscovery(
            files, retries, out PackageCacheCoordinator coordinator, out PackageCacheCommitter committer);
        string operationDirectory = stagingArea.GetOperationDirectory(new string('1', 32));
        Directory.CreateDirectory(operationDirectory);
        string payload = Path.Combine(operationDirectory, "keep.txt");
        File.WriteAllText(payload, "eligible partial candidate");
        List<TrackingEnumerator> enumerators = [];
        List<DirectoryNotFoundException> failures = [];
        List<DirectoryNotFoundException> disposalFailures = [];
        ScriptMissingStagingSnapshots(
            files, stagingArea.StagingRoot, operationDirectory, enumerators, failures,
            disposalFailures: secondaryDisposalFailure ? disposalFailures : null);

        await stagingArea.CleanupAbandonedAsync(
            coordinator, committer, TestContext.Current.CancellationToken);

        AssertDiscoveryOrder(files, stagingArea.StagingRoot);
        AssertSnapshotCalls(
            files, stagingArea.StagingRoot, retries + 1,
            advances: 2 * (retries + 1), currents: retries + 1, disposals: retries + 1);
        enumerators.Count.ShouldBe(retries + 1);
        foreach (TrackingEnumerator enumerator in enumerators)
            enumerator.DisposeCount.ShouldBe(1);
        failures.Count.ShouldBe(retries + 1);
        AssertTerminalSnapshotFailure(
            failures[^1], "DirectoryEnumerator.MoveNext", stagingArea.StagingRoot, retries, nativeError: 2);
        foreach (DirectoryNotFoundException earlier in failures.Take(retries))
            FileSystemFailureContext.TryGetTerminal(earlier, out _).ShouldBeFalse();
        disposalFailures.Count.ShouldBe(secondaryDisposalFailure ? retries + 1 : 0);
        foreach (DirectoryNotFoundException secondary in disposalFailures)
        {
            FileSystemFailureContext.TryGetOrigin(
                secondary, out FileSystemFailureContext? failure).ShouldBeTrue();
            failure!.Operation.ShouldBe("DirectoryEnumerator.Dispose");
            failure.Path.ShouldBe(stagingArea.StagingRoot);
            FileSystemFailureContext.TryGetTerminal(secondary, out _).ShouldBeFalse();
        }
        files.Calls.Where(call => call.Operation == "GetCurrent").Select(call => call.Path)
            .ShouldBe(Enumerable.Repeat(stagingArea.StagingRoot, retries + 1).ToArray());
        files.Calls.Where(call => call.Operation == "Probe").Select(call => call.Path)
            .ShouldBe([stagingArea.StagingRoot, GetTransactionDirectory()]);
        File.ReadAllText(payload).ShouldBe("eligible partial candidate");
        Directory.Exists(operationDirectory).ShouldBeTrue();
        files.AsyncWaits.ShouldBe(Enumerable.Repeat(10, retries).ToArray());
        files.Waits.ShouldBeEmpty();
        AssertNoOwnerOrDelete(files);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task CleanupAbandoned_RootReappearsWithinBudget_UsesOnlyCompleteSnapshot(
        int retries,
        bool failDuringMoveNext)
    {
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_cacheRoot);
        PackageCacheStagingArea stagingArea = CreateAbandonedDiscovery(
            files, retries, out PackageCacheCoordinator coordinator, out PackageCacheCommitter committer);
        string partialId = new('1', 32);
        string completeId = new('2', 32);
        string partialDirectory = stagingArea.GetOperationDirectory(partialId);
        string completeDirectory = stagingArea.GetOperationDirectory(completeId);
        Directory.CreateDirectory(partialDirectory);
        Directory.CreateDirectory(completeDirectory);
        string payload = Path.Combine(partialDirectory, "keep.txt");
        File.WriteAllText(payload, "discarded snapshot");
        int discoveryAttempts = 0;
        int parentSnapshotDisposals = 0;
        List<TrackingEnumerator> enumerators = [];
        List<DirectoryNotFoundException> failures = [];
        files.Set<IEnumerator<string>>("CreateDirectoryEnumerator", call =>
        {
            if (call.Path != stagingArea.StagingRoot)
                return SystemFileSystemPrimitives.Instance.CreateDirectoryEnumerator(call.Path!);
            if (discoveryAttempts == retries + 1)
            {
                FileSystemReplayOutcome<IEnumerator<string>> opened =
                    SystemFileSystemPrimitives.Instance.CreateDirectoryEnumerator(call.Path!);
                return FileSystemReplayOutcome<IEnumerator<string>>.Complete(
                    new AfterDisposeEnumerator(opened.Value, () => parentSnapshotDisposals++));
            }

            discoveryAttempts++;
            if (discoveryAttempts <= retries)
            {
                DirectoryNotFoundException failure = new("The staging root disappeared.");
                failures.Add(failure);
                if (!failDuringMoveNext)
                {
                    return ScriptedFileSystemPrimitives.Fail<IEnumerator<string>>(
                        failure, "EnumerateDirectory", call.Path, nativeError: 2);
                }

                TrackingEnumerator partial = new([partialDirectory]);
                enumerators.Add(partial);
                files.Enqueue<bool>("MoveNext", advance =>
                {
                    advance.Path.ShouldBe(stagingArea.StagingRoot);
                    return SystemFileSystemPrimitives.Instance.MoveNext(partial, advance.Path!);
                });
                files.Enqueue<bool>("MoveNext", advance =>
                {
                    advance.Path.ShouldBe(stagingArea.StagingRoot);
                    return ScriptedFileSystemPrimitives.Fail<bool>(
                        failure, "DirectoryEnumerator.MoveNext", advance.Path, nativeError: 2);
                });
                return FileSystemReplayOutcome<IEnumerator<string>>.Complete(partial);
            }

            TrackingEnumerator complete = new([completeDirectory]);
            enumerators.Add(complete);
            return FileSystemReplayOutcome<IEnumerator<string>>.Complete(complete);
        });

        await stagingArea.CleanupAbandonedAsync(
            coordinator, committer, TestContext.Current.CancellationToken);

        AssertDiscoveryOrder(files, stagingArea.StagingRoot);
        discoveryAttempts.ShouldBe(retries + 1);
        AssertSnapshotCalls(
            files, stagingArea.StagingRoot, retries + 2,
            advances: (failDuringMoveNext ? 2 * retries : 0) + 4,
            currents: (failDuringMoveNext ? retries : 0) + 2,
            disposals: (failDuringMoveNext ? retries : 0) + 2);
        enumerators.Count.ShouldBe(failDuringMoveNext ? retries + 1 : 1);
        foreach (TrackingEnumerator enumerator in enumerators)
            enumerator.DisposeCount.ShouldBe(1);
        parentSnapshotDisposals.ShouldBe(1);
        failures.Count.ShouldBe(retries);
        foreach (DirectoryNotFoundException failure in failures)
        {
            FileSystemFailureContext.TryGetOrigin(
                failure, out FileSystemFailureContext? origin).ShouldBeTrue();
            origin!.Operation.ShouldBe(failDuringMoveNext
                ? "DirectoryEnumerator.MoveNext"
                : "EnumerateDirectory");
            origin.Path.ShouldBe(stagingArea.StagingRoot);
            origin.NativeErrorCode.ShouldBe(2);
            FileSystemFailureContext.TryGetTerminal(failure, out _).ShouldBeFalse();
        }
        files.Calls.Where(call => call.Operation == "OpenFile").Select(call => call.Path)
            .ShouldBe([GetOwnerLockPath(completeId)]);
        files.Calls.Where(call => call.Operation == "DeleteEntry").Select(call => call.Path)
            .ShouldBe([completeDirectory]);
        files.Calls.ShouldNotContain(call => call.Path == partialDirectory
            || call.Path == payload || call.Path == GetOwnerLockPath(partialId));
        files.Count("TryLock").ShouldBe(1);
        files.Count("Unlock").ShouldBe(1);
        Directory.Exists(completeDirectory).ShouldBeFalse();
        File.ReadAllText(payload).ShouldBe("discarded snapshot");
        files.AsyncWaits.ShouldBe(Enumerable.Repeat(10, retries).ToArray());
        files.Waits.ShouldBeEmpty();
        PackageCacheCoordinator.GetProcessLockEntryCount(_cacheRoot).ShouldBe(0);
    }

    [Theory]
    [InlineData("access", 0)]
    [InlineData("access", 2)]
    [InlineData("io", 0)]
    [InlineData("io", 2)]
    [InlineData("wrong-path", 0)]
    [InlineData("wrong-path", 2)]
    [InlineData("wrong-operation", 0)]
    [InlineData("wrong-operation", 2)]
    [InlineData("untagged-callback", 0)]
    [InlineData("untagged-callback", 2)]
    [InlineData("origin-only-callback", 2)]
    [InlineData("unsafe", 2)]
    [InlineData("dispose-only", 0)]
    [InlineData("dispose-only", 2)]
    [InlineData("primary-and-disposal", 0)]
    [InlineData("primary-and-disposal", 2)]
    [InlineData("journal", 0)]
    [InlineData("journal", 2)]
    [InlineData("cancel-before", 0)]
    [InlineData("cancel-before", 2)]
    [InlineData("cancel-during-retry", 2)]
    public async Task CleanupAbandoned_UnrelatedFailuresPropagate(string failurePoint, int retries)
    {
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_cacheRoot);
        PackageCacheStagingArea stagingArea = CreateAbandonedDiscovery(
            files, retries, out PackageCacheCoordinator coordinator, out PackageCacheCommitter committer);
        string operationDirectory = stagingArea.GetOperationDirectory(new string('1', 32));
        Directory.CreateDirectory(operationDirectory);
        string payload = Path.Combine(operationDirectory, "keep.txt");
        File.WriteAllText(payload, "unrelated failure");
        string failedPath = failurePoint == "journal"
            ? GetTransactionDirectory()
            : stagingArea.StagingRoot;
        string evidencePath = failurePoint == "wrong-path"
            ? Path.Combine(stagingArea.StagingRoot, "another-directory")
            : failedPath;
        string operation = failurePoint switch
        {
            "wrong-operation" => "DirectoryEnumerator.Current",
            "dispose-only" => "DirectoryEnumerator.Dispose",
            "primary-and-disposal" => "DirectoryEnumerator.MoveNext",
            _ => "EnumerateDirectory"
        };
        Exception original = failurePoint switch
        {
            "access" or "primary-and-disposal" => new UnauthorizedAccessException("Access denied."),
            "io" or "journal" => new IOException("Unrelated physical failure."),
            _ => new DirectoryNotFoundException("Missing directory is not sufficient evidence.")
        };
        int? nativeError = failurePoint == "dispose-only"
            ? original.HResult & 0xFFFF
            : original is UnauthorizedAccessException ? 13 : original is DirectoryNotFoundException ? 2 : 5;
        DirectoryNotFoundException secondary = new("Secondary disposal failure.");
        List<TrackingEnumerator> enumerators = [];
        int failureAttempts = 0;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        if (failurePoint == "cancel-before")
            cancellation.Cancel();
        if (failurePoint == "cancel-during-retry")
            files.OnWait = cancellation.Cancel;
        files.Set<IEnumerator<string>>("CreateDirectoryEnumerator", call =>
        {
            if (call.Path != failedPath)
                return SystemFileSystemPrimitives.Instance.CreateDirectoryEnumerator(call.Path!);

            failureAttempts++;
            if (failurePoint == "untagged-callback")
                throw original;
            if (failurePoint == "origin-only-callback")
            {
                ScriptedFileSystemPrimitives.Fail<IEnumerator<string>>(
                    original, operation, evidencePath, nativeError: nativeError);
                throw original;
            }
            if (failurePoint is "dispose-only" or "primary-and-disposal")
            {
                TrackingEnumerator enumerator = new(
                    [operationDirectory], failurePoint == "dispose-only" ? original : secondary);
                enumerators.Add(enumerator);
                if (failurePoint == "primary-and-disposal")
                {
                    files.Enqueue<bool>("MoveNext", advance =>
                    {
                        advance.Path.ShouldBe(stagingArea.StagingRoot);
                        return ScriptedFileSystemPrimitives.Fail<bool>(
                            original, operation, evidencePath, nativeError: nativeError);
                    });
                }
                return FileSystemReplayOutcome<IEnumerator<string>>.Complete(enumerator);
            }

            return ScriptedFileSystemPrimitives.Fail<IEnumerator<string>>(
                original, operation, evidencePath,
                failurePoint == "unsafe" ? FileSystemReplaySafety.Unsafe : FileSystemReplaySafety.Safe,
                nativeError);
        });

        bool cancelled = failurePoint is "cancel-before" or "cancel-during-retry";
        if (cancelled)
        {
            OperationCanceledException exception = await Should.ThrowAsync<OperationCanceledException>(() =>
                stagingArea.CleanupAbandonedAsync(coordinator, committer, cancellation.Token));

            exception.CancellationToken.ShouldBe(cancellation.Token);
            FileSystemFailureContext.TryGetTerminal(original, out _).ShouldBeFalse();
            FileSystemFailureContext.TryGetOrigin(original, out FileSystemFailureContext? origin)
                .ShouldBe(failurePoint == "cancel-during-retry");
            if (origin is not null)
            {
                origin.Operation.ShouldBe(operation);
                origin.Path.ShouldBe(evidencePath);
                origin.Termination.ShouldBe(FileSystemFailureTermination.None);
            }
        }
        else
        {
            Exception exception = await Should.ThrowAsync<Exception>(() =>
                stagingArea.CleanupAbandonedAsync(coordinator, committer, cancellation.Token));

            exception.ShouldBeSameAs(original);
            if (failurePoint is "untagged-callback" or "origin-only-callback")
            {
                FileSystemFailureContext.TryGetTerminal(exception, out _).ShouldBeFalse();
                FileSystemFailureContext.TryGetOrigin(exception, out FileSystemFailureContext? origin)
                    .ShouldBe(failurePoint == "origin-only-callback");
                if (origin is not null)
                {
                    origin.Operation.ShouldBe(operation);
                    origin.Path.ShouldBe(evidencePath);
                    origin.Termination.ShouldBe(FileSystemFailureTermination.None);
                }
            }
            else
            {
                AssertTerminalSnapshotFailure(
                    exception, operation, evidencePath, failurePoint == "unsafe" ? 0 : retries,
                    nativeError, failurePoint == "unsafe"
                        ? FileSystemFailureTermination.Unsafe
                        : FileSystemFailureTermination.Exhausted);
                FileSystemFailureContext.TryGetTerminal(
                    exception, out FileSystemFailureContext? actual).ShouldBeTrue();
                FileSystemFailureContext.TryGetTerminal(
                    original, out FileSystemFailureContext? expected).ShouldBeTrue();
                actual.ShouldBeSameAs(expected);
            }
        }

        int attempts = failurePoint switch
        {
            "cancel-before" => 0,
            "untagged-callback" or "origin-only-callback" or "unsafe" or "cancel-during-retry" => 1,
            _ => retries + 1
        };
        failureAttempts.ShouldBe(attempts);
        bool createsEnumerator = failurePoint is "dispose-only" or "primary-and-disposal";
        AssertSnapshotCalls(
            files, stagingArea.StagingRoot, failurePoint == "journal" ? 0 : attempts,
            advances: failurePoint == "dispose-only" ? attempts * 2 : createsEnumerator ? attempts : 0,
            currents: failurePoint == "dispose-only" ? attempts : 0,
            disposals: createsEnumerator ? attempts : 0);
        enumerators.Count.ShouldBe(createsEnumerator ? attempts : 0);
        foreach (TrackingEnumerator enumerator in enumerators)
            enumerator.DisposeCount.ShouldBe(1);
        if (failurePoint == "primary-and-disposal")
        {
            FileSystemFailureContext.TryGetOrigin(
                secondary, out FileSystemFailureContext? disposal).ShouldBeTrue();
            disposal!.Operation.ShouldBe("DirectoryEnumerator.Dispose");
            disposal.Path.ShouldBe(stagingArea.StagingRoot);
            FileSystemFailureContext.TryGetTerminal(secondary, out _).ShouldBeFalse();
        }
        if (failurePoint == "cancel-before")
        {
            files.Calls.ShouldBeEmpty();
        }
        else if (failurePoint == "journal")
        {
            AssertSnapshotCalls(files, failedPath, attempts, advances: 0, currents: 0, disposals: 0);
        }
        else
        {
            AssertDiscoveryOrder(files, stagingArea.StagingRoot);
        }
        int waits = failurePoint == "cancel-during-retry" ? 1 : Math.Max(0, attempts - 1);
        files.AsyncWaits.ShouldBe(Enumerable.Repeat(10, waits).ToArray());
        files.Waits.ShouldBeEmpty();
        File.ReadAllText(payload).ShouldBe("unrelated failure");
        AssertNoOwnerOrDelete(files);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    public async Task CleanupAbandoned_CancellationDuringFinalMissingAttemptPropagates(
        int retries,
        bool failDuringMoveNext)
    {
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_cacheRoot);
        PackageCacheStagingArea stagingArea = CreateAbandonedDiscovery(
            files, retries, out PackageCacheCoordinator coordinator, out PackageCacheCommitter committer);
        string operationDirectory = stagingArea.GetOperationDirectory(new string('1', 32));
        Directory.CreateDirectory(operationDirectory);
        string payload = Path.Combine(operationDirectory, "keep.txt");
        File.WriteAllText(payload, "cancelled discovery");
        List<TrackingEnumerator> enumerators = [];
        List<DirectoryNotFoundException> failures = [];
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        ScriptMissingStagingSnapshots(
            files, stagingArea.StagingRoot, failDuringMoveNext ? operationDirectory : null,
            enumerators, failures,
            onFailure: attempt =>
            {
                if (attempt == retries + 1)
                    cancellation.Cancel();
            });

        OperationCanceledException exception = await Should.ThrowAsync<OperationCanceledException>(() =>
            stagingArea.CleanupAbandonedAsync(coordinator, committer, cancellation.Token));

        exception.CancellationToken.ShouldBe(cancellation.Token);
        AssertDiscoveryOrder(files, stagingArea.StagingRoot);
        failures.Count.ShouldBe(retries + 1);
        AssertTerminalSnapshotFailure(
            failures[^1], failDuringMoveNext ? "DirectoryEnumerator.MoveNext" : "EnumerateDirectory",
            stagingArea.StagingRoot, retries, nativeError: 2);
        AssertSnapshotCalls(
            files, stagingArea.StagingRoot, retries + 1,
            advances: failDuringMoveNext ? 2 * (retries + 1) : 0,
            currents: failDuringMoveNext ? retries + 1 : 0,
            disposals: failDuringMoveNext ? retries + 1 : 0);
        enumerators.Count.ShouldBe(failDuringMoveNext ? retries + 1 : 0);
        foreach (TrackingEnumerator enumerator in enumerators)
            enumerator.DisposeCount.ShouldBe(1);
        files.Calls.Where(call => call.Operation == "Probe").Select(call => call.Path)
            .ShouldBe([stagingArea.StagingRoot, GetTransactionDirectory()]);
        files.AsyncWaits.ShouldBe(Enumerable.Repeat(10, retries).ToArray());
        files.Waits.ShouldBeEmpty();
        File.ReadAllText(payload).ShouldBe("cancelled discovery");
        AssertNoOwnerOrDelete(files);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CleanupAbandoned_ReplacementLinkIsNotTraversed(
        bool replaceRoot,
        bool directoryLink)
    {
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_cacheRoot);
        PackageCacheStagingArea stagingArea = CreateAbandonedDiscovery(
            files, retries: 2, out PackageCacheCoordinator coordinator, out PackageCacheCommitter committer);
        string operationDirectory = stagingArea.GetOperationDirectory(new string('1', 32));
        Directory.CreateDirectory(operationDirectory);
        string foreignDirectory = Path.Combine(_cacheRoot, "foreign", new string('2', 32));
        Directory.CreateDirectory(foreignDirectory);
        string payload = Path.Combine(foreignDirectory, "keep.txt");
        File.WriteAllText(payload, "link target");
        string linkPath = replaceRoot ? stagingArea.StagingRoot : operationDirectory;
        bool replaced = replaceRoot;
        TrackingEnumerator enumerator = new([operationDirectory]);
        files.Set<IEnumerator<string>>("CreateDirectoryEnumerator", call =>
        {
            if (call.Path != stagingArea.StagingRoot)
                return SystemFileSystemPrimitives.Instance.CreateDirectoryEnumerator(call.Path!);
            replaced = true;
            return FileSystemReplayOutcome<IEnumerator<string>>.Complete(enumerator);
        });
        files.Set<FileSystemProbeResult>("Probe", call =>
        {
            call.Flags.ShouldBe(0);
            // Replacement is before the existing no-follow probe, not an atomic traversal guarantee.
            if (call.Path == linkPath && replaced)
            {
                return ScriptedFileSystemPrimitives.Entry(directoryLink
                    ? FileSystemEntryKind.DirectorySymbolicLink
                    : FileSystemEntryKind.SymbolicLink);
            }
            return SystemFileSystemPrimitives.Instance.Probe(call.Path!, followLinks: false);
        });

        await stagingArea.CleanupAbandonedAsync(
            coordinator, committer, TestContext.Current.CancellationToken);

        AssertSnapshotCalls(
            files, stagingArea.StagingRoot, replaceRoot ? 0 : 1,
            advances: replaceRoot ? 0 : 2, currents: replaceRoot ? 0 : 1, disposals: replaceRoot ? 0 : 1);
        enumerator.DisposeCount.ShouldBe(replaceRoot ? 0 : 1);
        if (replaceRoot)
        {
            files.Calls.ShouldHaveSingleItem().Operation.ShouldBe("Probe");
            files.Calls[0].Path.ShouldBe(stagingArea.StagingRoot);
        }
        else
        {
            AssertDiscoveryOrder(files, stagingArea.StagingRoot);
            files.Calls.Where(call => call.Operation == "Probe").Select(call => call.Path)
                .ShouldBe([stagingArea.StagingRoot, GetTransactionDirectory(), operationDirectory]);
        }
        files.Calls.ShouldNotContain(call => call.Path != null
            && call.Path.StartsWith(Path.Combine(_cacheRoot, "foreign"), StringComparison.Ordinal));
        File.ReadAllText(payload).ShouldBe("link target");
        Directory.Exists(operationDirectory).ShouldBeTrue();
        files.AsyncWaits.ShouldBeEmpty();
        files.Waits.ShouldBeEmpty();
        AssertNoOwnerOrDelete(files);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CleanupOwned_ParentPruningDoesNotRemoveConcurrentSibling(
        bool asynchronous,
        bool operationsParent)
    {
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_cacheRoot);
        FileSystemContext context = files.CreateContext(retries: 1);
        PackageCacheStagingArea stagingArea = new(_cacheRoot, context);
        string operationDirectory = stagingArea.GetOperationDirectory(Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operationDirectory);
        File.WriteAllText(Path.Combine(operationDirectory, "archive.tgz"), "owned");
        string parent = operationsParent
            ? Path.Combine(_cacheRoot, ".fhirpkg")
            : stagingArea.StagingRoot;
        string sibling = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        string siblingContent = Path.Combine(sibling, "concurrent.txt");
        int emptySnapshotDisposals = 0;
        files.Set<IEnumerator<string>>("CreateDirectoryEnumerator", call =>
        {
            FileSystemReplayOutcome<IEnumerator<string>> opened =
                SystemFileSystemPrimitives.Instance.CreateDirectoryEnumerator(call.Path!);
            if (call.Path != parent)
                return opened;

            return FileSystemReplayOutcome<IEnumerator<string>>.Complete(
                new AfterDisposeEnumerator(opened.Value, () =>
                {
                    // Populate the parent after its empty observation, before deletion.
                    emptySnapshotDisposals++;
                    Directory.CreateDirectory(sibling);
                    File.WriteAllText(siblingContent, "concurrent");
                }));
        });

        if (asynchronous)
            await stagingArea.CleanupOwnedAsync(operationDirectory);
        else
            stagingArea.CleanupOwned(operationDirectory);

        Directory.Exists(operationDirectory).ShouldBeFalse();
        Directory.Exists(parent).ShouldBeTrue();
        File.ReadAllText(siblingContent).ShouldBe("concurrent");
        emptySnapshotDisposals.ShouldBe(1);
        files.Calls.Count(call => call.Operation == "CreateDirectoryEnumerator" && call.Path == parent)
            .ShouldBe(1);
        files.Calls.Count(call => call.Operation == "DeleteEntry" && call.Path == parent)
            .ShouldBe(2);
        files.Calls.ShouldNotContain(call => call.Operation == "DeleteEntry"
            && (call.Path == sibling || call.Path == siblingContent));
        files.AsyncWaits.ShouldBe(asynchronous ? [10] : []);
        files.Waits.ShouldBe(asynchronous ? [] : [10]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot))
            Directory.Delete(_cacheRoot, recursive: true);

        GC.SuppressFinalize(this);
    }

    private PackageCacheStagingArea CreateAbandonedDiscovery(
        ScriptedFileSystemPrimitives files,
        int retries,
        out PackageCacheCoordinator coordinator,
        out PackageCacheCommitter committer)
    {
        FileSystemContext context = files.CreateContext(retries);
        PackageCacheStagingArea stagingArea = new(_cacheRoot, context);
        Directory.CreateDirectory(stagingArea.StagingRoot);
        Directory.CreateDirectory(GetTransactionDirectory());
        coordinator = new PackageCacheCoordinator(
            _cacheRoot, context, NullPackageCacheContentionObserver.Instance);
        committer = CreateCommitter(context, out PackageCacheJournalStore _);
        files.Calls.Clear();
        return stagingArea;
    }

    private static void ScriptMissingStagingSnapshots(
        ScriptedFileSystemPrimitives files,
        string stagingRoot,
        string? partialDirectory,
        List<TrackingEnumerator> enumerators,
        List<DirectoryNotFoundException> failures,
        Action<int>? onFailure = null,
        List<DirectoryNotFoundException>? disposalFailures = null)
    {
        files.Set<IEnumerator<string>>("CreateDirectoryEnumerator", call =>
        {
            if (call.Path != stagingRoot)
                return SystemFileSystemPrimitives.Instance.CreateDirectoryEnumerator(call.Path!);
            if (partialDirectory is null)
                return Missing<IEnumerator<string>>("EnumerateDirectory");

            DirectoryNotFoundException? secondary = null;
            if (disposalFailures is not null)
            {
                secondary = new DirectoryNotFoundException("Secondary disposal failure.");
                disposalFailures.Add(secondary);
            }
            TrackingEnumerator enumerator = new([partialDirectory], secondary);
            enumerators.Add(enumerator);
            files.Enqueue<bool>("MoveNext", advance =>
            {
                advance.Path.ShouldBe(stagingRoot);
                return SystemFileSystemPrimitives.Instance.MoveNext(enumerator, stagingRoot);
            });
            files.Enqueue<bool>("MoveNext", advance =>
            {
                advance.Path.ShouldBe(stagingRoot);
                return Missing<bool>("DirectoryEnumerator.MoveNext");
            });
            return FileSystemReplayOutcome<IEnumerator<string>>.Complete(enumerator);
        });

        FileSystemReplayOutcome<T> Missing<T>(string operation)
        {
            DirectoryNotFoundException failure = new("The staging root disappeared.");
            failures.Add(failure);
            FileSystemReplayOutcome<T> outcome = ScriptedFileSystemPrimitives.Fail<T>(
                failure, operation, stagingRoot, nativeError: 2);
            onFailure?.Invoke(failures.Count);
            return outcome;
        }
    }

    private void AssertDiscoveryOrder(ScriptedFileSystemPrimitives files, string stagingRoot)
    {
        files.Calls[0].Operation.ShouldBe("Probe");
        files.Calls[0].Path.ShouldBe(stagingRoot);
        string transactionDirectory = GetTransactionDirectory();
        int journalSnapshot = files.Calls.FindIndex(call =>
            call.Operation == "CreateDirectoryEnumerator" && call.Path == transactionDirectory);
        int journalDisposal = files.Calls.FindIndex(call =>
            call.Operation == "DisposeEnumerator" && call.Path == transactionDirectory);
        int stagingSnapshot = files.Calls.FindIndex(call =>
            call.Operation == "CreateDirectoryEnumerator" && call.Path == stagingRoot);
        journalSnapshot.ShouldBeGreaterThan(0);
        journalDisposal.ShouldBeGreaterThan(journalSnapshot);
        stagingSnapshot.ShouldBeGreaterThan(journalDisposal);
        AssertSnapshotCalls(files, transactionDirectory, 1, advances: 1, currents: 0, disposals: 1);
    }

    private static void AssertSnapshotCalls(
        ScriptedFileSystemPrimitives files,
        string path,
        int creations,
        int advances,
        int currents,
        int disposals)
    {
        files.Calls.Count(call => call.Operation == "CreateDirectoryEnumerator" && call.Path == path)
            .ShouldBe(creations);
        files.Calls.Count(call => call.Operation == "MoveNext" && call.Path == path).ShouldBe(advances);
        files.Calls.Count(call => call.Operation == "GetCurrent" && call.Path == path).ShouldBe(currents);
        files.Calls.Count(call => call.Operation == "DisposeEnumerator" && call.Path == path).ShouldBe(disposals);
    }

    private static void AssertTerminalSnapshotFailure(
        Exception exception,
        string operation,
        string path,
        int retries,
        int? nativeError,
        FileSystemFailureTermination termination = FileSystemFailureTermination.Exhausted)
    {
        FileSystemFailureContext.TryGetTerminal(
            exception, out FileSystemFailureContext? failure).ShouldBeTrue();
        failure!.Operation.ShouldBe(operation);
        failure.Path.ShouldBe(path);
        failure.DestinationPath.ShouldBeNull();
        failure.Phase.ShouldBe("SnapshotDirectory");
        failure.AttemptCount.ShouldBe((long)retries + 1);
        failure.RetriesUsed.ShouldBe((long)retries);
        failure.Termination.ShouldBe(termination);
        failure.NativeErrorCode.ShouldBe(nativeError);
        failure.NativeFlags.ShouldBeNull();
    }

    private void AssertNoOwnerOrDelete(ScriptedFileSystemPrimitives files)
    {
        files.Count("OpenFile").ShouldBe(0);
        files.Count("TryLock").ShouldBe(0);
        files.Count("Unlock").ShouldBe(0);
        files.Count("DeleteEntry").ShouldBe(0);
        files.Calls.ShouldNotContain(call => call.Operation == "Probe" && call.Flags != 0);
        PackageCacheCoordinator.GetProcessLockEntryCount(_cacheRoot).ShouldBe(0);
    }

    private string GetTransactionDirectory() =>
        Path.Combine(_cacheRoot, ".fhirpkg", "transactions");

    private PackageCacheCommitter CreateCommitter(
        FileSystemContext context,
        out PackageCacheJournalStore journalStore)
    {
        SystemPackageCacheFileOperations fileOperations = new(context);
        PackageCacheValidator validator = new(_cacheRoot, context);
        PackageCacheMetadataStore metadataStore = new(
            _cacheRoot, context, fileOperations, NullPackageCacheFaultObserver.Instance);
        journalStore = new PackageCacheJournalStore(
            _cacheRoot, context, fileOperations, NullPackageCacheFaultObserver.Instance);
        return new PackageCacheCommitter(
            _cacheRoot, context, validator, metadataStore, journalStore,
            fileOperations, NullPackageCacheFaultObserver.Instance);
    }

    private static async Task<string> WriteJournalAsync(
        PackageCacheJournalStore journalStore,
        string operationId)
    {
        PackageReference reference = new("staging.package", "1.0.0");
        PackageCacheKey cacheKey = PackageCacheKey.Create(reference);
        PackageCacheTransactionJournal journal = new()
        {
            OperationId = operationId,
            Operation = PackageCacheTransactionOperation.Install,
            State = PackageCacheTransactionState.Prepared,
            CanonicalIdentity = cacheKey.CanonicalIdentity,
            PackageName = reference.Name,
            PackageVersion = reference.Version!,
            TargetRelativePath = cacheKey.RelativePath.Replace('\\', '/'),
            StagingRelativePath = $".fhirpkg/staging/{operationId}/expanded",
            OriginalState = PackageCacheInspectionState.Missing,
            OriginalArtifactKind = PackageCacheArtifactKind.Missing,
            IntendedMetadata = new CacheMetadataEntry
            {
                DownloadDateTime = DateTime.UtcNow,
                ContentGeneration = operationId
            },
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await journalStore.WriteAsync(journal, TestContext.Current.CancellationToken);
        return journalStore.GetJournalPath(cacheKey);
    }

    private string GetOwnerLockPath(string operationId) =>
        Path.Combine(_cacheRoot, ".fhirpkg", "locks", "operations", $"{operationId}.lock");

    private static FileSystemReplayOutcome<FileStream> OpenOwnerFile(string path) =>
        SystemFileSystemPrimitives.Instance.OpenFile(
            path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            OperatingSystem.IsMacOS() ? FileShare.None : FileShare.ReadWrite, 1, FileOptions.None);

    private sealed class TrackingEnumerator : IEnumerator<string>
    {
        private readonly IEnumerator<string> _inner;
        private readonly Exception? _disposalFailure;

        internal TrackingEnumerator(IEnumerable<string> entries, Exception? disposalFailure = null)
        {
            _inner = entries.GetEnumerator();
            _disposalFailure = disposalFailure;
        }

        internal int DisposeCount { get; private set; }

        public string Current => _inner.Current;

        object System.Collections.IEnumerator.Current => Current;

        public bool MoveNext() => _inner.MoveNext();

        public void Reset() => throw new NotSupportedException();

        public void Dispose()
        {
            DisposeCount++;
            _inner.Dispose();
            if (_disposalFailure is not null)
                throw _disposalFailure;
        }
    }

    private sealed class AfterDisposeEnumerator : IEnumerator<string>
    {
        private readonly IEnumerator<string> _inner;
        private readonly Action _afterDispose;

        internal AfterDisposeEnumerator(IEnumerator<string> inner, Action afterDispose)
        {
            _inner = inner;
            _afterDispose = afterDispose;
        }

        public string Current => _inner.Current;

        object System.Collections.IEnumerator.Current => Current;

        public bool MoveNext() => _inner.MoveNext();

        public void Reset() => throw new NotSupportedException();

        public void Dispose()
        {
            _inner.Dispose();
            _afterDispose();
        }
    }
}
