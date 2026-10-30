// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Collections.Concurrent;
using FhirPkg.Installation;
using FhirPkg.IO;

namespace FhirPkg.Cache;

internal sealed class PackageCacheCoordinator
{
    private const int InitialRetryDelayMilliseconds = 10;
    private const int MaximumRetryDelayMilliseconds = 250;
    private static readonly ConcurrentDictionary<
        string,
        PackageCacheProcessLockEntry>
        s_processLocks = new(StringComparer.Ordinal);

    private readonly string _cacheRoot;
    private readonly string _lockRoot;
    private readonly FileSystemContext _context;
    private readonly IPackageCacheContentionObserver
        _contentionObserver;

    internal PackageCacheCoordinator(string cacheRoot)
        : this(
            cacheRoot,
            NullPackageCacheContentionObserver.Instance)
    {
    }

    internal PackageCacheCoordinator(
        string cacheRoot,
        IPackageCacheContentionObserver contentionObserver)
        : this(
            cacheRoot,
            FileSystemContext.CreateDefault(),
            contentionObserver)
    {
    }

    internal PackageCacheCoordinator(
        string cacheRoot,
        FileSystemContext context,
        IPackageCacheContentionObserver contentionObserver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(contentionObserver);
        _context = context;
        _cacheRoot = context.FileSystem.GetFullPath(cacheRoot);
        _lockRoot = Path.Combine(_cacheRoot, ".fhirpkg", "locks");
        _contentionObserver = contentionObserver;
    }

    internal Task<PackageCacheLease> AcquireIdentityAsync(
        PackageCacheKey cacheKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        return AcquireAsync(
            $"identity:{cacheKey.CanonicalIdentity}",
            Path.Combine(_lockRoot, $"{cacheKey.LockHash}.lock"),
            cancellationToken);
    }

