// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Installation;

namespace FhirPkg.Cache;

internal sealed class PackageCacheCommitter
{
    private readonly string _cacheRoot;
    private readonly PackageCacheValidator _validator;
    private readonly PackageCacheMetadataStore _metadataStore;
    private readonly PackageCacheJournalStore _journalStore;
    private readonly IPackageCacheFileOperations _fileOperations;
    private readonly IPackageCacheFaultObserver _faultObserver;

    internal PackageCacheCommitter(
        string cacheRoot,
        PackageCacheValidator validator,
        PackageCacheMetadataStore metadataStore,
        PackageCacheJournalStore journalStore,
        IPackageCacheFileOperations fileOperations,
        IPackageCacheFaultObserver faultObserver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(metadataStore);
        ArgumentNullException.ThrowIfNull(journalStore);
        ArgumentNullException.ThrowIfNull(fileOperations);
        ArgumentNullException.ThrowIfNull(faultObserver);

        _cacheRoot = Path.GetFullPath(cacheRoot);
        _validator = validator;
        _metadataStore = metadataStore;
        _journalStore = journalStore;
        _fileOperations = fileOperations;
        _faultObserver = faultObserver;
    }

    internal async Task CommitInstallAsync(
        PackageCacheKey cacheKey,
        PackageCacheInspection originalInspection,
        string stagingDirectory,
        string operationId,
        CacheMetadataEntry intendedMetadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        ArgumentNullException.ThrowIfNull(originalInspection);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(intendedMetadata);

        if (!_fileOperations.DirectoryExists(stagingDirectory))
        {
            throw CommitFailure(
                cacheKey,
                "The validated staging directory is missing.");
        }

        string targetPath = cacheKey.GetPackageDirectoryPath(_cacheRoot);
        string stagingRelativePath = GetRelativePath(stagingDirectory);
        PackageCacheArtifactKind artifactKind =
            _fileOperations.GetArtifactKind(targetPath);
        string? artifactRelativePath = artifactKind
            == PackageCacheArtifactKind.Missing
                ? null
                : GetArtifactRelativePath(
                    cacheKey,
                    operationId,
                    originalInspection.State);
        string? artifactPath = artifactRelativePath is null
            ? null
            : _journalStore.ResolveRelativePath(
                artifactRelativePath,
                originalInspection.State
                    == PackageCacheInspectionState.Corrupt
                        ? ".fhirpkg/quarantine"
                        : ".fhirpkg/backup");
        CacheMetadataEntry? priorMetadata;
        try
        {
            priorMetadata = await _metadataStore.GetEntryAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw CommitFailure(cacheKey, exception);
        }
        catch (IOException exception)
        {
            throw CommitFailure(cacheKey, exception);
        }

        PackageCacheTransactionJournal journal = new()
        {
            OperationId = operationId,
            Operation = PackageCacheTransactionOperation.Install,
            State = PackageCacheTransactionState.Prepared,
            CanonicalIdentity = cacheKey.CanonicalIdentity,
            PackageName = cacheKey.DisplayReference.Name,
            PackageVersion = cacheKey.DisplayReference.Version!,
            PackageScope = cacheKey.DisplayReference.Scope,
            TargetRelativePath = cacheKey.RelativePath.Replace('\\', '/'),
            StagingRelativePath = stagingRelativePath,
            ArtifactRelativePath = artifactRelativePath,
            OriginalState = originalInspection.State,
            OriginalArtifactKind = artifactKind,
            PriorMetadata = priorMetadata,
            IntendedMetadata = intendedMetadata,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        try
        {
            await _journalStore.WriteAsync(journal, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PackageCacheInjectedFaultException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            TryDeleteJournal(journal);
            throw CommitFailure(cacheKey, exception);
        }
        catch (IOException exception)
        {
            TryDeleteJournal(journal);
            throw CommitFailure(cacheKey, exception);
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            TryDeleteJournal(journal);
            throw;
        }

        bool destructiveWorkStarted = false;
        try
        {
            string? targetParent = Path.GetDirectoryName(targetPath);
            if (targetParent is not null)
                _fileOperations.CreateDirectory(targetParent);

            if (artifactPath is not null)
            {
                string? artifactParent = Path.GetDirectoryName(artifactPath);
                if (artifactParent is not null)
                    _fileOperations.CreateDirectory(artifactParent);

                destructiveWorkStarted = true;
                _fileOperations.MoveArtifact(
                    targetPath,
                    artifactPath,
                    artifactKind);
                await ObserveAsync(
                        journal,
                        PackageCacheFaultPoint.OriginalRenamed)
                    .ConfigureAwait(false);
            }
            else
            {
                destructiveWorkStarted = true;
            }

            journal = journal with
            {
                State = PackageCacheTransactionState.OriginalMoved
            };
            await _journalStore.WriteAsync(journal, CancellationToken.None)
                .ConfigureAwait(false);

            _fileOperations.MoveDirectory(stagingDirectory, targetPath);
            await ObserveAsync(
                    journal,
                    PackageCacheFaultPoint.ReplacementPromoted)
                .ConfigureAwait(false);

            PackageCacheInspection promotedInspection =
                await _validator.InspectAsync(
                        cacheKey,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            if (promotedInspection.State != PackageCacheInspectionState.Valid)
            {
                throw CommitFailure(
                    cacheKey,
                    "The promoted package failed cache validation.");
            }

            journal = journal with
            {
                State = PackageCacheTransactionState.NewPromoted
            };
            await _journalStore.WriteAsync(journal, CancellationToken.None)
                .ConfigureAwait(false);

            await _metadataStore.SetEntryAsync(
                    cacheKey,
                    intendedMetadata,
                    new PackageCacheMetadataMutation(
                        operationId,
                        PackageCacheTransactionState.MetadataCommitted,
                        cacheKey.CanonicalIdentity),
                    CancellationToken.None)
                .ConfigureAwait(false);

            journal = journal with
            {
                State = PackageCacheTransactionState.MetadataCommitted
            };
            await _journalStore.WriteAsync(journal, CancellationToken.None)
                .ConfigureAwait(false);
            journal = journal with
            {
                State = PackageCacheTransactionState.Completed
            };
            await _journalStore.WriteAsync(journal, CancellationToken.None)
                .ConfigureAwait(false);

            if (!await TryFinalizeForwardAsync(journal).ConfigureAwait(false))
            {
                // The completed journal remains durable for the next recovery.
                return;
            }
        }
        catch (PackageCacheInjectedFaultException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            if (destructiveWorkStarted)
                await RollbackAfterFailureAsync(journal).ConfigureAwait(false);
            else
                TryDeleteJournal(journal);
            throw CommitFailure(cacheKey, exception);
        }
        catch (IOException exception)
        {
            if (destructiveWorkStarted)
                await RollbackAfterFailureAsync(journal).ConfigureAwait(false);
            else
                TryDeleteJournal(journal);
            throw CommitFailure(cacheKey, exception);
        }
        catch (PackageInstallException exception)
        {
            if (destructiveWorkStarted)
                await RollbackAfterFailureAsync(journal).ConfigureAwait(false);
            else
                TryDeleteJournal(journal);
            if (exception.ErrorCode == PackageInstallErrorCode.CommitFailed)
                throw;
            throw CommitFailure(cacheKey, exception);
        }
    }

    internal async Task<bool> RemoveAsync(
        PackageCacheKey cacheKey,
        PackageCacheInspection originalInspection,
        string operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        ArgumentNullException.ThrowIfNull(originalInspection);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        if (originalInspection.State == PackageCacheInspectionState.Missing)
            return false;

        string targetPath = cacheKey.GetPackageDirectoryPath(_cacheRoot);
        PackageCacheArtifactKind artifactKind =
            _fileOperations.GetArtifactKind(targetPath);
        if (artifactKind == PackageCacheArtifactKind.Missing)
            return false;

        string artifactRelativePath = GetArtifactRelativePath(
            cacheKey,
            operationId,
            originalInspection.State);
        string artifactPath = _journalStore.ResolveRelativePath(
            artifactRelativePath,
            originalInspection.State == PackageCacheInspectionState.Corrupt
                ? ".fhirpkg/quarantine"
                : ".fhirpkg/backup");
        CacheMetadataEntry? priorMetadata;
        try
        {
            priorMetadata = await _metadataStore.GetEntryAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw CommitFailure(cacheKey, exception);
        }
        catch (IOException exception)
        {
            throw CommitFailure(cacheKey, exception);
        }
        PackageCacheTransactionJournal journal = new()
        {
            OperationId = operationId,
            Operation = PackageCacheTransactionOperation.Remove,
            State = PackageCacheTransactionState.Prepared,
            CanonicalIdentity = cacheKey.CanonicalIdentity,
            PackageName = cacheKey.DisplayReference.Name,
            PackageVersion = cacheKey.DisplayReference.Version!,
            PackageScope = cacheKey.DisplayReference.Scope,
            TargetRelativePath = cacheKey.RelativePath.Replace('\\', '/'),
            ArtifactRelativePath = artifactRelativePath,
            OriginalState = originalInspection.State,
            OriginalArtifactKind = artifactKind,
            PriorMetadata = priorMetadata,
            IntendedMetadata = null,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        try
        {
            await _journalStore.WriteAsync(journal, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PackageCacheInjectedFaultException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            TryDeleteJournal(journal);
            throw CommitFailure(cacheKey, exception);
        }
        catch (IOException exception)
        {
            TryDeleteJournal(journal);
            throw CommitFailure(cacheKey, exception);
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            TryDeleteJournal(journal);
            throw;
        }

        try
        {
            string? artifactParent = Path.GetDirectoryName(artifactPath);
            if (artifactParent is not null)
                _fileOperations.CreateDirectory(artifactParent);
            _fileOperations.MoveArtifact(
                targetPath,
                artifactPath,
                artifactKind);
            await ObserveAsync(
                    journal,
                    PackageCacheFaultPoint.OriginalRenamed)
                .ConfigureAwait(false);

            journal = journal with
            {
                State = PackageCacheTransactionState.OriginalMoved
            };
            await _journalStore.WriteAsync(journal, CancellationToken.None)
                .ConfigureAwait(false);
            await _metadataStore.SetEntryAsync(
                    cacheKey,
                    entry: null,
                    new PackageCacheMetadataMutation(
                        operationId,
                        PackageCacheTransactionState.MetadataCommitted,
                        cacheKey.CanonicalIdentity),
                    CancellationToken.None)
                .ConfigureAwait(false);

            journal = journal with
            {
                State = PackageCacheTransactionState.MetadataCommitted
            };
            await _journalStore.WriteAsync(journal, CancellationToken.None)
                .ConfigureAwait(false);
            journal = journal with
            {
                State = PackageCacheTransactionState.Completed
            };
            await _journalStore.WriteAsync(journal, CancellationToken.None)
                .ConfigureAwait(false);
            _ = await TryFinalizeForwardAsync(journal).ConfigureAwait(false);
            return true;
        }
        catch (PackageCacheInjectedFaultException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            await RollbackAfterFailureAsync(journal).ConfigureAwait(false);
            throw CommitFailure(cacheKey, exception);
        }
        catch (IOException exception)
        {
            await RollbackAfterFailureAsync(journal).ConfigureAwait(false);
            throw CommitFailure(cacheKey, exception);
        }
        catch (PackageInstallException exception)
        {
            await RollbackAfterFailureAsync(journal).ConfigureAwait(false);
            if (exception.ErrorCode == PackageInstallErrorCode.CommitFailed)
                throw;
            throw CommitFailure(cacheKey, exception);
        }
    }

    internal async Task RecoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PackageCacheTransactionJournal> journals =
            await _journalStore.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false);
        foreach (PackageCacheTransactionJournal journal in journals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RecoverAsync(journal).ConfigureAwait(false);
        }
    }

    internal async Task RecoverAsync(
        PackageCacheKey cacheKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        PackageCacheTransactionJournal? journal =
            await _journalStore.ReadAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
        if (journal is not null)
            await RecoverAsync(journal).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<PackageCacheTransactionJournal>>
        GetPendingTransactionsAsync(
            CancellationToken cancellationToken) =>
        await _journalStore.ReadAllAsync(cancellationToken)
            .ConfigureAwait(false);

    internal async Task<bool> HasPendingTransactionAsync(
        PackageCacheKey cacheKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        PackageCacheTransactionJournal? journal =
            await _journalStore.ReadAsync(
                    cacheKey,
                    cancellationToken)
                .ConfigureAwait(false);
        return journal is not null;
    }

    private async Task RecoverAsync(PackageCacheTransactionJournal journal)
    {
        PackageCacheKey cacheKey = journal.GetCacheKey();
        PackageCacheInspection targetInspection =
            await _validator.InspectAsync(
                    cacheKey,
                    CancellationToken.None)
                .ConfigureAwait(false);

        if (journal.State
            is PackageCacheTransactionState.RollbackStarted
                or PackageCacheTransactionState.RolledBack
                or PackageCacheTransactionState.Prepared)
        {
            await RollbackAsync(journal).ConfigureAwait(false);
            return;
        }

        if (journal.Operation == PackageCacheTransactionOperation.Remove)
        {
            if (targetInspection.State == PackageCacheInspectionState.Missing
                && journal.State
                    is PackageCacheTransactionState.OriginalMoved
                        or PackageCacheTransactionState.MetadataCommitted
                        or PackageCacheTransactionState.Completed)
            {
                await CompleteForwardAsync(journal).ConfigureAwait(false);
                return;
            }

            await RollbackAsync(journal).ConfigureAwait(false);
            return;
        }

        if (targetInspection.State == PackageCacheInspectionState.Valid
            && journal.State
                is PackageCacheTransactionState.NewPromoted
                    or PackageCacheTransactionState.MetadataCommitted
                    or PackageCacheTransactionState.Completed)
        {
            await CompleteForwardAsync(journal).ConfigureAwait(false);
            return;
        }

        await RollbackAsync(journal).ConfigureAwait(false);
    }

    private async Task CompleteForwardAsync(
        PackageCacheTransactionJournal journal)
    {
        PackageCacheKey cacheKey = journal.GetCacheKey();
        if (journal.Operation == PackageCacheTransactionOperation.Install)
        {
            PackageCacheInspection inspection =
                await _validator.InspectAsync(
                        cacheKey,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            if (inspection.State != PackageCacheInspectionState.Valid)
            {
                throw RecoveryFailure(
                    cacheKey,
                    "The promoted package is not valid during recovery.");
            }
        }
        else
        {
            PackageCacheInspection inspection =
                await _validator.InspectAsync(
                        cacheKey,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            if (inspection.State != PackageCacheInspectionState.Missing)
            {
                throw RecoveryFailure(
                    cacheKey,
                    "A removed package target is still present during recovery.");
            }
        }

        await _metadataStore.SetEntryAsync(
                cacheKey,
                journal.IntendedMetadata,
                mutation: null,
                CancellationToken.None)
            .ConfigureAwait(false);
        PackageCacheTransactionJournal completed = journal;
        if (journal.State != PackageCacheTransactionState.Completed)
        {
            completed = journal with
            {
                State = PackageCacheTransactionState.Completed
            };
            await _journalStore.WriteAsync(
                    completed,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        _ = await TryFinalizeForwardAsync(completed).ConfigureAwait(false);
    }

    private async Task<bool> TryFinalizeForwardAsync(
        PackageCacheTransactionJournal journal)
    {
        PackageCacheKey cacheKey = journal.GetCacheKey();
        if (journal.Operation == PackageCacheTransactionOperation.Install)
        {
            PackageCacheInspection inspection =
                await _validator.InspectAsync(
                        cacheKey,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            bool metadataMatches = await _metadataStore.EntryMatchesAsync(
                    cacheKey,
                    journal.IntendedMetadata,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (inspection.State != PackageCacheInspectionState.Valid
                || !metadataMatches)
            {
                return false;
            }
        }
        else
        {
            PackageCacheInspection inspection =
                await _validator.InspectAsync(
                        cacheKey,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            bool metadataMatches = await _metadataStore.EntryMatchesAsync(
                    cacheKey,
                    expected: null,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (inspection.State != PackageCacheInspectionState.Missing
                || !metadataMatches)
            {
                return false;
            }
        }

        string? artifactPath = GetArtifactPath(journal);
        try
        {
            if (artifactPath is not null
                && _fileOperations.ArtifactExists(
                    artifactPath,
                    journal.OriginalArtifactKind))
            {
                _fileOperations.DeleteArtifact(
                    artifactPath,
                    journal.OriginalArtifactKind);
                await ObserveAsync(
                        journal,
                        PackageCacheFaultPoint.ArtifactRemoved)
                    .ConfigureAwait(false);
            }

            if (artifactPath is not null)
                DeleteEmptyContainer(Path.GetDirectoryName(artifactPath));

            DeleteStagingIfPresent(journal);
            _journalStore.Delete(journal);
            return true;
        }
        catch (PackageCacheInjectedFaultException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task RollbackAfterFailureAsync(
        PackageCacheTransactionJournal journal)
    {
        try
        {
            await RollbackAsync(journal).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the durable journal and sole artifact for later recovery.
        }
        catch (IOException)
        {
            // Keep the durable journal and sole artifact for later recovery.
        }
        catch (PackageInstallException)
        {
            // Keep the durable journal and sole artifact for later recovery.
        }
    }

    private async Task RollbackAsync(
        PackageCacheTransactionJournal journal)
    {
        PackageCacheKey cacheKey = journal.GetCacheKey();
        string targetPath = cacheKey.GetPackageDirectoryPath(_cacheRoot);
        string? artifactPath = GetArtifactPath(journal);

        if (journal.State != PackageCacheTransactionState.RollbackStarted
            && journal.State != PackageCacheTransactionState.RolledBack)
        {
            journal = journal with
            {
                State = PackageCacheTransactionState.RollbackStarted
            };
            await _journalStore.WriteAsync(journal, CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (journal.State != PackageCacheTransactionState.RolledBack)
        {
            bool artifactExists = artifactPath is not null
                && _fileOperations.ArtifactExists(
                    artifactPath,
                    journal.OriginalArtifactKind);
            PackageCacheArtifactKind targetKind =
                _fileOperations.GetArtifactKind(targetPath);
            if (journal.OriginalState
                == PackageCacheInspectionState.Missing)
            {
                _fileOperations.DeleteArtifact(targetPath, targetKind);
            }
            else if (artifactExists)
            {
                _fileOperations.DeleteArtifact(targetPath, targetKind);
                _fileOperations.MoveArtifact(
                    artifactPath!,
                    targetPath,
                    journal.OriginalArtifactKind);
                DeleteEmptyContainer(Path.GetDirectoryName(artifactPath));
            }
            else if (targetKind == PackageCacheArtifactKind.Missing)
            {
                throw RecoveryFailure(
                    cacheKey,
                    "The prior package artifact is unavailable for rollback.");
            }

            await _metadataStore.SetEntryAsync(
                    cacheKey,
                    journal.PriorMetadata,
                    mutation: null,
                    CancellationToken.None)
                .ConfigureAwait(false);

            journal = journal with
            {
                State = PackageCacheTransactionState.RolledBack
            };
            await _journalStore.WriteAsync(journal, CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (!await PriorStateMatchesAsync(journal).ConfigureAwait(false))
        {
            throw RecoveryFailure(
                cacheKey,
                "The prior package state could not be verified after rollback.");
        }

        if (artifactPath is not null
            && _fileOperations.ArtifactExists(
                artifactPath,
                journal.OriginalArtifactKind))
        {
            _fileOperations.DeleteArtifact(
                artifactPath,
                journal.OriginalArtifactKind);
        }

        if (artifactPath is not null)
            DeleteEmptyContainer(Path.GetDirectoryName(artifactPath));

        CompleteRolledBack(journal);
    }

    private async Task<bool> PriorStateMatchesAsync(
        PackageCacheTransactionJournal journal)
    {
        PackageCacheKey cacheKey = journal.GetCacheKey();
        PackageCacheInspection inspection =
            await _validator.InspectAsync(
                    cacheKey,
                    CancellationToken.None)
                .ConfigureAwait(false);
        bool contentMatches = journal.OriginalState switch
        {
            PackageCacheInspectionState.Missing =>
                inspection.State == PackageCacheInspectionState.Missing,
            PackageCacheInspectionState.Valid =>
                inspection.State == PackageCacheInspectionState.Valid,
            PackageCacheInspectionState.Corrupt =>
                inspection.State == PackageCacheInspectionState.Corrupt,
            _ => false
        };
        if (!contentMatches)
            return false;

        return await _metadataStore.EntryMatchesAsync(
                cacheKey,
                journal.PriorMetadata,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private void CompleteRolledBack(PackageCacheTransactionJournal journal)
    {
        DeleteStagingIfPresent(journal);
        TryDeleteJournal(journal);
    }

    private void TryDeleteJournal(PackageCacheTransactionJournal journal)
    {
        try
        {
            _journalStore.Delete(journal);
        }
        catch (UnauthorizedAccessException)
        {
            // A retained journal is safe and will be retried during recovery.
        }
        catch (IOException)
        {
            // A retained journal is safe and will be retried during recovery.
        }
    }

    private void DeleteStagingIfPresent(
        PackageCacheTransactionJournal journal)
    {
        if (journal.StagingRelativePath is null)
            return;

        string stagingPath = _journalStore.ResolveRelativePath(
            journal.StagingRelativePath,
            ".fhirpkg/staging");
        string operationPath = Path.GetDirectoryName(stagingPath)
            ?? throw PackageCacheTransactionJournal.InvalidJournal(
                "The staging path has no operation directory.");
        if (_fileOperations.DirectoryExists(operationPath))
        {
            _fileOperations.DeleteDirectory(
                operationPath,
                recursive: true);
        }

        DeleteEmptyContainer(Path.GetDirectoryName(operationPath));
    }

    private static void DeleteEmptyContainer(string? path)
    {
        if (path is null)
            return;

        try
        {
            if (Directory.Exists(path)
                && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Empty-container cleanup is not part of the authoritative state.
        }
        catch (IOException)
        {
            // Empty-container cleanup is not part of the authoritative state.
        }
    }

    private string? GetArtifactPath(
        PackageCacheTransactionJournal journal)
    {
        if (journal.ArtifactRelativePath is null)
            return null;

        return _journalStore.ResolveRelativePath(
            journal.ArtifactRelativePath,
            journal.OriginalState == PackageCacheInspectionState.Corrupt
                ? ".fhirpkg/quarantine"
                : ".fhirpkg/backup");
    }

    private string GetArtifactRelativePath(
        PackageCacheKey cacheKey,
        string operationId,
        PackageCacheInspectionState originalState)
    {
        string container = originalState == PackageCacheInspectionState.Corrupt
            ? "quarantine"
            : "backup";
        return $".fhirpkg/{container}/{cacheKey.LockHash}-{operationId}";
    }

    private string GetRelativePath(string fullPath)
    {
        string relativePath = Path.GetRelativePath(
            _cacheRoot,
            Path.GetFullPath(fullPath));
        if (Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw PackageCacheTransactionJournal.InvalidJournal(
                "The staging path is outside the cache root.");
        }

        return relativePath.Replace('\\', '/');
    }

    private async ValueTask ObserveAsync(
        PackageCacheTransactionJournal journal,
        PackageCacheFaultPoint point)
    {
        await _faultObserver.OnEventAsync(
                new PackageCacheFaultEvent(
                    point,
                    journal.OperationId,
                    journal.State,
                    journal.CanonicalIdentity),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static PackageInstallException CommitFailure(
        PackageCacheKey cacheKey,
        Exception exception) =>
        new(
            PackageInstallErrorCode.CommitFailed,
            PackageInstallStage.Commit,
            $"Package '{cacheKey.DisplayReference.FhirDirective}' could not be committed.",
            cacheKey.DisplayReference.FhirDirective,
            exception);

    private static PackageInstallException CommitFailure(
        PackageCacheKey cacheKey,
        string message) =>
        new(
            PackageInstallErrorCode.CommitFailed,
            PackageInstallStage.Commit,
            message,
            cacheKey.DisplayReference.FhirDirective);

    private static PackageInstallException RecoveryFailure(
        PackageCacheKey cacheKey,
        string message) =>
        new(
            PackageInstallErrorCode.CoordinationFailed,
            PackageInstallStage.Coordination,
            message,
            cacheKey.DisplayReference.FhirDirective);
}
