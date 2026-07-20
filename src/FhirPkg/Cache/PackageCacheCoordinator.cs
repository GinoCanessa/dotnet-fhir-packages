// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using FhirPkg.Installation;

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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentNullException.ThrowIfNull(contentionObserver);
        _cacheRoot = Path.GetFullPath(cacheRoot);
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

    internal Task<PackageCacheLease> AcquireFileMutationAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string fullPath = Path.GetFullPath(filePath);
        string lockPath = Path.Combine(
            Path.GetDirectoryName(fullPath)!,
            ".fhirpkg-restore.lock");
        return AcquireAsync(
            "file-mutation",
            lockPath,
            cancellationToken,
            processKeyOverride:
                "fhirpkg:file-mutation");
    }

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

        FileStream? stream = null;
        try
        {
            stream = OpenLockFile(GetOperationLockPath(operationId));
            if (!PackageCacheFileLock.TryLock(stream))
            {
                ReleaseFailedAcquisition(stream, processLock);
                return null;
            }

            return new PackageCacheLease(processLock, stream);
        }
        catch (UnauthorizedAccessException exception)
        {
            ReleaseFailedAcquisition(stream, processLock);
            throw CoordinationFailure(exception);
        }
        catch (IOException exception)
        {
            ReleaseFailedAcquisition(stream, processLock);
            throw CoordinationFailure(exception);
        }
        catch (NotSupportedException exception)
        {
            ReleaseFailedAcquisition(stream, processLock);
            throw CoordinationFailure(exception);
        }
    }

    private async Task<PackageCacheLease> AcquireAsync(
        string logicalKey,
        string lockPath,
        CancellationToken cancellationToken,
        string? processKeyOverride = null)
    {
        string processKey =
            processKeyOverride
            ?? GetProcessKey(logicalKey);
        PackageCacheProcessLockReference processLock =
            RentProcessLock(processKey);

        FileStream? stream = null;
        try
        {
            await processLock.Semaphore.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            processLock.MarkSemaphoreHeld();
            stream = OpenLockFile(lockPath);
            int retryDelay = InitialRetryDelayMilliseconds;
            int retryAttempt = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (PackageCacheFileLock.TryLock(stream))
                    return new PackageCacheLease(processLock, stream);

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
            ReleaseFailedAcquisition(stream, processLock);
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            ReleaseFailedAcquisition(stream, processLock);
            throw CoordinationFailure(exception);
        }
        catch (IOException exception)
        {
            ReleaseFailedAcquisition(stream, processLock);
            throw CoordinationFailure(exception);
        }
        catch (NotSupportedException exception)
        {
            ReleaseFailedAcquisition(stream, processLock);
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

        FileStream? stream = null;
        try
        {
            processLock.Semaphore.Wait();
            processLock.MarkSemaphoreHeld();
            stream = OpenLockFile(lockPath);
            int retryDelay = InitialRetryDelayMilliseconds;
            int retryAttempt = 0;
            while (true)
            {
                if (PackageCacheFileLock.TryLock(stream))
                    return new PackageCacheLease(processLock, stream);

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
            ReleaseFailedAcquisition(stream, processLock);
            throw CoordinationFailure(exception);
        }
        catch (IOException exception)
        {
            ReleaseFailedAcquisition(stream, processLock);
            throw CoordinationFailure(exception);
        }
        catch (NotSupportedException exception)
        {
            ReleaseFailedAcquisition(stream, processLock);
            throw CoordinationFailure(exception);
        }
    }

    private static void ReleaseFailedAcquisition(
        FileStream? stream,
        PackageCacheProcessLockReference processLock)
    {
        try
        {
            stream?.Dispose();
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

    private FileStream OpenLockFile(string lockPath)
    {
        SystemPackageCacheFileOperations.Instance.CreateDirectory(
            Path.GetDirectoryName(lockPath)!);
        return new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            bufferSize: 1,
            FileOptions.None);
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
    private FileStream? _stream;

    internal PackageCacheLease(
        PackageCacheProcessLockReference processLock,
        FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(processLock);
        ArgumentNullException.ThrowIfNull(stream);
        _processLock = processLock;
        _stream = stream;
    }

    public void Dispose()
    {
        FileStream? stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
            return;

        try
        {
            PackageCacheFileLock.Unlock(stream);
        }
        finally
        {
            try
            {
                stream.Dispose();
            }
            finally
            {
                _processLock.Dispose();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class PackageCacheFileLock
{
    private const int MacLockExclusive = 2;
    private const int MacLockNonBlocking = 4;
    private const int MacLockUnlock = 8;
    private const int MacWouldBlock = 35;

    internal static bool TryLock(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (OperatingSystem.IsMacOS())
        {
            int descriptor = stream.SafeFileHandle
                .DangerousGetHandle()
                .ToInt32();
            if (Flock(
                    descriptor,
                    MacLockExclusive | MacLockNonBlocking) == 0)
            {
                return true;
            }

            int error = Marshal.GetLastPInvokeError();
            if (error == MacWouldBlock)
                return false;

            throw new IOException(
                "The macOS package cache lock could not be acquired.",
                new Win32Exception(error));
        }

        try
        {
            stream.Lock(0, 1);
            return true;
        }
        catch (IOException exception)
            when (IsManagedLockContention(exception))
        {
            return false;
        }
    }

    internal static void Unlock(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (OperatingSystem.IsMacOS())
        {
            int descriptor = stream.SafeFileHandle
                .DangerousGetHandle()
                .ToInt32();
            if (Flock(descriptor, MacLockUnlock) != 0)
            {
                throw new IOException(
                    "The macOS package cache lock could not be released.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            return;
        }

        stream.Unlock(0, 1);
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int descriptor, int operation);

    private static bool IsManagedLockContention(
        IOException exception)
    {
        int nativeError = exception.HResult & 0xFFFF;
        return OperatingSystem.IsWindows()
            ? nativeError is 32 or 33
            : nativeError is 11 or 13;
    }
}
