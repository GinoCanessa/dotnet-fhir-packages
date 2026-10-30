// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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

    public FileSystemReplayOutcome<SafeFileHandle> OpenFileHandle(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options)
    {
        if (mode is FileMode.Open or FileMode.Truncate)
        {
            try
            {
                // Truncation is a later, same-handle phase, not a constructor side effect.
                return FileSystemReplayOutcome<SafeFileHandle>.Complete(
                    File.OpenHandle(path, FileMode.Open, access, share, options));
            }
            catch (Exception exception) when (IsPhysicalFailure(exception))
            {
                return Failed<SafeFileHandle>(exception, "OpenFileHandle", path);
            }
        }

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unix owned creation uses a retained descriptor.");

        uint desiredAccess = (access.HasFlag(FileAccess.Read) ? 0x80000000U : 0)
            | (access.HasFlag(FileAccess.Write) ? 0x40000000U : 0);
        uint disposition = mode == FileMode.CreateNew ? 1U : 4U;
        uint flags = unchecked((uint)options) | 0x80U | 0x00100000U;
        FileSecurityAttributes security = new FileSecurityAttributes
        {
            Length = Marshal.SizeOf<FileSecurityAttributes>(),
            SecurityDescriptor = IntPtr.Zero,
            InheritHandle = (share & FileShare.Inheritable) != 0,
        };
        SafeFileHandle handle = CreateFile(
            NativeFileSystemPrimitives.ToWindowsExtendedPath(path),
            desiredAccess,
            (uint)(share & ~FileShare.Inheritable),
            ref security,
            disposition,
            flags,
            IntPtr.Zero);
        int error = Marshal.GetLastPInvokeError();
        if (!handle.IsInvalid)
            return FileSystemReplayOutcome<SafeFileHandle>.Complete(handle);

        handle.Dispose();
        return NativeFileSystemPrimitives.OwnedOpenFailure<SafeFileHandle>(path, mode, error, flags, Platform);
    }

    public FileSystemReplayOutcome<int> OpenOwnedDescriptor(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options) =>
        NativeFileSystemPrimitives.OpenOwnedDescriptor(path, mode, access, share, options);

    public FileSystemReplayOutcome<bool> ConfigureFileSharing(SafeFileHandle handle, FileShare share, string path)
    {
        int flags = (share & ~FileShare.Inheritable) == FileShare.None ? 2 | 4 : 1 | 4;
        int result = LockOwned(handle, flags);
        int error = Marshal.GetLastPInvokeError();
        return result == 0
            ? FileSystemReplayOutcome<bool>.Complete(true)
            : NativeHandleFailure<bool>(error, "flock(owned file)", path, flags);
    }

    public FileSystemReplayOutcome<bool> AdviseFileAccess(SafeFileHandle handle, FileOptions options, string path)
    {
        if ((options & (FileOptions.RandomAccess | FileOptions.SequentialScan)) == 0)
            return FileSystemReplayOutcome<bool>.Complete(true);

        bool random = (options & FileOptions.RandomAccess) != 0;
        if (OperatingSystem.IsLinux())
        {
            int advice = random ? 1 : 2;
            int error = AdviseOwned(handle, 0, 0, advice);
            return error == 0
                ? FileSystemReplayOutcome<bool>.Complete(true)
                : NativeHandleFailure<bool>(error, "posix_fadvise", path, advice);
        }

        UnixDescriptorResult result = UnixDescriptorInterop.ControlInteger(handle, 45, random ? 0 : 1);
        return result.ReturnValue == 0
            ? FileSystemReplayOutcome<bool>.Complete(true)
            : NativeHandleFailure<bool>(result.Error, "fcntl(F_RDAHEAD)", path, 45);
    }

    public FileSystemReplayOutcome<int> ReadAt(
        SafeFileHandle handle, Memory<byte> buffer, long offset, string path)
    {
        try
        {
            return FileSystemReplayOutcome<int>.Complete(RandomAccess.Read(handle, buffer.Span, offset));
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<int>(exception, "RandomAccess.Read", path);
        }
    }

    public async ValueTask<FileSystemReplayOutcome<int>> ReadAtAsync(
        SafeFileHandle handle, Memory<byte> buffer, long offset, string path, CancellationToken cancellationToken)
    {
        try
        {
            return FileSystemReplayOutcome<int>.Complete(
                await RandomAccess.ReadAsync(handle, buffer, offset, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<int>(exception, "RandomAccess.ReadAsync", path);
        }
    }

    public FileSystemReplayOutcome<bool> WriteAt(
        SafeFileHandle handle, ReadOnlyMemory<byte> buffer, long offset, string path)
    {
        try
        {
            RandomAccess.Write(handle, buffer.Span, offset);
            return FileSystemReplayOutcome<bool>.Complete(true);
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            // The owner retains these bytes and an exclusive, non-append handle.
            return Failed<bool>(exception, "RandomAccess.Write", path);
        }
    }

    public async ValueTask<FileSystemReplayOutcome<bool>> WriteAtAsync(
        SafeFileHandle handle, ReadOnlyMemory<byte> buffer, long offset, string path, CancellationToken cancellationToken)
    {
        try
        {
            await RandomAccess.WriteAsync(handle, buffer, offset, cancellationToken).ConfigureAwait(false);
            return FileSystemReplayOutcome<bool>.Complete(true);
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<bool>(exception, "RandomAccess.WriteAsync", path);
        }
    }

    public FileSystemReplayOutcome<long> GetLength(SafeFileHandle handle, string path)
    {
        try
        {
            return FileSystemReplayOutcome<long>.Complete(RandomAccess.GetLength(handle));
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<long>(exception, "RandomAccess.GetLength", path);
        }
    }

    public FileSystemReplayOutcome<bool> SetLength(SafeFileHandle handle, long length, string path)
    {
        try
        {
            RandomAccess.SetLength(handle, length);
            return FileSystemReplayOutcome<bool>.Complete(true);
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<bool>(exception, "RandomAccess.SetLength", path);
        }
    }

    public FileSystemReplayOutcome<bool> FlushFile(SafeFileHandle handle, string path)
    {
        try
        {
            RandomAccess.FlushToDisk(handle);
            return FileSystemReplayOutcome<bool>.Complete(true);
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<bool>(exception, "RandomAccess.FlushToDisk", path);
        }
    }

    public FileSystemReplayOutcome<bool> CloseFile(SafeFileHandle handle, string path)
    {
        try
        {
            handle.Dispose();
            return FileSystemReplayOutcome<bool>.Complete(true);
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<bool>(exception, "SafeFileHandle.Dispose", path, FileSystemReplaySafety.Unsafe);
        }
    }

    public FileSystemReplayOutcome<BorrowedFileReadState> PrepareBorrowedRead(FileStream stream)
    {
        try
        {
            // Flush/getter/seek can emit pending caller writes. This preparation
            // is deliberately one-shot, even for a readable, seekable FileStream.
            stream.Flush();
            SafeFileHandle handle = stream.SafeFileHandle;
            return FileSystemReplayOutcome<BorrowedFileReadState>.Complete(
                new BorrowedFileReadState(handle, stream.Position));
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<BorrowedFileReadState>(
                exception, "PrepareBorrowedRead", stream.Name, FileSystemReplaySafety.Unsafe);
        }
    }

    public FileSystemReplayOutcome<long> GetPosition(FileStream stream)
    {
        try
        {
            return FileSystemReplayOutcome<long>.Complete(stream.Position);
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<long>(exception, "FileStream.Position", stream.Name);
        }
    }

    public FileSystemReplayOutcome<long> SetPosition(FileStream stream, long position)
    {
        try
        {
            return FileSystemReplayOutcome<long>.Complete(stream.Seek(position, SeekOrigin.Begin));
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<long>(exception, "FileStream.Seek", stream.Name);
        }
    }

    public FileSystemReplayOutcome<int> ReadBorrowed(FileStream stream, Memory<byte> buffer)
    {
        try
        {
            return FileSystemReplayOutcome<int>.Complete(stream.Read(buffer.Span));
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<int>(exception, "FileStream.Read", stream.Name);
        }
    }

    public async ValueTask<FileSystemReplayOutcome<int>> ReadBorrowedAsync(
        FileStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            return FileSystemReplayOutcome<int>.Complete(
                await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (IsPhysicalFailure(exception))
        {
            return Failed<int>(exception, "FileStream.ReadAsync", stream.Name);
        }
    }

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

    private static FileSystemReplayOutcome<T> NativeHandleFailure<T>(
        int error, string operation, string path, long flags) =>
        FileSystemReplayOutcome<T>.Failed(FileSystemFailureContext.CapturePrimitiveFailure(
            new IOException($"The file handle operation '{operation}' failed.", new Win32Exception(error)),
            FileSystemReplaySafety.Safe, operation, path, nativeErrorCode: error, nativeFlags: flags));

    [StructLayout(LayoutKind.Sequential)]
    private struct FileSecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        internal bool InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string path, uint access, uint share, ref FileSecurityAttributes security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int LockOwned(SafeFileHandle handle, int flags);

    [DllImport("libc", EntryPoint = "posix_fadvise")]
    private static extern int AdviseOwned(SafeFileHandle handle, long offset, long length, int advice);

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
