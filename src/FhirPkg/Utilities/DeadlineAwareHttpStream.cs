// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Buffers;

namespace FhirPkg.Utilities;

internal sealed class DeadlineAwareHttpStream : Stream
{
    private readonly Stream _inner;
    private readonly HttpResponseMessage _response;
    private readonly CancellationTokenSource _timeoutSource;
    private readonly CancellationToken _operationToken;
    private readonly Func<Exception> _timeoutExceptionFactory;
    private int _disposed;

    internal DeadlineAwareHttpStream(
        Stream inner,
        HttpResponseMessage response,
        CancellationTokenSource timeoutSource,
        CancellationToken operationToken,
        Func<Exception> timeoutExceptionFactory,
        long? contentLength)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(timeoutSource);
        ArgumentNullException.ThrowIfNull(timeoutExceptionFactory);

        _inner = inner;
        _response = response;
        _timeoutSource = timeoutSource;
        _operationToken = operationToken;
        _timeoutExceptionFactory = timeoutExceptionFactory;
        ContentLength = contentLength;
    }

    internal long? ContentLength { get; }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                _operationToken,
                cancellationToken,
                _timeoutSource.Token);

        try
        {
            return await _inner.ReadAsync(buffer, linkedSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (_operationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                exception.Message,
                exception,
                _operationToken);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                exception.Message,
                exception,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (_timeoutSource.IsCancellationRequested)
        {
            throw _timeoutExceptionFactory();
        }
    }

    public override async Task CopyToAsync(
        Stream destination,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);

        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                _operationToken,
                cancellationToken,
                _timeoutSource.Token);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (true)
            {
                int read = await ReadAsync(
                        buffer.AsMemory(0, bufferSize),
                        linkedSource.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        linkedSource.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception)
            when (_operationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                exception.Message,
                exception,
                _operationToken);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                exception.Message,
                exception,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (_timeoutSource.IsCancellationRequested)
        {
            throw _timeoutExceptionFactory();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        _inner.Seek(offset, origin);

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing
            && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _inner.Dispose();
            _response.Dispose();
            _timeoutSource.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _response.Dispose();
                _timeoutSource.Dispose();
            }
        }

        GC.SuppressFinalize(this);
    }
}
