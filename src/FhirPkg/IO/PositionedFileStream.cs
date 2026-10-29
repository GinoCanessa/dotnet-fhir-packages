// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace FhirPkg.IO;

/// <summary>
/// An owned, unbuffered file with a logical position. Each block retains its
/// handle, offset and (for writes) private bytes for the lifetime of its budget.
/// Like a borrowed file, this stream requires nonconcurrent use.
/// </summary>
internal sealed class PositionedFileStream : Stream
{
    private readonly FileSystemContext _context;
    private readonly FileAccess _access;
    private readonly string _path;
    private readonly bool _writeExclusionEstablished;
    private SafeFileHandle? _handle;
    private long _position;
    private long _length;
    private int _active;
    private bool _hadFailure;

    internal PositionedFileStream(
        FileSystemContext context, SafeFileHandle handle, string path, FileAccess access, long length,
        bool writeExclusionEstablished = false)
    {
        _context = context;
        _handle = handle;
        _path = path;
        _access = access;
        _length = length;
        _writeExclusionEstablished = writeExclusionEstablished;
    }

    internal FileSystemContext Context => _context;

    internal string FilePath => _path;

    public override bool CanRead => _handle is not null && (_access & FileAccess.Read) != 0;

    public override bool CanSeek => _handle is not null;

    public override bool CanWrite => _handle is not null && (_access & FileAccess.Write) != 0;

