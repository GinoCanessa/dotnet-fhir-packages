// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.IO;

internal interface IDurableFileOperations
{
    bool FileExists(string path);

    ValueTask<string> GetFullPathAsync(string path, CancellationToken cancellationToken);

    void CreateDirectory(string path);

    ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    void DeleteFile(string path);

    ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken);

    // A failed write cleans up only a file whose create-new handle it acquired.
    ValueTask WriteFileAndFlushAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);

    ValueTask AtomicReplaceFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken);

    // Promotion and all remaining parent barriers share one operation/budget.
    ValueTask AtomicReplaceFileAsync(
        DurableFileReplacement replacement,
        CancellationToken cancellationToken);

    void SynchronizeDirectory(string directoryPath);

    ValueTask SynchronizeDirectoryAsync(string directoryPath, CancellationToken cancellationToken);
}

internal sealed class DurableFileReplacement
{
    internal DurableFileReplacement(string sourcePath, string destinationPath)
    {
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
    }

    internal string SourcePath { get; }

    internal string DestinationPath { get; }

    internal bool Promoted { get; set; }

    internal bool OutcomeUncertain { get; set; }

    internal bool CanCleanSource => !Promoted && !OutcomeUncertain;
}

internal sealed class SystemDurableFileOperations : IDurableFileOperations
{
    private readonly FileSystemContext _context;

    internal SystemDurableFileOperations(FileSystemContext context) => _context = context;

    public bool FileExists(string path) =>
        _context.FileSystem.ProbeFollowing(path).Kind is FileSystemEntryKind.File
            or FileSystemEntryKind.Other or FileSystemEntryKind.SymbolicLink;

    public ValueTask<string> GetFullPathAsync(string path, CancellationToken cancellationToken) =>
        _context.FileSystem.GetFullPathAsync(path, cancellationToken);

    public void CreateDirectory(string path) => _context.FileSystem.CreateDirectory(path);

    public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        _context.FileSystem.CreateDirectoryAsync(path, cancellationToken);

    public void DeleteFile(string path) => _context.FileSystem.DeleteFile(path);

    public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken) =>
        _context.FileSystem.DeleteAsync(
            path, FileSystemEntryKind.File, recursive: false, missingAllowed: true, cancellationToken);

    public ValueTask WriteFileAndFlushAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken) =>
        _context.FileSystem.WriteFileAndFlushAsync(path, content, cancellationToken);

    public ValueTask AtomicReplaceFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken) =>
        AtomicReplaceFileAsync(new DurableFileReplacement(sourcePath, destinationPath), cancellationToken);

    public ValueTask AtomicReplaceFileAsync(
        DurableFileReplacement replacement,
        CancellationToken cancellationToken) =>
        _context.FileSystem.ReplaceFileAsync(replacement, cancellationToken);

    public void SynchronizeDirectory(string directoryPath) =>
        _context.FileSystem.SynchronizeDirectory(directoryPath);

    public ValueTask SynchronizeDirectoryAsync(string directoryPath, CancellationToken cancellationToken) =>
        _context.FileSystem.SynchronizeDirectoryAsync(directoryPath, cancellationToken);
}
