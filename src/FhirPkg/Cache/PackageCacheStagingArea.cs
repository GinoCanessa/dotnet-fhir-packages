// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.IO;

namespace FhirPkg.Cache;

/// <summary>Cache-local acquisition paths and owner-gated staging cleanup.</summary>
internal sealed class PackageCacheStagingArea
{
    private const string OperationsDirectoryName = ".fhirpkg";
    private const string StagingDirectoryName = "staging";
    private const string ArchiveFileName = "archive.tgz";

    private readonly string _cacheRoot;
    private readonly FileSystemContext _context;

    internal PackageCacheStagingArea(string cacheRoot, FileSystemContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentNullException.ThrowIfNull(context);
        _cacheRoot = cacheRoot;
        _context = context;
    }

    internal FileSystemContext Context => _context;

    internal string StagingRoot => Path.Combine(
        _cacheRoot,
        OperationsDirectoryName,
        StagingDirectoryName);

    internal string GetOperationDirectory(string operationId) =>
        Path.Combine(StagingRoot, operationId);

    internal string GetArchivePath(string operationId) =>
        Path.Combine(GetOperationDirectory(operationId), ArchiveFileName);

    internal async Task CleanupAbandonedAsync(
        PackageCacheCoordinator coordinator,
        PackageCacheCommitter committer,
        CancellationToken cancellationToken,
        string? protectedOperationId = null)
    {
        string stagingRoot = StagingRoot;
        if ((await _context.FileSystem.ProbeAsync(stagingRoot, cancellationToken)
                .ConfigureAwait(false)).Kind != FileSystemEntryKind.Directory)
            return;

        IReadOnlyList<PackageCacheTransactionJournal> journals =
            await committer.GetPendingTransactionsAsync(cancellationToken)
                .ConfigureAwait(false);
        HashSet<string> journalOperationIds = journals
            .Select(journal => journal.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        string[] operationDirectories =
            await _context.FileSystem.SnapshotDirectoryAsync(
                    stagingRoot,
                    cancellationToken)
                .ConfigureAwait(false);
        Array.Sort(operationDirectories, StringComparer.Ordinal);
        foreach (string operationDirectory in operationDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string operationId = Path.GetFileName(operationDirectory);
            if (journalOperationIds.Contains(operationId)
                || string.Equals(
                    operationId,
                    protectedOperationId,
                    StringComparison.Ordinal)
                || !Guid.TryParseExact(operationId, "N", out Guid _)
                || (await _context.FileSystem.ProbeAsync(
                        operationDirectory,
                        cancellationToken)
                    .ConfigureAwait(false)).Kind != FileSystemEntryKind.Directory)
            {
                continue;
            }

            PackageCacheLease? ownerLease =
                await coordinator.TryAcquireOperationOwnerAsync(operationId, cancellationToken)
                    .ConfigureAwait(false);
            if (ownerLease is null)
                continue;

            await using (ownerLease.ConfigureAwait(false))
            {
                await CleanupOwnedAsync(operationDirectory).ConfigureAwait(false);
            }
        }
    }

    internal void CleanupOwned(string operationDirectory)
    {
        ResilientFileSystem fileSystem = _context.FileSystem;
        try
        {
            if (fileSystem.Probe(operationDirectory).Kind == FileSystemEntryKind.Directory)
            {
                fileSystem.Delete(
                    operationDirectory,
                    FileSystemEntryKind.Directory,
                    recursive: true,
                    missingAllowed: true);
            }

            string? stagingRoot = Path.GetDirectoryName(operationDirectory);
            if (stagingRoot is not null
                && fileSystem.Probe(stagingRoot).Kind == FileSystemEntryKind.Directory
                && fileSystem.SnapshotDirectory(stagingRoot).Length == 0)
            {
                fileSystem.Delete(
                    stagingRoot,
                    FileSystemEntryKind.Directory,
                    recursive: false,
                    missingAllowed: true);
            }

            string? operationsRoot = stagingRoot is null
                ? null
                : Path.GetDirectoryName(stagingRoot);
            if (operationsRoot is not null
                && fileSystem.Probe(operationsRoot).Kind == FileSystemEntryKind.Directory
                && fileSystem.SnapshotDirectory(operationsRoot).Length == 0)
            {
                fileSystem.Delete(
                    operationsRoot,
                    FileSystemEntryKind.Directory,
                    recursive: false,
                    missingAllowed: true);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup must not replace the primary installation outcome.
        }
        catch (IOException)
        {
            // Cleanup must not replace the primary installation outcome.
        }
    }

    internal async ValueTask CleanupOwnedAsync(string operationDirectory)
    {
        ResilientFileSystem fileSystem = _context.FileSystem;
        try
        {
            // Cleanup is its own bounded operation and never observes the primary
            // operation's cancellation.
            if ((await fileSystem.ProbeAsync(operationDirectory).ConfigureAwait(false)).Kind
                == FileSystemEntryKind.Directory)
            {
                await fileSystem.DeleteAsync(
                        operationDirectory,
                        FileSystemEntryKind.Directory,
                        recursive: true,
                        missingAllowed: true)
                    .ConfigureAwait(false);
            }

            string? stagingRoot = Path.GetDirectoryName(operationDirectory);
            if (stagingRoot is not null
                && await IsEmptyDirectoryAsync(fileSystem, stagingRoot).ConfigureAwait(false))
            {
                await fileSystem.DeleteAsync(
                        stagingRoot,
                        FileSystemEntryKind.Directory,
                        recursive: false,
                        missingAllowed: true)
                    .ConfigureAwait(false);
            }

            string? operationsRoot = stagingRoot is null
                ? null
                : Path.GetDirectoryName(stagingRoot);
            if (operationsRoot is not null
                && await IsEmptyDirectoryAsync(fileSystem, operationsRoot).ConfigureAwait(false))
            {
                await fileSystem.DeleteAsync(
                        operationsRoot,
                        FileSystemEntryKind.Directory,
                        recursive: false,
                        missingAllowed: true)
                    .ConfigureAwait(false);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup must not replace the primary installation outcome.
        }
        catch (IOException)
        {
            // Cleanup must not replace the primary installation outcome.
        }
    }

    private static async ValueTask<bool> IsEmptyDirectoryAsync(
        ResilientFileSystem fileSystem,
        string path) =>
        (await fileSystem.ProbeAsync(path).ConfigureAwait(false)).Kind
            == FileSystemEntryKind.Directory
        && (await fileSystem.SnapshotDirectoryAsync(path).ConfigureAwait(false)).Length == 0;
}
