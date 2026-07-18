// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FhirPkg.Cache;

internal interface IPackageCacheFileOperations
{
    bool DirectoryExists(string path);

    bool FileExists(string path);

    void CreateDirectory(string path);

    void MoveDirectory(string sourcePath, string destinationPath);

    void MoveFile(string sourcePath, string destinationPath);

    void DeleteDirectory(string path, bool recursive);

    void DeleteFile(string path);

    ValueTask WriteFileAndFlushAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);

    void AtomicReplaceFile(string sourcePath, string destinationPath);

    void SynchronizeDirectory(string directoryPath);

    PackageCacheArtifactKind GetArtifactKind(string path);

    bool ArtifactExists(
        string path,
        PackageCacheArtifactKind artifactKind);

    void MoveArtifact(
        string sourcePath,
        string destinationPath,
        PackageCacheArtifactKind artifactKind);

    void DeleteArtifact(
        string path,
        PackageCacheArtifactKind artifactKind);
}

internal sealed class SystemPackageCacheFileOperations :
    IPackageCacheFileOperations
{
    private const int BufferSize = 16_384;

    internal static SystemPackageCacheFileOperations Instance { get; } = new();

    private SystemPackageCacheFileOperations()
    {
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public void CreateDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        List<string> missingDirectories = [];
        string? candidate = fullPath;
        while (candidate is not null && !Directory.Exists(candidate))
        {
            missingDirectories.Add(candidate);
            candidate = Path.GetDirectoryName(candidate);
        }

        Directory.CreateDirectory(fullPath);
        for (int index = missingDirectories.Count - 1; index >= 0; index--)
        {
            string createdPath = missingDirectories[index];
            SynchronizeDirectory(createdPath);
            string? parent = Path.GetDirectoryName(createdPath);
            if (parent is not null)
                SynchronizeDirectory(parent);
        }
    }

    public void MoveDirectory(string sourcePath, string destinationPath) =>
        PackageCacheNativeFileSystem.MovePathDurably(
            sourcePath,
            destinationPath);

    public void MoveFile(string sourcePath, string destinationPath) =>
        PackageCacheNativeFileSystem.MovePathDurably(
            sourcePath,
            destinationPath);

    public void DeleteDirectory(string path, bool recursive)
    {
        Directory.Delete(path, recursive);
        SynchronizeParent(path);
    }

    public void DeleteFile(string path)
    {
        File.Delete(path);
        SynchronizeParent(path);
    }

    public async ValueTask WriteFileAndFlushAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    public void AtomicReplaceFile(
        string sourcePath,
        string destinationPath) =>
        PackageCacheNativeFileSystem.ReplaceFileDurably(
            sourcePath,
            destinationPath);

    public void SynchronizeDirectory(string directoryPath) =>
        PackageCacheNativeFileSystem.SynchronizeDirectory(directoryPath);

    public PackageCacheArtifactKind GetArtifactKind(string path)
    {
        if (!OperatingSystem.IsWindows()
            && PackageCacheNativeFileSystem.IsSymbolicLinkNoFollow(path))
        {
            return PackageCacheArtifactKind.SymbolicLink;
        }

        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return attributes.HasFlag(FileAttributes.Directory)
                    ? PackageCacheArtifactKind.DirectorySymbolicLink
                    : PackageCacheArtifactKind.SymbolicLink;
            }

            return attributes.HasFlag(FileAttributes.Directory)
                ? PackageCacheArtifactKind.Directory
                : PackageCacheArtifactKind.File;
        }
        catch (FileNotFoundException)
        {
            return PackageCacheNativeFileSystem.PathExistsNoFollow(path)
                ? PackageCacheArtifactKind.SymbolicLink
                : PackageCacheArtifactKind.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return PackageCacheNativeFileSystem.PathExistsNoFollow(path)
                ? PackageCacheArtifactKind.SymbolicLink
                : PackageCacheArtifactKind.Missing;
        }
    }

    public bool ArtifactExists(
        string path,
        PackageCacheArtifactKind artifactKind) =>
        artifactKind != PackageCacheArtifactKind.Missing
        && PackageCacheNativeFileSystem.PathExistsNoFollow(path);

    public void MoveArtifact(
        string sourcePath,
        string destinationPath,
        PackageCacheArtifactKind artifactKind)
    {
        if (artifactKind == PackageCacheArtifactKind.Missing)
            return;

        PackageCacheNativeFileSystem.MovePathDurably(
            sourcePath,
            destinationPath);
    }

    public void DeleteArtifact(
        string path,
        PackageCacheArtifactKind artifactKind)
    {
        if (artifactKind == PackageCacheArtifactKind.Missing
            || !PackageCacheNativeFileSystem.PathExistsNoFollow(path))
        {
            return;
        }

        switch (artifactKind)
        {
            case PackageCacheArtifactKind.Directory:
                DeleteDirectory(path, recursive: true);
                break;
            case PackageCacheArtifactKind.File:
                DeleteFile(path);
                break;
            case PackageCacheArtifactKind.SymbolicLink:
            case PackageCacheArtifactKind.DirectorySymbolicLink:
                PackageCacheNativeFileSystem.DeleteSymbolicLink(
                    path,
                    artifactKind
                        == PackageCacheArtifactKind.DirectorySymbolicLink);
                break;
        }
    }

    private void SynchronizeParent(string path)
    {
        string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is not null)
            SynchronizeDirectory(parent);
    }
}

