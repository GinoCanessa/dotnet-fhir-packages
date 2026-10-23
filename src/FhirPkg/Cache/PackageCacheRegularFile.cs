// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.IO;

namespace FhirPkg.Cache;

internal static class PackageCacheRegularFile
{
    internal const int LinuxDuplicateFileDescriptorCloseOnExec =
        NativeFileSystemPrimitives.LinuxDuplicateFileDescriptorCloseOnExec;
    internal const int DarwinDuplicateFileDescriptorCloseOnExec =
        NativeFileSystemPrimitives.DarwinDuplicateFileDescriptorCloseOnExec;
    internal const int MinimumTransferDescriptor =
        NativeFileSystemPrimitives.MinimumTransferDescriptor;
    internal const int LinuxManifestOpenFlags =
        NativeFileSystemPrimitives.LinuxManifestOpenFlags;

    internal static FileStream OpenRead(string path) =>
        FileSystemContext.CreateDefault().FileSystem.OpenRegularRead(path);

    internal static int OpenValidatedLinuxDescriptor(
        string path,
        Func<string, int, uint, int> openFile,
        Func<int, int> getDescriptorMode,
        Func<int, int, int, int> duplicateDescriptor,
        Func<int, int> closeDescriptor) =>
        ResilientFileSystem.OpenValidatedLinuxDescriptor(
            path, openFile, getDescriptorMode, duplicateDescriptor, closeDescriptor);

    internal static int GuardDescriptorForTransfer(
        int descriptor,
        int duplicateCommand,
        Func<int, int, int, int> duplicateDescriptor,
        Func<int, int> closeDescriptor) =>
        ResilientFileSystem.GuardDescriptorForTransfer(
            descriptor, duplicateCommand, duplicateDescriptor, closeDescriptor);

    internal static TResult TransferDescriptorOwnership<THandle, TResult>(
        int descriptor,
        Func<int, THandle> createHandle,
        Func<THandle, TResult> createOwner,
        Action<THandle> disposeHandle,
        Func<int, int> closeDescriptor)
        where THandle : class
        where TResult : class =>
        ResilientFileSystem.TransferDescriptorOwnership(
            descriptor, createHandle, createOwner, disposeHandle, closeDescriptor);
}
