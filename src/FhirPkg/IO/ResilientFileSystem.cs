// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FhirPkg.IO;

internal sealed class ResilientFileSystem
{
    private readonly FileSystemContext _context;

    internal ResilientFileSystem(FileSystemContext context) => _context = context;

    internal string GetCurrentDirectory(CancellationToken cancellationToken = default) =>
        Execute("DiscoverCurrentDirectory", _context.Primitives.GetCurrentDirectory, cancellationToken);

    internal ValueTask<string> GetCurrentDirectoryAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("DiscoverCurrentDirectory", _context.Primitives.GetCurrentDirectory, cancellationToken);

    internal string GetTempPath(CancellationToken cancellationToken = default) =>
        Execute("DiscoverTempDirectory", _context.Primitives.GetTempPath, cancellationToken);

    internal ValueTask<string> GetTempPathAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("DiscoverTempDirectory", _context.Primitives.GetTempPath, cancellationToken);

    internal string GetHomeDirectory(CancellationToken cancellationToken = default) =>
        Execute("DiscoverHomeDirectory", _context.Primitives.GetHomeDirectory, cancellationToken);

    internal ValueTask<string> GetHomeDirectoryAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("DiscoverHomeDirectory", _context.Primitives.GetHomeDirectory, cancellationToken);

    internal string GetFullPath(string path, CancellationToken cancellationToken = default) =>
        Execute("ResolvePath", () => ResolvePathAttempt(_context.Primitives, path), cancellationToken);

    internal ValueTask<string> GetFullPathAsync(string path, CancellationToken cancellationToken = default) =>
        ExecuteAsync("ResolvePath", () => ResolvePathAttempt(_context.Primitives, path), cancellationToken);

    internal FileSystemProbeResult Probe(string path, CancellationToken cancellationToken = default) =>
        ExecutePath("Probe", path,
            fullPath => _context.Primitives.Probe(Path.TrimEndingDirectorySeparator(fullPath)), cancellationToken);

