// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.IO;

namespace FhirPkg.Cache;

internal interface IPackageCacheFileOperations :
    IDurableFileOperations
{
    bool DirectoryExists(string path);

    Stream OpenRead(string path);

    ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);

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

    // Async companions default to the synchronous members so existing fault-injecting
    // adapters keep observing the same calls; the system adapter overrides them.
    ValueTask<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) =>
        ValueTask.FromResult(DirectoryExists(path));

    ValueTask MoveDirectoryAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        MoveDirectory(sourcePath, destinationPath);
        return ValueTask.CompletedTask;
    }

    ValueTask DeleteDirectoryAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        DeleteDirectory(path, recursive);
        return ValueTask.CompletedTask;
    }

    ValueTask<PackageCacheArtifactKind> GetArtifactKindAsync(
        string path,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetArtifactKind(path));

    ValueTask<bool> ArtifactExistsAsync(
        string path,
        PackageCacheArtifactKind artifactKind,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ArtifactExists(path, artifactKind));

    ValueTask MoveArtifactAsync(
        string sourcePath,
        string destinationPath,
        PackageCacheArtifactKind artifactKind,
        CancellationToken cancellationToken)
    {
        MoveArtifact(sourcePath, destinationPath, artifactKind);
        return ValueTask.CompletedTask;
    }

    ValueTask DeleteArtifactAsync(
        string path,
        PackageCacheArtifactKind artifactKind,
        CancellationToken cancellationToken)
    {
        DeleteArtifact(path, artifactKind);
        return ValueTask.CompletedTask;
    }
}

internal sealed class SystemPackageCacheFileOperations :
    IPackageCacheFileOperations
{
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
        Context.DurableFiles.FileExists(path);

    public Stream OpenRead(string path) =>
        Context.FileSystem.OpenProtectedRegularRead(path);

    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
        Context.FileSystem.OpenProtectedRegularReadAsync(path, cancellationToken);

    public ValueTask<string> GetFullPathAsync(string path, CancellationToken cancellationToken) =>
        Context.DurableFiles.GetFullPathAsync(path, cancellationToken);

    public void CreateDirectory(string path) =>
        Context.DurableFiles.CreateDirectory(path);

    public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        Context.DurableFiles.CreateDirectoryAsync(path, cancellationToken);

    public void MoveDirectory(string sourcePath, string destinationPath) =>
        Context.FileSystem.MovePath(sourcePath, destinationPath);

    public void MoveFile(string sourcePath, string destinationPath) =>
        Context.FileSystem.MovePath(sourcePath, destinationPath);

    public void DeleteDirectory(string path, bool recursive) =>
        Context.FileSystem.DeleteDirectory(path, recursive);

    public void DeleteFile(string path) =>
        Context.DurableFiles.DeleteFile(path);

    public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken) =>
        Context.DurableFiles.DeleteFileAsync(path, cancellationToken);

    public ValueTask WriteFileAndFlushAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken) =>
        Context.DurableFiles.WriteFileAndFlushAsync(path, content, cancellationToken);

    public ValueTask AtomicReplaceFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken) =>
        Context.DurableFiles.AtomicReplaceFileAsync(sourcePath, destinationPath, cancellationToken);

    public ValueTask AtomicReplaceFileAsync(DurableFileReplacement replacement, CancellationToken cancellationToken) =>
        Context.DurableFiles.AtomicReplaceFileAsync(replacement, cancellationToken);

    public void SynchronizeDirectory(string directoryPath) =>
        Context.DurableFiles.SynchronizeDirectory(directoryPath);

    public ValueTask SynchronizeDirectoryAsync(string directoryPath, CancellationToken cancellationToken) =>
        Context.DurableFiles.SynchronizeDirectoryAsync(directoryPath, cancellationToken);

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

        Context.FileSystem.Delete(
            path, ToEntryKind(artifactKind),
            recursive: artifactKind == PackageCacheArtifactKind.Directory,
            missingAllowed: true);
    }

    private static FileSystemEntryKind ToEntryKind(PackageCacheArtifactKind artifactKind) =>
        artifactKind switch
        {
            PackageCacheArtifactKind.Directory => FileSystemEntryKind.Directory,
            PackageCacheArtifactKind.SymbolicLink => FileSystemEntryKind.SymbolicLink,
            PackageCacheArtifactKind.DirectorySymbolicLink => FileSystemEntryKind.DirectorySymbolicLink,
            _ => FileSystemEntryKind.File,
        };

    public async ValueTask<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) =>
        (await Context.FileSystem.ProbeFollowingAsync(path, cancellationToken).ConfigureAwait(false)).Kind
            is FileSystemEntryKind.Directory or FileSystemEntryKind.DirectorySymbolicLink;

    public ValueTask MoveDirectoryAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken) =>
        Context.FileSystem.MovePathAsync(sourcePath, destinationPath, cancellationToken);

    public ValueTask DeleteDirectoryAsync(string path, bool recursive, CancellationToken cancellationToken) =>
        Context.FileSystem.DeleteAsync(
            path, FileSystemEntryKind.Directory, recursive, missingAllowed: false, cancellationToken);

    public async ValueTask<PackageCacheArtifactKind> GetArtifactKindAsync(
        string path,
        CancellationToken cancellationToken) =>
        (await Context.FileSystem.ProbeAsync(path, cancellationToken).ConfigureAwait(false)).Kind switch
        {
            FileSystemEntryKind.Missing => PackageCacheArtifactKind.Missing,
            FileSystemEntryKind.Directory => PackageCacheArtifactKind.Directory,
            FileSystemEntryKind.SymbolicLink => PackageCacheArtifactKind.SymbolicLink,
            FileSystemEntryKind.DirectorySymbolicLink => PackageCacheArtifactKind.DirectorySymbolicLink,
            _ => PackageCacheArtifactKind.File,
        };

    public async ValueTask<bool> ArtifactExistsAsync(
        string path,
        PackageCacheArtifactKind artifactKind,
        CancellationToken cancellationToken) =>
        artifactKind != PackageCacheArtifactKind.Missing
        && (await Context.FileSystem.ProbeAsync(path, cancellationToken).ConfigureAwait(false)).Exists;

    public async ValueTask MoveArtifactAsync(
        string sourcePath,
        string destinationPath,
        PackageCacheArtifactKind artifactKind,
        CancellationToken cancellationToken)
    {
        if (artifactKind != PackageCacheArtifactKind.Missing)
            await Context.FileSystem.MovePathAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteArtifactAsync(
        string path,
        PackageCacheArtifactKind artifactKind,
        CancellationToken cancellationToken)
    {
        if (artifactKind == PackageCacheArtifactKind.Missing)
            return;

        await Context.FileSystem.DeleteAsync(
            path, ToEntryKind(artifactKind),
            recursive: artifactKind == PackageCacheArtifactKind.Directory,
            missingAllowed: true,
            cancellationToken).ConfigureAwait(false);
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
