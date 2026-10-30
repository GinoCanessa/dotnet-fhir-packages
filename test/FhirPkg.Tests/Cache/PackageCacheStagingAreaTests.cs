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