internal static class PackageCacheDurableFileWriter
{
    internal static async Task WriteAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        IPackageCacheFileOperations fileOperations,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(fileOperations);

        string fullDestinationPath = Path.GetFullPath(destinationPath);
        string directoryPath = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new IOException(
                "The durable file destination has no parent directory.");
        fileOperations.CreateDirectory(directoryPath);
        string tempPath = Path.Combine(
            directoryPath,
            $".{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await fileOperations.WriteFileAndFlushAsync(
                    tempPath,
                    content,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            fileOperations.AtomicReplaceFile(
                tempPath,
                fullDestinationPath);
            fileOperations.SynchronizeDirectory(directoryPath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (UnauthorizedAccessException)
            {
                // Temporary cleanup must not replace the durable write outcome.
            }
            catch (IOException)
            {
                // Temporary cleanup must not replace the durable write outcome.
            }
        }
    }
}

internal enum PackageCacheDirectorySyncOutcome
{
    Full = 0,
    Standard = 1,
    Unsupported = 2
}

internal enum PackageCacheLinuxStatxOutcome
{
    Success = 0,
    Missing = 1,
    Unsupported = 2,
    Failure = 3
}

internal static class PackageCacheNativeFileSystem
{
    private const uint MoveFileReplaceExisting = 0x00000001;
    private const uint MoveFileWriteThrough = 0x00000008;
    private const uint InvalidFileAttributes = 0xFFFFFFFF;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int UnixInvalidArgument = 22;
    private const int UnixInappropriateIoControl = 25;
    private const int UnixFunctionNotImplemented = 38;
    private const int MacOperationNotSupported = 45;
    private const int UnixNotDirectory = 20;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymbolicLinkType = 0xA000;
    private const int UnixOpenReadOnly = 0;
    private const int MacFullFileSync = 51;
    private const int AtFileDescriptorCurrentWorkingDirectory = -100;
    private const int AtSymbolicLinkNoFollow = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x00000001;

    internal static void ReplaceFileDurably(
        string sourcePath,
        string destinationPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(
                ToWindowsExtendedPath(sourcePath),
                ToWindowsExtendedPath(destinationPath),
                MoveFileReplaceExisting | MoveFileWriteThrough))
            {
                throw NativeIOException(
                    "The durable file replacement failed.");
            }

