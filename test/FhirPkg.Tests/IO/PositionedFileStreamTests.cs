// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
    [InlineData(FileMode.CreateNew, false)]
    [InlineData(FileMode.CreateNew, true)]
    [InlineData(FileMode.Create, false)]
    [InlineData(FileMode.Create, true)]
    [InlineData(FileMode.OpenOrCreate, false)]
    [InlineData(FileMode.OpenOrCreate, true)]
    public async Task OwnedCreation_MatchesManagedPermissionsAndCanReopen(FileMode mode, bool asynchronous)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Skip("Physical owned-creation permission checks require Linux or macOS.");

        OwnedCreationDiagnostics diagnostics = new OwnedCreationDiagnostics($"fresh {mode}; async={asynchronous}");
        ScriptedFileSystemPrimitives primitives = diagnostics.SystemFiles();
        ResilientFileSystem fileSystem = primitives.CreateContext().FileSystem;
        string control = Path.Combine(_root, "managed-control");
        string path = Path.Combine(_root, "native-created");
        byte[] payload = "owned creation payload"u8.ToArray();
        FileOptions options = asynchronous ? FileOptions.Asynchronous : FileOptions.None;
        await using (FileStream stream = new FileStream(control, mode, FileAccess.ReadWrite, FileShare.None, 4096, options))
            await WriteCharacterizationPayloadAsync(stream, payload, asynchronous);
        UnixFileMode expectedMode = ReadUnixMode(control);
        diagnostics.WriteLine($"Managed control '{control}': mode={FormatUnixMode(expectedMode)}; requested native mode=0666 (subject to umask).");

        await diagnostics.ObserveAsync("owned create/write/close", async () =>
        {
            await using PositionedFileStream stream = asynchronous
                ? await fileSystem.OpenPositionedFileAsync(
                    path, mode, FileAccess.ReadWrite, FileShare.None, options, TestContext.Current.CancellationToken)
                : fileSystem.OpenPositionedFile(
                    path, mode, FileAccess.ReadWrite, FileShare.None, options, TestContext.Current.CancellationToken);
            stream.Length.ShouldBe(0);
            await WriteCharacterizationPayloadAsync(stream, payload, asynchronous);
        });

        // Keep collecting reopen evidence even when the permission comparison fails.
        diagnostics.CheckMode(path, expectedMode);
        await CharacterizeReopensAsync(diagnostics, fileSystem, control, payload, asynchronous);
        await CharacterizeReopensAsync(diagnostics, fileSystem, path, payload, asynchronous);
        diagnostics.AssertComplete();
    }

    [Theory]
    [InlineData(FileMode.Create, false)]
    [InlineData(FileMode.Create, true)]
    [InlineData(FileMode.OpenOrCreate, false)]
    [InlineData(FileMode.OpenOrCreate, true)]
    public async Task OwnedCreation_ExistingFilePreservesModeAndModeSemantics(FileMode mode, bool asynchronous)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Skip("Physical owned-creation permission checks require Linux or macOS.");

        OwnedCreationDiagnostics diagnostics = new OwnedCreationDiagnostics($"existing {mode}; async={asynchronous}");
        ScriptedFileSystemPrimitives primitives = diagnostics.SystemFiles();
        ResilientFileSystem fileSystem = primitives.CreateContext().FileSystem;
        string path = Path.Combine(_root, "existing");
        byte[] original = "original existing file contents"u8.ToArray();
        byte[] payload = "new payload"u8.ToArray();
        File.WriteAllBytes(path, original);
        UnixFileMode expectedMode = ReadUnixMode(path);
        byte[] expected = mode == FileMode.Create ? payload : original.Concat(payload).ToArray();
        FileOptions options = asynchronous ? FileOptions.Asynchronous : FileOptions.None;

        await diagnostics.ObserveAsync("existing owned open/write/close", async () =>
        {
            await using PositionedFileStream stream = asynchronous
                ? await fileSystem.OpenPositionedFileAsync(
                    path, mode, FileAccess.ReadWrite, FileShare.None, options, TestContext.Current.CancellationToken)
                : fileSystem.OpenPositionedFile(
                    path, mode, FileAccess.ReadWrite, FileShare.None, options, TestContext.Current.CancellationToken);
            stream.Length.ShouldBe(mode == FileMode.Create ? 0 : original.Length);
            stream.Position.ShouldBe(0);
            if (mode == FileMode.OpenOrCreate)
                stream.Seek(0, SeekOrigin.End).ShouldBe(original.Length);
            await WriteCharacterizationPayloadAsync(stream, payload, asynchronous);
        });
        diagnostics.Observe("retained-handle truncation semantics", () =>
        {
            primitives.Count("OpenOwnedDescriptor").ShouldBe(1);
            primitives.Count("OpenFile").ShouldBe(0);
            SafeFileHandle retained = primitives.Calls.Single(call => call.Operation == "ConfigureFileSharing").Handle!;
            FileSystemPrimitiveCall[] truncations = primitives.Calls.Where(call => call.Operation == "SetLength").ToArray();
            truncations.Length.ShouldBe(mode == FileMode.Create ? 1 : 0);
            foreach (FileSystemPrimitiveCall truncation in truncations)
            {
                truncation.Length.ShouldBe(0);
                truncation.Handle.ShouldBeSameAs(retained);
            }
            primitives.Calls.Where(call => call.Operation is "GetHandleLength" or "WriteAt" or "WriteAtAsync")
                .ShouldAllBe(call => ReferenceEquals(call.Handle, retained));
        });

        diagnostics.CheckMode(path, expectedMode);
        await CharacterizeReopensAsync(diagnostics, fileSystem, path, expected, asynchronous);
        diagnostics.CheckMode(path, expectedMode);
        diagnostics.AssertComplete();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedCreation_CreateNewCollisionPreservesForeignFile(bool asynchronous)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Skip("Physical owned-creation permission checks require Linux or macOS.");

        OwnedCreationDiagnostics diagnostics = new OwnedCreationDiagnostics($"CreateNew collision; async={asynchronous}");
        ScriptedFileSystemPrimitives primitives = diagnostics.SystemFiles();
        ResilientFileSystem fileSystem = primitives.CreateContext().FileSystem;
        string path = Path.Combine(_root, "foreign");
        byte[] original = "foreign file must survive"u8.ToArray();
        File.WriteAllBytes(path, original);
        UnixFileMode expectedMode = ReadUnixMode(path);
        FileOptions options = asynchronous ? FileOptions.Asynchronous : FileOptions.None;

        await diagnostics.ObserveAsync("CreateNew collision rejects without adoption or deletion", async () =>
        {
            IOException exception = await Should.ThrowAsync<IOException>(async () =>
            {
                await using PositionedFileStream stream = asynchronous
                    ? await fileSystem.OpenPositionedFileAsync(
                        path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, options,
                        TestContext.Current.CancellationToken)
                    : fileSystem.OpenPositionedFile(
                        path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, options,
                        TestContext.Current.CancellationToken);
            });
            diagnostics.WriteException("expected collision", exception);
            exception.InnerException.ShouldBeOfType<Win32Exception>().NativeErrorCode.ShouldBe(17);
            primitives.Count("OpenOwnedDescriptor").ShouldBe(1);
            foreach (string operation in new[] { "ConfigureFileSharing", "AdviseFileAccess", "SetLength", "DeleteEntry", "CloseFile", "CloseDescriptor" })
                primitives.Count(operation).ShouldBe(0);
            primitives.Waits.ShouldBeEmpty();
            primitives.AsyncWaits.ShouldBeEmpty();
            File.Exists(path).ShouldBeTrue();
        });

        diagnostics.CheckMode(path, expectedMode);
        await CharacterizeReopensAsync(diagnostics, fileSystem, path, original, asynchronous);
        diagnostics.CheckMode(path, expectedMode);
        diagnostics.AssertComplete();
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LengthMetadata_RetainsHandleAndPositionWithMatchingWaits(bool asynchronous)
    {
        string path = Path.Combine(_root, "metadata");
        File.WriteAllBytes(path, "metadata"u8.ToArray());
        ScriptedFileSystemPrimitives files = SystemFiles();
        PositionedFileStream stream = await files.CreateContext(retries: 2).FileSystem.OpenPositionedFileAsync(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous,
            TestContext.Current.CancellationToken);
        stream.Position = 3;
        files.Calls.Clear();
        files.Enqueue("GetHandleLength", ScriptedFileSystemPrimitives.NativeFailure<long>(5, "retained metadata", path));
        files.Enqueue("GetHandleLength", ScriptedFileSystemPrimitives.NativeFailure<long>(1117, "retained metadata", path));
        try
        {
            long length = asynchronous
                ? await stream.GetLengthAsync(TestContext.Current.CancellationToken)
                : stream.Length;

            length.ShouldBe(8);
            stream.Position.ShouldBe(3);
            FileSystemPrimitiveCall[] metadata = files.Calls.Where(call => call.Operation == "GetHandleLength").ToArray();
            metadata.Length.ShouldBe(3);
            metadata.Select(call => call.Handle).Distinct().Count().ShouldBe(1);
            files.Count("OpenFileHandle").ShouldBe(0);
            (asynchronous ? files.AsyncWaits : files.Waits).ShouldBe([10, 10]);
            (asynchronous ? files.Waits : files.AsyncWaits).ShouldBeEmpty();
        }
        finally
        {
            await stream.DisposeAsync();
        }
        await stream.DisposeAsync();
        files.Count("CloseFile").ShouldBe(1);
    }

    private static async Task WriteCharacterizationPayloadAsync(Stream stream, byte[] payload, bool asynchronous)
    {
        if (asynchronous)
        {
            await stream.WriteAsync(payload.AsMemory(), TestContext.Current.CancellationToken);
            await stream.FlushAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            stream.Write(payload);
            stream.Flush();
        }
    }

    private static async Task CharacterizeReopensAsync(
        OwnedCreationDiagnostics diagnostics, ResilientFileSystem fileSystem, string path, byte[] expected, bool asynchronous)
    {
        await diagnostics.ObserveAsync($"managed read after close '{path}'", async () =>
        {
            byte[] actual = asynchronous
                ? await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)
                : File.ReadAllBytes(path);
            actual.ShouldBe(expected);
        });
        await diagnostics.ObserveAsync($"managed read/write reopen '{path}'", async () =>
        {
            FileOptions options = asynchronous ? FileOptions.Asynchronous : FileOptions.None;
            await using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, options);
            using MemoryStream copy = new MemoryStream();
            if (asynchronous)
                await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
            else
                stream.CopyTo(copy);
            copy.ToArray().ShouldBe(expected);
            stream.Position = 0;
            await WriteCharacterizationPayloadAsync(stream, expected, asynchronous);
        });
        await diagnostics.ObserveAsync($"protected read reopen '{path}'", async () =>
        {
            await using Stream stream = asynchronous
                ? await fileSystem.OpenProtectedRegularReadAsync(path, TestContext.Current.CancellationToken)
                : fileSystem.OpenProtectedRegularRead(path, TestContext.Current.CancellationToken);
            using MemoryStream copy = new MemoryStream();
            if (asynchronous)
                await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
            else
                stream.CopyTo(copy);
            copy.ToArray().ShouldBe(expected);
        });
    }

    private static UnixFileMode ReadUnixMode(string path)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unix permission characterization requires Linux or macOS.");
        return File.GetUnixFileMode(path);
    }

    private static string FormatUnixMode(UnixFileMode mode) =>
        $"0{Convert.ToString((int)mode, 8).PadLeft(3, '0')} (0x{(int)mode:X})";

    private sealed class OwnedCreationDiagnostics
    {
        private readonly StringBuilder _text = new StringBuilder();
        private readonly List<string> _failures = [];

        internal OwnedCreationDiagnostics(string scenario)
        {
            WriteLine($"Owned creation characterization: {scenario}");
            WriteLine($"OS: {RuntimeInformation.OSDescription}; ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}; " +
                $"OSArchitecture: {RuntimeInformation.OSArchitecture}; Runtime: {RuntimeInformation.FrameworkDescription}; " +
                $"Environment.Version: {Environment.Version}; AppContext TFM: {AppContext.TargetFrameworkName}; " +
                $"Test assembly TFM: {typeof(PositionedFileStreamTests).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName}");
        }

        internal void WriteLine(string message) => _text.AppendLine(message);

        internal void WriteException(string phase, Exception exception)
        {
            WriteLine($"{phase}: {exception}");
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                WriteLine($"Exception: {current.GetType().FullName}; HResult=0x{current.HResult:X8}; " +
                    $"native error={(current is Win32Exception native ? native.NativeErrorCode.ToString() : "not supplied by this exception")}");
            }
            WriteLine(FileSystemFailureContext.TryGetOrigin(exception, out FileSystemFailureContext? evidence)
                ? evidence.Format()
                : "No primitive failure context (managed exception or semantic rejection).");
        }

        internal void Observe(string phase, Action action)
        {
            try
            {
                action();
                WriteLine($"{phase}: PASS");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _failures.Add(phase);
                WriteException($"{phase}: FAIL", exception);
            }
        }

        internal async Task ObserveAsync(string phase, Func<Task> action)
        {
            try
            {
                await action();
                WriteLine($"{phase}: PASS");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _failures.Add(phase);
                WriteException($"{phase}: FAIL", exception);
            }
        }

        internal void CheckMode(string path, UnixFileMode expected)
        {
            WriteLine($"Unix mode '{path}': expected={FormatUnixMode(expected)}");
            Observe("Unix mode comparison", () =>
            {
                UnixFileMode actual = ReadUnixMode(path);
                WriteLine($"Unix mode '{path}': expected={FormatUnixMode(expected)}; actual={FormatUnixMode(actual)}");
                actual.ShouldBe(expected);
            });
        }

        internal ScriptedFileSystemPrimitives SystemFiles()
        {
            ScriptedFileSystemPrimitives primitives = PositionedFileStreamTests.SystemFiles();
            SystemFileSystemPrimitives system = SystemFileSystemPrimitives.Instance;
            // Observe the live primitives without replacing their calls, results, or ownership.
            primitives.Set<int>("OpenOwnedDescriptor", call => RecordPrimitive(
                $"OpenOwnedDescriptor '{call.Path}' mode={call.Mode} access={call.Access} share={call.Share} options={call.Options}",
                system.OpenOwnedDescriptor(call.Path!, call.Mode!.Value, call.Access!.Value, call.Share!.Value, call.Options!.Value)));
            primitives.Set<int>("OpenDescriptor", call => RecordPrimitive(
                $"OpenDescriptor '{call.Path}' flags=0x{call.Flags:X}",
                system.OpenDescriptor(call.Path!, (int)call.Flags!.Value, 0)));
            primitives.Set<int>("DuplicateDescriptor", call => RecordPrimitive(
                $"DuplicateDescriptor '{call.Path}'",
                system.DuplicateDescriptor(call.Descriptor!.Value, (int)call.Flags!.Value,
                    NativeFileSystemPrimitives.MinimumTransferDescriptor, call.Path)));
            primitives.Set<bool>("ConfigureFileSharing", call => RecordPrimitive(
                $"ConfigureFileSharing '{call.Path}'",
                system.ConfigureFileSharing(call.Handle!, call.Share!.Value, call.Path!)));
            primitives.Set<bool>("AdviseFileAccess", call => RecordPrimitive(
                $"AdviseFileAccess '{call.Path}' options={call.Options}",
                system.AdviseFileAccess(call.Handle!, call.Options!.Value, call.Path!)));
            primitives.Set<bool>("SetLength", call => RecordPrimitive(
                $"SetLength '{call.Path}' length={call.Length}",
                system.SetLength(call.Handle!, call.Length!.Value, call.Path!)));
            return primitives;
        }

        private FileSystemReplayOutcome<T> RecordPrimitive<T>(string operation, FileSystemReplayOutcome<T> outcome)
        {
            WriteLine($"Primitive {operation}: {outcome.Kind}");
            if (outcome.Kind == FileSystemReplayOutcomeKind.Completed)
                WriteLine($"Primitive value: {outcome.Value}; no reported failure.");
            if (outcome.PrimitiveFailure is FileSystemPrimitiveFailure failure)
            {
                WriteLine($"Primitive replay safety: {failure.ReplaySafety}");
                WriteException(operation, failure.Exception);
            }
            if (outcome.SemanticException is not null)
                WriteException(operation, outcome.SemanticException.SourceException);
            return outcome;
        }

        internal void AssertComplete()
        {
            string diagnostics = _text.ToString();
            TestContext.Current.TestOutputHelper?.WriteLine(diagnostics);
            _failures.ShouldBeEmpty(diagnostics);
        }
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
