// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Buffers;
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FhirPkg.Registry;

internal sealed class NpmPackumentContent : HttpContent
{
    private const int Base64InputBufferSize = 49_152;
    private readonly string _archivePath;
    private readonly byte[] _prefix;
    private readonly byte[] _suffix;
    private readonly long _contentLength;

    internal NpmPackumentContent(
        string archivePath,
        long archiveLength,
        JsonObject packument,
        string dataMarker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(packument);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataMarker);
        ArgumentOutOfRangeException.ThrowIfNegative(archiveLength);

        _archivePath = archivePath;
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(packument);
        byte[] marker = Encoding.UTF8.GetBytes(dataMarker);
        int markerIndex = serialized.AsSpan().IndexOf(marker);
        if (markerIndex < 0
            || serialized.AsSpan(markerIndex + marker.Length).IndexOf(marker) >= 0)
        {
            throw new InvalidOperationException(
                "The NPM packument data marker must occur exactly once.");
        }

        _prefix = serialized[..markerIndex];
        _suffix = serialized[(markerIndex + marker.Length)..];
        long base64Length = checked(
            4L * ((archiveLength + 2L) / 3L));
        _contentLength = checked(
            _prefix.LongLength
            + base64Length
            + _suffix.LongLength);
        Headers.ContentType = new MediaTypeHeaderValue("application/json");
        Headers.ContentLength = _contentLength;
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _contentLength;
        return true;
    }

    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context) =>
        SerializeToStreamAsync(
            stream,
            context,
            CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(_prefix, cancellationToken)
            .ConfigureAwait(false);
        await using FileStream archive = new FileStream(
            _archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            Base64InputBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await WriteBase64Async(
                archive,
                stream,
                cancellationToken)
            .ConfigureAwait(false);
        await stream.WriteAsync(_suffix, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteBase64Async(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        byte[] input = ArrayPool<byte>.Shared.Rent(
            Base64InputBufferSize);
        byte[] output = ArrayPool<byte>.Shared.Rent(
            Base64.GetMaxEncodedToUtf8Length(
                Base64InputBufferSize));
        try
        {
            int buffered = 0;
            while (true)
            {
                int read = await source.ReadAsync(
                        input.AsMemory(
                            buffered,
                            Base64InputBufferSize - buffered),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                int total = buffered + read;
                int encodeLength = total - (total % 3);
                if (encodeLength > 0)
                {
                    OperationStatus status = Base64.EncodeToUtf8(
                        input.AsSpan(0, encodeLength),
                        output,
                        out int consumed,
                        out int written,
                        isFinalBlock: false);
                    if (status != OperationStatus.Done
                        || consumed != encodeLength)
                    {
                        throw new InvalidOperationException(
                            "The package attachment could not be base64 encoded.");
                    }

                    await destination.WriteAsync(
                            output.AsMemory(0, written),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                buffered = total - encodeLength;
                if (buffered > 0)
                {
                    input.AsSpan(
                            encodeLength,
                            buffered)
                        .CopyTo(input);
                }
            }

            if (buffered > 0)
            {
                OperationStatus status = Base64.EncodeToUtf8(
                    input.AsSpan(0, buffered),
                    output,
                    out int consumed,
                    out int written,
                    isFinalBlock: true);
                if (status != OperationStatus.Done
                    || consumed != buffered)
                {
                    throw new InvalidOperationException(
                        "The package attachment could not be base64 encoded.");
                }

                await destination.WriteAsync(
                        output.AsMemory(0, written),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input, clearArray: true);
            ArrayPool<byte>.Shared.Return(output, clearArray: true);
        }
    }
}
