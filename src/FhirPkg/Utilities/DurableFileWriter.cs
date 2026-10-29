// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.IO;

namespace FhirPkg.Utilities;

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

        string fullDestinationPath = await fileOperations.GetFullPathAsync(
                destinationPath, cancellationToken)
            .ConfigureAwait(false);
        string directoryPath =
            Path.GetDirectoryName(fullDestinationPath)
            ?? throw new IOException(
                "The durable file destination has no parent directory.");
        await fileOperations.CreateDirectoryAsync(directoryPath, cancellationToken)
            .ConfigureAwait(false);
        string tempPath = Path.Combine(
            directoryPath,
            $".{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.tmp");
        DurableFileReplacement replacement = new DurableFileReplacement(tempPath, fullDestinationPath);
        bool ownsTemp = false;
        try
        {
            await fileOperations.WriteFileAndFlushAsync(
                    tempPath,
                    content,
                    cancellationToken)
                .ConfigureAwait(false);
            ownsTemp = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (beforeCommit is not null)
            {
                await beforeCommit(cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            await fileOperations.AtomicReplaceFileAsync(
                    replacement,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                // A failed create-new never transfers ownership to this writer.
                // After promotion (or an ambiguous mutation), the old pathname
                // is not evidence of ownership and must not be deleted.
                if (ownsTemp && replacement.CanCleanSource)
                {
                    await fileOperations.DeleteFileAsync(tempPath, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // Temporary cleanup must not replace the durable write outcome.
            }
        }
    }
}
