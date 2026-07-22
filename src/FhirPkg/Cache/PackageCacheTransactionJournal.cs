// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Utilities;

namespace FhirPkg.Cache;

internal enum PackageCacheTransactionOperation
{
    Install = 0,
    Remove = 1
}

internal enum PackageCacheTransactionState
{
    Prepared = 0,
    OriginalMoved = 1,
    NewPromoted = 2,
    MetadataCommitted = 3,
    Completed = 4,
    RollbackStarted = 5,
    RolledBack = 6
}

internal enum PackageCacheArtifactKind
{
    Missing = 0,
    Directory = 1,
    File = 2,
    SymbolicLink = 3,
    DirectorySymbolicLink = 4
}

internal sealed record PackageCacheTransactionJournal
{
    internal const int CurrentVersion = 2;

    public int Version { get; init; } = CurrentVersion;

    public required string OperationId { get; init; }

    public required PackageCacheTransactionOperation Operation { get; init; }

    public required PackageCacheTransactionState State { get; init; }

    public required string CanonicalIdentity { get; init; }

    public required string PackageName { get; init; }

    public required string PackageVersion { get; init; }

    public string? PackageScope { get; init; }

    public required string TargetRelativePath { get; init; }

    public string? StagingRelativePath { get; init; }

    public string? ArtifactRelativePath { get; init; }

    public required PackageCacheInspectionState OriginalState { get; init; }

    public required PackageCacheArtifactKind OriginalArtifactKind { get; init; }

    public CacheMetadataEntry? PriorMetadata { get; init; }

    public CacheMetadataEntry? IntendedMetadata { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    internal PackageReference GetReference() =>
        new(PackageName, PackageVersion, PackageScope);

    internal PackageCacheKey GetCacheKey()
    {
        PackageCacheKey cacheKey = PackageCacheKey.Create(GetReference());
        if (!string.Equals(
                cacheKey.CanonicalIdentity,
                CanonicalIdentity,
                StringComparison.Ordinal)
            || !string.Equals(
                cacheKey.RelativePath.Replace('\\', '/'),
                TargetRelativePath.Replace('\\', '/'),
                StringComparison.Ordinal))
        {
            throw InvalidJournal(
                "The transaction identity does not match its target path.");
        }

        return cacheKey;
    }

    internal static PackageInstallException InvalidJournal(
        string message,
        Exception? innerException = null) =>
        new(
            PackageInstallErrorCode.CoordinationFailed,
            PackageInstallStage.Coordination,
            message,
            innerException: innerException);
}

internal sealed class PackageCacheJournalStore
{
    private const int InitialReadRetryDelayMilliseconds = 10;
    private const int MaximumReadRetryDelayMilliseconds = 250;
    private const int MaximumReadRetryAttempts = 8;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _cacheRoot;
    private readonly string _transactionDirectory;
    private readonly IPackageCacheFileOperations _fileOperations;
    private readonly IPackageCacheFaultObserver _faultObserver;

    internal PackageCacheJournalStore(
        string cacheRoot,
        IPackageCacheFileOperations fileOperations,
        IPackageCacheFaultObserver faultObserver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentNullException.ThrowIfNull(fileOperations);
        ArgumentNullException.ThrowIfNull(faultObserver);

        _cacheRoot = Path.GetFullPath(cacheRoot);
        _transactionDirectory = Path.Combine(
            _cacheRoot,
            ".fhirpkg",
            "transactions");
        _fileOperations = fileOperations;
        _faultObserver = faultObserver;
    }

    internal string GetJournalPath(PackageCacheKey cacheKey) =>
        Path.Combine(_transactionDirectory, $"{cacheKey.LockHash}.json");

    internal async Task WriteAsync(
        PackageCacheTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        ValidateJournal(journal);
        PackageCacheKey cacheKey = journal.GetCacheKey();
        string journalPath = GetJournalPath(cacheKey);
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(
            journal,
            s_jsonOptions);
        await DurableFileWriter.WriteAsync(
                journalPath,
                content,
                _fileOperations,
                cancellationToken)
            .ConfigureAwait(false);
        await _faultObserver.OnEventAsync(
                new PackageCacheFaultEvent(
                    PackageCacheFaultPoint.JournalWritten,
                    journal.OperationId,
                    journal.State,
                    journal.CanonicalIdentity),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<PackageCacheTransactionJournal>>
        ReadAllAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_transactionDirectory))
            return [];