    public override long Length
    {
        get
        {
            BeginOperation();
            try
            {
                return GetLengthCore();
            }
            catch
            {
                _hadFailure = true;
                throw;
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
            EnsureOpen();
            return _position;
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Seek(value, SeekOrigin.Begin);
        }
    }

    internal async ValueTask<long> GetLengthAsync(CancellationToken cancellationToken)
    {
        BeginOperation();
        try
        {
            long length = await _context.FileSystem.ExecuteAsync(
                    "GetFileLength", () => _context.Primitives.GetLength(_handle!, _path), cancellationToken)
                .ConfigureAwait(false);
            _length = length;
            return length;
        }
        catch
        {
            _hadFailure = true;
            throw;
        }
        finally
        {
            EndOperation();
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        BeginOperation();
        try
        {
            long basis = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => _position,
                SeekOrigin.End => GetLengthCore(),
                _ => throw new ArgumentException("Invalid seek origin.", nameof(origin)),
            };
            long position;
            try
            {
                position = checked(basis + offset);
            }
            catch (OverflowException exception)
            {
                throw new IOException("The requested file position is outside the supported range.", exception);
            }
            if (position < 0)
                throw new IOException("Cannot seek before the beginning of the file.");
            _position = position;
            return position;
        }
        catch
        {
            _hadFailure = true;
            throw;
        }
        finally
        {
            EndOperation();
        }
    }

    public override void SetLength(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        RequireWrite();
        BeginOperation();
        try
        {
            _context.Executor.Execute(new SetLengthOperation(this, value));
            _length = value;
            if (_position > value)
                _position = value;
        }
        catch
        {
            _hadFailure = true;
            throw;
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
        RequireRead();
        BeginOperation();
        try
        {
            if (buffer.IsEmpty)
                return 0;
            ReadOperation operation = new ReadOperation(this, buffer);
            int read = _context.Executor.Execute(operation).Value;
            _position = checked(_position + read);
            return read;
        }
        catch
        {
            _hadFailure = true;
            throw;
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
        RequireRead();
        BeginOperation();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty)
                return 0;
            ReadOperation operation = new ReadOperation(this, buffer);
            int read = (await _context.Executor.ExecuteAsync(operation, cancellationToken).ConfigureAwait(false)).Value;
            _position = checked(_position + read);
            return read;
        }
        catch
        {
            _hadFailure = true;
            throw;
        }
        finally
        {
            EndOperation();
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        RequireWrite();
        BeginOperation();
        try
        {
            long end = checked(_position + buffer.Length);
            WriteOperation operation = new WriteOperation(this, buffer.ToArray());
            _context.Executor.Execute(operation);
            CommitWrite(end);
        }
        catch
        {
            _hadFailure = true;
            throw;
        }
        finally
        {
            EndOperation();
        }
    }

    public override void WriteByte(byte value)
    {
        ReadOnlySpan<byte> buffer = [value];
        Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        RequireWrite();
        cancellationToken.ThrowIfCancellationRequested();
        // Copy before the first await: caller memory is not the replay buffer.
        return WriteRetainedAsync(buffer.ToArray(), cancellationToken);
    }

    private async ValueTask WriteRetainedAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        BeginOperation();
        try
        {
            long end = checked(_position + bytes.Length);
            WriteOperation operation = new WriteOperation(this, bytes);
            await _context.Executor.ExecuteAsync(operation, cancellationToken).ConfigureAwait(false);
            CommitWrite(end);
        }
        catch
        {
            _hadFailure = true;
            throw;
        }
        finally
        {
            EndOperation();
        }
    }

    private void CommitWrite(long end)
    {
        _position = end;
        _length = Math.Max(_length, end);
    }

    public override void Flush()
    {
        BeginOperation();
        try
        {
            _context.FileSystem.Execute("FlushFile", () => _context.Primitives.FlushFile(_handle!, _path));
        }
        catch
        {
            _hadFailure = true;
            throw;
        }
        finally
        {
            EndOperation();
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        BeginOperation();
        try
        {
            await _context.FileSystem.ExecuteAsync(
                "FlushFile", () => _context.Primitives.FlushFile(_handle!, _path), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _hadFailure = true;
            throw;
        }
        finally
        {
            EndOperation();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SafeFileHandle? handle = Interlocked.Exchange(ref _handle, null);
            if (handle is not null)
            {
                try
                {
                    // There is no managed write buffer to flush at close. Consume
                    // the ownership token once, even when release reports failure.
                    CompleteRelease(_context.Primitives.CloseFile(handle, _path));
                }
                catch when (_hadFailure)
                {
                    // A close failure must not replace the read/write/flush error.
                }
            }
        }
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal void ReleaseAfterFailure()
    {
        _hadFailure = true;
        Dispose();
    }

    internal static void CompleteRelease(FileSystemReplayOutcome<bool> outcome)
    {
        if (outcome.Kind == FileSystemReplayOutcomeKind.PrimitiveFailure)
            outcome.PrimitiveFailure!.ThrowTerminal("CloseFile", 1, 0, FileSystemFailureTermination.Unsafe);
        ResilientFileSystem.ValueOrThrow(outcome);
    }

    internal static FileSystemReplayOutcome<bool> CheckMutationReplaySafety(
        FileSystemReplayOutcome<bool> outcome,
        bool writeExclusionEstablished,
        string phase,
        long attemptCount,
        long retriesUsed)
    {
        if (!writeExclusionEstablished && outcome.Kind == FileSystemReplayOutcomeKind.PrimitiveFailure)
            outcome.PrimitiveFailure!.ThrowTerminal(
                phase, attemptCount, retriesUsed, FileSystemFailureTermination.Unsafe);
        return outcome;
    }

    private long GetLengthCore()
    {
        long length = _context.FileSystem.Execute(
            "GetFileLength", () => _context.Primitives.GetLength(_handle!, _path));
        _length = length;
        return length;
    }

    private void BeginOperation()
    {
        EnsureOpen();
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new InvalidOperationException("Concurrent use of a positioned file is not supported.");
    }

    private void EndOperation() => Volatile.Write(ref _active, 0);

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(_handle is null, this);

    private void RequireRead()
    {
        EnsureOpen();
        if (!CanRead)
            throw new NotSupportedException("The file is not readable.");
    }

    private void RequireWrite()
    {
        EnsureOpen();
        if (!CanWrite)
            throw new NotSupportedException("The file is not writable.");
    }

    private sealed class SetLengthOperation : FileSystemReplayOperation<bool>
    {
        private readonly PositionedFileStream _owner;
        private readonly SafeFileHandle _handle;
        private readonly long _length;

        internal SetLengthOperation(PositionedFileStream owner, long length)
            : base(owner._context.Policy, "SetFileLength")
        {
            _owner = owner;
            _handle = owner._handle!;
            _length = length;
        }

        internal override FileSystemReplayOutcome<bool> ExecuteStep(CancellationToken cancellationToken) =>
            CheckMutationReplaySafety(
                _owner._context.Primitives.SetLength(_handle, _length, _owner._path),
                _owner._writeExclusionEstablished, Phase, AttemptCount, RetriesUsed);
    }

    private sealed class ReadOperation : FileSystemReplayOperation<int>
    {
        private readonly PositionedFileStream _owner;
        private readonly SafeFileHandle _handle;
        private readonly Memory<byte> _buffer;
        private readonly long _offset;

        internal ReadOperation(PositionedFileStream owner, Memory<byte> buffer)
            : base(owner._context.Policy, "ReadFileBlock")
        {
            _owner = owner;
            _handle = owner._handle!;
            _buffer = buffer;
            _offset = owner._position;
        }

        internal override FileSystemReplayOutcome<int> ExecuteStep(CancellationToken cancellationToken) =>
            _owner._context.Primitives.ReadAt(_handle, _buffer, _offset, _owner._path);

        internal override ValueTask<FileSystemReplayOutcome<int>> ExecuteStepAsync(CancellationToken cancellationToken) =>
            _owner._context.Primitives.ReadAtAsync(_handle, _buffer, _offset, _owner._path, cancellationToken);
    }

    private sealed class WriteOperation : FileSystemReplayOperation<bool>
    {
        private readonly PositionedFileStream _owner;
        private readonly SafeFileHandle _handle;
        private readonly ReadOnlyMemory<byte> _bytes;
        private readonly long _offset;

        internal WriteOperation(PositionedFileStream owner, byte[] bytes)
            : base(owner._context.Policy, "WriteFileBlock")
        {
            _owner = owner;
            _handle = owner._handle!;
            _bytes = bytes;
            _offset = owner._position;
        }

        internal override FileSystemReplayOutcome<bool> ExecuteStep(CancellationToken cancellationToken) =>
            CheckMutationReplaySafety(
                _owner._context.Primitives.WriteAt(_handle, _bytes, _offset, _owner._path),
                _owner._writeExclusionEstablished, Phase, AttemptCount, RetriesUsed);

        internal override async ValueTask<FileSystemReplayOutcome<bool>> ExecuteStepAsync(CancellationToken cancellationToken)
        {
            FileSystemReplayOutcome<bool> outcome = await _owner._context.Primitives.WriteAtAsync(
                _handle, _bytes, _offset, _owner._path, cancellationToken).ConfigureAwait(false);
            return CheckMutationReplaySafety(
                outcome, _owner._writeExclusionEstablished, Phase, AttemptCount, RetriesUsed);
        }
    }
}
