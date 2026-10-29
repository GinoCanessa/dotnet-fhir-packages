// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace FhirPkg.IO;

internal sealed record BorrowedFileReadState(SafeFileHandle Handle, long Position);

/// <summary>
/// Leaves an ordinary caller file open, at its actual current position. After
/// one-shot write-buffer preparation the caller must not mutate/close the file
/// concurrently. Derived streams and opaque wrappers are never unwrapped.
/// </summary>
internal sealed class BorrowedFileReadStream : Stream
{
    private readonly FileSystemContext _context;
    private readonly FileStream _stream;
    private readonly SafeFileHandle _handle;
    private bool _disposed;
    private int _active;

    internal BorrowedFileReadStream(FileSystemContext context, FileStream stream)
    {
        if (!CanBorrow(stream))
            throw new ArgumentException("Only an ordinary readable, seekable FileStream can be borrowed.", nameof(stream));

        _context = context;
        _stream = stream;
        FileSystemReplayOutcome<BorrowedFileReadState> prepared = context.Primitives.PrepareBorrowedRead(stream);
        if (prepared.Kind == FileSystemReplayOutcomeKind.PrimitiveFailure)
        {
            // Even a read/handle getter can flush pending writes. Never give
            // preparation a retry, regardless of how far it progressed.
            prepared.PrimitiveFailure!.ThrowTerminal(
                "PrepareBorrowedRead", 1, 0, FileSystemFailureTermination.Unsafe);
        }
        _handle = ResilientFileSystem.ValueOrThrow(prepared).Handle;
    }

    internal static bool CanBorrow(Stream stream) =>
        stream.GetType() == typeof(FileStream) && stream.CanRead && stream.CanSeek;

    internal FileSystemContext Context => _context;

    public override bool CanRead => !_disposed && _stream.CanRead;

