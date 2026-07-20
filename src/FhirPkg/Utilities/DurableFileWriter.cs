// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.Utilities;

internal interface IDurableFileOperations
{
    bool FileExists(string path);

    void CreateDirectory(string path);

    void DeleteFile(string path);

    ValueTask WriteFileAndFlushAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);

    void AtomicReplaceFile(
        string sourcePath,
        string destinationPath);

    void SynchronizeDirectory(string directoryPath);
}

internal static class DurableFileWriter
{
    internal static async Task WriteAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        IDurableFileOperations fileOperations,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask>? beforeCommit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(fileOperations);

        string fullDestinationPath =
            Path.GetFullPath(destinationPath);
        string directoryPath =
            Path.GetDirectoryName(fullDestinationPath)
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
            if (beforeCommit is not null)
            {
                await beforeCommit(cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            fileOperations.AtomicReplaceFile(
                tempPath,
                fullDestinationPath);
            fileOperations.SynchronizeDirectory(directoryPath);
        }
        finally
        {
            try
            {
                if (fileOperations.FileExists(tempPath))
                    fileOperations.DeleteFile(tempPath);
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
