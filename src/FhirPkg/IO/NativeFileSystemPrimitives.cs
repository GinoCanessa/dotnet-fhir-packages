// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FhirPkg.IO;

internal static class NativeFileSystemPrimitives
{
    internal const uint MoveFileReplaceExisting = 0x00000001;
    internal const uint MoveFileWriteThrough = 0x00000008;
    internal const int LinuxManifestOpenFlags = 0x00000800 | 0x00020000 | 0x00080000;
    internal const int MacManifestOpenFlags = 0x00000004 | 0x00000100 | 0x01000000;
    internal const int LinuxDuplicateFileDescriptorCloseOnExec = 1030;
    internal const int DarwinDuplicateFileDescriptorCloseOnExec = 67;
    internal const int MinimumTransferDescriptor = 3;

    private const int MacFullFileSync = 51;
    private const int AtFileDescriptorCurrentWorkingDirectory = -100;
    private const int AtSymbolicLinkNoFollow = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x00000001;
    private const uint InvalidFileAttributes = 0xFFFFFFFF;

    internal static FileSystemPlatform Platform => OperatingSystem.IsWindows()
        ? FileSystemPlatform.Windows
        : OperatingSystem.IsLinux()
            ? FileSystemPlatform.Linux
            : OperatingSystem.IsMacOS()
                ? FileSystemPlatform.MacOS
                : FileSystemPlatform.Unsupported;