    public override bool CanSeek => !_disposed && _stream.CanSeek;

    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            BeginOperation();
            try
            {
                return _context.FileSystem.Execute(
                    "BorrowedFileLength", () => _context.Primitives.GetLength(_handle, _stream.Name));
            }
            finally
            {
                EndOperation();
            }
        }
    }

    public override long Position
    {
        get
        {
            BeginOperation();
            try
            {
                return _context.FileSystem.Execute(
                    "BorrowedFilePosition", () => _context.Primitives.GetPosition(_stream));
            }
            finally
            {
                EndOperation();
            }
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Seek(value, SeekOrigin.Begin);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (origin is not (SeekOrigin.Begin or SeekOrigin.Current or SeekOrigin.End))
            throw new ArgumentException("Invalid seek origin.", nameof(origin));
        BeginOperation();
        try
        {
            return _context.Executor.Execute(new SeekOperation(this, offset, origin)).Value;
        }
        finally
        {
            EndOperation();
        }
    }

    internal async ValueTask<long> SeekAsync(long offset, SeekOrigin origin, CancellationToken cancellationToken)
    {
        if (origin is not (SeekOrigin.Begin or SeekOrigin.Current or SeekOrigin.End))
            throw new ArgumentException("Invalid seek origin.", nameof(origin));
        BeginOperation();
        try
        {
            return (await _context.Executor.ExecuteAsync(
                new SeekOperation(this, offset, origin), cancellationToken).ConfigureAwait(false)).Value;
        }
        finally
        {
            EndOperation();
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadCore(buffer.AsMemory(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            int read = ReadCore(rented.AsMemory(0, buffer.Length));
            rented.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override int ReadByte()
    {
        Span<byte> value = stackalloc byte[1];
        return Read(value) == 0 ? -1 : value[0];
    }

    private int ReadCore(Memory<byte> buffer)
    {
        BeginOperation();
        try
        {
            return buffer.IsEmpty ? 0 : _context.Executor.Execute(new ReadOperation(this, buffer)).Value;
        }
        finally
        {
            EndOperation();
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        BeginOperation();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return buffer.IsEmpty
                ? 0
                : (await _context.Executor.ExecuteAsync(
                    new ReadOperation(this, buffer), cancellationToken).ConfigureAwait(false)).Value;
        }
        finally
        {
            EndOperation();
        }
    }

    public override void Flush() => EnsureOpen();

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();
        return Task.CompletedTask;
    }

    public override void SetLength(long value) => throw new NotSupportedException("The borrowed adapter is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("The borrowed adapter is read-only.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _disposed = true;
        // No flush, close or release of the caller's ownership token.
        base.Dispose(disposing);
    }

    private void BeginOperation()
    {
        EnsureOpen();
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new InvalidOperationException("Concurrent use of a borrowed file is not supported.");
    }

    private void EndOperation() => Volatile.Write(ref _active, 0);

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class ReadOperation : FileSystemReplayOperation<int>
    {
        private readonly BorrowedFileReadStream _owner;
        private readonly Memory<byte> _buffer;
        private long? _checkpoint;
        private bool _restore;

        internal ReadOperation(BorrowedFileReadStream owner, Memory<byte> buffer)
            : base(owner._context.Policy, "CheckpointBorrowedRead")
        {
            _owner = owner;
            _buffer = buffer;
        }

        internal override FileSystemReplayOutcome<int> ExecuteStep(CancellationToken cancellationToken)
        {
            FileSystemReplayOutcome<int>? prepared = PrepareRead();
            return prepared ?? FinishRead(_owner._context.Primitives.ReadBorrowed(_owner._stream, _buffer));
        }

        internal override async ValueTask<FileSystemReplayOutcome<int>> ExecuteStepAsync(CancellationToken cancellationToken)
        {
            FileSystemReplayOutcome<int>? prepared = PrepareRead();
            return prepared ?? FinishRead(await _owner._context.Primitives.ReadBorrowedAsync(
                _owner._stream, _buffer, cancellationToken).ConfigureAwait(false));
        }

        private FileSystemReplayOutcome<int>? PrepareRead()
        {
            if (!_checkpoint.HasValue)
            {
                FileSystemReplayOutcome<long> checkpoint = _owner._context.Primitives.GetPosition(_owner._stream);
                if (checkpoint.Kind != FileSystemReplayOutcomeKind.Completed)
                    return ResilientFileSystem.Failure<long, int>(checkpoint);
                _checkpoint = checkpoint.Value;
                SetPhase("ReadBorrowedFileBlock");
            }

            if (_restore)
            {
                SetPhase("RestoreBorrowedReadCheckpoint");
                FileSystemReplayOutcome<long> restored = _owner._context.Primitives.SetPosition(
                    _owner._stream, _checkpoint.Value);
                if (restored.Kind != FileSystemReplayOutcomeKind.Completed)
                    return ResilientFileSystem.Failure<long, int>(restored);
                if (restored.Value != _checkpoint.Value)
                    throw new IOException("The borrowed file position could not be restored.");
                _restore = false;
                SetPhase("ReadBorrowedFileBlock");
                return FileSystemReplayOutcome<int>.Continue();
            }

            return null;
        }

        private FileSystemReplayOutcome<int> FinishRead(FileSystemReplayOutcome<int> read)
        {
            if (read.Kind == FileSystemReplayOutcomeKind.PrimitiveFailure)
                _restore = true;
            return read;
        }
    }

    private sealed class SeekOperation : FileSystemReplayOperation<long>
    {
        private readonly BorrowedFileReadStream _owner;
        private readonly long _offset;
        private readonly SeekOrigin _origin;
        private long? _target;

        internal SeekOperation(BorrowedFileReadStream owner, long offset, SeekOrigin origin)
            : base(owner._context.Policy, "BorrowedSeekCheckpoint")
        {
            _owner = owner;
            _offset = offset;
            _origin = origin;
        }

        internal override FileSystemReplayOutcome<long> ExecuteStep(CancellationToken cancellationToken)
        {
            if (!_target.HasValue)
            {
                FileSystemReplayOutcome<long> basis = _origin switch
                {
                    SeekOrigin.Begin => FileSystemReplayOutcome<long>.Complete(0),
                    SeekOrigin.Current => _owner._context.Primitives.GetPosition(_owner._stream),
                    _ => _owner._context.Primitives.GetLength(_owner._handle, _owner._stream.Name),
                };
                if (basis.Kind != FileSystemReplayOutcomeKind.Completed)
                    return basis;
                try
                {
                    _target = checked(basis.Value + _offset);
                }
                catch (OverflowException exception)
                {
                    throw new IOException("The requested file position is outside the supported range.", exception);
                }
                if (_target < 0)
                    throw new IOException("Cannot seek before the beginning of the file.");
            }
            SetPhase("SeekBorrowedFile");
            FileSystemReplayOutcome<long> result = _owner._context.Primitives.SetPosition(_owner._stream, _target.Value);
            if (result.Kind == FileSystemReplayOutcomeKind.Completed && result.Value != _target)
                throw new IOException("The borrowed file position could not be established.");
            return result;
        }
    }
}