            return;
        }

        EnsureSupportedUnix();
        if (Rename(sourcePath, destinationPath) != 0)
            throw NativeIOException("The atomic file replacement failed.");
    }

    internal static void MovePathDurably(
        string sourcePath,
        string destinationPath)
    {
        if (PathExistsNoFollow(destinationPath))
        {
            throw new IOException(
                $"The destination path '{destinationPath}' already exists.");
        }

        string sourceDirectory = Path.GetDirectoryName(
                Path.GetFullPath(sourcePath))
            ?? throw new IOException("The source path has no parent directory.");
        string destinationDirectory = Path.GetDirectoryName(
                Path.GetFullPath(destinationPath))
            ?? throw new IOException(
                "The destination path has no parent directory.");

        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(
                ToWindowsExtendedPath(sourcePath),
                ToWindowsExtendedPath(destinationPath),
                MoveFileWriteThrough))
            {
                throw NativeIOException("The durable path rename failed.");
            }

            return;
        }

        EnsureSupportedUnix();
        if (Rename(sourcePath, destinationPath) != 0)
            throw NativeIOException("The durable path rename failed.");

        SynchronizeDirectory(sourceDirectory);
        if (!string.Equals(
            sourceDirectory,
            destinationDirectory,
            StringComparison.Ordinal))
        {
            SynchronizeDirectory(destinationDirectory);
        }
    }

    internal static void SynchronizeDirectory(string directoryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            // MoveFileEx with MOVEFILE_WRITE_THROUGH provides the Windows
            // durability barrier for both replacements and path renames.
            return;
        }

        EnsureSupportedUnix();
        int descriptor = Open(directoryPath, UnixOpenReadOnly, 0);
        if (descriptor < 0)
            throw NativeIOException("The destination directory could not be opened for synchronization.");

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                _ = SynchronizeMacDirectory(
                    () => Fcntl(descriptor, MacFullFileSync),
                    () => Fsync(descriptor),
                    Marshal.GetLastPInvokeError);
            }
            else if (Fsync(descriptor) != 0)
            {
                throw NativeIOException("The destination directory synchronization failed.");
            }
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    internal static PackageCacheDirectorySyncOutcome SynchronizeMacDirectory(
        Func<int> fullSync,
        Func<int> standardSync,
        Func<int> getLastError)
    {
        ArgumentNullException.ThrowIfNull(fullSync);
        ArgumentNullException.ThrowIfNull(standardSync);
        ArgumentNullException.ThrowIfNull(getLastError);

        if (fullSync() == 0)
            return PackageCacheDirectorySyncOutcome.Full;

        int fullSyncError = getLastError();
        if (!IsUnsupportedMacFullSyncError(fullSyncError))
        {
            throw NativeIOException(
                "The macOS full directory synchronization failed.",
                fullSyncError);
        }

        if (standardSync() == 0)
            return PackageCacheDirectorySyncOutcome.Standard;

        int standardSyncError = getLastError();
        if (IsUnsupportedMacStandardSyncError(standardSyncError))
            return PackageCacheDirectorySyncOutcome.Unsupported;

        throw NativeIOException(
            "The macOS directory synchronization failed.",
            standardSyncError);
    }

    internal static bool PathExistsNoFollow(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            uint attributes = GetFileAttributes(
                ToWindowsExtendedPath(path));
            if (attributes != InvalidFileAttributes)
                return true;

            int error = Marshal.GetLastPInvokeError();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
                return false;

            throw NativeIOException(
                "The path could not be inspected without following links.",
                error);
        }

        if (OperatingSystem.IsLinux())
        {
            return TryGetLinuxPathModeNoFollow(path, out int _);
        }

        EnsureSupportedUnix();
        IntPtr buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (LStat(path, buffer) == 0)
                return true;

            int error = Marshal.GetLastPInvokeError();
            if (error is ErrorFileNotFound or UnixNotDirectory)
                return false;

            throw NativeIOException(
                "The path could not be inspected without following links.",
                error);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static bool IsSymbolicLinkNoFollow(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            uint attributes = GetFileAttributes(
                ToWindowsExtendedPath(path));
            if (attributes != InvalidFileAttributes)
            {
                return ((FileAttributes)attributes)
                    .HasFlag(FileAttributes.ReparsePoint);
            }

            int error = Marshal.GetLastPInvokeError();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
                return false;

            throw NativeIOException(
                "The path could not be inspected without following links.",
                error);
        }

        if (OperatingSystem.IsLinux())
        {
            return TryGetLinuxPathModeNoFollow(path, out int mode)
                && (mode & UnixFileTypeMask) == UnixSymbolicLinkType;
        }

        EnsureSupportedUnix();
        IntPtr buffer = Marshal.AllocHGlobal(512);
        try
        {
            if (LStat(path, buffer) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is ErrorFileNotFound or UnixNotDirectory)
                    return false;

                throw NativeIOException(
                    "The path could not be inspected without following links.",
                    error);
            }

            int mode = ReadMacMode(buffer);
            return (mode & UnixFileTypeMask) == UnixSymbolicLinkType;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void DeleteSymbolicLink(
        string path,
        bool directoryLink)
    {
        if (OperatingSystem.IsWindows())
        {
            bool succeeded = directoryLink
                ? RemoveDirectory(ToWindowsExtendedPath(path))
                : DeleteFileNative(ToWindowsExtendedPath(path));
            if (!succeeded)
                throw NativeIOException("The symbolic link could not be deleted.");

            return;
        }

        EnsureSupportedUnix();
        if (Unlink(path) != 0)
            throw NativeIOException("The symbolic link could not be deleted.");

        string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is not null)
            SynchronizeDirectory(parent);
    }

    private static void EnsureSupportedUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Durable package-cache renames require Windows, Linux, or macOS.");
        }
    }

    internal static bool TryGetLinuxPathModeNoFollow(
        string path,
        out int mode)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Linux statx inspection is only available on Linux.");
        }

        IntPtr buffer = Marshal.AllocHGlobal(256);
        try
        {
            int result;
            try
            {
                result = Statx(
                    AtFileDescriptorCurrentWorkingDirectory,
                    path,
                    AtSymbolicLinkNoFollow,
                    StatxType,
                    buffer);
            }
            catch (EntryPointNotFoundException exception)
            {
                throw StatxUnavailable(exception);
            }

            int error = Marshal.GetLastPInvokeError();
            PackageCacheLinuxStatxOutcome outcome =
                ClassifyLinuxStatxResult(result, error);
            if (outcome == PackageCacheLinuxStatxOutcome.Success)
            {
                mode = ReadStatxMode(buffer);
                return true;
            }

            if (outcome == PackageCacheLinuxStatxOutcome.Missing)
            {
                mode = 0;
                return false;
            }

            if (outcome == PackageCacheLinuxStatxOutcome.Unsupported)
                throw StatxUnavailable(new Win32Exception(error));

            throw NativeIOException(
                "The Linux path could not be inspected with statx.",
                error);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static int GetLinuxDescriptorMode(int descriptor)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Linux statx inspection is only available on Linux.");
        }

        IntPtr buffer = Marshal.AllocHGlobal(256);
        try
        {
            int result;
            try
            {
                result = Statx(
                    descriptor,
                    string.Empty,
                    AtEmptyPath,
                    StatxType,
                    buffer);
            }
            catch (EntryPointNotFoundException exception)
            {
                throw StatxUnavailable(exception);
            }

            int error = Marshal.GetLastPInvokeError();
            PackageCacheLinuxStatxOutcome outcome =
                ClassifyLinuxStatxResult(result, error);
            if (outcome == PackageCacheLinuxStatxOutcome.Success)
                return ReadStatxMode(buffer);

            if (outcome == PackageCacheLinuxStatxOutcome.Unsupported)
                throw StatxUnavailable(new Win32Exception(error));

            throw NativeIOException(
                "The Linux file descriptor could not be inspected with statx.",
                error);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ReadStatxMode(IntPtr statxBuffer)
    {
        uint mask = unchecked((uint)Marshal.ReadInt32(statxBuffer, 0));
        if ((mask & StatxType) == 0)
        {
            throw new IOException(
                "Linux statx did not return the requested file type.");
        }

        return unchecked((ushort)Marshal.ReadInt16(statxBuffer, 28));
    }

    internal static PackageCacheLinuxStatxOutcome ClassifyLinuxStatxResult(
        int nativeResult,
        int error)
    {
        if (nativeResult == 0)
            return PackageCacheLinuxStatxOutcome.Success;

        return error switch
        {
            ErrorFileNotFound or UnixNotDirectory =>
                PackageCacheLinuxStatxOutcome.Missing,
            UnixFunctionNotImplemented =>
                PackageCacheLinuxStatxOutcome.Unsupported,
            _ => PackageCacheLinuxStatxOutcome.Failure
        };
    }

    private static int ReadMacMode(IntPtr statBuffer) =>
        unchecked((ushort)Marshal.ReadInt16(statBuffer, 4));

    private static bool IsUnsupportedMacFullSyncError(int error) =>
        error is UnixInvalidArgument
            or UnixInappropriateIoControl
            or MacOperationNotSupported;

    private static bool IsUnsupportedMacStandardSyncError(int error) =>
        error is UnixInvalidArgument or MacOperationNotSupported;

    private static PlatformNotSupportedException StatxUnavailable(
        Exception innerException) =>
        new(
            "Linux statx is required for ABI-stable package-cache file-type inspection.",
            innerException);

    private static string ToWindowsExtendedPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(
            @"\\?\",
            StringComparison.Ordinal))
        {
            return fullPath;
        }

        if (fullPath.StartsWith(
            @"\\",
            StringComparison.Ordinal))
        {
            return $@"\\?\UNC\{fullPath[2..]}";
        }

        return $@"\\?\{fullPath}";
    }

    private static IOException NativeIOException(string message) =>
        new(
            message,
            new Win32Exception(Marshal.GetLastPInvokeError()));

    private static IOException NativeIOException(
        string message,
        int error) =>
        new(message, new Win32Exception(error));

    [DllImport(
        "kernel32.dll",
        EntryPoint = "MoveFileExW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileAttributesW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFileAttributes(string fileName);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "DeleteFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileNative(string fileName);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "RemoveDirectoryW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDirectory(string pathName);

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int Rename(string oldPath, string newPath);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int descriptor, int command);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int directoryDescriptor,
        string path,
        int flags,
        uint mask,
        IntPtr statxBuffer);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat(string path, IntPtr statBuffer);

    [DllImport("libc", EntryPoint = "unlink", SetLastError = true)]
    private static extern int Unlink(string path);
}

internal enum PackageCacheFaultPoint
{
    JournalWritten = 0,
    OriginalRenamed = 1,
    ReplacementPromoted = 2,
    MetadataReplaced = 3,
    ArtifactRemoved = 4
}

internal sealed record PackageCacheFaultEvent(
    PackageCacheFaultPoint Point,
    string OperationId,
    PackageCacheTransactionState State,
    string CanonicalIdentity);

internal interface IPackageCacheFaultObserver
{
    ValueTask OnEventAsync(
        PackageCacheFaultEvent faultEvent,
        CancellationToken cancellationToken);
}

internal sealed class NullPackageCacheFaultObserver :
    IPackageCacheFaultObserver
{
    internal static NullPackageCacheFaultObserver Instance { get; } = new();

    private NullPackageCacheFaultObserver()
    {
    }

    public ValueTask OnEventAsync(
        PackageCacheFaultEvent faultEvent,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

internal sealed class PackageCacheInjectedFaultException : IOException
{
    internal PackageCacheInjectedFaultException(string message)
        : base(message)
    {
    }
}