    internal ValueTask<FileSystemProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default) =>
        ExecutePathAsync("Probe", path,
            fullPath => _context.Primitives.Probe(Path.TrimEndingDirectorySeparator(fullPath)), cancellationToken);

    internal FileSystemProbeResult ProbeFollowing(string path, CancellationToken cancellationToken = default) =>
        ExecutePath("ProbeFollowing", path,
            fullPath => _context.Primitives.Probe(fullPath, followLinks: true), cancellationToken);

    internal FileAttributes GetAttributes(string path, CancellationToken cancellationToken = default) =>
        ExecutePath("GetAttributes", path, _context.Primitives.GetAttributes, cancellationToken);

    internal ValueTask<FileAttributes> GetAttributesAsync(string path, CancellationToken cancellationToken = default) =>
        ExecutePathAsync("GetAttributes", path, _context.Primitives.GetAttributes, cancellationToken);

    internal long GetLength(string path, CancellationToken cancellationToken = default) =>
        ExecutePath("GetLength", path, _context.Primitives.GetLength, cancellationToken);

    internal ValueTask<long> GetLengthAsync(string path, CancellationToken cancellationToken = default) =>
        ExecutePathAsync("GetLength", path, _context.Primitives.GetLength, cancellationToken);

    internal string[] SnapshotDirectory(string path, CancellationToken cancellationToken = default) =>
        ExecutePath(
            "SnapshotDirectory", path,
            fullPath => SnapshotAttempt(_context.Primitives, fullPath, cancellationToken),
            cancellationToken);

    internal ValueTask<string[]> SnapshotDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        ExecutePathAsync(
            "SnapshotDirectory", path,
            fullPath => SnapshotAttempt(_context.Primitives, fullPath, cancellationToken),
            cancellationToken);

    internal FileStream OpenFile(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        int bufferSize,
        FileOptions options,
        CancellationToken cancellationToken = default) =>
        ExecutePath(
            "OpenFile", path,
            fullPath => _context.Primitives.OpenFile(fullPath, mode, access, share, bufferSize, options),
            cancellationToken);

    internal ValueTask<FileStream> OpenFileAsync(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        int bufferSize,
        FileOptions options,
        CancellationToken cancellationToken = default) =>
        ExecutePathAsync(
            "OpenFile", path,
            fullPath => _context.Primitives.OpenFile(fullPath, mode, access, share, bufferSize, options),
            cancellationToken);

    internal FileStream OpenRegularRead(string path, CancellationToken cancellationToken = default)
    {
        RegularFileReplayOperation<FileStream> operation = CreateRegularFileOperation(path);
        try
        {
            return _context.Executor.Execute(operation, cancellationToken).Value;
        }
        catch
        {
            operation.ReleaseAfterFailure();
            throw;
        }
    }

    internal async ValueTask<FileStream> OpenRegularReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        RegularFileReplayOperation<FileStream> operation = CreateRegularFileOperation(path);
        try
        {
            return (await _context.Executor.ExecuteAsync(operation, cancellationToken).ConfigureAwait(false)).Value;
        }
        catch
        {
            operation.ReleaseAfterFailure();
            throw;
        }
    }

    private RegularFileReplayOperation<FileStream> CreateRegularFileOperation(string path) =>
        new RegularFileReplayOperation<FileStream>(
            _context, path,
            fullPath => _context.Primitives.OpenFile(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.SequentialScan),
            handle => new FileStream(handle, FileAccess.Read, 16_384, isAsync: false));

    internal Stream OpenProtectedRegularRead(string path, CancellationToken cancellationToken = default)
    {
        RegularFileReplayOperation<SafeFileHandle> operation = CreateProtectedRegularFileOperation(path);
        try
        {
            SafeFileHandle handle = _context.Executor.Execute(operation, cancellationToken).Value;
            return new PositionedFileStream(_context, handle, operation.FullPath!, FileAccess.Read, 0);
        }
        catch
        {
            operation.ReleaseAfterFailure();
            throw;
        }
    }

    internal async ValueTask<Stream> OpenProtectedRegularReadAsync(
        string path, CancellationToken cancellationToken = default)
    {
        RegularFileReplayOperation<SafeFileHandle> operation = CreateProtectedRegularFileOperation(path);
        try
        {
            SafeFileHandle handle = (await _context.Executor.ExecuteAsync(operation, cancellationToken).ConfigureAwait(false)).Value;
            return new PositionedFileStream(_context, handle, operation.FullPath!, FileAccess.Read, 0);
        }
        catch
        {
            operation.ReleaseAfterFailure();
            throw;
        }
    }

    private RegularFileReplayOperation<SafeFileHandle> CreateProtectedRegularFileOperation(string path) =>
        new RegularFileReplayOperation<SafeFileHandle>(
            _context, path,
            fullPath => _context.Primitives.OpenFileHandle(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan),
            handle => handle);

    internal PositionedFileStream OpenPositionedFile(
        string path, FileMode mode, FileAccess access, FileShare share, FileOptions options,
        CancellationToken cancellationToken = default)
    {
        OwnedFileOpenReplayOperation operation = new OwnedFileOpenReplayOperation(
            _context, path, mode, access, share, options);
        try
        {
            return _context.Executor.Execute(operation, cancellationToken).Value;
        }
        catch
        {
            operation.ReleaseAfterFailure();
            if (operation.CreatedPath is string createdPath)
                CleanOwnedFile(createdPath);
            throw;
        }
    }

    internal async ValueTask<PositionedFileStream> OpenPositionedFileAsync(
        string path, FileMode mode, FileAccess access, FileShare share, FileOptions options,
        CancellationToken cancellationToken = default)
    {
        OwnedFileOpenReplayOperation operation = new OwnedFileOpenReplayOperation(
            _context, path, mode, access, share, options);
        try
        {
            return (await _context.Executor.ExecuteAsync(operation, cancellationToken).ConfigureAwait(false)).Value;
        }
        catch
        {
            operation.ReleaseAfterFailure();
            if (operation.CreatedPath is string createdPath)
                await CleanOwnedFileAsync(createdPath).ConfigureAwait(false);
            throw;
        }
    }

    internal async ValueTask WriteFileAndFlushAsync(
        string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        // Capture before open can await: later block replay must not depend on
        // caller memory, encoding or a source stream being consumed again.
        byte[] bytes = content.ToArray();
        PositionedFileStream? stream = null;
        try
        {
            stream = await OpenPositionedFileAsync(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                FileOptions.Asynchronous | FileOptions.WriteThrough, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            if (stream is not null)
            {
                stream.ReleaseAfterFailure();
                await CleanOwnedFileAsync(stream.FilePath).ConfigureAwait(false);
            }
            throw;
        }
    }

    private void CleanOwnedFile(string path)
    {
        try
        {
            DeleteFile(path, CancellationToken.None);
        }
        catch
        {
            // Only owned create-new files reach here; cleanup has its own bounded
            // operation and cannot replace the failed open/write/flush.
        }
    }

    private async ValueTask CleanOwnedFileAsync(string path)
    {
        try
        {
            await DeleteAsync(path, FileSystemEntryKind.File, recursive: false, missingAllowed: true,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the primary failure, including cancellation.
        }
    }

    internal void CreateDirectory(string path, CancellationToken cancellationToken = default) =>
        ExecuteDurable(new DurableFileReplayOperation(_context, DurableFileOperationKind.CreateDirectory, path), cancellationToken);

    internal ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        ExecuteDurableAsync(new DurableFileReplayOperation(_context, DurableFileOperationKind.CreateDirectory, path), cancellationToken);

    internal void MovePath(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        ExecuteDurable(new DurableFileReplayOperation(
            _context, DurableFileOperationKind.Move, sourcePath, destinationPath), cancellationToken);

    internal ValueTask MovePathAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        ExecuteDurableAsync(new DurableFileReplayOperation(
            _context, DurableFileOperationKind.Move, sourcePath, destinationPath), cancellationToken);

    internal ValueTask ReplaceFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        ReplaceFileAsync(new DurableFileReplacement(sourcePath, destinationPath), cancellationToken);

    internal ValueTask ReplaceFileAsync(DurableFileReplacement replacement, CancellationToken cancellationToken = default) =>
        ExecuteDurableAsync(new DurableFileReplayOperation(
            _context, DurableFileOperationKind.Replace, replacement.SourcePath, replacement.DestinationPath,
            replacement: replacement), cancellationToken);

    internal void DeleteFile(string path, CancellationToken cancellationToken = default) =>
        Delete(path, FileSystemEntryKind.File, recursive: false, missingAllowed: true, cancellationToken);

    internal void DeleteDirectory(string path, bool recursive, CancellationToken cancellationToken = default) =>
        Delete(path, FileSystemEntryKind.Directory, recursive, missingAllowed: false, cancellationToken);

    internal void Delete(
        string path,
        FileSystemEntryKind expectedKind,
        bool recursive,
        bool missingAllowed,
        CancellationToken cancellationToken = default) =>
        ExecuteDurable(new DurableFileReplayOperation(
            _context, DurableFileOperationKind.Delete, path,
            expectedKind: expectedKind, recursive: recursive, missingAllowed: missingAllowed), cancellationToken);

    internal ValueTask DeleteAsync(
        string path,
        FileSystemEntryKind expectedKind,
        bool recursive,
        bool missingAllowed,
        CancellationToken cancellationToken = default) =>
        ExecuteDurableAsync(new DurableFileReplayOperation(
            _context, DurableFileOperationKind.Delete, path,
            expectedKind: expectedKind, recursive: recursive, missingAllowed: missingAllowed), cancellationToken);

    internal void SynchronizeDirectory(string path, CancellationToken cancellationToken = default) =>
        ExecuteDurable(new DurableFileReplayOperation(_context, DurableFileOperationKind.Synchronize, path), cancellationToken);

    internal ValueTask SynchronizeDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        ExecuteDurableAsync(new DurableFileReplayOperation(_context, DurableFileOperationKind.Synchronize, path), cancellationToken);

    internal T Execute<T>(
        string phase,
        Func<FileSystemReplayOutcome<T>> primitive,
        CancellationToken cancellationToken = default) =>
        _context.Executor.Execute(
            new PrimitiveReplayOperation<T>(_context.Policy, phase, primitive), cancellationToken).Value;

    internal async ValueTask<T> ExecuteAsync<T>(
        string phase,
        Func<FileSystemReplayOutcome<T>> primitive,
        CancellationToken cancellationToken = default) =>
        (await _context.Executor.ExecuteAsync(
            new PrimitiveReplayOperation<T>(_context.Policy, phase, primitive), cancellationToken).ConfigureAwait(false)).Value;

    private T ExecutePath<T>(
        string phase,
        string path,
        Func<string, FileSystemReplayOutcome<T>> primitive,
        CancellationToken cancellationToken) =>
        _context.Executor.Execute(
            new PathReplayOperation<T>(_context, phase, path, primitive), cancellationToken).Value;

    private async ValueTask<T> ExecutePathAsync<T>(
        string phase,
        string path,
        Func<string, FileSystemReplayOutcome<T>> primitive,
        CancellationToken cancellationToken) =>
        (await _context.Executor.ExecuteAsync(
            new PathReplayOperation<T>(_context, phase, path, primitive), cancellationToken).ConfigureAwait(false)).Value;

    private void ExecuteDurable(DurableFileReplayOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            _context.Executor.Execute(operation, cancellationToken);
        }
        catch
        {
            operation.ReleaseAfterFailure();
            throw;
        }
    }

    private async ValueTask ExecuteDurableAsync(DurableFileReplayOperation operation, CancellationToken cancellationToken)
    {
        try
        {
            await _context.Executor.ExecuteAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            operation.ReleaseAfterFailure();
            throw;
        }
    }

    internal static FileSystemReplayOutcome<string> ResolvePathAttempt(IFileSystemPrimitives primitives, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.IsPathFullyQualified(path)
            ? FileSystemReplayOutcome<string>.Complete(Path.GetFullPath(path))
            : primitives.GetFullPath(path);
    }

    internal static FileSystemReplayOutcome<TTo> Failure<TFrom, TTo>(FileSystemReplayOutcome<TFrom> outcome)
    {
        if (outcome.Kind == FileSystemReplayOutcomeKind.PrimitiveFailure)
            return FileSystemReplayOutcome<TTo>.Failed(outcome.PrimitiveFailure!);
        if (outcome.Kind == FileSystemReplayOutcomeKind.SemanticFailure)
            outcome.SemanticException!.Throw();

        throw new InvalidOperationException("Expected a failed primitive outcome.");
    }

    // Compatibility seams may consume a single raw result without scheduling.
    // In particular lock coordination keeps its existing timing until migration.
    internal static T ValueOrThrow<T>(FileSystemReplayOutcome<T> outcome)
    {
        if (outcome.Kind == FileSystemReplayOutcomeKind.PrimitiveFailure)
            outcome.PrimitiveFailure!.Throw();
        if (outcome.Kind == FileSystemReplayOutcomeKind.SemanticFailure)
            outcome.SemanticException!.Throw();
        return outcome.Value;
    }

    internal static FileSystemReplayOutcome<string[]> SnapshotAttempt(
        IFileSystemPrimitives primitives,
        string path,
        CancellationToken cancellationToken)
    {
        FileSystemReplayOutcome<IEnumerator<string>> opened = primitives.CreateDirectoryEnumerator(path);
        if (opened.Kind != FileSystemReplayOutcomeKind.Completed)
            return Failure<IEnumerator<string>, string[]>(opened);

        IEnumerator<string> enumerator = opened.Value;
        FileSystemReplayOutcome<string[]> snapshot;
        try
        {
            snapshot = ReadSnapshot(primitives, path, enumerator, cancellationToken);
        }
        catch
        {
            try
            {
                primitives.DisposeEnumerator(enumerator, path);
            }
            catch
            {
                // An application/semantic exception remains primary.
            }
            throw;
        }

        FileSystemReplayOutcome<bool> disposed;
        try
        {
            disposed = primitives.DisposeEnumerator(enumerator, path);
        }
        catch when (snapshot.Kind != FileSystemReplayOutcomeKind.Completed)
        {
            return snapshot;
        }
        if (snapshot.Kind != FileSystemReplayOutcomeKind.Completed)
            return snapshot;
        return disposed.Kind == FileSystemReplayOutcomeKind.Completed
            ? snapshot
            : Failure<bool, string[]>(disposed);
    }

    private static FileSystemReplayOutcome<string[]> ReadSnapshot(
        IFileSystemPrimitives primitives,
        string path,
        IEnumerator<string> enumerator,
        CancellationToken cancellationToken)
    {
        List<string> entries = [];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemReplayOutcome<bool> advanced = primitives.MoveNext(enumerator, path);
            if (advanced.Kind != FileSystemReplayOutcomeKind.Completed)
                return Failure<bool, string[]>(advanced);
            if (!advanced.Value)
                return FileSystemReplayOutcome<string[]>.Complete(entries.ToArray());

            FileSystemReplayOutcome<string> current = primitives.GetCurrent(enumerator, path);
            if (current.Kind != FileSystemReplayOutcomeKind.Completed)
                return Failure<string, string[]>(current);
            entries.Add(current.Value);
        }
    }

    internal static async ValueTask ReplaceWindowsFileDurablyAsync(
        string sourcePath,
        string destinationPath,
        Func<string, string, uint, bool> moveFileEx,
        Func<int> getLastError,
        Func<TimeSpan, CancellationToken, ValueTask> delayAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(moveFileEx);
        ArgumentNullException.ThrowIfNull(getLastError);
        ArgumentNullException.ThrowIfNull(delayAsync);
        FileSystemRetryExecutor executor = new FileSystemRetryExecutor(
            Random.Shared.NextInt64,
            static (_, _) => throw new InvalidOperationException("This compatibility operation is asynchronous."),
            (milliseconds, token) => delayAsync(TimeSpan.FromMilliseconds(milliseconds), token));
        FileSystemContext context = new FileSystemContext(
            new FileSystemRetryOptions(), SystemFileSystemPrimitives.Instance, executor);
        await context.FileSystem.ExecuteAsync(
            "Replace",
            () => NativeFileSystemPrimitives.MoveWindowsPath(
                sourcePath, destinationPath,
                NativeFileSystemPrimitives.MoveFileReplaceExisting | NativeFileSystemPrimitives.MoveFileWriteThrough,
                moveFileEx, getLastError),
            cancellationToken).ConfigureAwait(false);
    }

    internal static FileSystemDirectorySyncOutcome SynchronizeMacDirectory(
        Func<int> fullSync,
        Func<int> standardSync,
        Func<int> getLastError)
    {
        ArgumentNullException.ThrowIfNull(fullSync);
        ArgumentNullException.ThrowIfNull(standardSync);
        ArgumentNullException.ThrowIfNull(getLastError);
        // The historical injected seam describes one capability attempt. The live
        // operation below retains its full/standard phase and uses the sole executor.
        FileSystemDirectorySyncOutcome outcome = ValueOrThrow(
            NativeFileSystemPrimitives.SynchronizeMacFull(fullSync, getLastError));
        return outcome == FileSystemDirectorySyncOutcome.Unsupported
            ? ValueOrThrow(NativeFileSystemPrimitives.SynchronizeStandard(standardSync, getLastError, macOS: true))
            : outcome;
    }

    internal static int OpenValidatedLinuxDescriptor(
        string path,
        Func<string, int, uint, int> openFile,
        Func<int, int> getDescriptorMode,
        Func<int, int, int, int> duplicateDescriptor,
        Func<int, int> closeDescriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(openFile);
        ArgumentNullException.ThrowIfNull(getDescriptorMode);
        ArgumentNullException.ThrowIfNull(duplicateDescriptor);
        ArgumentNullException.ThrowIfNull(closeDescriptor);
        int descriptor = ValueOrThrow(NativeFileSystemPrimitives.OpenDescriptor(
            path, NativeFileSystemPrimitives.LinuxManifestOpenFlags, 0,
            openFile, Marshal.GetLastPInvokeError));
        bool ownsDescriptor = true;
        try
        {
            if ((getDescriptorMode(descriptor) & 0xF000) != 0x8000)
                throw new IOException("The package manifest is not a regular file.");

            ownsDescriptor = false;
            return GuardDescriptorForTransfer(
                descriptor, NativeFileSystemPrimitives.LinuxDuplicateFileDescriptorCloseOnExec,
                duplicateDescriptor, closeDescriptor);
        }
        catch
        {
            if (ownsDescriptor)
                CloseAfterFailure(descriptor, closeDescriptor);
            throw;
        }
    }

    internal static int GuardDescriptorForTransfer(
        int descriptor,
        int duplicateCommand,
        Func<int, int, int, int> duplicateDescriptor,
        Func<int, int> closeDescriptor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(descriptor);
        ArgumentNullException.ThrowIfNull(duplicateDescriptor);
        ArgumentNullException.ThrowIfNull(closeDescriptor);
        if (descriptor != 0)
            return descriptor;

        bool ownsOriginal = true;
        int? duplicate = null;
        try
        {
            int duplicated = ValueOrThrow(NativeFileSystemPrimitives.DuplicateDescriptor(
                descriptor, duplicateCommand, NativeFileSystemPrimitives.MinimumTransferDescriptor,
                duplicateDescriptor, Marshal.GetLastPInvokeError));
            if (duplicated != descriptor)
                duplicate = duplicated;
            if (duplicated < NativeFileSystemPrimitives.MinimumTransferDescriptor)
                throw new IOException("The duplicated package manifest descriptor was below the safe minimum.");

            ownsOriginal = false;
            ValueOrThrow(NativeFileSystemPrimitives.CloseDescriptor(
                descriptor, closeDescriptor, Marshal.GetLastPInvokeError));
            duplicate = null;
            return duplicated;
        }
        catch
        {
            if (ownsOriginal)
                CloseAfterFailure(descriptor, closeDescriptor);
            if (duplicate.HasValue)
                CloseAfterFailure(duplicate.Value, closeDescriptor);
            throw;
        }
    }

    internal static TResult TransferDescriptorOwnership<THandle, TResult>(
        int descriptor,
        Func<int, THandle> createHandle,
        Func<THandle, TResult> createOwner,
        Action<THandle> disposeHandle,
        Func<int, int> closeDescriptor)
        where THandle : class
        where TResult : class
    {
        ArgumentOutOfRangeException.ThrowIfNegative(descriptor);
        ArgumentNullException.ThrowIfNull(createHandle);
        ArgumentNullException.ThrowIfNull(createOwner);
        ArgumentNullException.ThrowIfNull(disposeHandle);
        ArgumentNullException.ThrowIfNull(closeDescriptor);
        THandle? handle = null;
        try
        {
            handle = createHandle(descriptor)
                ?? throw new IOException("The package manifest handle could not be created.");
            return createOwner(handle)
                ?? throw new IOException("The package manifest stream could not be created.");
        }
        catch
        {
            if (handle is null)
            {
                CloseAfterFailure(descriptor, closeDescriptor);
            }
            else
            {
                try
                {
                    disposeHandle(handle);
                }
                catch
                {
                    // Release once without replacing the construction failure.
                }
            }
            throw;
        }
    }

    private static void CloseAfterFailure(int descriptor, Func<int, int> closeDescriptor)
    {
        try
        {
            closeDescriptor(descriptor);
        }
        catch
        {
            // A failed/ambiguous close cannot be repeated or replace the primary error.
        }
    }

    private sealed class PrimitiveReplayOperation<T> : FileSystemReplayOperation<T>
    {
        private readonly Func<FileSystemReplayOutcome<T>> _primitive;

        internal PrimitiveReplayOperation(
            FileSystemRetryPolicy policy,
            string phase,
            Func<FileSystemReplayOutcome<T>> primitive) : base(policy, phase) =>
            _primitive = primitive;

        internal override FileSystemReplayOutcome<T> ExecuteStep(CancellationToken cancellationToken) => _primitive();
    }

    private sealed class PathReplayOperation<T> : FileSystemReplayOperation<T>
    {
        private readonly IFileSystemPrimitives _primitives;
        private readonly string _path;
        private readonly string _operationPhase;
        private readonly Func<string, FileSystemReplayOutcome<T>> _primitive;
        private string? _fullPath;

        internal PathReplayOperation(
            FileSystemContext context,
            string phase,
            string path,
            Func<string, FileSystemReplayOutcome<T>> primitive) : base(context.Policy, "ResolvePath")
        {
            _primitives = context.Primitives;
            _path = path;
            _operationPhase = phase;
            _primitive = primitive;
        }

        internal override FileSystemReplayOutcome<T> ExecuteStep(CancellationToken cancellationToken)
        {
            if (_fullPath is null)
            {
                FileSystemReplayOutcome<string> resolved = ResolvePathAttempt(_primitives, _path);
                if (resolved.Kind != FileSystemReplayOutcomeKind.Completed)
                    return Failure<string, T>(resolved);
                _fullPath = resolved.Value;
                SetPhase(_operationPhase);
            }
            return _primitive(_fullPath);
        }
    }

    private sealed class RegularFileReplayOperation<T> : FileSystemReplayOperation<T> where T : class
    {
        private readonly IFileSystemPrimitives _primitives;
        private readonly string _path;
        private readonly Func<string, FileSystemReplayOutcome<T>> _openWindows;
        private readonly Func<SafeFileHandle, T> _createOwner;
        private string? _fullPath;
        private int? _descriptor;
        private int? _duplicate;
        private int _stage;

        internal RegularFileReplayOperation(
            FileSystemContext context, string path,
            Func<string, FileSystemReplayOutcome<T>> openWindows,
            Func<SafeFileHandle, T> createOwner) : base(context.Policy, "ResolvePath")
        {
            _primitives = context.Primitives;
            _path = path;
            _openWindows = openWindows;
            _createOwner = createOwner;
        }

        internal string? FullPath => _fullPath;

        internal override FileSystemReplayOutcome<T> ExecuteStep(CancellationToken cancellationToken)
        {
            if (_fullPath is null)
            {
                FileSystemReplayOutcome<string> resolved = ResolvePathAttempt(_primitives, _path);
                if (resolved.Kind != FileSystemReplayOutcomeKind.Completed)
                    return Failure<string, T>(resolved);
                _fullPath = resolved.Value;
                if (_primitives.Platform == FileSystemPlatform.Unsupported)
                    throw new PlatformNotSupportedException("Regular cache-file verification requires Windows, Linux, or macOS.");
                SetPhase("InspectRegularPath");
            }

            if (_stage == 0)
            {
                SetPhase("InspectRegularPath");
                if (_primitives.Platform == FileSystemPlatform.Windows)
                {
                    FileSystemReplayOutcome<FileAttributes> attributes = _primitives.GetAttributes(_fullPath);
                    if (attributes.Kind != FileSystemReplayOutcomeKind.Completed)
                        return Failure<FileAttributes, T>(attributes);
                    RequireRegular(FileSystemProbeResult.FromAttributes(attributes.Value));
                }
                else if (_primitives.Platform == FileSystemPlatform.MacOS)
                {
                    FileSystemReplayOutcome<FileSystemProbeResult> probe = _primitives.Probe(_fullPath);
                    if (probe.Kind is not (FileSystemReplayOutcomeKind.Completed or FileSystemReplayOutcomeKind.Missing))
                        return Failure<FileSystemProbeResult, T>(probe);
                    if (probe.Value.Exists)
                        RequireRegular(probe.Value);
                }
                _stage = 1;
                SetPhase("OpenRegularFile");
                return FileSystemReplayOutcome<T>.Continue();
            }

            if (_primitives.Platform == FileSystemPlatform.Windows)
            {
                FileSystemReplayOutcome<T> opened = _openWindows(_fullPath);
                if (opened.Kind != FileSystemReplayOutcomeKind.Completed)
                    _stage = 0;
                return opened;
            }

            if (_stage == 1)
            {
                int flags = _primitives.Platform == FileSystemPlatform.Linux
                    ? NativeFileSystemPrimitives.LinuxManifestOpenFlags
                    : NativeFileSystemPrimitives.MacManifestOpenFlags;
                FileSystemReplayOutcome<int> opened = _primitives.OpenDescriptor(_fullPath, flags, 0);
                if (opened.Kind != FileSystemReplayOutcomeKind.Completed)
                {
                    _stage = 0;
                    return Failure<int, T>(opened);
                }
                _descriptor = opened.Value;
                _stage = 2;
                SetPhase("InspectRegularDescriptor");
                return FileSystemReplayOutcome<T>.Continue();
            }

            if (_stage == 2)
            {
                FileSystemReplayOutcome<FileSystemProbeResult> inspected = _primitives.InspectDescriptor(_descriptor!.Value, _fullPath);
                if (inspected.Kind != FileSystemReplayOutcomeKind.Completed)
                    return Failure<FileSystemProbeResult, T>(inspected);
                RequireRegular(inspected.Value);
                _stage = _descriptor == 0 ? 3 : 5;
                SetPhase(_stage == 3 ? "DuplicateZeroDescriptor" : "TransferDescriptor");
                return FileSystemReplayOutcome<T>.Continue();
            }

            if (_stage == 3)
            {
                int command = _primitives.Platform == FileSystemPlatform.Linux
                    ? NativeFileSystemPrimitives.LinuxDuplicateFileDescriptorCloseOnExec
                    : NativeFileSystemPrimitives.DarwinDuplicateFileDescriptorCloseOnExec;
                FileSystemReplayOutcome<int> duplicated = _primitives.DuplicateDescriptor(
                    _descriptor!.Value, command, NativeFileSystemPrimitives.MinimumTransferDescriptor, _fullPath);
                if (duplicated.Kind != FileSystemReplayOutcomeKind.Completed)
                    return Failure<int, T>(duplicated);
                if (duplicated.Value != _descriptor)
                    _duplicate = duplicated.Value;
                if (duplicated.Value < NativeFileSystemPrimitives.MinimumTransferDescriptor)
                    throw new IOException("The duplicated package manifest descriptor was below the safe minimum.");
                _stage = 4;
                SetPhase("CloseOriginalDescriptor");
                return FileSystemReplayOutcome<T>.Continue();
            }

            if (_stage == 4)
            {
                int descriptor = _descriptor!.Value;
                _descriptor = null;
                FileSystemReplayOutcome<bool> closed = _primitives.CloseDescriptor(descriptor, _fullPath);
                if (closed.Kind != FileSystemReplayOutcomeKind.Completed)
                    return Failure<bool, T>(closed);
                _descriptor = _duplicate;
                _duplicate = null;
                _stage = 5;
                SetPhase("TransferDescriptor");
                return FileSystemReplayOutcome<T>.Continue();
            }

            int transferDescriptor = _descriptor!.Value;
            _descriptor = null;
            T owner = TransferDescriptorOwnership<SafeFileHandle, T>(
                transferDescriptor,
                value => new SafeFileHandle(new IntPtr(value), ownsHandle: true),
                _createOwner,
                handle => handle.Dispose(),
                value => ValueOrThrow(_primitives.CloseDescriptor(value, _fullPath)) ? 0 : -1);
            return FileSystemReplayOutcome<T>.Complete(owner);
        }

        private static void RequireRegular(FileSystemProbeResult probe)
        {
            if (probe.Kind != FileSystemEntryKind.File)
                throw new IOException("The package manifest is not a regular file.");
        }

        internal void ReleaseAfterFailure()
        {
            int? descriptor = _descriptor;
            int? duplicate = _duplicate;
            _descriptor = null;
            _duplicate = null;
            if (descriptor.HasValue)
                Release(descriptor.Value);
            if (duplicate.HasValue)
                Release(duplicate.Value);
        }

        private void Release(int descriptor)
        {
            try
            {
                _primitives.CloseDescriptor(descriptor, _fullPath);
            }
            catch
            {
                // Preserve the primary error, and never retry release.
            }
        }
    }
}
