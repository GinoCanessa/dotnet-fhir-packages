// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.IO;

internal sealed class SystemFileSystemPrimitives : IFileSystemPrimitives
{
    internal static SystemFileSystemPrimitives Instance { get; } = new();

    private SystemFileSystemPrimitives()
    {
    }

    public FileSystemPlatform Platform => NativeFileSystemPrimitives.Platform;

    public FileSystemReplayOutcome<string> GetCurrentDirectory()
    {
        try
        {
            return FileSystemReplayOutcome<string>.Complete(Directory.GetCurrentDirectory());
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<string>(exception, "GetCurrentDirectory");
        }
    }

    public FileSystemReplayOutcome<string> GetTempPath()
    {
        try
        {
            return FileSystemReplayOutcome<string>.Complete(Path.GetTempPath());
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<string>(exception, "GetTempPath");
        }
    }

    public FileSystemReplayOutcome<string> GetHomeDirectory()
    {
        try
        {
            return FileSystemReplayOutcome<string>.Complete(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<string>(exception, "GetFolderPath(UserProfile)");
        }
    }

    public FileSystemReplayOutcome<string> GetFullPath(string path)
    {
        try
        {
            // Do not substitute a supplied base path: drive-relative Windows paths
            // also consult the per-drive working directory.
            return FileSystemReplayOutcome<string>.Complete(Path.GetFullPath(path));
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<string>(exception, "GetFullPath", path);
        }
    }

    public FileSystemReplayOutcome<FileSystemProbeResult> Probe(string path, bool followLinks = false) =>
        NativeFileSystemPrimitives.Probe(path, followLinks);

    public FileSystemReplayOutcome<FileAttributes> GetAttributes(string path)
    {
        try
        {
            return FileSystemReplayOutcome<FileAttributes>.Complete(File.GetAttributes(path));
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<FileAttributes>(exception, "GetAttributes", path);
        }
    }

    public FileSystemReplayOutcome<long> GetLength(string path)
    {
        FileInfo file = new FileInfo(path);
        try
        {
            return FileSystemReplayOutcome<long>.Complete(file.Length);
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<long>(exception, "FileInfo.Length", path);
        }
    }

    public FileSystemReplayOutcome<IEnumerator<string>> CreateDirectoryEnumerator(string path)
    {
        try
        {
            return FileSystemReplayOutcome<IEnumerator<string>>.Complete(
                Directory.EnumerateFileSystemEntries(path).GetEnumerator());
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<IEnumerator<string>>(exception, "EnumerateDirectory", path);
        }
    }

    public FileSystemReplayOutcome<bool> MoveNext(IEnumerator<string> enumerator, string path)
    {
        try
        {
            return FileSystemReplayOutcome<bool>.Complete(enumerator.MoveNext());
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<bool>(exception, "DirectoryEnumerator.MoveNext", path);
        }
    }

    public FileSystemReplayOutcome<string> GetCurrent(IEnumerator<string> enumerator, string path)
    {
        try
        {
            return FileSystemReplayOutcome<string>.Complete(enumerator.Current);
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<string>(exception, "DirectoryEnumerator.Current", path);
        }
    }

    public FileSystemReplayOutcome<bool> DisposeEnumerator(IEnumerator<string> enumerator, string path)
    {
        try
        {
            enumerator.Dispose();
            return FileSystemReplayOutcome<bool>.Complete(true);
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            // Safe for a new snapshot, not for a second disposal of this enumerator.
            return Failed<bool>(exception, "DirectoryEnumerator.Dispose", path);
        }
    }

    public FileSystemReplayOutcome<FileStream> OpenFile(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        int bufferSize,
        FileOptions options)
    {
        try
        {
            return FileSystemReplayOutcome<FileStream>.Complete(
                new FileStream(path, mode, access, share, bufferSize, options));
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            // A failed managed constructor may have created/truncated the file
            // before later setup failed. Never adopt that pathname on a retry.
            return Failed<FileStream>(
                exception,
                "OpenFile",
                path,
                mode == FileMode.Open ? FileSystemReplaySafety.Safe : FileSystemReplaySafety.Unsafe);
        }
    }

    public FileSystemReplayOutcome<bool> CreateDirectory(string path) =>
        NativeFileSystemPrimitives.CreateDirectory(path);

    public FileSystemReplayOutcome<bool> MovePath(
        string sourcePath,
        string destinationPath,
        bool replace) =>
        NativeFileSystemPrimitives.MovePath(sourcePath, destinationPath, replace);

    public FileSystemReplayOutcome<bool> DeleteEntry(string path, FileSystemEntryKind kind) =>
        NativeFileSystemPrimitives.DeleteEntry(path, kind);

    public FileSystemReplayOutcome<int> OpenDescriptor(string path, int flags, uint mode) =>
        NativeFileSystemPrimitives.OpenDescriptor(path, flags, mode);

    public FileSystemReplayOutcome<FileSystemProbeResult> InspectDescriptor(int descriptor, string? path = null) =>
        NativeFileSystemPrimitives.InspectDescriptor(descriptor, path);

    public FileSystemReplayOutcome<int> DuplicateDescriptor(int descriptor, int command, int minimum, string? path = null) =>
        NativeFileSystemPrimitives.DuplicateDescriptor(descriptor, command, minimum, path);

    public FileSystemReplayOutcome<FileSystemDirectorySyncOutcome> SynchronizeDescriptor(
        int descriptor,
        bool fullSync,
        string? path = null) =>
        NativeFileSystemPrimitives.SynchronizeDescriptor(descriptor, fullSync, path);

    public FileSystemReplayOutcome<bool> CloseDescriptor(int descriptor, string? path = null) =>
        NativeFileSystemPrimitives.CloseDescriptor(descriptor, path);

    public FileSystemReplayOutcome<bool> TryLock(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (OperatingSystem.IsMacOS())
        {
            return NativeFileSystemPrimitives.TryLockDescriptor(
                stream.SafeFileHandle.DangerousGetHandle().ToInt32());
        }

        try
        {
            stream.Lock(0, 1);
            return FileSystemReplayOutcome<bool>.Complete(true);
        }
        catch (IOException exception) when (IsManagedLockContention(exception))
        {
            return FileSystemReplayOutcome<bool>.Contended(false);
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<bool>(exception, "FileStream.Lock");
        }
    }

    public FileSystemReplayOutcome<bool> Unlock(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (OperatingSystem.IsMacOS())
        {
            return NativeFileSystemPrimitives.UnlockDescriptor(
                stream.SafeFileHandle.DangerousGetHandle().ToInt32());
        }

        try
        {
            stream.Unlock(0, 1);
            return FileSystemReplayOutcome<bool>.Complete(true);
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<bool>(exception, "FileStream.Unlock");
        }
    }

    internal static bool IsMacOpenContention(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return (exception.HResult & 0xFFFF) == 35;
    }

    internal static bool IsManagedLockContention(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        int error = exception.HResult & 0xFFFF;
        return OperatingSystem.IsWindows()
            ? error is 32 or 33
            : !OperatingSystem.IsMacOS() && error is 11 or 13;
    }

    private static bool IsPhysicalFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private static FileSystemReplayOutcome<T> Failed<T>(
        Exception exception,
        string operation,
        string? path = null,
        FileSystemReplaySafety safety = FileSystemReplaySafety.Safe) =>
        FileSystemReplayOutcome<T>.Failed(FileSystemFailureContext.CapturePrimitiveFailure(
            exception,
            safety,
            operation,
            path,
            nativeErrorCode: exception.HResult & 0xFFFF));
}
