// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.IO;
using FhirPkg.Tests.Support;
using FhirPkg.Utilities;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.IO;

[Collection("EnvironmentVariable")]
public sealed class BorrowedFileReadStreamTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"borrowed-{Guid.NewGuid():N}");

    public BorrowedFileReadStreamTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BorrowedRead_RestoresChangedPositionWithinSameBudget(bool asynchronous)
    {
        string path = CreateFile("0123456789"u8.ToArray());
        using FileStream caller = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32, FileOptions.Asynchronous);
        caller.Position = 2;
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        FileSystemContext context = primitives.CreateContext(retries: 2);
        using Stream borrowed = context.ProtectRead(caller);
        string readOperation = asynchronous ? "ReadBorrowedAsync" : "ReadBorrowed";
        primitives.Enqueue<int>(readOperation, call =>
        {
            call.Stream!.Read(call.Buffer.Span[..2]).ShouldBe(2);
            return ScriptedFileSystemPrimitives.NativeFailure<int>(1117, readOperation, path);
        });
        primitives.Enqueue<long>("SetPosition", call =>
        {
            call.Stream!.Position = 7;
            return ScriptedFileSystemPrimitives.NativeFailure<long>(5, "SetPosition", path);
        });
        byte[] bytes = new byte[4];

        int read = asynchronous
            ? await borrowed.ReadAsync(bytes.AsMemory(), TestContext.Current.CancellationToken)
            : borrowed.Read(bytes, 0, bytes.Length);

        read.ShouldBe(4);
        bytes.ShouldBe("2345"u8.ToArray());
        caller.Position.ShouldBe(6);
        borrowed.Dispose();
        caller.CanRead.ShouldBeTrue();
        primitives.Count("PrepareBorrowedRead").ShouldBe(1);
        primitives.Count("GetPosition").ShouldBe(1);
        primitives.Count(readOperation).ShouldBe(2);
        primitives.Count("SetPosition").ShouldBe(2);
        primitives.Calls.Where(call => call.Operation == "SetPosition").Select(call => call.Offset).ShouldBe([2L, 2L]);
        primitives.Count("CloseFile").ShouldBe(0);
        (asynchronous ? primitives.AsyncWaits : primitives.Waits).ShouldBe([10, 10]);
        (asynchronous ? primitives.Waits : primitives.AsyncWaits).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BorrowedRead_ExhaustedRestoreDoesNotReadAgain(bool asynchronous)
    {
        string path = CreateFile("abcdef"u8.ToArray());
        using FileStream caller = File.OpenRead(path);
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        using Stream borrowed = primitives.CreateContext(retries: 2).ProtectRead(caller);
        string readOperation = asynchronous ? "ReadBorrowedAsync" : "ReadBorrowed";
        primitives.Enqueue<int>(readOperation, call =>
        {
            call.Stream!.ReadByte().ShouldBe((int)'a');
            return ScriptedFileSystemPrimitives.NativeFailure<int>(1117, readOperation, path);
        });
        IOException restoreFailure = new IOException("restore exhausted");
        primitives.Set<long>("SetPosition", call =>
            ScriptedFileSystemPrimitives.Fail<long>(restoreFailure, "SetPosition", call.Path));

        IOException final = asynchronous
            ? await Should.ThrowAsync<IOException>(() => borrowed.ReadAsync(new byte[4].AsMemory(), TestContext.Current.CancellationToken).AsTask())
            : Should.Throw<IOException>(() => borrowed.Read(new byte[4], 0, 4));

        final.ShouldBeSameAs(restoreFailure);
        primitives.Count(readOperation).ShouldBe(1);
        primitives.Count("SetPosition").ShouldBe(2);
        primitives.Count("GetPosition").ShouldBe(1);
        caller.Position.ShouldBe(1);
        caller.CanRead.ShouldBeTrue();
        (asynchronous ? primitives.AsyncWaits : primitives.Waits).ShouldBe([10, 10]);
        FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? evidence).ShouldBeTrue();
        evidence!.RetriesUsed.ShouldBe(2);
        evidence.Phase.ShouldBe("RestoreBorrowedReadCheckpoint");
    }

    [Fact]
    public void BorrowedRead_CheckpointReadAndRecoveryShareOneBudget()
    {
        string path = CreateFile("abcdef"u8.ToArray());
        using FileStream caller = File.OpenRead(path);
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        using Stream borrowed = primitives.CreateContext(retries: 2).ProtectRead(caller);
        primitives.Enqueue("GetPosition", ScriptedFileSystemPrimitives.NativeFailure<long>(5, "checkpoint"));
        primitives.Enqueue<int>("ReadBorrowed", call =>
        {
            call.Stream!.Position = 3;
            return ScriptedFileSystemPrimitives.NativeFailure<int>(1117, "read");
        });
        primitives.Set("SetPosition", ScriptedFileSystemPrimitives.NativeFailure<long>(123456, "restore"));

        Should.Throw<IOException>(() => borrowed.Read(new byte[2], 0, 2));

        primitives.Count("GetPosition").ShouldBe(2);
        primitives.Count("ReadBorrowed").ShouldBe(1);
        primitives.Count("SetPosition").ShouldBe(1);
        primitives.Waits.ShouldBe([10, 10]);
    }

    [Fact]
    public void BorrowedRead_PendingWritePreparationIsNotReplayed()
    {
        string path = CreateFile([]);
        FileStream caller = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096);
        try
        {
            caller.Write("pending write"u8);
            new FileInfo(path).Length.ShouldBe(0);
            ScriptedFileSystemPrimitives primitives = SystemFiles();
            IOException failure = new IOException("preparation failed after emitting caller bytes");
            primitives.Enqueue<BorrowedFileReadState>("PrepareBorrowedRead", call =>
            {
                call.Stream!.Flush();
                // Even a mistakenly safe outcome cannot authorize replay of
                // preparation that may have written caller-owned buffered data.
                return ScriptedFileSystemPrimitives.Fail<BorrowedFileReadState>(failure, "PrepareBorrowedRead", path);
            });

            Should.Throw<IOException>(() => primitives.CreateContext().ProtectRead(caller)).ShouldBeSameAs(failure);

            primitives.Count("PrepareBorrowedRead").ShouldBe(1);
            primitives.Count("ReadBorrowed").ShouldBe(0);
            primitives.Count("GetPosition").ShouldBe(0);
            primitives.Waits.ShouldBeEmpty();
            primitives.AsyncWaits.ShouldBeEmpty();
            caller.CanWrite.ShouldBeTrue();
            caller.Position.ShouldBe(13);
            FileSystemFailureContext.TryGetTerminal(failure, out FileSystemFailureContext? evidence).ShouldBeTrue();
            evidence!.Termination.ShouldBe(FileSystemFailureTermination.Unsafe);
        }
        finally
        {
            caller.Dispose();
        }
        File.ReadAllText(path).ShouldBe("pending write");
    }

    [Fact]
    public void BorrowedRead_ReadWriteFileRemainsCompatibleAfterSafePreparation()
    {
        string path = CreateFile("abcdef"u8.ToArray());
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        using (FileStream caller = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096))
        {
            caller.Write("AB"u8);
            caller.Position.ShouldBe(2);
            using Stream borrowed = primitives.CreateContext().ProtectRead(caller);
            byte[] remaining = new byte[4];

            borrowed.Read(remaining, 0, remaining.Length).ShouldBe(4);

            remaining.ShouldBe("cdef"u8.ToArray());
            caller.Position.ShouldBe(6);
            borrowed.Dispose();
            caller.CanWrite.ShouldBeTrue();
            caller.CanRead.ShouldBeTrue();
            primitives.Count("PrepareBorrowedRead").ShouldBe(1);
            primitives.Count("ReadBorrowed").ShouldBe(1);
            primitives.Waits.ShouldBeEmpty();
            primitives.Count("CloseFile").ShouldBe(0);
        }
        File.ReadAllText(path).ShouldBe("ABcdef");
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("nonseekable")]
    [InlineData("derived")]
    [InlineData("memory")]
    public void OpaqueOrNonSeekableStream_IsReadOnce(string kind)
    {
        IOException failure = new IOException("caller stream failure");
        int reads = 0;
        using Stream caller = kind switch
        {
            "derived" => new DerivedFile(CreateFile("bytes"u8.ToArray()), () => reads++, failure),
            "memory" => new FailingMemory(() => reads++, failure),
            _ => new OpaqueStream(kind == "opaque", () => reads++, failure),
        };
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        Stream adapted = primitives.CreateContext().ProtectRead(caller);

        adapted.ShouldBeSameAs(caller);
        Should.Throw<IOException>(() => CheckSum.ComputeSha1(adapted)).ShouldBeSameAs(failure);

        reads.ShouldBe(1);
        primitives.Calls.ShouldBeEmpty();
        primitives.Waits.ShouldBeEmpty();
        FileSystemFailureContext.TryGetOrigin(failure, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtectedStream_IsNotRewrapped(bool owned)
    {
        string path = CreateFile("protected"u8.ToArray());
        ScriptedFileSystemPrimitives originalPrimitives = SystemFiles();
        FileSystemContext originalContext = originalPrimitives.CreateContext(retries: 1);
        using FileStream caller = File.OpenRead(path);
        using Stream protectedStream = owned
            ? originalContext.FileSystem.OpenProtectedRegularRead(path, TestContext.Current.CancellationToken)
            : originalContext.ProtectRead(caller);
        ScriptedFileSystemPrimitives otherPrimitives = SystemFiles();
        FileSystemContext otherContext = otherPrimitives.CreateContext(retries: 0);
        string? original = Environment.GetEnvironmentVariable(FileSystemRetryOptions.RetryCountEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(FileSystemRetryOptions.RetryCountEnvironmentVariable, "invalid");

            FileSystemContext.ProtectRead(protectedStream, context: null).ShouldBeSameAs(protectedStream);
            otherContext.ProtectRead(protectedStream).ShouldBeSameAs(protectedStream);
            string readOperation = owned ? "ReadAt" : "ReadBorrowed";
            originalPrimitives.Enqueue<int>(readOperation, call =>
                ScriptedFileSystemPrimitives.NativeFailure<int>(1117, readOperation, call.Path));
            CheckSum.ComputeSha1(protectedStream).ShouldBe(CheckSum.ComputeSha1("protected"u8.ToArray()));

            originalPrimitives.Waits.ShouldBe([10]);
            otherPrimitives.Calls.ShouldBeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileSystemRetryOptions.RetryCountEnvironmentVariable, original);
        }
    }

    [Fact]
    public void BorrowedSeek_RetainsAbsoluteTargetAfterPositionChangingFailure()
    {
        using FileStream caller = File.OpenRead(CreateFile("0123456789"u8.ToArray()));
        caller.Position = 2;
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        using Stream borrowed = primitives.CreateContext().ProtectRead(caller);
        primitives.Enqueue<long>("SetPosition", call =>
        {
            call.Stream!.Position = 8;
            return ScriptedFileSystemPrimitives.NativeFailure<long>(1117, "SetPosition", call.Path);
        });

        borrowed.Seek(3, SeekOrigin.Current).ShouldBe(5);

        caller.Position.ShouldBe(5);
        primitives.Count("GetPosition").ShouldBe(1);
        primitives.Calls.Where(call => call.Operation == "SetPosition").Select(call => call.Offset).ShouldBe([5L, 5L]);
        primitives.Waits.ShouldBe([10]);
    }

    [Fact]
    public void BorrowedRead_InexactRestorationStopsWithoutAnotherRead()
    {
        using FileStream caller = File.OpenRead(CreateFile("abcdef"u8.ToArray()));
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        using Stream borrowed = primitives.CreateContext().ProtectRead(caller);
        primitives.Enqueue("ReadBorrowed", ScriptedFileSystemPrimitives.NativeFailure<int>(1117, "ReadBorrowed"));
        primitives.Set("SetPosition", FileSystemReplayOutcome<long>.Complete(3));

        Should.Throw<IOException>(() => borrowed.ReadByte());

        primitives.Count("ReadBorrowed").ShouldBe(1);
        primitives.Count("SetPosition").ShouldBe(1);
        primitives.Waits.ShouldBe([10]);
    }

    [Fact]
    public void BorrowedRead_PreparationPreservesReadAheadLogicalPosition()
    {
        using FileStream caller = new FileStream(
            CreateFile("0123456789"u8.ToArray()), FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
        caller.ReadByte().ShouldBe((int)'0');
        caller.Position.ShouldBe(1);
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        using Stream borrowed = primitives.CreateContext().ProtectRead(caller);
        byte[] result = new byte[3];

        borrowed.Read(result, 0, result.Length).ShouldBe(3);

        result.ShouldBe("123"u8.ToArray());
        caller.Position.ShouldBe(4);
        primitives.Count("PrepareBorrowedRead").ShouldBe(1);
    }

    [Fact]
    public void BorrowedRead_RetryRetainsHandleWhenPathGenerationChanges()
    {
        string path = CreateFile("original"u8.ToArray());
        using FileStream caller = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        using Stream borrowed = primitives.CreateContext().ProtectRead(caller);
        primitives.Enqueue<int>("ReadBorrowed", call =>
        {
            call.Stream!.ReadByte().ShouldBe((int)'o');
            File.Move(path, path + ".old");
            File.WriteAllText(path, "replacement");
            return ScriptedFileSystemPrimitives.NativeFailure<int>(1117, "read", path);
        });
        byte[] result = new byte[8];

        borrowed.Read(result, 0, result.Length).ShouldBe(8);

        result.ShouldBe("original"u8.ToArray());
        caller.Position.ShouldBe(8);
        File.ReadAllText(path).ShouldBe("replacement");
        primitives.Count("ReadBorrowed").ShouldBe(2);
        primitives.Count("OpenFileHandle").ShouldBe(0);
        primitives.Count("PrepareBorrowedRead").ShouldBe(1);
        primitives.Waits.ShouldBe([10]);
    }

    private string CreateFile(byte[] bytes)
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BorrowedMetadata_UsesRetainedHandleAndMatchingWaits(bool asynchronous)
    {
        string path = CreateFile("0123456789"u8.ToArray());
        using FileStream caller = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        caller.Position = 3;
        ScriptedFileSystemPrimitives files = SystemFiles();
        using BorrowedFileReadStream borrowed = (BorrowedFileReadStream)files.CreateContext(retries: 1).ProtectRead(caller);
        files.Enqueue("GetPosition", ScriptedFileSystemPrimitives.NativeFailure<long>(5, "borrowed position", path));
        files.Enqueue("GetHandleLength", ScriptedFileSystemPrimitives.NativeFailure<long>(1117, "borrowed length", path));

        long position = asynchronous
            ? await borrowed.GetPositionAsync(TestContext.Current.CancellationToken)
            : borrowed.Position;
        long length = asynchronous
            ? await borrowed.GetLengthAsync(TestContext.Current.CancellationToken)
            : borrowed.Length;
        borrowed.Dispose();

        position.ShouldBe(3);
        length.ShouldBe(10);
        caller.Position.ShouldBe(3);
        caller.CanRead.ShouldBeTrue();
        caller.CanWrite.ShouldBeTrue();
        files.Count("PrepareBorrowedRead").ShouldBe(1);
        files.Count("GetPosition").ShouldBe(2);
        files.Count("GetHandleLength").ShouldBe(2);
        files.Calls.Where(call => call.Operation == "GetHandleLength")
            .Select(call => call.Handle).Distinct().Count().ShouldBe(1);
        files.Count("SetPosition").ShouldBe(0);
        files.Count("CloseFile").ShouldBe(0);
        (asynchronous ? files.AsyncWaits : files.Waits).ShouldBe([10, 10]);
        (asynchronous ? files.Waits : files.AsyncWaits).ShouldBeEmpty();
    }

    private static ScriptedFileSystemPrimitives SystemFiles()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(SystemFileSystemPrimitives.Instance.Platform);
        primitives.UseSystemFiles();
        return primitives;
    }

    private sealed class DerivedFile : FileStream
    {
        private readonly Action _onRead;
        private readonly Exception _failure;
        internal DerivedFile(string path, Action onRead, Exception failure) : base(path, FileMode.Open, FileAccess.Read)
        {
            _onRead = onRead;
            _failure = failure;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            _onRead();
            throw _failure;
        }
        public override int Read(Span<byte> buffer)
        {
            _onRead();
            throw _failure;
        }
    }

    private sealed class FailingMemory : MemoryStream
    {
        private readonly Action _onRead;
        private readonly Exception _failure;
        internal FailingMemory(Action onRead, Exception failure)
        {
            _onRead = onRead;
            _failure = failure;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            _onRead();
            throw _failure;
        }
        public override int Read(Span<byte> buffer)
        {
            _onRead();
            throw _failure;
        }
    }

    private sealed class OpaqueStream : Stream
    {
        private readonly bool _seekable;
        private readonly Action _onRead;
        private readonly Exception _failure;
        internal OpaqueStream(bool seekable, Action onRead, Exception failure)
        {
            _seekable = seekable;
            _onRead = onRead;
            _failure = failure;
        }
        public override bool CanRead => true;
        public override bool CanSeek => _seekable;
        public override bool CanWrite => false;
        public override long Length => 10;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            _onRead();
            throw _failure;
        }
        public override int Read(Span<byte> buffer)
        {
            _onRead();
            throw _failure;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
