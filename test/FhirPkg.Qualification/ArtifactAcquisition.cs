// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Buffers;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using FhirPkg.Qualification.Models;

namespace FhirPkg.Qualification;

internal sealed record ArchiveMetrics(
    long ExpandedBytes,
    long LargestEntryBytes,
    int EntryCount);

internal sealed record DownloadedArtifact(
    string Id,
    string FilePath,
    string ActualSha256,
    long CompressedBytes,
    ArchiveMetrics Metrics,
    Uri? FinalUri,
    DateTimeOffset? PublicationDate);

internal sealed class ArtifactDownloader : IDisposable
{
    private const long MaximumDownloadBytes =
        1024L * 1024L * 1024L;

    private readonly HttpClient _httpClient;
    private readonly string _downloadRoot;
    private readonly TimeSpan _timeout;

    internal ArtifactDownloader(
        string downloadRoot,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadRoot);
        _downloadRoot = downloadRoot;
        _timeout = timeout;
        Directory.CreateDirectory(_downloadRoot);
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        };
        _httpClient = new HttpClient(
            handler,
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal async Task<DownloadedArtifact> DownloadAsync(
        string id,
        Uri uri,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(uri);
        string filePath = Path.Combine(
            _downloadRoot,
            $"{SanitizeFileName(id)}.tgz");
        if (File.Exists(filePath))
            File.Delete(filePath);

        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeoutSource.CancelAfter(_timeout);
        bool completed = false;
        try
        {
            try
            {
                DownloadedArtifact artifact =
                    await DownloadCoreAsync(
                            id,
                            uri,
                            filePath,
                            timeoutSource.Token)
                        .ConfigureAwait(false);
                completed = true;
                return artifact;
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested
                    && timeoutSource.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Artifact '{id}' exceeded the qualification HTTP timeout.",
                    exception);
            }
        }
        finally
        {
            if (!completed)
                DeletePartialFile(filePath);
        }
    }

    private async Task<DownloadedArtifact> DownloadCoreAsync(
        string id,
        Uri uri,
        string filePath,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            uri);
        using HttpResponseMessage response =
            await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? reportedLength = response.Content.Headers.ContentLength;
        if (reportedLength is > MaximumDownloadBytes)
        {
            throw new InvalidDataException(
                $"Artifact '{id}' exceeds the qualification download limit.");
        }

        await using Stream source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65_536);
        long compressedBytes = 0;
        try
        {
            await using (FileStream destination = new(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65_536,
                FileOptions.Asynchronous
                    | FileOptions.SequentialScan))
            {
                while (true)
                {
                    int bytesRead = await source.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (bytesRead == 0)
                        break;

                    if (compressedBytes
                        > MaximumDownloadBytes - bytesRead)
                    {
                        throw new InvalidDataException(
                            $"Artifact '{id}' exceeds the qualification download limit.");
                    }

                    compressedBytes += bytesRead;
                    cancellationToken.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, bytesRead);
                    await destination.WriteAsync(
                            buffer.AsMemory(0, bytesRead),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await destination.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        cancellationToken.ThrowIfCancellationRequested();
        string actualSha256 = Convert.ToHexString(
                hash.GetHashAndReset())
            .ToLowerInvariant();
        ArchiveMetrics metrics =
            await ArchiveMetricsInspector.InspectAsync(
                    filePath,
                    cancellationToken)
                .ConfigureAwait(false);
        return new DownloadedArtifact(
            id,
            filePath,
            actualSha256,
            compressedBytes,
            metrics,
            response.RequestMessage?.RequestUri ?? uri,
            response.Content.Headers.LastModified);
    }

    public void Dispose() => _httpClient.Dispose();

    private static string SanitizeFileName(string value)
    {
        char[] chars = value
            .Select(character =>
                char.IsAsciiLetterOrDigit(character)
                    || character is '.' or '-' or '_'
                    ? character
                    : '_')
            .ToArray();
        return new string(chars);
    }

    private static void DeletePartialFile(string filePath)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
    }
}

internal static class ArchiveMetricsInspector
{
    internal static async Task<ArchiveMetrics> InspectAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        await using FileStream file = new(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await InspectAsync(
                file,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<ArchiveMetrics> InspectAsync(
        Stream archive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        await using GZipStream gzip = new(
            archive,
            CompressionMode.Decompress,
            leaveOpen: true);
        using TarReader reader = new(
            gzip,
            leaveOpen: false);
        long expandedBytes = 0;
        long largestEntryBytes = 0;
        int entryCount = 0;
        while (await reader.GetNextEntryAsync(
                copyData: false,
                cancellationToken)
            .ConfigureAwait(false) is TarEntry entry)
        {
            entryCount = checked(entryCount + 1);
            if (entry.EntryType is not (
                TarEntryType.RegularFile
                    or TarEntryType.V7RegularFile
                    or TarEntryType.ContiguousFile))
            {
                continue;
            }

            if (entry.Length < 0)
            {
                throw new InvalidDataException(
                    "The archive contains a negative entry length.");
            }

            expandedBytes = checked(expandedBytes + entry.Length);
            largestEntryBytes = Math.Max(
                largestEntryBytes,
                entry.Length);
        }

        return new ArchiveMetrics(
            expandedBytes,
            largestEntryBytes,
            entryCount);
    }
}