    internal static FileSystemReplayOutcome<FileSystemProbeResult> Probe(string path, bool followLinks = false)
    {
        if (!followLinks)
            path = Path.TrimEndingDirectorySeparator(path);
        if (OperatingSystem.IsWindows())
        {
            string nativePath = ToWindowsExtendedPath(path);
            uint attributes = GetFileAttributes(nativePath);
            int error = Marshal.GetLastPInvokeError();
            if (attributes != InvalidFileAttributes)
            {
                return FileSystemReplayOutcome<FileSystemProbeResult>.Complete(
                    FileSystemProbeResult.FromAttributes((FileAttributes)attributes));
            }

            if (error is 2 or 3)
                return MissingProbe();

            return Failed<FileSystemProbeResult>(
                "The path could not be inspected without following links.",
                error, "GetFileAttributesW", path);
        }

        if (OperatingSystem.IsLinux())
            return ProbeLinuxPath(path, followLinks);

        EnsureSupportedUnix();
        IntPtr buffer = Marshal.AllocHGlobal(512);
        try
        {
            int result;
            int error;
            if (followLinks)
            {
                result = Stat(path, buffer);
                error = Marshal.GetLastPInvokeError();
            }
            else
            {
                result = LStat(path, buffer);
                error = Marshal.GetLastPInvokeError();
            }
            if (result == 0)
            {
                return FileSystemReplayOutcome<FileSystemProbeResult>.Complete(
                    FileSystemProbeResult.FromMode(ReadMacMode(buffer)));
            }

            if (error is 2 or 20)
                return MissingProbe();

            return Failed<FileSystemProbeResult>(
                "The path could not be inspected.",
                error, followLinks ? "stat" : "lstat", path);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static FileSystemReplayOutcome<bool> CreateDirectory(string path)
    {
        int result;
        int error;
        if (OperatingSystem.IsWindows())
        {
            string nativePath = ToWindowsExtendedPath(path);
            bool succeeded = CreateDirectoryNative(nativePath, IntPtr.Zero);
            error = Marshal.GetLastPInvokeError();
            result = succeeded ? 0 : -1;
            if (result != 0 && error is 80 or 183)
                return FileSystemReplayOutcome<bool>.KindRejected(false);
        }
        else
        {
            EnsureSupportedUnix();
            result = Mkdir(path, 0x1FF);
            error = Marshal.GetLastPInvokeError();
            if (result != 0 && error == 17)
                return FileSystemReplayOutcome<bool>.KindRejected(false);
        }

        return result == 0
            ? FileSystemReplayOutcome<bool>.Complete(true)
            : Failed<bool>(
                "The directory could not be created.",
                error,
                OperatingSystem.IsWindows() ? "CreateDirectoryW" : "mkdir",
                path,
                safety: MutationSafety(error, Platform));
    }

    internal static FileSystemReplayOutcome<bool> MovePath(
        string sourcePath,
        string destinationPath,
        bool replace)
    {
        if (OperatingSystem.IsWindows())
        {
            string nativeSource = ToWindowsExtendedPath(sourcePath);
            string nativeDestination = ToWindowsExtendedPath(destinationPath);
            return MoveWindowsPath(
                nativeSource,
                nativeDestination,
                MoveFileWriteThrough | (replace ? MoveFileReplaceExisting : 0),
                MoveFileEx,
                Marshal.GetLastPInvokeError,
                sourcePath,
                destinationPath);
        }

        EnsureSupportedUnix();
        int result = Rename(sourcePath, destinationPath);
        int error = Marshal.GetLastPInvokeError();
        return result == 0
            ? FileSystemReplayOutcome<bool>.Complete(true)
            : RenameFailure(sourcePath, destinationPath, "rename", 0, error, Platform);
    }

    internal static FileSystemReplayOutcome<bool> MoveWindowsPath(
        string sourcePath,
        string destinationPath,
        uint flags,
        Func<string, string, uint, bool> moveFileEx,
        Func<int> getLastError,
        string? diagnosticSource = null,
        string? diagnosticDestination = null)
    {
        bool succeeded = moveFileEx(sourcePath, destinationPath, flags);
        int error = getLastError();
        return succeeded
            ? FileSystemReplayOutcome<bool>.Complete(true)
            : RenameFailure(
                diagnosticSource ?? sourcePath,
                diagnosticDestination ?? destinationPath,
                "MoveFileExW",
                flags,
                error,
                FileSystemPlatform.Windows);
    }

    internal static FileSystemReplayOutcome<bool> DeleteEntry(string path, FileSystemEntryKind kind)
    {
        bool directory = kind is FileSystemEntryKind.Directory
            or FileSystemEntryKind.DirectorySymbolicLink;
        int result;
        int error;
        string operation;
        if (OperatingSystem.IsWindows())
        {
            string nativePath = ToWindowsExtendedPath(path);
            bool succeeded;
            if (directory)
            {
                succeeded = RemoveDirectory(nativePath);
                error = Marshal.GetLastPInvokeError();
                operation = "RemoveDirectoryW";
            }
            else
            {
                succeeded = DeleteFileNative(nativePath);
                error = Marshal.GetLastPInvokeError();
                operation = "DeleteFileW";
            }
            result = succeeded ? 0 : -1;
        }
        else
        {
            EnsureSupportedUnix();
            if (kind == FileSystemEntryKind.Directory)
            {
                result = Rmdir(path);
                error = Marshal.GetLastPInvokeError();
                operation = "rmdir";
            }
            else
            {
                result = Unlink(path);
                error = Marshal.GetLastPInvokeError();
                operation = "unlink";
            }
        }

        if (result == 0)
            return FileSystemReplayOutcome<bool>.Complete(true);
        if (error == 2 || (OperatingSystem.IsWindows() && error == 3))
            return FileSystemReplayOutcome<bool>.Missing(false);

        return Failed<bool>(
            "The filesystem entry could not be deleted.",
            error, operation, path, safety: MutationSafety(error, Platform));
    }

    internal static FileSystemReplayOutcome<int> OpenDescriptor(string path, int flags, uint mode)
    {
        int createFlag = Platform == FileSystemPlatform.Linux ? 0x40 : 0x200;
        UnixDescriptorResult result = (flags & createFlag) != 0
            ? UnixDescriptorInterop.OpenWithMode(path, flags, mode)
            : UnixDescriptorInterop.OpenExisting(path, flags);
        return OpenDescriptorResult(path, flags, result);
    }

    internal static FileSystemReplayOutcome<int> OpenDescriptor(
        string path,
        int flags,
        uint mode,
        Func<string, int, uint, int> open,
        Func<int> getLastError)
    {
        int descriptor = open(path, flags, mode);
        int error = getLastError();
        return OpenDescriptorResult(path, flags, new UnixDescriptorResult(descriptor, error));
    }

    private static FileSystemReplayOutcome<int> OpenDescriptorResult(
        string path, int flags, UnixDescriptorResult result) =>
        result.ReturnValue >= 0
            ? FileSystemReplayOutcome<int>.Complete(result.ReturnValue)
            : Failed<int>(
                "The file or directory could not be opened safely.",
                result.Error, "open", path, nativeFlags: flags);

    internal static FileSystemReplayOutcome<int> OpenOwnedDescriptor(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options)
    {
        int flags = OwnedOpenFlags(mode, access, share, options, Platform);
        UnixDescriptorResult result = UnixDescriptorInterop.OpenWithMode(path, flags, 0x1B6);
        return result.ReturnValue >= 0
            ? FileSystemReplayOutcome<int>.Complete(result.ReturnValue)
            : OwnedOpenFailure<int>(path, mode, result.Error, flags, Platform);
    }

    internal static int OwnedOpenFlags(
        FileMode mode, FileAccess access, FileShare share, FileOptions options, FileSystemPlatform platform)
    {
        if (platform is not (FileSystemPlatform.Linux or FileSystemPlatform.MacOS))
            throw new PlatformNotSupportedException("Owned descriptors require Linux or macOS.");

        bool linux = platform == FileSystemPlatform.Linux;
        int flags = access == FileAccess.Read ? 0 : access == FileAccess.Write ? 1 : 2;
        if (mode is FileMode.Create or FileMode.CreateNew or FileMode.OpenOrCreate)
            flags |= linux ? 0x40 : 0x200;
        if (mode == FileMode.CreateNew)
            flags |= linux ? 0x80 : 0x800;
        if ((share & FileShare.Inheritable) == 0)
            flags |= linux ? 0x80000 : 0x01000000;
        if ((options & FileOptions.WriteThrough) != 0)
            flags |= linux ? 0x101000 : 0x80;
        return flags;
    }

    internal static FileSystemReplayOutcome<FileSystemProbeResult> InspectDescriptor(int descriptor, string? path = null)
    {
        if (OperatingSystem.IsLinux())
        {
            return InspectLinux(
                descriptor, string.Empty, AtEmptyPath, optional: false,
                Statx, Marshal.GetLastPInvokeError, diagnosticPath: path);
        }

        EnsureSupportedUnix();
        IntPtr buffer = Marshal.AllocHGlobal(512);
        try
        {
            int result = FStat(descriptor, buffer);
            int error = Marshal.GetLastPInvokeError();
            return result == 0
                ? FileSystemReplayOutcome<FileSystemProbeResult>.Complete(
                    FileSystemProbeResult.FromMode(ReadMacMode(buffer)))
                : Failed<FileSystemProbeResult>(
                    "The file descriptor type could not be verified.",
                    error, "fstat", path);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static FileSystemReplayOutcome<int> DuplicateDescriptor(
        int descriptor,
        int command,
        int minimum,
        string? path = null)
    {
        return DuplicateDescriptorResult(UnixDescriptorInterop.ControlInteger(descriptor, command, minimum), command, path);
    }

    internal static FileSystemReplayOutcome<int> DuplicateDescriptor(
        int descriptor,
        int command,
        int minimum,
        Func<int, int, int, int> duplicate,
        Func<int> getLastError,
        string? path = null)
    {
        int result = duplicate(descriptor, command, minimum);
        int error = getLastError();
        return DuplicateDescriptorResult(new UnixDescriptorResult(result, error), command, path);
    }

    private static FileSystemReplayOutcome<int> DuplicateDescriptorResult(
        UnixDescriptorResult result, int command, string? path) =>
        result.ReturnValue >= 0
            ? FileSystemReplayOutcome<int>.Complete(result.ReturnValue)
            : Failed<int>(
                "The package manifest descriptor could not be duplicated safely.",
                result.Error, "fcntl(F_DUPFD_CLOEXEC)", path, nativeFlags: command);

    internal static FileSystemReplayOutcome<FileSystemDirectorySyncOutcome> SynchronizeDescriptor(
        int descriptor,
        bool fullSync,
        string? path = null)
    {
        EnsureSupportedUnix();
        if (fullSync)
        {
            return SynchronizeMacFull(
                UnixDescriptorInterop.Control(descriptor, MacFullFileSync),
                path);
        }

        return SynchronizeStandard(
            () => Fsync(descriptor),
            Marshal.GetLastPInvokeError,
            OperatingSystem.IsMacOS(),
            path);
    }

    internal static FileSystemReplayOutcome<FileSystemDirectorySyncOutcome> SynchronizeMacFull(
        Func<int> fullSync,
        Func<int> getLastError,
        string? path = null)
    {
        int result = fullSync();
        int error = getLastError();
        return SynchronizeMacFull(new UnixDescriptorResult(result, error), path);
    }

    private static FileSystemReplayOutcome<FileSystemDirectorySyncOutcome> SynchronizeMacFull(
        UnixDescriptorResult result, string? path)
    {
        if (result.ReturnValue == 0)
            return FileSystemReplayOutcome<FileSystemDirectorySyncOutcome>.Complete(FileSystemDirectorySyncOutcome.Full);
        if (result.Error is 22 or 25 or 45)
            return FileSystemReplayOutcome<FileSystemDirectorySyncOutcome>.Complete(FileSystemDirectorySyncOutcome.Unsupported);

        return Failed<FileSystemDirectorySyncOutcome>(
            "The macOS full directory synchronization failed.",
            result.Error, "fcntl(F_FULLFSYNC)", path, nativeFlags: MacFullFileSync);
    }

    internal static FileSystemReplayOutcome<FileSystemDirectorySyncOutcome> SynchronizeStandard(
        Func<int> standardSync,
        Func<int> getLastError,
        bool macOS,
        string? path = null)
    {
        int result = standardSync();
        int error = getLastError();
        if (result == 0)
            return FileSystemReplayOutcome<FileSystemDirectorySyncOutcome>.Complete(FileSystemDirectorySyncOutcome.Standard);
        if (macOS && error is 22 or 45)
            return FileSystemReplayOutcome<FileSystemDirectorySyncOutcome>.Complete(FileSystemDirectorySyncOutcome.Unsupported);

        return Failed<FileSystemDirectorySyncOutcome>(
            "The destination directory synchronization failed.",
            error, "fsync", path);
    }

    internal static FileSystemReplayOutcome<bool> CloseDescriptor(int descriptor, string? path = null)
    {
        EnsureSupportedUnix();
        return CloseDescriptor(descriptor, Close, Marshal.GetLastPInvokeError, path);
    }

    internal static FileSystemReplayOutcome<bool> CloseDescriptor(
        int descriptor,
        Func<int, int> close,
        Func<int> getLastError,
        string? path = null)
    {
        int result = close(descriptor);
        int error = getLastError();
        return result == 0
            ? FileSystemReplayOutcome<bool>.Complete(true)
            : Failed<bool>(
                "The file descriptor could not be closed.",
                error, "close", path, safety: FileSystemReplaySafety.Unsafe);
    }

    internal static FileSystemReplayOutcome<bool> TryLockDescriptor(int descriptor) =>
        TryLockDescriptor(descriptor, Flock, Marshal.GetLastPInvokeError);

    internal static FileSystemReplayOutcome<bool> TryLockDescriptor(
        int descriptor,
        Func<int, int, int> flock,
        Func<int> getLastError)
    {
        int result = flock(descriptor, 2 | 4);
        int error = getLastError();
        if (result == 0)
            return FileSystemReplayOutcome<bool>.Complete(true);
        if (error == 35)
            return FileSystemReplayOutcome<bool>.Contended(false);

        return Failed<bool>(
            "The macOS package cache lock could not be acquired.",
            error, "flock", nativeFlags: 2 | 4);
    }

    internal static FileSystemReplayOutcome<bool> UnlockDescriptor(int descriptor)
    {
        int result = Flock(descriptor, 8);
        int error = Marshal.GetLastPInvokeError();
        return result == 0
            ? FileSystemReplayOutcome<bool>.Complete(true)
            : Failed<bool>(
                "The macOS package cache lock could not be released.",
                error, "flock", nativeFlags: 8);
    }

    internal static FileSystemReplayOutcome<FileSystemProbeResult> ProbeLinuxPath(string path, bool followLinks = false)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Linux statx inspection is only available on Linux.");

        return InspectLinux(
            AtFileDescriptorCurrentWorkingDirectory, followLinks ? path : Path.TrimEndingDirectorySeparator(path),
            followLinks ? 0 : AtSymbolicLinkNoFollow, optional: true,
            Statx, Marshal.GetLastPInvokeError);
    }

    internal static FileSystemReplayOutcome<FileSystemProbeResult> InspectLinux(
        int descriptor,
        string path,
        int flags,
        bool optional,
        Func<int, string, int, uint, IntPtr, int> statx,
        Func<int> getLastError,
        string? diagnosticPath = null)
    {
        IntPtr buffer = Marshal.AllocHGlobal(256);
        try
        {
            int result;
            int error;
            try
            {
                result = statx(descriptor, path, flags, StatxType, buffer);
                error = getLastError();
            }
            catch (EntryPointNotFoundException exception)
            {
                return FileSystemReplayOutcome<FileSystemProbeResult>.SemanticFailure(StatxUnavailable(exception));
            }

            FileSystemLinuxStatxOutcome outcome = ClassifyLinuxStatxResult(result, error);
            if (outcome == FileSystemLinuxStatxOutcome.Success)
            {
                uint mask = unchecked((uint)Marshal.ReadInt32(buffer, 0));
                if ((mask & StatxType) == 0)
                {
                    return FileSystemReplayOutcome<FileSystemProbeResult>.SemanticFailure(
                        new IOException("Linux statx did not return the requested file type."));
                }

                int mode = unchecked((ushort)Marshal.ReadInt16(buffer, 28));
                return FileSystemReplayOutcome<FileSystemProbeResult>.Complete(FileSystemProbeResult.FromMode(mode));
            }

            if (optional && outcome == FileSystemLinuxStatxOutcome.Missing)
                return MissingProbe();
            if (outcome == FileSystemLinuxStatxOutcome.Unsupported)
            {
                return FileSystemReplayOutcome<FileSystemProbeResult>.Failed(
                    FileSystemFailureContext.CapturePrimitiveFailure(
                        StatxUnavailable(new Win32Exception(error)),
                        FileSystemReplaySafety.Safe,
                        "statx",
                        diagnosticPath ?? path,
                        nativeErrorCode: error,
                        nativeFlags: flags));
            }

            return Failed<FileSystemProbeResult>(
                "The Linux path or file descriptor could not be inspected with statx.",
                error, "statx", diagnosticPath ?? path, nativeFlags: flags);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static FileSystemLinuxStatxOutcome ClassifyLinuxStatxResult(int result, int error) =>
        result == 0
            ? FileSystemLinuxStatxOutcome.Success
            : error switch
            {
                2 or 20 => FileSystemLinuxStatxOutcome.Missing,
                38 => FileSystemLinuxStatxOutcome.Unsupported,
                _ => FileSystemLinuxStatxOutcome.Failure,
            };

    internal static string ToWindowsExtendedPath(string fullPath)
    {
        if (!Path.IsPathFullyQualified(fullPath))
            throw new ArgumentException("A native Windows path must already be fully qualified.", nameof(fullPath));
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            return fullPath;
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return $@"\\?\UNC\{fullPath[2..]}";

        return $@"\\?\{fullPath}";
    }

    private static FileSystemReplayOutcome<bool> RenameFailure(
        string sourcePath,
        string destinationPath,
        string operation,
        uint flags,
        int error,
        FileSystemPlatform platform)
    {
        IOException exception = new IOException(
            $"The durable path rename failed. Source: '{sourcePath}'; " +
            $"Destination: '{destinationPath}'; Operation: {operation}; " +
            $"NativeFlags: 0x{flags:X8}; NativeErrorCode: {error}.",
            new Win32Exception(error));
        exception.Data["SourcePath"] = sourcePath;
        exception.Data["DestinationPath"] = destinationPath;
        exception.Data["Operation"] = operation;
        exception.Data["NativeFlags"] = flags;
        exception.Data["NativeErrorCode"] = error;
        return FileSystemReplayOutcome<bool>.Failed(FileSystemFailureContext.CapturePrimitiveFailure(
            exception, MutationSafety(error, platform), operation,
            sourcePath, destinationPath, error, flags));
    }

    private static FileSystemReplaySafety MutationSafety(int error, FileSystemPlatform platform)
    {
        // These are rejections before a namespace change, not a transient-error
        // allowlist. Device/I/O, interruption, and unknown mutation results have
        // no ownership evidence with which to reconcile a possibly completed call.
        bool rejected = platform == FileSystemPlatform.Windows
            ? error is 2 or 3 or 5 or 17 or 32 or 33 or 80 or 87 or 123 or 145 or 183 or 206 or 267
            : error is 1 or 2 or 9 or 13 or 17 or 18 or 20 or 21 or 22 or 28 or 30 or 36 or 39 or 40 or 45 or 62 or 63 or 66 or 95;
        return rejected ? FileSystemReplaySafety.Safe : FileSystemReplaySafety.Unsafe;
    }

    internal static FileSystemReplayOutcome<T> OwnedOpenFailure<T>(
        string path, FileMode mode, int error, long flags, FileSystemPlatform platform)
    {
        bool windows = platform == FileSystemPlatform.Windows;
        bool collision = mode == FileMode.CreateNew && (windows ? error is 80 or 183 : error == 17);
        Win32Exception native = new Win32Exception(error);
        string message = $"The file '{path}' could not be opened: {native.Message}";
        Exception exception = error switch
        {
            2 => new FileNotFoundException(message, path, native),
            3 when windows => new DirectoryNotFoundException(message, native),
            20 when !windows => new DirectoryNotFoundException(message, native),
            5 when windows => new UnauthorizedAccessException(message, native),
            1 or 13 when !windows => new UnauthorizedAccessException(message, native),
            206 when windows => new PathTooLongException(message, native),
            36 or 63 when !windows => new PathTooLongException(message, native),
            _ => new IOException(message, native),
        };
        if (collision)
            return FileSystemReplayOutcome<T>.SemanticFailure(exception);

        return FileSystemReplayOutcome<T>.Failed(FileSystemFailureContext.CapturePrimitiveFailure(
            exception,
            MutationSafety(error, platform),
            windows ? "CreateFileW" : "open(owned file)",
            path,
            nativeErrorCode: error,
            nativeFlags: flags));
    }

    private static FileSystemReplayOutcome<FileSystemProbeResult> MissingProbe() =>
        FileSystemReplayOutcome<FileSystemProbeResult>.Missing(new FileSystemProbeResult(FileSystemEntryKind.Missing));

    private static FileSystemReplayOutcome<T> Failed<T>(
        string message,
        int error,
        string operation,
        string? path = null,
        long? nativeFlags = null,
        FileSystemReplaySafety safety = FileSystemReplaySafety.Safe) =>
        FileSystemReplayOutcome<T>.Failed(FileSystemFailureContext.CapturePrimitiveFailure(
            new IOException(message, new Win32Exception(error)),
            safety, operation, path, nativeErrorCode: error, nativeFlags: nativeFlags));

    private static int ReadMacMode(IntPtr buffer) =>
        unchecked((ushort)Marshal.ReadInt16(buffer, 4));

    private static PlatformNotSupportedException StatxUnavailable(Exception innerException) =>
        new PlatformNotSupportedException(
            "Linux statx is required for ABI-stable package-cache file-type inspection.", innerException);

    private static void EnsureSupportedUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Durable package-cache operations require Windows, Linux, or macOS.");
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributes(string fileName);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryNative(string pathName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileNative(string fileName);

    [DllImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDirectory(string pathName);

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int Rename(string oldPath, string newPath);

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int Mkdir(string path, uint mode);

    [DllImport("libc", EntryPoint = "rmdir", SetLastError = true)]
    private static extern int Rmdir(string path);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directoryDescriptor, string path, int flags, uint mask, IntPtr buffer);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat(string path, IntPtr buffer);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int Stat(string path, IntPtr buffer);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int descriptor, IntPtr buffer);

    [DllImport("libc", EntryPoint = "unlink", SetLastError = true)]
    private static extern int Unlink(string path);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int descriptor, int operation);
}
