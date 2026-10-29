// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using FhirPkg.IO;
using FhirPkg.Tests.Support;
using Microsoft.Win32.SafeHandles;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.IO;

public sealed class PositionedFileStreamTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"positioned-{Guid.NewGuid():N}");

    public PositionedFileStreamTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialWrite_ReplaysSameRangeAndCommitsOffsetOnce(bool asynchronous)
    {
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        FileSystemContext context = primitives.CreateContext();
        string path = Path.Combine(_root, "partial");
        PositionedFileStream stream = context.FileSystem.OpenPositionedFile(
            path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous,
            TestContext.Current.CancellationToken);
        stream.Position = 5;
        byte[] input = "retained bytes"u8.ToArray();
        byte[] expected = input.ToArray();
        string operation = asynchronous ? "WriteAtAsync" : "WriteAt";
        primitives.Enqueue<bool>(operation, call =>
        {
            RandomAccess.Write(call.Handle!, call.Content.Span[..4], call.Offset!.Value);
            stream.Position.ShouldBe(5);
            return ScriptedFileSystemPrimitives.NativeFailure<bool>(1117, operation, call.Path);
        });
        primitives.OnWait = () =>
        {
            stream.Position.ShouldBe(5);
            input.AsSpan().Fill(0xFF);
        };

        try
        {
            if (asynchronous)
                await stream.WriteAsync(input.AsMemory(), TestContext.Current.CancellationToken);
            else
                stream.Write(input, 0, input.Length);

            stream.Position.ShouldBe(5 + expected.Length);
            stream.Length.ShouldBe(5 + expected.Length);
            FileSystemPrimitiveCall[] writes = primitives.Calls.Where(call => call.Operation == operation).ToArray();
            writes.Length.ShouldBe(2);
            writes.Select(call => call.Offset).ShouldBe([5L, 5L]);
            writes[0].Handle.ShouldBeSameAs(writes[1].Handle);
            writes[0].Content.Equals(writes[1].Content).ShouldBeTrue();
            foreach (FileSystemPrimitiveCall write in writes)
                write.Content.ToArray().ShouldBe(expected);
            (asynchronous ? primitives.AsyncWaits : primitives.Waits).ShouldBe([10]);
            (asynchronous ? primitives.Waits : primitives.AsyncWaits).ShouldBeEmpty();
            await stream.FlushAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await stream.DisposeAsync();
        }

        File.ReadAllBytes(path).ShouldBe(new byte[5].Concat(expected).ToArray());
        primitives.Count("CloseFile").ShouldBe(1);
        primitives.Count("OpenFile").ShouldBe(0);
    }

    [Theory]
    [InlineData("Windows", FileShare.None, false)]
    [InlineData("Windows", FileShare.None, true)]
    [InlineData("Windows", FileShare.Read, false)]
    [InlineData("Windows", FileShare.Read, true)]
    [InlineData("Linux", FileShare.None, false)]
    [InlineData("Linux", FileShare.None, true)]
    [InlineData("Linux", FileShare.Read, false)]
    [InlineData("Linux", FileShare.Read, true)]
    [InlineData("MacOS", FileShare.None, false)]
    [InlineData("MacOS", FileShare.None, true)]
    [InlineData("MacOS", FileShare.Read, false)]
    [InlineData("MacOS", FileShare.Read, true)]
    public async Task PartialWrite_SharingRequiresDemonstratedWriteExclusion(
        string platform, FileShare share, bool asynchronous)
    {
        ScriptedFileSystemPrimitives primitives = ScriptedOwnedFile(Enum.Parse<FileSystemPlatform>(platform));
        bool writeExcluded = platform == "Windows" || share == FileShare.None;
        long nativeLength = 5;
        primitives.Set<long>("GetHandleLength", _ => FileSystemReplayOutcome<long>.Complete(nativeLength));
        FileSystemContext context = primitives.CreateContext();
        string path = Path.Combine(_root, "sharing-proof");
        FileOptions options = asynchronous ? FileOptions.Asynchronous : FileOptions.None;
        PositionedFileStream stream = asynchronous
            ? await context.FileSystem.OpenPositionedFileAsync(
                path, FileMode.CreateNew, FileAccess.ReadWrite, share, options, TestContext.Current.CancellationToken)
            : context.FileSystem.OpenPositionedFile(
                path, FileMode.CreateNew, FileAccess.ReadWrite, share, options, TestContext.Current.CancellationToken);
        stream.Position = 5;
        byte[] input = "retained bytes"u8.ToArray();
        byte[] expected = input.ToArray();
        byte[] stored = new byte[5 + expected.Length];
        IOException failure = CapturedWriteFailure();
        string operation = asynchronous ? "WriteAtAsync" : "WriteAt";
        int completedBlocks = 0;
        primitives.Enqueue<bool>(operation, call =>
        {
            call.Content.Span[..3].CopyTo(stored.AsSpan((int)call.Offset!.Value));
            nativeLength = call.Offset.Value + 3;
            return FileSystemReplayOutcome<bool>.Failed(FileSystemFailureContext.CapturePrimitiveFailure(
                failure, FileSystemReplaySafety.Safe, operation, call.Path,
                nativeErrorCode: 1117, nativeFlags: 0x55));
        });
        primitives.Set<bool>(operation, call =>
        {
            stream.Position.ShouldBe(5);
            completedBlocks.ShouldBe(0);
            call.Content.Span.CopyTo(stored.AsSpan((int)call.Offset!.Value));
            nativeLength = call.Offset.Value + call.Content.Length;
            return FileSystemReplayOutcome<bool>.Complete(true);
        });
        primitives.OnWait = () =>
        {
            stream.Position.ShouldBe(5);
            completedBlocks.ShouldBe(0);
            input.AsSpan().Fill(0xFF);
        };
        if (!writeExcluded)
        {
            primitives.Enqueue<bool>("CloseFile", call =>
            {
                call.Handle!.SetHandleAsInvalid();
                call.Handle.Dispose();
                return ScriptedFileSystemPrimitives.NativeFailure<bool>(
                    5, "cleanup", call.Path, FileSystemReplaySafety.Unsafe);
            });
        }

        async Task WriteBlock()
        {
            if (asynchronous)
                await stream.WriteAsync(input.AsMemory(), TestContext.Current.CancellationToken);
            else
                stream.Write(input, 0, input.Length);
            completedBlocks++;
        }

        try
        {
            if (writeExcluded)
            {
                await WriteBlock();
                stream.Position.ShouldBe(5 + expected.Length);
                stream.Length.ShouldBe(5 + expected.Length);
                stored.ShouldBe(new byte[5].Concat(expected).ToArray());
                completedBlocks.ShouldBe(1);
                (asynchronous ? primitives.AsyncWaits : primitives.Waits).ShouldBe([10]);
                (asynchronous ? primitives.Waits : primitives.AsyncWaits).ShouldBeEmpty();
            }
            else
            {
                IOException final = await Should.ThrowAsync<IOException>(WriteBlock);
                final.ShouldBeSameAs(failure);
                final.InnerException.ShouldBeSameAs(failure.InnerException);
                final.StackTrace.ShouldNotBeNull().ShouldContain(nameof(CapturedWriteFailure));
                ((Win32Exception)final.InnerException!).NativeErrorCode.ShouldBe(1117);
                stream.Position.ShouldBe(5);
                stream.Length.ShouldBe(8);
                completedBlocks.ShouldBe(0);
                stored.AsSpan(5, 3).ToArray().ShouldBe(expected[..3]);
                stored.AsSpan(8).ToArray().ShouldBe(new byte[expected.Length - 3]);
                primitives.Waits.ShouldBeEmpty();
                primitives.AsyncWaits.ShouldBeEmpty();
                FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? evidence).ShouldBeTrue();
                evidence!.Termination.ShouldBe(FileSystemFailureTermination.Unsafe);
                evidence.Phase.ShouldBe("WriteFileBlock");
                evidence.Operation.ShouldBe(operation);
                evidence.Path.ShouldBe(path);
                evidence.NativeErrorCode.ShouldBe(1117);
                evidence.NativeFlags.ShouldBe(0x55);
                evidence.AttemptCount.ShouldBe(1);
                evidence.RetriesUsed.ShouldBe(0);
            }

            FileSystemPrimitiveCall[] writes = primitives.Calls.Where(call => call.Operation == operation).ToArray();
            writes.Length.ShouldBe(writeExcluded ? 2 : 1);
            writes.Select(call => call.Offset).ShouldAllBe(offset => offset == 5);
            foreach (FileSystemPrimitiveCall write in writes)
            {
                write.Handle.ShouldBeSameAs(writes[0].Handle);
                write.Content.Equals(writes[0].Content).ShouldBeTrue();
                write.Content.ToArray().ShouldBe(expected);
            }
        }
        finally
        {
            await stream.DisposeAsync();
            stream.Dispose();
        }

        string openOperation = platform == "Windows" ? "OpenFileHandle" : "OpenOwnedDescriptor";
        FileSystemPrimitiveCall opened = primitives.Calls.Single(call => call.Operation == openOperation);
        opened.Share.ShouldBe(share);
        opened.Options.ShouldBe(options);
        primitives.Calls.Where(call => call.Operation == "ConfigureFileSharing")
            .Select(call => call.Share).ShouldBe(platform == "Windows" ? [] : [share]);
        primitives.Count("CloseFile").ShouldBe(1);
        primitives.Count("FlushFile").ShouldBe(0);
        primitives.Count("SetLength").ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFailure_DoesNotSwitchFileGeneration(bool asynchronous)
    {
        string path = Path.Combine(_root, "generation");
        byte[] oldBytes = "old generation contents"u8.ToArray();
        File.WriteAllBytes(path, oldBytes);
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        using PositionedFileStream stream = primitives.CreateContext().FileSystem.OpenPositionedFile(
            path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.Asynchronous,
            TestContext.Current.CancellationToken);
        string operation = asynchronous ? "ReadAtAsync" : "ReadAt";
        primitives.Enqueue<int>(operation, call =>
        {
            RandomAccess.Read(call.Handle!, call.Buffer.Span[..3], call.Offset!.Value).ShouldBe(3);
            File.Move(path, path + ".old");
            File.WriteAllBytes(path, "new generation"u8.ToArray());
            return ScriptedFileSystemPrimitives.NativeFailure<int>(123456, operation, path);
        });
        byte[] result = new byte[oldBytes.Length];

        int count = asynchronous
            ? await stream.ReadAsync(result.AsMemory(), TestContext.Current.CancellationToken)
            : stream.Read(result, 0, result.Length);

        count.ShouldBe(oldBytes.Length);
        result.ShouldBe(oldBytes);
        stream.Position.ShouldBe(oldBytes.Length);
        File.ReadAllText(path).ShouldBe("new generation");
        FileSystemPrimitiveCall[] reads = primitives.Calls.Where(call => call.Operation == operation).ToArray();
        reads.Length.ShouldBe(2);
        reads.Select(call => call.Offset).ShouldBe([0L, 0L]);
        reads[0].Handle.ShouldBeSameAs(reads[1].Handle);
        primitives.Count("OpenFileHandle").ShouldBe(1);
        (asynchronous ? primitives.AsyncWaits : primitives.Waits).ShouldBe([10]);
    }

    [Fact]
    public void LengthAndEndSeek_UsePolicy()
    {
        string path = Path.Combine(_root, "metadata");
        File.WriteAllBytes(path, new byte[12]);
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        using PositionedFileStream stream = primitives.CreateContext().FileSystem.OpenPositionedFile(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None,
            TestContext.Current.CancellationToken);
        primitives.Enqueue("GetHandleLength", ScriptedFileSystemPrimitives.NativeFailure<long>(5, "length"));
        stream.Length.ShouldBe(12);
        stream.Position.ShouldBe(0);
        primitives.Enqueue("GetHandleLength", ScriptedFileSystemPrimitives.NativeFailure<long>(1117, "length"));

        stream.Seek(-3, SeekOrigin.End).ShouldBe(9);

        stream.Position.ShouldBe(9);
        primitives.Count("GetHandleLength").ShouldBe(5);
        primitives.Count("GetLength").ShouldBe(0);
        primitives.Calls.Where(call => call.Operation == "GetHandleLength")
            .Select(call => call.Handle).Distinct().Count().ShouldBe(1);
        primitives.Waits.ShouldBe([10, 10]);
    }

    [Fact]
    public void SetLength_ReplaysSameHandleAndUpdatesPositionAfterSuccess()
    {
        string path = Path.Combine(_root, "set-length");
        File.WriteAllBytes(path, "abcdef"u8.ToArray());
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        using PositionedFileStream stream = primitives.CreateContext().FileSystem.OpenPositionedFile(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, FileOptions.None,
            TestContext.Current.CancellationToken);
        stream.Position = 5;
        primitives.Enqueue<bool>("SetLength", call =>
        {
            RandomAccess.SetLength(call.Handle!, call.Length!.Value);
            stream.Position.ShouldBe(5);
            return ScriptedFileSystemPrimitives.NativeFailure<bool>(1117, "SetLength", path);
        });

        stream.SetLength(2);

        stream.Position.ShouldBe(2);
        stream.Length.ShouldBe(2);
        primitives.Count("SetLength").ShouldBe(2);
        primitives.Calls.Where(call => call.Operation == "SetLength").Select(call => call.Length).ShouldBe([2L, 2L]);
        primitives.Waits.ShouldBe([10]);
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("Linux")]
    [InlineData("MacOS")]
    public void SetLength_ReaderSharingRequiresWriteExclusion(string platform)
    {
        ScriptedFileSystemPrimitives primitives = ScriptedOwnedFile(Enum.Parse<FileSystemPlatform>(platform));
        long nativeLength = 6;
        primitives.Set<long>("GetHandleLength", _ => FileSystemReplayOutcome<long>.Complete(nativeLength));
        PositionedFileStream stream = primitives.CreateContext().FileSystem.OpenPositionedFile(
            Path.Combine(_root, "shared-length"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.None,
            TestContext.Current.CancellationToken);
        stream.Position = 5;
        FileSystemReplayOutcome<bool> failure = ScriptedFileSystemPrimitives.NativeFailure<bool>(1117, "SetLength");
        primitives.Enqueue<bool>("SetLength", _ =>
        {
            nativeLength = 2;
            return failure;
        });
        primitives.Set("SetLength", FileSystemReplayOutcome<bool>.Complete(true));

        try
        {
            if (platform == "Windows")
            {
                stream.SetLength(2);
                stream.Position.ShouldBe(2);
                primitives.Count("SetLength").ShouldBe(2);
                primitives.Waits.ShouldBe([10]);
            }
            else
            {
                Should.Throw<IOException>(() => stream.SetLength(2)).ShouldBeSameAs(failure.PrimitiveFailure!.Exception);
                stream.Position.ShouldBe(5);
                primitives.Count("SetLength").ShouldBe(1);
                primitives.Waits.ShouldBeEmpty();
                FileSystemFailureContext.TryGetTerminal(
                    failure.PrimitiveFailure!.Exception, out FileSystemFailureContext? evidence).ShouldBeTrue();
                evidence!.Termination.ShouldBe(FileSystemFailureTermination.Unsafe);
                evidence.Phase.ShouldBe("SetFileLength");
                evidence.AttemptCount.ShouldBe(1);
                evidence.RetriesUsed.ShouldBe(0);
            }
            stream.Length.ShouldBe(2);
            primitives.Calls.Where(call => call.Operation == "SetLength").Select(call => call.Handle)
                .Distinct().Count().ShouldBe(1);
            primitives.Calls.Where(call => call.Operation == "SetLength").Select(call => call.Length)
                .ShouldAllBe(length => length == 2);
            primitives.AsyncWaits.ShouldBeEmpty();
        }
        finally
        {
            stream.Dispose();
        }
        primitives.Count("OpenFileHandle").ShouldBe(1);
        primitives.Count("OpenOwnedDescriptor").ShouldBe(0);
        primitives.Count("CloseFile").ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlushFailure_RetriesBeforeSingleDispose(bool asynchronous)
    {
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        PositionedFileStream stream = primitives.CreateContext().FileSystem.OpenPositionedFile(
            Path.Combine(_root, "flush"), FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.Asynchronous,
            TestContext.Current.CancellationToken);
        stream.WriteByte(42);
        primitives.Enqueue<bool>("FlushFile", call =>
        {
            call.Handle!.IsClosed.ShouldBeFalse();
            return ScriptedFileSystemPrimitives.NativeFailure<bool>(1117, "FlushFile", call.Path);
        });
        primitives.Enqueue<bool>("FlushFile", call =>
        {
            call.Handle!.IsClosed.ShouldBeFalse();
            return ScriptedFileSystemPrimitives.NativeFailure<bool>(123456, "FlushFile", call.Path);
        });

        if (asynchronous)
            await stream.FlushAsync(TestContext.Current.CancellationToken);
        else
            stream.Flush();
        primitives.Count("CloseFile").ShouldBe(0);
        await stream.DisposeAsync();
        stream.Dispose();

        primitives.Count("FlushFile").ShouldBe(3);
        primitives.Count("CloseFile").ShouldBe(1);
        primitives.Calls.Last().Operation.ShouldBe("CloseFile");
        (asynchronous ? primitives.AsyncWaits : primitives.Waits).ShouldBe([10, 10]);
        (asynchronous ? primitives.Waits : primitives.AsyncWaits).ShouldBeEmpty();
    }

    [Fact]
    public void ExhaustedWrite_DoesNotAdvanceOrFlushAgainDuringDisposal()
    {
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        PositionedFileStream stream = primitives.CreateContext(retries: 1).FileSystem.OpenPositionedFile(
            Path.Combine(_root, "exhausted"), FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.None,
            TestContext.Current.CancellationToken);
        IOException writeFailure = new IOException("write failed");
        IOException closeFailure = new IOException("close failed");
        primitives.Set<bool>("WriteAt", call =>
        {
            RandomAccess.Write(call.Handle!, call.Content.Span[..1], call.Offset!.Value);
            return ScriptedFileSystemPrimitives.Fail<bool>(writeFailure, "WriteAt", call.Path);
        });
        primitives.Enqueue<bool>("CloseFile", call =>
        {
            call.Handle!.Dispose();
            return ScriptedFileSystemPrimitives.Fail<bool>(
                closeFailure, "CloseFile", call.Path, FileSystemReplaySafety.Unsafe);
        });

        Should.Throw<IOException>(() => stream.Write("abc"u8)).ShouldBeSameAs(writeFailure);
        stream.Position.ShouldBe(0);
        stream.Dispose();
        stream.Dispose();

        primitives.Count("WriteAt").ShouldBe(2);
        primitives.Count("FlushFile").ShouldBe(0);
        primitives.Count("CloseFile").ShouldBe(1);
        primitives.Waits.ShouldBe([10]);
    }

    [Fact]
    public void CloseFailure_ConsumesOwnershipEvenIfReportedReplayable()
    {
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        PositionedFileStream stream = primitives.CreateContext().FileSystem.OpenPositionedFile(
            Path.Combine(_root, "close"), FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.None,
            TestContext.Current.CancellationToken);
        IOException failure = new IOException("released then failed");
        primitives.Enqueue<bool>("CloseFile", call =>
        {
            call.Handle!.Dispose();
            return ScriptedFileSystemPrimitives.Fail<bool>(failure, "CloseFile", call.Path);
        });

        Should.Throw<IOException>(stream.Dispose).ShouldBeSameAs(failure);
        stream.Dispose();

        primitives.Count("CloseFile").ShouldBe(1);
        primitives.Waits.ShouldBeEmpty();
        FileSystemFailureContext.TryGetTerminal(failure, out FileSystemFailureContext? evidence).ShouldBeTrue();
        evidence!.Termination.ShouldBe(FileSystemFailureTermination.Unsafe);
    }

    [Fact]
    public async Task StreamOverloads_CopyAndJsonUseProtectedBlocks()
    {
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        string path = Path.Combine(_root, "overloads");
        await using PositionedFileStream stream = primitives.CreateContext().FileSystem.OpenPositionedFile(
            path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous,
            TestContext.Current.CancellationToken);
        stream.Write([1, 2, 3], 0, 3);
        stream.Write(new byte[] { 4, 5 }.AsSpan());
        stream.WriteByte(6);
        await stream.WriteAsync(new byte[] { 7, 8 }, 0, 2, TestContext.Current.CancellationToken);
        await stream.WriteAsync(new byte[] { 9, 10 }.AsMemory(), TestContext.Current.CancellationToken);
        stream.Position = 0;
        byte[] read = new byte[10];
        stream.Read(read, 0, 2).ShouldBe(2);
        stream.Read(read.AsSpan(2, 2)).ShouldBe(2);
        read[4] = (byte)stream.ReadByte();
        (await stream.ReadAsync(read, 5, 2, TestContext.Current.CancellationToken)).ShouldBe(2);
        (await stream.ReadAsync(read.AsMemory(7), TestContext.Current.CancellationToken)).ShouldBe(3);
        read.ShouldBe(Enumerable.Range(1, 10).Select(value => (byte)value).ToArray());

        stream.Position = 0;
        using MemoryStream copy = new MemoryStream();
        await stream.CopyToAsync(copy, 3, TestContext.Current.CancellationToken);
        copy.ToArray().ShouldBe(read);
        stream.Position = 0;
        using MemoryStream syncCopy = new MemoryStream();
        stream.CopyTo(syncCopy, 3);
        syncCopy.ToArray().ShouldBe(read);

        stream.SetLength(0);
        await JsonSerializer.SerializeAsync(stream, new[] { "one", "two" }, cancellationToken: TestContext.Current.CancellationToken);
        stream.Position = 0;
        string[]? values = await JsonSerializer.DeserializeAsync<string[]>(
            stream, cancellationToken: TestContext.Current.CancellationToken);
        values.ShouldBe(["one", "two"]);
        primitives.Waits.ShouldBeEmpty();
        primitives.AsyncWaits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Copy_PartialDestinationWriteDoesNotReadSourceAgain()
    {
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        string path = Path.Combine(_root, "copy");
        await using PositionedFileStream destination = primitives.CreateContext().FileSystem.OpenPositionedFile(
            path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous,
            TestContext.Current.CancellationToken);
        primitives.Enqueue<bool>("WriteAtAsync", call =>
        {
            RandomAccess.Write(call.Handle!, call.Content.Span[..1], call.Offset!.Value);
            return ScriptedFileSystemPrimitives.NativeFailure<bool>(1117, "WriteAtAsync", call.Path);
        });
        byte[] content = Encoding.UTF8.GetBytes("one source block");
        using CountingSource source = new CountingSource(content);

        await source.CopyToAsync(destination, 32, TestContext.Current.CancellationToken);

        source.Reads.ShouldBe(2); // One block and EOF, not a replay of the source.
        destination.Position.ShouldBe(content.Length);
        destination.Position = 0;
        byte[] output = new byte[content.Length];
        destination.Read(output, 0, output.Length).ShouldBe(content.Length);
        output.ShouldBe(content);
        primitives.Count("WriteAtAsync").ShouldBe(2);
        primitives.AsyncWaits.ShouldBe([10]);
    }

    [Fact]
    public void AppendAndSharedWriters_AreRejectedBeforeOpening()
    {
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        ResilientFileSystem fileSystem = primitives.CreateContext().FileSystem;
        string path = Path.Combine(_root, "unsafe");

        Should.Throw<ArgumentException>(() => fileSystem.OpenPositionedFile(
            path, FileMode.Append, FileAccess.Write, FileShare.None, FileOptions.None, TestContext.Current.CancellationToken));
        Should.Throw<ArgumentException>(() => fileSystem.OpenPositionedFile(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite, FileOptions.None, TestContext.Current.CancellationToken));

        primitives.Calls.ShouldBeEmpty();
    }

    private static ScriptedFileSystemPrimitives SystemFiles()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(SystemFileSystemPrimitives.Instance.Platform);
        primitives.UseSystemFiles();
        return primitives;
    }

    private static ScriptedFileSystemPrimitives ScriptedOwnedFile(FileSystemPlatform platform)
    {
        // All handle operations are injected: Unix cases do not execute native
        // Unix calls on another host.
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(platform);
        primitives.Set<SafeFileHandle>("OpenFileHandle", _ => FileSystemReplayOutcome<SafeFileHandle>.Complete(
            new SafeFileHandle(new IntPtr(123), ownsHandle: false)));
        primitives.Set("OpenOwnedDescriptor", FileSystemReplayOutcome<int>.Complete(123));
        primitives.Set("ConfigureFileSharing", FileSystemReplayOutcome<bool>.Complete(true));
        primitives.Set("AdviseFileAccess", FileSystemReplayOutcome<bool>.Complete(true));
        primitives.Set<bool>("CloseFile", call =>
        {
            call.Handle!.SetHandleAsInvalid();
            call.Handle.Dispose();
            return FileSystemReplayOutcome<bool>.Complete(true);
        });
        return primitives;
    }

    private static IOException CapturedWriteFailure()
    {
        try
        {
            throw new IOException("partial native write", new Win32Exception(1117));
        }
        catch (IOException exception)
        {
            return exception;
        }
    }

    private sealed class CountingSource : Stream
    {
        private readonly MemoryStream _inner;

        internal CountingSource(byte[] content) => _inner = new MemoryStream(content);
        internal int Reads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return _inner.Read(buffer, offset, count);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            return _inner.ReadAsync(buffer, cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
