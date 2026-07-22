// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Security.Cryptography;
using FhirPkg.Cache;

namespace FhirPkg.Installation;

/// <summary>
/// Copies package content into bounded, cache-local staging while computing
/// integrity hashes.
/// </summary>
internal static class PackageContentAcquirer
{
    private const string OperationsDirectoryName = ".fhirpkg";
    private const string StagingDirectoryName = "staging";
    private const string ArchiveFileName = "archive.tgz";
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

        string operationId = Guid.NewGuid().ToString("N");
        string stagingRoot = Path.Combine(
            cacheRoot,
            OperationsDirectoryName,
            StagingDirectoryName);
        string operationDirectory = Path.Combine(stagingRoot, operationId);
        string archivePath = Path.Combine(operationDirectory, ArchiveFileName);
        PackageCacheLease? operationOwner = coordinator is null
            ? null
            : await coordinator.AcquireOperationOwnerAsync(
                    operationId,
                    cancellationToken)
                .ConfigureAwait(false);
        bool completed = false;

        try
        {
            Directory.CreateDirectory(operationDirectory);

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
                await using FileStream destination = new FileStream(
                    archivePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                while (true)
                {
                    int bytesRead = await source.ReadAsync(
                            buffer.AsMemory(),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (bytesRead == 0)
                        break;

                    if (bytesRead > limits.MaxCompressedBytes - actualLength)
                        throw CompressedLimitExceeded(directive, limits.MaxCompressedBytes);

                    actualLength += bytesRead;
                    sha256.AppendData(buffer, 0, bytesRead);
                    sha1?.AppendData(buffer, 0, bytesRead);
                    await destination.WriteAsync(
                            buffer.AsMemory(0, bytesRead),
                            cancellationToken)
                        .ConfigureAwait(false);
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
                PackageContentAcquisition.TryDeleteOperationDirectory(operationDirectory);
                operationOwner?.Dispose();
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
        PackageCacheLease? operationOwner = null)
    {
        OperationId = operationId;
        OperationDirectory = operationDirectory;
        ArchivePath = archivePath;
        ActualLength = actualLength;
        Sha256 = sha256;
        Sha1 = sha1;
        _operationOwner = operationOwner;
    }

    private readonly PackageCacheLease? _operationOwner;

    internal string OperationId { get; }

    internal string OperationDirectory { get; }

    internal string ArchivePath { get; }

    internal long ActualLength { get; }

    internal string Sha256 { get; }

    internal string? Sha1 { get; }

    internal FileStream OpenArchiveRead() =>
        new FileStream(
            ArchivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    public ValueTask DisposeAsync()
    {
        try
        {
            TryDeleteOperationDirectory(OperationDirectory);
        }
        finally
        {
            _operationOwner?.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    internal static void TryDeleteOperationDirectory(string operationDirectory)
    {
        try
        {
            if (Directory.Exists(operationDirectory))
                Directory.Delete(operationDirectory, recursive: true);

            string? stagingRoot = Path.GetDirectoryName(operationDirectory);
            if (stagingRoot is not null
                && Directory.Exists(stagingRoot)
                && !Directory.EnumerateFileSystemEntries(stagingRoot).Any())
            {
                Directory.Delete(stagingRoot);
            }

            string? operationsRoot = stagingRoot is null
                ? null
                : Path.GetDirectoryName(stagingRoot);
            if (operationsRoot is not null
                && Directory.Exists(operationsRoot)
                && !Directory.EnumerateFileSystemEntries(operationsRoot).Any())
            {
                Directory.Delete(operationsRoot);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup must not replace the primary installation outcome.
        }
        catch (IOException)
        {
            // Cleanup must not replace the primary installation outcome.
        }
    }
}