        string[] paths;
        try
        {
            paths = Directory.GetFiles(
                _transactionDirectory,
                "*.json",
                SearchOption.TopDirectoryOnly);
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        Array.Sort(paths, StringComparer.Ordinal);
        List<PackageCacheTransactionJournal> journals = new(paths.Length);
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                journals.Add(
                    await ReadPathAsync(path, cancellationToken)
                        .ConfigureAwait(false));
            }
            catch (FileNotFoundException)
            {
                // Concurrent finalization removed an already enumerated journal.
            }
            catch (DirectoryNotFoundException)
            {
                // Concurrent finalization removed the now-empty journal directory.
            }
        }

        return journals;
    }

    internal async Task<PackageCacheTransactionJournal?> ReadAsync(
        PackageCacheKey cacheKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        string path = GetJournalPath(cacheKey);
        if (!File.Exists(path))
            return null;

        try
        {
            return await ReadPathAsync(path, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal void Delete(PackageCacheTransactionJournal journal)
    {
        PackageCacheKey cacheKey = journal.GetCacheKey();
        string journalPath = GetJournalPath(cacheKey);
        if (_fileOperations.FileExists(journalPath))
            _fileOperations.DeleteFile(journalPath);

        try
        {
            if (Directory.Exists(_transactionDirectory)
                && !Directory.EnumerateFileSystemEntries(
                    _transactionDirectory).Any())
            {
                Directory.Delete(_transactionDirectory);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Empty-container cleanup is not part of transaction durability.
        }
        catch (IOException)
        {
            // Empty-container cleanup is not part of transaction durability.
        }
    }

    internal string ResolveRelativePath(
        string relativePath,
        string requiredPrefix)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath))
        {
            throw PackageCacheTransactionJournal.InvalidJournal(
                "A transaction path is not a valid relative path.");
        }

        string normalized = relativePath.Replace('\\', '/');
        string prefix = requiredPrefix.Trim('/').Replace('\\', '/');
        if (!string.Equals(normalized, prefix, StringComparison.Ordinal)
            && !normalized.StartsWith($"{prefix}/", StringComparison.Ordinal))
        {
            throw PackageCacheTransactionJournal.InvalidJournal(
                "A transaction path is outside its required cache area.");
        }

        string[] segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Any(
            segment => segment.Length == 0
                || segment is "." or ".."))
        {
            throw PackageCacheTransactionJournal.InvalidJournal(
                "A transaction path contains an unsafe segment.");
        }

        string fullPath = Path.GetFullPath(
            Path.Combine(
                _cacheRoot,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
        string containment = Path.GetRelativePath(_cacheRoot, fullPath);
        if (Path.IsPathRooted(containment)
            || containment == ".."
            || containment.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw PackageCacheTransactionJournal.InvalidJournal(
                "A transaction path escapes the cache root.");
        }

        return fullPath;
    }

    private void ValidateJournal(PackageCacheTransactionJournal journal)
    {
        if (journal.Version != PackageCacheTransactionJournal.CurrentVersion
            || string.IsNullOrWhiteSpace(journal.OperationId)
            || string.IsNullOrWhiteSpace(journal.CanonicalIdentity)
            || string.IsNullOrWhiteSpace(journal.PackageName)
            || string.IsNullOrWhiteSpace(journal.PackageVersion)
            || !Enum.IsDefined(journal.Operation)
            || !Enum.IsDefined(journal.State)
            || !Enum.IsDefined(journal.OriginalState)
            || !Enum.IsDefined(journal.OriginalArtifactKind))
        {
            throw PackageCacheTransactionJournal.InvalidJournal(
                "A cache transaction journal is invalid or unsupported.");
        }

        if (!Guid.TryParseExact(
                journal.OperationId,
                "N",
                out Guid operationId)
            || !string.Equals(
                operationId.ToString("N"),
                journal.OperationId,
                StringComparison.Ordinal))
        {
            throw PackageCacheTransactionJournal.InvalidJournal(
                "A cache transaction journal has an invalid operation id.");
        }

        PackageCacheKey cacheKey = journal.GetCacheKey();
        _ = ResolveRelativePath(
            journal.TargetRelativePath,
            cacheKey.RelativePath.Replace('\\', '/'));

        if (journal.StagingRelativePath is not null)
        {
            _ = ResolveRelativePath(
                journal.StagingRelativePath,
                ".fhirpkg/staging");
        }

        if (journal.ArtifactRelativePath is not null)
        {
            string requiredPrefix =
                journal.OriginalState == PackageCacheInspectionState.Corrupt
                    ? ".fhirpkg/quarantine"
                    : ".fhirpkg/backup";
            _ = ResolveRelativePath(
                journal.ArtifactRelativePath,
                requiredPrefix);
        }

        if (journal.Operation == PackageCacheTransactionOperation.Install
            && (journal.IntendedMetadata is null
                || journal.StagingRelativePath is null))
        {
            throw PackageCacheTransactionJournal.InvalidJournal(
                "An install transaction is missing required state.");
        }

        if (journal.Operation == PackageCacheTransactionOperation.Install
            && !string.Equals(
                journal.StagingRelativePath!.Replace('\\', '/'),
                $".fhirpkg/staging/{journal.OperationId}/expanded",
                StringComparison.Ordinal))
        {
            throw PackageCacheTransactionJournal.InvalidJournal(
                "An install transaction has an unexpected staging path.");
        }

        if (journal.Operation == PackageCacheTransactionOperation.Remove
            && (journal.StagingRelativePath is not null
                || journal.IntendedMetadata is not null
                || journal.State == PackageCacheTransactionState.NewPromoted))
        {
            throw PackageCacheTransactionJournal.InvalidJournal(
                "A remove transaction contains install-only state.");
        }

        bool originalMissing =
            journal.OriginalArtifactKind == PackageCacheArtifactKind.Missing;
        if (originalMissing != (journal.ArtifactRelativePath is null)
            || originalMissing
                != (journal.OriginalState
                    == PackageCacheInspectionState.Missing))
        {
            throw PackageCacheTransactionJournal.InvalidJournal(
                "A transaction journal has inconsistent original artifact state.");
        }

        if (journal.OriginalState == PackageCacheInspectionState.Valid
            && journal.OriginalArtifactKind
                != PackageCacheArtifactKind.Directory)
        {
            throw PackageCacheTransactionJournal.InvalidJournal(
                "A valid original package must be a directory.");
        }

        if (journal.ArtifactRelativePath is not null)
        {
            string container =
                journal.OriginalState == PackageCacheInspectionState.Corrupt
                    ? "quarantine"
                    : "backup";
            string expectedArtifact =
                $".fhirpkg/{container}/{cacheKey.LockHash}-{journal.OperationId}";
            if (!string.Equals(
                journal.ArtifactRelativePath.Replace('\\', '/'),
                expectedArtifact,
                StringComparison.Ordinal))
            {
                throw PackageCacheTransactionJournal.InvalidJournal(
                    "A transaction journal has an unexpected artifact path.");
            }
        }
    }

    private async Task<PackageCacheTransactionJournal> ReadPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        PackageCacheTransactionJournal journal;
        int retryDelay = InitialReadRetryDelayMilliseconds;
        int retryAttempt = 0;
        while (true)
        {
            try
            {
                await using FileStream stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    16_384,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                journal =
                    await JsonSerializer
                        .DeserializeAsync<PackageCacheTransactionJournal>(
                            stream,
                            s_jsonOptions,
                            cancellationToken)
                        .ConfigureAwait(false)
                    ?? throw PackageCacheTransactionJournal.InvalidJournal(
                        "A cache transaction journal was empty.");
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FileNotFoundException)
            {
                throw;
            }
            catch (DirectoryNotFoundException)
            {
                throw;
            }
            catch (PackageInstallException)
            {
                throw;
            }
            catch (JsonException exception)
            {
                throw PackageCacheTransactionJournal.InvalidJournal(
                    "A cache transaction journal contains invalid JSON.",
                    exception);
            }
            catch (NotSupportedException exception)
            {
                throw PackageCacheTransactionJournal.InvalidJournal(
                    "A cache transaction journal has an unsupported shape.",
                    exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw PackageCacheTransactionJournal.InvalidJournal(
                    "A cache transaction journal could not be read.",
                    exception);
            }
            catch (IOException exception)
                when (OperatingSystem.IsWindows()
                    && IsWindowsReadContention(exception)
                    && retryAttempt < MaximumReadRetryAttempts)
            {
                retryAttempt = checked(retryAttempt + 1);
                await Task.Delay(retryDelay, cancellationToken)
                    .ConfigureAwait(false);
                retryDelay = Math.Min(
                    retryDelay * 2,
                    MaximumReadRetryDelayMilliseconds);
            }
            catch (IOException exception)
            {
                throw PackageCacheTransactionJournal.InvalidJournal(
                    "A cache transaction journal could not be read.",
                    exception);
            }
        }

        ValidateJournal(journal);
        PackageCacheKey journalKey = journal.GetCacheKey();
        if (!string.Equals(
                Path.GetFileName(path),
                $"{journalKey.LockHash}.json",
                StringComparison.Ordinal))
        {
            throw PackageCacheTransactionJournal.InvalidJournal(
                "A cache transaction journal has an invalid file name.");
        }

        return journal;
    }

    internal static bool IsWindowsReadContention(
        IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        int nativeError = exception.HResult & 0xFFFF;
        return nativeError is 32 or 33;
    }
}
