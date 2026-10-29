// Copyright (c) Gino Canessa. Licensed under the MIT License.

using Microsoft.Win32.SafeHandles;

namespace FhirPkg.IO;

/// <summary>
/// Single-attempt physical operations. Implementations capture errors at the call
/// boundary, do not schedule retries, and never call the resilient facade.
/// </summary>
internal interface IFileSystemPrimitives
{
    FileSystemPlatform Platform { get; }

    FileSystemReplayOutcome<string> GetCurrentDirectory();

    FileSystemReplayOutcome<string> GetTempPath();

    FileSystemReplayOutcome<string> GetHomeDirectory();

    FileSystemReplayOutcome<string> GetFullPath(string path);

    FileSystemReplayOutcome<FileSystemProbeResult> Probe(string path, bool followLinks = false);

    FileSystemReplayOutcome<FileAttributes> GetAttributes(string path);

    FileSystemReplayOutcome<long> GetLength(string path);

    FileSystemReplayOutcome<IEnumerator<string>> CreateDirectoryEnumerator(string path);

    FileSystemReplayOutcome<bool> MoveNext(IEnumerator<string> enumerator, string path);

    FileSystemReplayOutcome<string> GetCurrent(IEnumerator<string> enumerator, string path);

    FileSystemReplayOutcome<bool> DisposeEnumerator(IEnumerator<string> enumerator, string path);

    FileSystemReplayOutcome<FileStream> OpenFile(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        int bufferSize,
        FileOptions options);

    FileSystemReplayOutcome<SafeFileHandle> OpenFileHandle(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options);

    FileSystemReplayOutcome<int> OpenOwnedDescriptor(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options);

    FileSystemReplayOutcome<bool> ConfigureFileSharing(SafeFileHandle handle, FileShare share, string path);

    FileSystemReplayOutcome<bool> AdviseFileAccess(SafeFileHandle handle, FileOptions options, string path);

    FileSystemReplayOutcome<int> ReadAt(SafeFileHandle handle, Memory<byte> buffer, long offset, string path);

    ValueTask<FileSystemReplayOutcome<int>> ReadAtAsync(
        SafeFileHandle handle, Memory<byte> buffer, long offset, string path, CancellationToken cancellationToken);

    FileSystemReplayOutcome<bool> WriteAt(SafeFileHandle handle, ReadOnlyMemory<byte> buffer, long offset, string path);

    ValueTask<FileSystemReplayOutcome<bool>> WriteAtAsync(
        SafeFileHandle handle, ReadOnlyMemory<byte> buffer, long offset, string path, CancellationToken cancellationToken);

    FileSystemReplayOutcome<long> GetLength(SafeFileHandle handle, string path);

    FileSystemReplayOutcome<bool> SetLength(SafeFileHandle handle, long length, string path);

    FileSystemReplayOutcome<bool> FlushFile(SafeFileHandle handle, string path);

    FileSystemReplayOutcome<bool> CloseFile(SafeFileHandle handle, string path);

    FileSystemReplayOutcome<BorrowedFileReadState> PrepareBorrowedRead(FileStream stream);

    FileSystemReplayOutcome<long> GetPosition(FileStream stream);

    FileSystemReplayOutcome<long> SetPosition(FileStream stream, long position);

    FileSystemReplayOutcome<int> ReadBorrowed(FileStream stream, Memory<byte> buffer);

    ValueTask<FileSystemReplayOutcome<int>> ReadBorrowedAsync(
        FileStream stream, Memory<byte> buffer, CancellationToken cancellationToken);

    FileSystemReplayOutcome<bool> CreateDirectory(string path);

    FileSystemReplayOutcome<bool> MovePath(string sourcePath, string destinationPath, bool replace);

    FileSystemReplayOutcome<bool> DeleteEntry(string path, FileSystemEntryKind kind);

    FileSystemReplayOutcome<int> OpenDescriptor(string path, int flags, uint mode);

    FileSystemReplayOutcome<FileSystemProbeResult> InspectDescriptor(int descriptor, string? path = null);

    FileSystemReplayOutcome<int> DuplicateDescriptor(int descriptor, int command, int minimum, string? path = null);

    FileSystemReplayOutcome<FileSystemDirectorySyncOutcome> SynchronizeDescriptor(
        int descriptor,
        bool fullSync,
        string? path = null);

    FileSystemReplayOutcome<bool> CloseDescriptor(int descriptor, string? path = null);

    FileSystemReplayOutcome<bool> TryLock(FileStream stream);

    FileSystemReplayOutcome<bool> Unlock(FileStream stream);
}
