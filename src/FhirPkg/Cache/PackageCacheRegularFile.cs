// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FhirPkg.Cache;

internal static class PackageCacheRegularFile
{
    private const int BufferSize = 16_384;
    private const int LinuxOpenReadOnly = 0;
    private const int LinuxOpenNonBlocking = 0x00000800;
    private const int LinuxOpenNoFollow = 0x00020000;
    private const int LinuxOpenCloseOnExec = 0x00080000;
    internal const int LinuxDuplicateFileDescriptorCloseOnExec = 1030;
    internal const int DarwinDuplicateFileDescriptorCloseOnExec = 67;
    internal const int MinimumTransferDescriptor = 3;
    private const int MacOpenNonBlocking = 0x00000004;
    private const int MacOpenNoFollow = 0x00000100;
    private const int MacOpenCloseOnExec = 0x01000000;
    private const int FileTypeMask = 0xF000;
    private const int RegularFileType = 0x8000;

    internal const int LinuxManifestOpenFlags =
        LinuxOpenReadOnly
        | LinuxOpenNonBlocking
        | LinuxOpenNoFollow
        | LinuxOpenCloseOnExec;

    internal static FileStream OpenRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (OperatingSystem.IsWindows())
            return OpenWindows(path);

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Regular cache-file verification requires Windows, Linux, or macOS.");
        }

        return OperatingSystem.IsLinux()
            ? OpenLinux(path)
            : OpenMac(path);
    }

    private static FileStream OpenLinux(string path)
    {
        int descriptor = OpenValidatedLinuxDescriptor(
            path,
            Open,
            PackageCacheNativeFileSystem.GetLinuxDescriptorMode,
            Fcntl,
            Close);
        return TransferDescriptorToFileStream(descriptor);
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

        int descriptor = openFile(
            path,
            LinuxManifestOpenFlags,
            0);
        if (descriptor < 0)
        {
            throw NativeIOException(
                "The package manifest could not be opened safely.");
        }

        bool descriptorTransferred = false;
        try
        {
            int mode = getDescriptorMode(descriptor);
            if ((mode & FileTypeMask) != RegularFileType)
            {
                throw new IOException(
                    "The package manifest is not a regular file.");
            }

            descriptorTransferred = true;
            return GuardDescriptorForTransfer(
                descriptor,
                LinuxDuplicateFileDescriptorCloseOnExec,
                duplicateDescriptor,
                closeDescriptor);
        }
        finally
        {
            if (!descriptorTransferred)
                _ = closeDescriptor(descriptor);
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
        int duplicatedDescriptor = -1;
        bool ownsDuplicate = false;
        try
        {
            duplicatedDescriptor = duplicateDescriptor(
                descriptor,
                duplicateCommand,
                MinimumTransferDescriptor);
            if (duplicatedDescriptor < 0)
            {
                throw NativeIOException(
                    "The package manifest descriptor could not be duplicated safely.");
            }

            if (duplicatedDescriptor != descriptor)
                ownsDuplicate = true;

            if (duplicatedDescriptor < MinimumTransferDescriptor)
            {
                throw new IOException(
                    "The duplicated package manifest descriptor was below the safe minimum.");
            }

            int closeResult;
            try
            {
                closeResult = closeDescriptor(descriptor);
            }
            finally
            {
                ownsOriginal = false;
            }

            if (closeResult != 0)
            {
                throw NativeIOException(
                    "The original package manifest descriptor could not be closed.");
            }

            ownsDuplicate = false;
            return duplicatedDescriptor;
        }
        finally
        {
            try
            {
                if (ownsOriginal)
                    _ = closeDescriptor(descriptor);
            }
            finally
            {
                if (ownsDuplicate)
                    _ = closeDescriptor(duplicatedDescriptor);
            }
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

        bool ownsDescriptor = true;
        THandle? handle = null;
        bool ownsHandle = false;
        try
        {
            handle = createHandle(descriptor)
                ?? throw new IOException(
                    "The package manifest handle could not be created.");
            ownsDescriptor = false;
            ownsHandle = true;

            TResult owner = createOwner(handle)
                ?? throw new IOException(
                    "The package manifest stream could not be created.");
            ownsHandle = false;
            return owner;
        }
        finally
        {
            try
            {
                if (ownsDescriptor)
                    _ = closeDescriptor(descriptor);
            }
            finally
            {
                if (ownsHandle && handle is not null)
                    disposeHandle(handle);
            }
        }
    }

    private static FileStream OpenMac(string path)
    {
        IntPtr pathStatBuffer = Marshal.AllocHGlobal(512);
        try
        {
            if (LStat(path, pathStatBuffer) != 0)
            {
                throw NativeIOException(
                    "The package manifest file type could not be inspected.");
            }

            int pathMode = ReadMacMode(pathStatBuffer);
            if ((pathMode & FileTypeMask) != RegularFileType)
            {
                throw new IOException(
                    "The package manifest is not a regular file.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pathStatBuffer);
        }

        int flags =
            MacOpenNonBlocking | MacOpenNoFollow | MacOpenCloseOnExec;
        int descriptor = Open(path, flags, 0);
        if (descriptor < 0)
            throw NativeIOException("The package manifest could not be opened safely.");

        bool ownsDescriptor = true;
        try
        {
            IntPtr statBuffer = Marshal.AllocHGlobal(512);
            int mode;
            try
            {
                if (FStat(descriptor, statBuffer) != 0)
                {
                    throw NativeIOException(
                        "The package manifest file type could not be verified.");
                }

                mode = ReadMacMode(statBuffer);
            }
            finally
            {
                Marshal.FreeHGlobal(statBuffer);
            }

            if ((mode & FileTypeMask) != RegularFileType)
            {
                throw new IOException(
                    "The package manifest is not a regular file.");
            }

            ownsDescriptor = false;
            int transferDescriptor = GuardDescriptorForTransfer(
                descriptor,
                DarwinDuplicateFileDescriptorCloseOnExec,
                Fcntl,
                Close);
            return TransferDescriptorToFileStream(transferDescriptor);
        }
        finally
        {
            if (ownsDescriptor)
                _ = Close(descriptor);
        }
    }

    private static FileStream TransferDescriptorToFileStream(
        int descriptor) =>
        TransferDescriptorOwnership<SafeFileHandle, FileStream>(
            descriptor,
            (int value) => new SafeFileHandle(
                new IntPtr(value),
                ownsHandle: true),
            (SafeFileHandle handle) => new FileStream(
                handle,
                FileAccess.Read,
                BufferSize,
                isAsync: false),
            (SafeFileHandle handle) => handle.Dispose(),
            Close);

    private static FileStream OpenWindows(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint)
            || attributes.HasFlag(FileAttributes.Device))
        {
            throw new IOException(
                "The package manifest is not a regular file.");
        }

        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.SequentialScan);
    }

    private static int ReadMacMode(IntPtr statBuffer) =>
        unchecked((ushort)Marshal.ReadInt16(statBuffer, 4));

    private static IOException NativeIOException(string message) =>
        new(
            message,
            new Win32Exception(Marshal.GetLastPInvokeError()));

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int descriptor, IntPtr statBuffer);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(
        int descriptor,
        int command,
        int argument);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat(string path, IntPtr statBuffer);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}
