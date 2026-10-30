// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FhirPkg.IO;

internal readonly record struct UnixDescriptorResult(int ReturnValue, int Error);

internal enum UnixDescriptorAbi
{
    Standard,
    DarwinArm64,
}

internal static class UnixDescriptorInterop
{
    internal static UnixDescriptorAbi SelectAbi(FileSystemPlatform platform, Architecture processArchitecture) =>
        platform switch
        {
            FileSystemPlatform.Linux => UnixDescriptorAbi.Standard,
            FileSystemPlatform.MacOS when processArchitecture == Architecture.X64 => UnixDescriptorAbi.Standard,
            FileSystemPlatform.MacOS when processArchitecture == Architecture.Arm64 => UnixDescriptorAbi.DarwinArm64,
            _ => throw new PlatformNotSupportedException("Unix descriptor operations require Linux or supported macOS architectures."),
        };

    private static UnixDescriptorAbi CurrentAbi =>
        SelectAbi(NativeFileSystemPrimitives.Platform, RuntimeInformation.ProcessArchitecture);

    internal static UnixDescriptorResult OpenExisting(string path, int flags)
    {
        _ = CurrentAbi;
        int result = Open(path, flags);
        return new UnixDescriptorResult(result, Marshal.GetLastPInvokeError());
    }

    internal static UnixDescriptorResult OpenWithMode(string path, int flags, uint mode)
    {
        int result = CurrentAbi == UnixDescriptorAbi.DarwinArm64
            ? OpenDarwinArm64(path, flags, 0, 0, 0, 0, 0, 0, mode)
            : Open(path, flags, mode);
        return new UnixDescriptorResult(result, Marshal.GetLastPInvokeError());
    }

    internal static UnixDescriptorResult Control(int descriptor, int command)
    {
        _ = CurrentAbi;
        int result = Fcntl(descriptor, command);
        return new UnixDescriptorResult(result, Marshal.GetLastPInvokeError());
    }

    internal static UnixDescriptorResult ControlInteger(int descriptor, int command, int value)
    {
        int result = CurrentAbi == UnixDescriptorAbi.DarwinArm64
            ? FcntlDarwinArm64(descriptor, command, 0, 0, 0, 0, 0, 0, value)
            : Fcntl(descriptor, command, value);
        return new UnixDescriptorResult(result, Marshal.GetLastPInvokeError());
    }

    internal static UnixDescriptorResult ControlInteger(SafeFileHandle handle, int command, int value)
    {
        // SafeHandle marshalling protects the live descriptor throughout the call.
        int result = CurrentAbi == UnixDescriptorAbi.DarwinArm64
            ? FcntlDarwinArm64(handle, command, 0, 0, 0, 0, 0, 0, value)
            : Fcntl(handle, command, value);
        return new UnixDescriptorResult(result, Marshal.GetLastPInvokeError());
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags, uint mode);

    // Darwin arm64 passes variadic arguments on the stack, not in the remaining
    // integer registers. Reserve x2-x7 with pointer-sized placeholders so the
    // promoted mode/integer argument occupies the first stack argument slot.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDarwinArm64(
        string path, int flags, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, uint mode);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int descriptor, int command);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int descriptor, int command, int value);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(SafeFileHandle handle, int command, int value);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FcntlDarwinArm64(
        int descriptor, int command, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, int value);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int FcntlDarwinArm64(
        SafeFileHandle handle, int command, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, int value);
}