    internal PackageCacheLease AcquireIdentity(
        PackageCacheKey cacheKey)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        return Acquire(
            $"identity:{cacheKey.CanonicalIdentity}",
            Path.Combine(_lockRoot, $"{cacheKey.LockHash}.lock"));
    }

    internal Task<PackageCacheLease> AcquireGlobalAsync(
        CancellationToken cancellationToken) =>
        AcquireAsync(
            "global",
            Path.Combine(_lockRoot, "global.lock"),
            cancellationToken);

    internal PackageCacheLease AcquireGlobal() =>
        Acquire(
            "global",
            Path.Combine(_lockRoot, "global.lock"));

    internal Task<PackageCacheLease> AcquireOperationOwnerAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        ValidateOperationId(operationId);
        return AcquireAsync(
            $"operation:{operationId}",
            GetOperationLockPath(operationId),
            cancellationToken);
    }

    internal PackageCacheLease? TryAcquireOperationOwner(
        string operationId)
    {
        ValidateOperationId(operationId);
        string processKey = GetProcessKey($"operation:{operationId}");
        PackageCacheProcessLockReference processLock =
            RentProcessLock(processKey);
        if (!processLock.Semaphore.Wait(0))
        {
            processLock.Dispose();
            return null;
        }

        processLock.MarkSemaphoreHeld();

        LockAcquisitionOperation operation = new(
            _context,
            GetOperationLockPath(operationId));
        try
        {
            EnsureLockDirectory(GetOperationLockPath(operationId));
            // A single poll: contention means another owner, never a fault.
            FileSystemReplayOutcome<FileStream?> outcome =
                _context.Executor.Execute(operation);
            if (outcome.Kind != FileSystemReplayOutcomeKind.Completed)
            {
                ReleaseFailedAcquisition(operation, processLock);
                return null;
            }

            return new PackageCacheLease(processLock, _context, outcome.Value!);
        }
        catch (UnauthorizedAccessException exception)
        {
            ReleaseFailedAcquisition(operation, processLock);
            throw CoordinationFailure(exception);
        }
        catch (IOException exception)
        {
            ReleaseFailedAcquisition(operation, processLock);
            throw CoordinationFailure(exception);
        }
        catch (NotSupportedException exception)
        {
            ReleaseFailedAcquisition(operation, processLock);
            throw CoordinationFailure(exception);
        }
        catch
        {
            ReleaseFailedAcquisition(operation, processLock);
            throw;
        }
    }

    internal async ValueTask<PackageCacheLease?> TryAcquireOperationOwnerAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        ValidateOperationId(operationId);
        string processKey = GetProcessKey($"operation:{operationId}");
        PackageCacheProcessLockReference processLock = RentProcessLock(processKey);
        LockAcquisitionOperation operation = new(_context, GetOperationLockPath(operationId));
        try
        {
            if (!await processLock.Semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                processLock.Dispose();
                return null;
            }

            processLock.MarkSemaphoreHeld();
            await EnsureLockDirectoryAsync(GetOperationLockPath(operationId), cancellationToken)
                .ConfigureAwait(false);
            FileSystemReplayOutcome<FileStream?> outcome =
                await _context.Executor.ExecuteAsync(operation, cancellationToken).ConfigureAwait(false);
            if (outcome.Kind != FileSystemReplayOutcomeKind.Completed)
            {
                ReleaseFailedAcquisition(operation, processLock);
                return null;
            }

            return new PackageCacheLease(processLock, _context, outcome.Value!);
        }
        catch (UnauthorizedAccessException exception)
        {
            ReleaseFailedAcquisition(operation, processLock);
            throw CoordinationFailure(exception);
        }
        catch (IOException exception)
        {
            ReleaseFailedAcquisition(operation, processLock);
            throw CoordinationFailure(exception);
        }
        catch (NotSupportedException exception)
        {
            ReleaseFailedAcquisition(operation, processLock);
            throw CoordinationFailure(exception);
        }
        catch
        {
            ReleaseFailedAcquisition(operation, processLock);
            throw;
        }
    }

    private async Task<PackageCacheLease> AcquireAsync(
        string logicalKey,
        string lockPath,
        CancellationToken cancellationToken)
    {
        string processKey = GetProcessKey(logicalKey);
        PackageCacheProcessLockReference processLock =
            RentProcessLock(processKey);

        // One operation per acquisition episode: faults spend its budget, while
        // contention suspends it without replenishing or consuming that budget.
        LockAcquisitionOperation operation = new(_context, lockPath);
        try
        {
            await processLock.Semaphore.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            processLock.MarkSemaphoreHeld();
            await EnsureLockDirectoryAsync(lockPath, cancellationToken)
                .ConfigureAwait(false);
            int retryDelay = InitialRetryDelayMilliseconds;
            int retryAttempt = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileSystemReplayOutcome<FileStream?> outcome =
                    await _context.Executor.ExecuteAsync(operation, cancellationToken)
                        .ConfigureAwait(false);
                if (outcome.Kind == FileSystemReplayOutcomeKind.Completed)
                    return new PackageCacheLease(processLock, _context, outcome.Value!);

                retryAttempt = checked(retryAttempt + 1);
                _contentionObserver.OnRetry(
                    new PackageCacheContentionEvent(
                        logicalKey,
                        retryAttempt));
                await Task.Delay(retryDelay, cancellationToken)
                    .ConfigureAwait(false);
                retryDelay = Math.Min(
                    retryDelay * 2,
                    MaximumRetryDelayMilliseconds);
            }
        }
        catch (OperationCanceledException)
        {
            ReleaseFailedAcquisition(operation, processLock);
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            ReleaseFailedAcquisition(operation, processLock);
            throw CoordinationFailure(exception);
        }
        catch (IOException exception)
        {
            ReleaseFailedAcquisition(operation, processLock);
            throw CoordinationFailure(exception);
        }
        catch (NotSupportedException exception)
        {
            ReleaseFailedAcquisition(operation, processLock);
            throw CoordinationFailure(exception);
        }
    }

    private PackageCacheLease Acquire(
        string logicalKey,
        string lockPath)
    {
        string processKey = GetProcessKey(logicalKey);
        PackageCacheProcessLockReference processLock =
            RentProcessLock(processKey);

        LockAcquisitionOperation operation = new(_context, lockPath);
        try
        {
            processLock.Semaphore.Wait();
            processLock.MarkSemaphoreHeld();
            EnsureLockDirectory(lockPath);
            int retryDelay = InitialRetryDelayMilliseconds;
            int retryAttempt = 0;
            while (true)
            {
                FileSystemReplayOutcome<FileStream?> outcome =
                    _context.Executor.Execute(operation);
                if (outcome.Kind == FileSystemReplayOutcomeKind.Completed)
                    return new PackageCacheLease(processLock, _context, outcome.Value!);

                retryAttempt = checked(retryAttempt + 1);
                _contentionObserver.OnRetry(
                    new PackageCacheContentionEvent(
                        logicalKey,
                        retryAttempt));
                Thread.Sleep(retryDelay);
                retryDelay = Math.Min(
                    retryDelay * 2,
                    MaximumRetryDelayMilliseconds);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            ReleaseFailedAcquisition(operation, processLock);
            throw CoordinationFailure(exception);
        }
        catch (IOException exception)
        {
            ReleaseFailedAcquisition(operation, processLock);
            throw CoordinationFailure(exception);
        }
        catch (NotSupportedException exception)
        {
            ReleaseFailedAcquisition(operation, processLock);
            throw CoordinationFailure(exception);
        }
    }

    private void EnsureLockDirectory(string lockPath)
    {
        string directory = Path.GetDirectoryName(lockPath)!;
        if (_context.FileSystem.Probe(directory).Kind != FileSystemEntryKind.Directory)
            _context.FileSystem.CreateDirectory(directory);
    }

    private async ValueTask EnsureLockDirectoryAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(lockPath)!;
        if ((await _context.FileSystem.ProbeAsync(directory, cancellationToken)
                .ConfigureAwait(false)).Kind != FileSystemEntryKind.Directory)
        {
            await _context.FileSystem.CreateDirectoryAsync(directory, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void ReleaseFailedAcquisition(
        LockAcquisitionOperation operation,
        PackageCacheProcessLockReference processLock)
    {
        try
        {
            operation.ReleaseAfterFailure();
        }
        finally
        {
            processLock.Dispose();
        }
    }

    internal static int GetProcessLockEntryCount(string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        string prefix = $"{Path.GetFullPath(cacheRoot)}\0";
        return s_processLocks.Keys.Count(
            key => key.StartsWith(prefix, StringComparison.Ordinal));
    }

    internal static int GetIdentityReferenceCount(
        string cacheRoot,
        PackageCacheKey cacheKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentNullException.ThrowIfNull(cacheKey);
        string processKey =
            $"{Path.GetFullPath(cacheRoot)}\0identity:{cacheKey.CanonicalIdentity}";
        return s_processLocks.TryGetValue(
            processKey,
            out PackageCacheProcessLockEntry? entry)
            ? entry.ReferenceCount
            : 0;
    }

    private sealed class LockAcquisitionOperation :
        FileSystemReplayOperation<FileStream?>
    {
        private readonly IFileSystemPrimitives _primitives;
        private readonly string _lockPath;
        private FileStream? _stream;

        internal LockAcquisitionOperation(FileSystemContext context, string lockPath)
            : base(context.Policy, "OpenLockFile")
        {
            _primitives = context.Primitives;
            _lockPath = lockPath;
        }

        internal override FileSystemReplayOutcome<FileStream?> ExecuteStep(
            CancellationToken cancellationToken)
        {
            if (_stream is null)
            {
                SetPhase("OpenLockFile");
                FileSystemReplayOutcome<FileStream> opened = _primitives.OpenFile(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    OperatingSystem.IsMacOS()
                        ? FileShare.None
                        : FileShare.ReadWrite,
                    bufferSize: 1,
                    FileOptions.None);
                if (opened.Kind == FileSystemReplayOutcomeKind.PrimitiveFailure)
                {
                    Exception exception = opened.PrimitiveFailure!.Exception;
                    if (OperatingSystem.IsMacOS()
                        && exception is IOException ioException
                        && PackageCacheFileLock.IsMacOpenContention(ioException))
                    {
                        // Open contention is coordination, not a filesystem fault.
                        return FileSystemReplayOutcome<FileStream?>.Contended(null);
                    }

                    // OpenOrCreate of the lock file is idempotent: replaying it adopts
                    // or recreates the same empty file, so a physical failure may retry.
                    return FileSystemReplayOutcome<FileStream?>.Failed(
                        FileSystemFailureContext.CapturePrimitiveFailure(
                            exception,
                            FileSystemReplaySafety.Safe,
                            "OpenLockFile",
                            _lockPath));
                }

                if (opened.Kind != FileSystemReplayOutcomeKind.Completed)
                    return ResilientFileSystem.Failure<FileStream, FileStream?>(opened);

                _stream = opened.Value;
            }

            SetPhase("LockFile");
            FileSystemReplayOutcome<bool> locked = _primitives.TryLock(_stream);
            if (locked.Kind == FileSystemReplayOutcomeKind.Completed && locked.Value)
            {
                FileStream acquired = _stream;
                _stream = null;
                return FileSystemReplayOutcome<FileStream?>.Complete(acquired);
            }

            if (locked.Kind is FileSystemReplayOutcomeKind.Contended
                or FileSystemReplayOutcomeKind.Completed)
            {
                // Never hold the handle across the coordination wait.
                _stream.Dispose();
                _stream = null;
                return FileSystemReplayOutcome<FileStream?>.Contended(null);
            }

            // A lock fault keeps the opened handle so only the lock step is replayed.
            return ResilientFileSystem.Failure<bool, FileStream?>(locked);
        }

        internal void ReleaseAfterFailure()
        {
            FileStream? stream = _stream;
            _stream = null;
            stream?.Dispose();
        }
    }

    private string GetProcessKey(string logicalKey) =>
        $"{_cacheRoot}\0{logicalKey}";

    private static PackageCacheProcessLockReference RentProcessLock(
        string processKey)
    {
        SpinWait spinWait = new();
        while (true)
        {
            PackageCacheProcessLockEntry entry =
                s_processLocks.GetOrAdd(
                    processKey,
                    static _ => new PackageCacheProcessLockEntry());
            if (entry.TryAddReference())
            {
                return new PackageCacheProcessLockReference(
                    processKey,
                    entry);
            }

            spinWait.SpinOnce();
        }
    }

    internal static void ReleaseProcessLockReference(
        string processKey,
        PackageCacheProcessLockEntry entry)
    {
        if (!entry.ReleaseReference())
            return;

        bool removed = ((ICollection<KeyValuePair<
                string,
                PackageCacheProcessLockEntry>>)s_processLocks)
            .Remove(new KeyValuePair<
                string,
                PackageCacheProcessLockEntry>(
                processKey,
                entry));
        if (removed)
            entry.Dispose();
    }

    private string GetOperationLockPath(string operationId) =>
        Path.Combine(_lockRoot, "operations", $"{operationId}.lock");

    private static void ValidateOperationId(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (operationId.Length > 128
            || operationId.Any(
                character => !char.IsAsciiLetterOrDigit(character)
                    && character is not '-' and not '_'))
        {
            throw new ArgumentException(
                "Operation identifiers may contain only ASCII letters, digits, '-' and '_'.",
                nameof(operationId));
        }
    }

    private static PackageInstallException CoordinationFailure(
        Exception exception) =>
        new(
            PackageInstallErrorCode.CoordinationFailed,
            PackageInstallStage.Coordination,
            "The package cache coordination lock could not be acquired.",
            innerException: exception);
}

internal sealed record PackageCacheContentionEvent(
    string LogicalKey,
    int RetryAttempt);

internal interface IPackageCacheContentionObserver
{
    void OnRetry(PackageCacheContentionEvent contentionEvent);
}

internal sealed class NullPackageCacheContentionObserver :
    IPackageCacheContentionObserver
{
    internal static NullPackageCacheContentionObserver Instance
        { get; } = new();

    private NullPackageCacheContentionObserver()
    {
    }

    public void OnRetry(
        PackageCacheContentionEvent contentionEvent)
    {
    }
}

internal sealed class PackageCacheProcessLockEntry : IDisposable
{
    private readonly object _sync = new();
    private int _referenceCount;
    private bool _retired;

    internal SemaphoreSlim Semaphore { get; } = new(1, 1);

    internal int ReferenceCount
    {
        get
        {
            lock (_sync)
                return _referenceCount;
        }
    }

    internal bool TryAddReference()
    {
        lock (_sync)
        {
            if (_retired)
                return false;

            _referenceCount = checked(_referenceCount + 1);
            return true;
        }
    }

    internal bool ReleaseReference()
    {
        lock (_sync)
        {
            if (_referenceCount <= 0)
            {
                throw new InvalidOperationException(
                    "The package cache process lock reference count is invalid.");
            }

            _referenceCount--;
            if (_referenceCount != 0)
                return false;

            _retired = true;
            return true;
        }
    }

    public void Dispose() => Semaphore.Dispose();
}

internal sealed class PackageCacheProcessLockReference : IDisposable
{
    private readonly string _processKey;
    private PackageCacheProcessLockEntry? _entry;
    private int _semaphoreHeld;

    internal PackageCacheProcessLockReference(
        string processKey,
        PackageCacheProcessLockEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processKey);
        ArgumentNullException.ThrowIfNull(entry);
        _processKey = processKey;
        _entry = entry;
    }

    internal SemaphoreSlim Semaphore =>
        _entry?.Semaphore
        ?? throw new ObjectDisposedException(
            nameof(PackageCacheProcessLockReference));

    internal void MarkSemaphoreHeld()
    {
        if (Interlocked.Exchange(ref _semaphoreHeld, 1) != 0)
        {
            throw new InvalidOperationException(
                "The package cache process semaphore is already held.");
        }
    }

    public void Dispose()
    {
        PackageCacheProcessLockEntry? entry =
            Interlocked.Exchange(ref _entry, null);
        if (entry is null)
            return;

        try
        {
            if (Interlocked.Exchange(ref _semaphoreHeld, 0) != 0)
                entry.Semaphore.Release();
        }
        finally
        {
            PackageCacheCoordinator.ReleaseProcessLockReference(
                _processKey,
                entry);
        }
    }
}

internal sealed class PackageCacheLease :
    IDisposable,
    IAsyncDisposable
{
    private readonly PackageCacheProcessLockReference _processLock;
    private readonly FileSystemContext _context;
    private FileStream? _stream;

    internal PackageCacheLease(
        PackageCacheProcessLockReference processLock,
        FileSystemContext context,
        FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(processLock);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stream);
        _processLock = processLock;
        _context = context;
        _stream = stream;
    }

    public void Dispose()
    {
        FileStream? stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
            return;

        bool unlocked = false;
        try
        {
            // Unlock is replayed against the same handle; release below happens once.
            _ = _context.FileSystem.Execute(
                "UnlockLockFile",
                () => _context.Primitives.Unlock(stream));
            unlocked = true;
        }
        finally
        {
            try
            {
                stream.Dispose();
            }
            catch when (!unlocked)
            {
                // Release once without replacing the unlock failure.
            }
            finally
            {
                _processLock.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        FileStream? stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
            return;

        bool unlocked = false;
        try
        {
            _ = await _context.FileSystem.ExecuteAsync(
                    "UnlockLockFile",
                    () => _context.Primitives.Unlock(stream),
                    CancellationToken.None)
                .ConfigureAwait(false);
            unlocked = true;
        }
        finally
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch when (!unlocked)
            {
                // Release once without replacing the unlock failure.
            }
            finally
            {
                _processLock.Dispose();
            }
        }
    }
}

internal static class PackageCacheFileLock
{
    // Extraction only: the coordinator still owns its original contention loop.
    internal static bool TryLock(FileStream stream) =>
        ResilientFileSystem.ValueOrThrow(SystemFileSystemPrimitives.Instance.TryLock(stream));

    internal static void Unlock(FileStream stream) =>
        ResilientFileSystem.ValueOrThrow(SystemFileSystemPrimitives.Instance.Unlock(stream));

    internal static bool IsMacOpenContention(IOException exception) =>
        SystemFileSystemPrimitives.IsMacOpenContention(exception);

    internal static bool IsManagedLockContention(IOException exception) =>
        SystemFileSystemPrimitives.IsManagedLockContention(exception);
}
