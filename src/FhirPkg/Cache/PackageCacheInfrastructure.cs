// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.IO;
using FhirPkg.Utilities;

namespace FhirPkg.Cache;

internal interface IPackageCacheFileOperations :
    IDurableFileOperations
{
    bool DirectoryExists(string path);

    FileStream OpenRead(string path);

    void MoveDirectory(string sourcePath, string destinationPath);

    void MoveFile(string sourcePath, string destinationPath);

    void DeleteDirectory(string path, bool recursive);

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

    // Retain the process-host access shape, without process-global configuration.
    internal static SystemPackageCacheFileOperations Instance =>
        new SystemPackageCacheFileOperations(FileSystemContext.CreateDefault());

    internal SystemPackageCacheFileOperations(FileSystemContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
    }

    internal FileSystemContext Context { get; }

    public bool DirectoryExists(string path) =>
        Context.FileSystem.ProbeFollowing(path).Kind is FileSystemEntryKind.Directory
            or FileSystemEntryKind.DirectorySymbolicLink;

    public bool FileExists(string path) =>
        Context.FileSystem.ProbeFollowing(path).Kind is FileSystemEntryKind.File
            or FileSystemEntryKind.Other or FileSystemEntryKind.SymbolicLink;

    public FileStream OpenRead(string path) =>
        Context.FileSystem.OpenRegularRead(path);

    public void CreateDirectory(string path) =>
        Context.FileSystem.CreateDirectory(path);

    public void MoveDirectory(string sourcePath, string destinationPath) =>
        Context.FileSystem.MovePath(sourcePath, destinationPath);

    public void MoveFile(string sourcePath, string destinationPath) =>
        Context.FileSystem.MovePath(sourcePath, destinationPath);

    public void DeleteDirectory(string path, bool recursive) =>
        Context.FileSystem.DeleteDirectory(path, recursive);

    public void DeleteFile(string path) =>
        Context.FileSystem.DeleteFile(path);

    public async ValueTask WriteFileAndFlushAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        // Stream-block replay and explicit live-handle flush move in Phase 3.
        // A create-new constructor failure is not reconciled from path existence.
        await using FileStream stream = await Context.FileSystem.OpenFileAsync(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough,
                cancellationToken)
            .ConfigureAwait(false);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    public ValueTask AtomicReplaceFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken) =>
        Context.FileSystem.ReplaceFileAsync(sourcePath, destinationPath, cancellationToken);

    public void SynchronizeDirectory(string directoryPath) =>
        Context.FileSystem.SynchronizeDirectory(directoryPath);

    public PackageCacheArtifactKind GetArtifactKind(string path) =>
        Context.FileSystem.Probe(path).Kind switch
        {
            FileSystemEntryKind.Missing => PackageCacheArtifactKind.Missing,
            FileSystemEntryKind.Directory => PackageCacheArtifactKind.Directory,
            FileSystemEntryKind.SymbolicLink => PackageCacheArtifactKind.SymbolicLink,
            FileSystemEntryKind.DirectorySymbolicLink => PackageCacheArtifactKind.DirectorySymbolicLink,
            _ => PackageCacheArtifactKind.File,
        };

    public bool ArtifactExists(string path, PackageCacheArtifactKind artifactKind) =>
        artifactKind != PackageCacheArtifactKind.Missing
        && Context.FileSystem.Probe(path).Exists;

    public void MoveArtifact(
        string sourcePath,
        string destinationPath,
        PackageCacheArtifactKind artifactKind)
    {
        if (artifactKind != PackageCacheArtifactKind.Missing)
            Context.FileSystem.MovePath(sourcePath, destinationPath);
    }

    public void DeleteArtifact(string path, PackageCacheArtifactKind artifactKind)
    {
        if (artifactKind == PackageCacheArtifactKind.Missing)
            return;

        FileSystemEntryKind kind = artifactKind switch
        {
            PackageCacheArtifactKind.Directory => FileSystemEntryKind.Directory,
            PackageCacheArtifactKind.SymbolicLink => FileSystemEntryKind.SymbolicLink,
            PackageCacheArtifactKind.DirectorySymbolicLink => FileSystemEntryKind.DirectorySymbolicLink,
            _ => FileSystemEntryKind.File,
        };
        Context.FileSystem.Delete(
            path, kind,
            recursive: kind == FileSystemEntryKind.Directory,
            missingAllowed: true);
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

// Compatibility entry points delegate once. Context-bound adapters do not use
// these entry points and therefore never resolve a second policy/budget.
internal static class PackageCacheNativeFileSystem
{
    internal static ValueTask ReplaceFileDurablyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken) =>
        FileSystemContext.CreateDefault().FileSystem.ReplaceFileAsync(
            sourcePath, destinationPath, cancellationToken);

    internal static ValueTask ReplaceWindowsFileDurablyAsync(
        string sourcePath,
        string destinationPath,
        Func<string, string, uint, bool> moveFileEx,
        Func<int> getLastError,
        Func<TimeSpan, CancellationToken, ValueTask> delayAsync,
        CancellationToken cancellationToken) =>
        ResilientFileSystem.ReplaceWindowsFileDurablyAsync(
            sourcePath, destinationPath, moveFileEx, getLastError, delayAsync, cancellationToken);

    internal static void MovePathDurably(string sourcePath, string destinationPath) =>
        FileSystemContext.CreateDefault().FileSystem.MovePath(sourcePath, destinationPath);

    internal static void SynchronizeDirectory(string directoryPath) =>
        FileSystemContext.CreateDefault().FileSystem.SynchronizeDirectory(directoryPath);

    internal static PackageCacheDirectorySyncOutcome SynchronizeMacDirectory(
        Func<int> fullSync,
        Func<int> standardSync,
        Func<int> getLastError) =>
        (PackageCacheDirectorySyncOutcome)ResilientFileSystem.SynchronizeMacDirectory(fullSync, standardSync, getLastError);

    internal static bool PathExistsNoFollow(string path) =>
        FileSystemContext.CreateDefault().FileSystem.Probe(path).Exists;

    internal static bool IsSymbolicLinkNoFollow(string path) =>
        FileSystemContext.CreateDefault().FileSystem.Probe(path).IsLink;

    internal static void DeleteSymbolicLink(string path, bool directoryLink) =>
        FileSystemContext.CreateDefault().FileSystem.Delete(
            path,
            directoryLink && OperatingSystem.IsWindows()
                ? FileSystemEntryKind.DirectorySymbolicLink
                : FileSystemEntryKind.SymbolicLink,
            recursive: false,
            missingAllowed: true);

    internal static bool TryGetLinuxPathModeNoFollow(string path, out int mode)
    {
        FileSystemProbeResult probe = FileSystemContext.CreateDefault().FileSystem.Execute(
            "InspectLinuxPath", () => NativeFileSystemPrimitives.ProbeLinuxPath(path));
        mode = probe.Mode;
        return probe.Exists;
    }

    internal static int GetLinuxDescriptorMode(int descriptor)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Linux statx inspection is only available on Linux.");

        return FileSystemContext.CreateDefault().FileSystem.Execute(
            "InspectLinuxDescriptor", () => NativeFileSystemPrimitives.InspectDescriptor(descriptor)).Mode;
    }

    internal static PackageCacheLinuxStatxOutcome ClassifyLinuxStatxResult(int nativeResult, int error) =>
        (PackageCacheLinuxStatxOutcome)NativeFileSystemPrimitives.ClassifyLinuxStatxResult(nativeResult, error);
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
