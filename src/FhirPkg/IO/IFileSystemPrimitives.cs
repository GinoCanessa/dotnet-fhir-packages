// Copyright (c) Gino Canessa. Licensed under the MIT License.

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
