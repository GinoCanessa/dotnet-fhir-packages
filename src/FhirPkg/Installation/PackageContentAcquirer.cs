// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Security.Cryptography;
using FhirPkg.Cache;
using FhirPkg.IO;

namespace FhirPkg.Installation;

/// <summary>
/// Copies package content into bounded, cache-local staging while computing
/// integrity hashes.
/// </summary>
internal static class PackageContentAcquirer
{
    private const int BufferSize = 81_920;

    internal static async Task<PackageContentAcquisition> AcquireAsync(
        Stream source,
        string cacheRoot,
        PackageInstallLimits limits,
        long? reportedContentLength = null,
        string? expectedSha256 = null,
        string? expectedSha1 = null,
        bool verifyChecksums = true,
        string? directive = null,
        CancellationToken cancellationToken = default) =>
        await AcquireCoreAsync(
                source,
                cacheRoot,
                limits,
                reportedContentLength,
                expectedSha256,
                expectedSha1,
                verifyChecksums,
                directive,
                coordinator: null,
                FileSystemContext.CreateDefault(),
                cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<PackageContentAcquisition> AcquireAsync(
        Stream source,
        string cacheRoot,
        PackageInstallLimits limits,
        long? reportedContentLength,
        string? expectedSha256,
        string? expectedSha1,
        bool verifyChecksums,
        string? directive,
        FileSystemContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await AcquireCoreAsync(
                source,
                cacheRoot,
                limits,
                reportedContentLength,
                expectedSha256,
                expectedSha1,
                verifyChecksums,
                directive,
                coordinator: null,
                context,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<PackageContentAcquisition> AcquireAsync(
        Stream source,
        string cacheRoot,
        PackageInstallLimits limits,
        long? reportedContentLength,
        string? expectedSha256,
        string? expectedSha1,
        bool verifyChecksums,
        string? directive,
        PackageCacheCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return await AcquireCoreAsync(
                source,
                cacheRoot,
                limits,
                reportedContentLength,
                expectedSha256,
                expectedSha1,
                verifyChecksums,
                directive,
                coordinator,
                FileSystemContext.CreateDefault(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<PackageContentAcquisition> AcquireAsync(
        Stream source,
        string cacheRoot,
        PackageInstallLimits limits,
        long? reportedContentLength,
        string? expectedSha256,
        string? expectedSha1,
        bool verifyChecksums,
        string? directive,
        PackageCacheCoordinator coordinator,
        FileSystemContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(context);
        return await AcquireCoreAsync(
                source,
                cacheRoot,
                limits,
                reportedContentLength,
                expectedSha256,
                expectedSha1,
                verifyChecksums,
                directive,
                coordinator,
                context,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<PackageContentAcquisition> AcquireCoreAsync(
        Stream source,
        string cacheRoot,
        PackageInstallLimits limits,
        long? reportedContentLength,
        string? expectedSha256,
        string? expectedSha1,
        bool verifyChecksums,
        string? directive,
        PackageCacheCoordinator? coordinator,
        FileSystemContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();

        if (!source.CanRead)
            throw new ArgumentException("Package content stream must be readable.", nameof(source));

        if (reportedContentLength is < 0)
        {
            throw new PackageInstallException(
                PackageInstallErrorCode.DownloadFailed,
                PackageInstallStage.Acquisition,
                $"Package '{directive ?? "content"}' reported an invalid content length.",
                directive);
        }

        if (reportedContentLength > limits.MaxCompressedBytes)
            throw CompressedLimitExceeded(directive, limits.MaxCompressedBytes);

        PackageCacheStagingArea stagingArea = new(cacheRoot, context);
        string operationId = Guid.NewGuid().ToString("N");
        string operationDirectory = stagingArea.GetOperationDirectory(operationId);
        string archivePath = stagingArea.GetArchivePath(operationId);
        PackageCacheLease? operationOwner = coordinator is null
            ? null
            : await coordinator.AcquireOperationOwnerAsync(
                    operationId,
                    cancellationToken)
                .ConfigureAwait(false);
        bool completed = false;

        try
        {
            await context.FileSystem.CreateDirectoryAsync(operationDirectory, cancellationToken)
                .ConfigureAwait(false);

            using IncrementalHash sha256 = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            using IncrementalHash? sha1 = verifyChecksums
                && !string.IsNullOrWhiteSpace(expectedSha1)
                    ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1)
                    : null;

            long actualLength = 0;
            byte[] buffer = new byte[BufferSize];

            try
            {
                // An ordinary caller file is borrowed once; downloads and other
                // opaque sources remain one-shot, unwrapped streams.
                Stream input = FileSystemContext.ProtectRead(source, context);
                try
                {
                    await using PositionedFileStream destination =
                        await context.FileSystem.OpenPositionedFileAsync(
                                archivePath,
                                FileMode.CreateNew,
                                FileAccess.Write,
                                FileShare.None,
                                FileOptions.Asynchronous | FileOptions.SequentialScan,
                                cancellationToken)
                            .ConfigureAwait(false);

                    while (true)
                    {
                        int bytesRead = await input.ReadAsync(
                                buffer.AsMemory(),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (bytesRead == 0)
                            break;

                        if (bytesRead > limits.MaxCompressedBytes - actualLength)
                            throw CompressedLimitExceeded(directive, limits.MaxCompressedBytes);

                        // The destination retains its own copy of the block until the
                        // write succeeds; length and hashes advance exactly once after.
                        await destination.WriteAsync(
                                buffer.AsMemory(0, bytesRead),
                                cancellationToken)
                            .ConfigureAwait(false);

                        actualLength += bytesRead;
                        sha256.AppendData(buffer, 0, bytesRead);
                        sha1?.AppendData(buffer, 0, bytesRead);
                    }
                }
                finally
                {
                    if (!ReferenceEquals(input, source))
                        await input.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (IOException exception)
            {
                throw new PackageInstallException(
                    PackageInstallErrorCode.DownloadFailed,
                    PackageInstallStage.Acquisition,
                    $"Package '{directive ?? "content"}' could not be staged.",
                    directive,
                    exception);
            }

            cancellationToken.ThrowIfCancellationRequested();

            string actualSha256 = ToLowerHex(sha256.GetHashAndReset());
            string? actualSha1 = sha1 is null
                ? null
                : ToLowerHex(sha1.GetHashAndReset());

            if (verifyChecksums)
            {
                VerifyChecksum(
                    expectedSha256,
                    expectedSha1,
                    actualSha256,
                    actualSha1,
                    directive);
            }

            PackageContentAcquisition result = new PackageContentAcquisition(
                operationId,
                operationDirectory,
                archivePath,
                actualLength,
                actualSha256,
                actualSha1,
                stagingArea,
                operationOwner);
            completed = true;
            return result;
        }
        catch (PackageInstallException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw AcquisitionFailure(directive, exception);
        }
        catch (IOException exception)
        {
            throw AcquisitionFailure(directive, exception);
        }
        finally
        {
            if (!completed)
            {
                try
                {
                    await stagingArea.CleanupOwnedAsync(operationDirectory)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Cleanup cannot replace the failed acquisition, including cancellation.
                }
                finally
                {
                    if (operationOwner is not null)
                    {
                        try
                        {
                            await operationOwner.DisposeAsync().ConfigureAwait(false);
                        }
                        catch
                        {
                            // Releasing the owner must not replace the acquisition failure.
                        }
                    }
                }
            }
        }
    }

    private static void VerifyChecksum(
        string? expectedSha256,
        string? expectedSha1,
        string actualSha256,
        string? actualSha1,
        string? directive)
    {
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            if (string.Equals(
                    expectedSha256.Trim(),
                    actualSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw ChecksumMismatch("SHA-256", directive);
        }

        if (string.IsNullOrWhiteSpace(expectedSha1))
            return;

        if (actualSha1 is not null
            && string.Equals(
                expectedSha1.Trim(),
                actualSha1,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw ChecksumMismatch("SHA-1", directive);
    }

    private static PackageInstallException ChecksumMismatch(
        string algorithm,
        string? directive) =>
        new PackageInstallException(
            PackageInstallErrorCode.ChecksumMismatch,
            PackageInstallStage.ChecksumValidation,
            $"{algorithm} checksum verification failed for '{directive ?? "package content"}'.",
            directive);

    private static PackageInstallException AcquisitionFailure(
        string? directive,
        Exception exception) =>
        new PackageInstallException(
            PackageInstallErrorCode.DownloadFailed,
            PackageInstallStage.Acquisition,
            $"Package '{directive ?? "content"}' could not be staged.",
            directive,
            exception);

    private static PackageInstallException CompressedLimitExceeded(
        string? directive,
        long maxCompressedBytes) =>
        new PackageInstallException(
            PackageInstallErrorCode.CompressedSizeLimitExceeded,
            PackageInstallStage.Acquisition,
            $"Package '{directive ?? "content"}' exceeds the compressed size limit of " +
            $"{maxCompressedBytes} bytes.",
            directive);

    private static string ToLowerHex(byte[] value)
    {
#if NET9_0_OR_GREATER
        return Convert.ToHexStringLower(value);
#else
        return Convert.ToHexString(value).ToLowerInvariant();
#endif
    }
}

/// <summary>Cache-local package content acquired for one install operation.</summary>
internal sealed class PackageContentAcquisition : IAsyncDisposable
{
    internal PackageContentAcquisition(
        string operationId,
        string operationDirectory,
        string archivePath,
        long actualLength,
        string sha256,
        string? sha1,
        PackageCacheStagingArea stagingArea,
        PackageCacheLease? operationOwner = null)
    {
        ArgumentNullException.ThrowIfNull(stagingArea);
        OperationId = operationId;
        OperationDirectory = operationDirectory;
        ArchivePath = archivePath;
        ActualLength = actualLength;
        Sha256 = sha256;
        Sha1 = sha1;
        _stagingArea = stagingArea;
        _operationOwner = operationOwner;
        _context = stagingArea.Context;
    }

    private readonly PackageCacheStagingArea _stagingArea;
    private readonly PackageCacheLease? _operationOwner;
    private readonly FileSystemContext _context;

    internal string OperationId { get; }

    internal string OperationDirectory { get; }

    internal string ArchivePath { get; }

    internal long ActualLength { get; }

    internal string Sha256 { get; }

    internal string? Sha1 { get; }

    /// <summary>
    /// Opens the staged archive with the acquisition's retained policy. Each
    /// later read replays only its own block against the same open handle.
    /// </summary>
    internal Stream OpenArchiveRead() =>
        _context.FileSystem.OpenProtectedRegularRead(ArchivePath);

    internal ValueTask<Stream> OpenArchiveReadAsync(CancellationToken cancellationToken) =>
        _context.FileSystem.OpenProtectedRegularReadAsync(ArchivePath, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        bool cleaned = false;
        try
        {
            await _stagingArea.CleanupOwnedAsync(OperationDirectory)
                .ConfigureAwait(false);
            cleaned = true;
        }
        finally
        {
            if (_operationOwner is not null)
            {
                try
                {
                    await _operationOwner.DisposeAsync().ConfigureAwait(false);
                }
                catch when (!cleaned)
                {
                    // Preserve a primary cleanup failure after releasing the lease.
                }
            }
        }
    }
}
