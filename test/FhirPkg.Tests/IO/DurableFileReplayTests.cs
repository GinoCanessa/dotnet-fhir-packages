// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.IO;
using FhirPkg.Tests.Support;
using FhirPkg.Utilities;
using Microsoft.Win32.SafeHandles;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.IO;

public sealed class DurableFileReplayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"durable-replay-{Guid.NewGuid():N}");

    public DurableFileReplayTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Promotion_SyncFailureRetriesOnlyPendingBarrier()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.EnableDirectorySynchronization();
        bool sourceExists = true;
        bool destinationCommitted = false;
        primitives.Set<bool>("MovePath", _ =>
        {
            sourceExists.ShouldBeTrue();
            sourceExists = false;
            destinationCommitted = true;
            return FileSystemReplayOutcome<bool>.Complete(true);
        });
        primitives.Enqueue<FileSystemDirectorySyncOutcome>("SynchronizeDescriptor", _ =>
        {
            sourceExists.ShouldBeFalse();
            destinationCommitted.ShouldBeTrue();
            return ScriptedFileSystemPrimitives.NativeFailure<FileSystemDirectorySyncOutcome>(5, "fsync");
        });
        DurableFileReplacement replacement = new DurableFileReplacement(
            Path.Combine(_root, "temporary"), Path.Combine(_root, "destination"));

        await primitives.CreateContext().DurableFiles.AtomicReplaceFileAsync(replacement, TestContext.Current.CancellationToken);

        replacement.Promoted.ShouldBeTrue();
        replacement.CanCleanSource.ShouldBeFalse();
        primitives.Count("MovePath").ShouldBe(1);
        primitives.Count("SynchronizeDescriptor").ShouldBe(2);
        primitives.Count("OpenDescriptor").ShouldBe(1);
        primitives.Count("CloseDescriptor").ShouldBe(1);
        primitives.Count("Probe").ShouldBe(0);
        primitives.AsyncWaits.ShouldBe([10]);
        primitives.Waits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Promotion_AndAllParentBarriersShareRemainingBudget()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.EnableDirectorySynchronization();
        primitives.Enqueue("MovePath", ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "rename"));
        primitives.Set("MovePath", FileSystemReplayOutcome<bool>.Complete(true));
        primitives.Enqueue("SynchronizeDescriptor",
            FileSystemReplayOutcome<FileSystemDirectorySyncOutcome>.Complete(FileSystemDirectorySyncOutcome.Standard));
        IOException barrierFailure = new IOException("destination barrier");
        primitives.Set<FileSystemDirectorySyncOutcome>("SynchronizeDescriptor", call =>
            ScriptedFileSystemPrimitives.Fail<FileSystemDirectorySyncOutcome>(barrierFailure, "fsync", call.Path));
        DurableFileReplacement replacement = new DurableFileReplacement(
            Path.Combine(_root, "source-parent", "temporary"), Path.Combine(_root, "destination-parent", "file"));

        IOException final = await Should.ThrowAsync<IOException>(() =>
            primitives.CreateContext(retries: 2).DurableFiles.AtomicReplaceFileAsync(
                replacement, TestContext.Current.CancellationToken).AsTask());

        final.ShouldBeSameAs(barrierFailure);
        replacement.Promoted.ShouldBeTrue();
        primitives.Count("MovePath").ShouldBe(2);
        primitives.Count("SynchronizeDescriptor").ShouldBe(3); // One completed parent, two failed attempts.
        primitives.Count("OpenDescriptor").ShouldBe(2);
        primitives.Count("CloseDescriptor").ShouldBe(2);
        primitives.Calls.Where(call => call.Operation == "SynchronizeDescriptor")
            .Select(call => call.Path).ShouldBe(
            [
                Path.Combine(_root, "source-parent"),
                Path.Combine(_root, "destination-parent"),
                Path.Combine(_root, "destination-parent"),
            ]);
        primitives.AsyncWaits.ShouldBe([10, 10]);
        FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? evidence).ShouldBeTrue();
        evidence!.RetriesUsed.ShouldBe(2);
    }

    [Fact]
    public async Task Writer_UsesGenericContextPortAndLiveHandleDurabilityOrder()
    {
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        string path = Path.Combine(_root, "result");
        File.WriteAllText(path, "old");
        int observerCalls = 0;

        await DurableFileWriter.WriteAsync(
            path, "new bytes"u8.ToArray(), primitives.CreateContext().DurableFiles,
            TestContext.Current.CancellationToken,
            _ =>
            {
                observerCalls++;
                primitives.Count("FlushFile").ShouldBe(1);
                primitives.Count("CloseFile").ShouldBe(1);
                primitives.Count("MovePath").ShouldBe(0);
                File.ReadAllText(path).ShouldBe("old");
                return ValueTask.CompletedTask;
            });

        File.ReadAllText(path).ShouldBe("new bytes");
        observerCalls.ShouldBe(1);
        string[] operations = primitives.Calls.Select(call => call.Operation).ToArray();
        Array.IndexOf(operations, "WriteAtAsync").ShouldBeLessThan(Array.IndexOf(operations, "FlushFile"));
        Array.IndexOf(operations, "FlushFile").ShouldBeLessThan(Array.IndexOf(operations, "CloseFile"));
        Array.IndexOf(operations, "CloseFile").ShouldBeLessThan(Array.IndexOf(operations, "MovePath"));
        primitives.Count("OpenFile").ShouldBe(0);
        primitives.Count("MovePath").ShouldBe(1);
        primitives.Count("DeleteEntry").ShouldBe(0);
        primitives.Count("GetFullPath").ShouldBe(0);
        primitives.Waits.ShouldBeEmpty();
        primitives.AsyncWaits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Writer_PrecommitObserverIsNotRetriedAndCleanupCannotReplaceIt()
    {
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        primitives.Enqueue<bool>("WriteAtAsync", call =>
        {
            RandomAccess.Write(call.Handle!, call.Content.Span[..1], call.Offset!.Value);
            return ScriptedFileSystemPrimitives.NativeFailure<bool>(1117, "write", call.Path);
        });
        primitives.Set<bool>("DeleteEntry", call =>
            ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "cleanup", call.Path));
        IOException observerFailure = new IOException("domain observer, not a physical call");
        int observerCalls = 0;

        IOException final = await Should.ThrowAsync<IOException>(() => DurableFileWriter.WriteAsync(
            Path.Combine(_root, "not-committed"), "retained"u8.ToArray(), primitives.CreateContext(retries: 2).DurableFiles,
            TestContext.Current.CancellationToken, _ =>
            {
                observerCalls++;
                throw observerFailure;
            }));

        final.ShouldBeSameAs(observerFailure);
        FileSystemFailureContext.TryGetOrigin(observerFailure, out _).ShouldBeFalse();
        observerCalls.ShouldBe(1);
        primitives.Count("WriteAtAsync").ShouldBe(2);
        primitives.Count("FlushFile").ShouldBe(1);
        primitives.Count("CloseFile").ShouldBe(1);
        primitives.Count("MovePath").ShouldBe(0);
        primitives.Count("DeleteEntry").ShouldBe(3);
        primitives.AsyncWaits.ShouldBe([10, 10, 10]); // One write retry, two independent cleanup retries.
        primitives.Waits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Writer_CreateNewCollisionNeverAdoptsOrDeletesForeignFile()
    {
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        string? foreignPath = null;
        if (OperatingSystem.IsWindows())
        {
            primitives.Enqueue<SafeFileHandle>("OpenFileHandle", call =>
            {
                foreignPath = call.Path!;
                File.WriteAllText(foreignPath, "foreign");
                return SystemFileSystemPrimitives.Instance.OpenFileHandle(
                    foreignPath, call.Mode!.Value, call.Access!.Value, call.Share!.Value, call.Options!.Value);
            });
        }
        else
        {
            primitives.Enqueue<int>("OpenOwnedDescriptor", call =>
            {
                foreignPath = call.Path!;
                File.WriteAllText(foreignPath, "foreign");
                return SystemFileSystemPrimitives.Instance.OpenOwnedDescriptor(
                    foreignPath, call.Mode!.Value, call.Access!.Value, call.Share!.Value, call.Options!.Value);
            });
        }
        string destination = Path.Combine(_root, "collision-result");
        File.WriteAllText(destination, "old");

        await Should.ThrowAsync<IOException>(() => DurableFileWriter.WriteAsync(
            destination, "new"u8.ToArray(), primitives.CreateContext().DurableFiles, TestContext.Current.CancellationToken));

        File.ReadAllText(destination).ShouldBe("old");
        File.ReadAllText(foreignPath!).ShouldBe("foreign");
        OwnedOpenCount(primitives).ShouldBe(1);
        primitives.Count("WriteAtAsync").ShouldBe(0);
        primitives.Count("CloseFile").ShouldBe(0);
        primitives.Count("DeleteEntry").ShouldBe(0);
        primitives.Count("MovePath").ShouldBe(0);
        primitives.AsyncWaits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Writer_AmbiguousPromotionDoesNotDeleteEitherPath()
    {
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        string destination = Path.Combine(_root, "ambiguous-result");
        primitives.Set<bool>("MovePath", call =>
        {
            File.Move(call.Path!, call.DestinationPath!);
            File.WriteAllText(call.Path!, "foreign replacement at old name");
            return ScriptedFileSystemPrimitives.NativeFailure<bool>(
                123456, "rename", call.Path, FileSystemReplaySafety.Unsafe);
        });

        await Should.ThrowAsync<IOException>(() => DurableFileWriter.WriteAsync(
            destination, "committed"u8.ToArray(), primitives.CreateContext().DurableFiles, TestContext.Current.CancellationToken));

        File.ReadAllText(destination).ShouldBe("committed");
        string source = primitives.Calls.Single(call => call.Operation == "MovePath").Path!;
        File.ReadAllText(source).ShouldBe("foreign replacement at old name");
        primitives.Count("MovePath").ShouldBe(1);
        primitives.Count("DeleteEntry").ShouldBe(0);
        primitives.AsyncWaits.ShouldBeEmpty();
    }

    [Fact]
    public async Task WriteFailure_ClosesOnceAndCleansOnlyItsOwnedCreation()
    {
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        IOException writeFailure = new IOException("failed local block");
        primitives.Set<bool>("WriteAtAsync", call =>
        {
            RandomAccess.Write(call.Handle!, call.Content.Span[..1], call.Offset!.Value);
            return ScriptedFileSystemPrimitives.Fail<bool>(writeFailure, "write", call.Path);
        });
        primitives.Enqueue<bool>("CloseFile", call =>
        {
            call.Handle!.Dispose();
            return ScriptedFileSystemPrimitives.NativeFailure<bool>(
                1117, "close", call.Path, FileSystemReplaySafety.Unsafe);
        });
        string path = Path.Combine(_root, "failed-write");
        string unrelated = Path.Combine(_root, "unrelated");
        File.WriteAllText(unrelated, "foreign");

        IOException final = await Should.ThrowAsync<IOException>(() =>
            primitives.CreateContext(retries: 1).DurableFiles.WriteFileAndFlushAsync(
                path, "bytes"u8.ToArray(), TestContext.Current.CancellationToken).AsTask());

        final.ShouldBeSameAs(writeFailure);
        File.Exists(path).ShouldBeFalse();
        File.ReadAllText(unrelated).ShouldBe("foreign");
        primitives.Count("WriteAtAsync").ShouldBe(2);
        primitives.Count("CloseFile").ShouldBe(1);
        primitives.Count("FlushFile").ShouldBe(0);
        primitives.Calls.Where(call => call.Operation == "DeleteEntry").Select(call => call.Path).ShouldBe([path]);
        primitives.AsyncWaits.ShouldBe([10]);
    }

    [Fact]
    public async Task OwnedCreate_DefiniteRejectionsUseBudgetWithoutManagedFileStreamConstructor()
    {
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        string path = Path.Combine(_root, "missing-parent", "file");

        IOException final = await Should.ThrowAsync<IOException>(() =>
            primitives.CreateContext(retries: 2).FileSystem.OpenPositionedFileAsync(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.Asynchronous,
                TestContext.Current.CancellationToken).AsTask());

        OwnedOpenCount(primitives).ShouldBe(3);
        primitives.Count("OpenFile").ShouldBe(0);
        primitives.Count("DeleteEntry").ShouldBe(0);
        primitives.Count("Probe").ShouldBe(0);
        primitives.AsyncWaits.ShouldBe([10, 10]);
        FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? evidence).ShouldBeTrue();
        evidence!.Termination.ShouldBe(FileSystemFailureTermination.Exhausted);
        evidence.NativeErrorCode.ShouldNotBeNull();
        final.InnerException.ShouldBeOfType<System.ComponentModel.Win32Exception>();
    }

    [Fact]
    public async Task OwnedCreate_UnknownOutcomeIsNotReopenedOrReconciledByExistence()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(FileSystemPlatform.Windows);
        primitives.Set("OpenFileHandle", ScriptedFileSystemPrimitives.NativeFailure<SafeFileHandle>(
            123456, "CreateFileW", safety: FileSystemReplaySafety.Unsafe));

        await Should.ThrowAsync<IOException>(() => primitives.CreateContext().FileSystem.OpenPositionedFileAsync(
            Path.Combine(_root, "uncertain"), FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.None,
            TestContext.Current.CancellationToken).AsTask());

        primitives.Count("OpenFileHandle").ShouldBe(1);
        primitives.Count("Probe").ShouldBe(0);
        primitives.Count("DeleteEntry").ShouldBe(0);
        primitives.AsyncWaits.ShouldBeEmpty();
    }

    [Fact]
    public async Task OwnedCreate_MetadataFailureRetainsHandleAndOwnership()
    {
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        primitives.Enqueue("GetHandleLength", ScriptedFileSystemPrimitives.NativeFailure<long>(5, "length"));
        primitives.Enqueue("GetHandleLength", ScriptedFileSystemPrimitives.NativeFailure<long>(1117, "length"));
        string path = Path.Combine(_root, "retained-create");

        await using (PositionedFileStream stream = await primitives.CreateContext(retries: 2).FileSystem.OpenPositionedFileAsync(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.Asynchronous,
            TestContext.Current.CancellationToken))
        {
            await stream.WriteAsync("complete"u8.ToArray(), TestContext.Current.CancellationToken);
            await stream.FlushAsync(TestContext.Current.CancellationToken);
        }

        File.ReadAllText(path).ShouldBe("complete");
        OwnedOpenCount(primitives).ShouldBe(1);
        primitives.Count("GetHandleLength").ShouldBe(3);
        primitives.Calls.Where(call => call.Operation == "GetHandleLength").Select(call => call.Handle)
            .Distinct().Count().ShouldBe(1);
        primitives.Count("CloseFile").ShouldBe(1);
        primitives.Count("DeleteEntry").ShouldBe(0);
        primitives.AsyncWaits.ShouldBe([10, 10]);
    }

    [Fact]
    public async Task OwnedCreate_FailedMetadataReleasesAndCleansOwnedPath()
    {
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        primitives.Set("GetHandleLength", ScriptedFileSystemPrimitives.NativeFailure<long>(5, "length"));
        string path = Path.Combine(_root, "failed-create");

        await Should.ThrowAsync<IOException>(() => primitives.CreateContext(retries: 1).FileSystem.OpenPositionedFileAsync(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.Asynchronous,
            TestContext.Current.CancellationToken).AsTask());

        File.Exists(path).ShouldBeFalse();
        OwnedOpenCount(primitives).ShouldBe(1);
        primitives.Count("GetHandleLength").ShouldBe(2);
        primitives.Count("CloseFile").ShouldBe(1);
        primitives.Count("DeleteEntry").ShouldBe(1);
        primitives.AsyncWaits.ShouldBe([10]);
    }

    [Fact]
    public void OwnedCreate_ZeroDescriptorGuardNeverRepeatsAmbiguousClose()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.Set("OpenOwnedDescriptor", FileSystemReplayOutcome<int>.Complete(0));
        primitives.Enqueue("DuplicateDescriptor", ScriptedFileSystemPrimitives.NativeFailure<int>(5, "fcntl"));
        primitives.Set("DuplicateDescriptor", FileSystemReplayOutcome<int>.Complete(3));
        primitives.Enqueue("CloseDescriptor", ScriptedFileSystemPrimitives.NativeFailure<bool>(
            4, "close", safety: FileSystemReplaySafety.Unsafe));
        primitives.Set("CloseDescriptor", FileSystemReplayOutcome<bool>.Complete(true));
        primitives.Set("Probe", ScriptedFileSystemPrimitives.Missing());

        Should.Throw<IOException>(() => primitives.CreateContext().FileSystem.OpenPositionedFile(
            Path.Combine(_root, "zero-descriptor"), FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.None,
            TestContext.Current.CancellationToken));

        primitives.Count("OpenOwnedDescriptor").ShouldBe(1);
        primitives.Count("DuplicateDescriptor").ShouldBe(2);
        primitives.Calls.Where(call => call.Operation == "CloseDescriptor").Select(call => call.Descriptor).ShouldBe([0, 3]);
        primitives.Count("CloseFile").ShouldBe(0);
        primitives.Waits.ShouldBe([10]);
    }

    [Fact]
    public async Task Writer_PrecommitCancellationCleansOwnedTempWithoutPromotion()
    {
        ScriptedFileSystemPrimitives primitives = SystemFiles();
        using CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        string destination = Path.Combine(_root, "canceled");
        int observerCalls = 0;

        await Should.ThrowAsync<OperationCanceledException>(() => DurableFileWriter.WriteAsync(
            destination, "bytes"u8.ToArray(), primitives.CreateContext().DurableFiles, source.Token,
            _ =>
            {
                observerCalls++;
                source.Cancel();
                return ValueTask.CompletedTask;
            }));

        observerCalls.ShouldBe(1);
        File.Exists(destination).ShouldBeFalse();
        primitives.Count("MovePath").ShouldBe(0);
        primitives.Count("CloseFile").ShouldBe(1);
        primitives.Count("DeleteEntry").ShouldBe(1);
        Directory.GetFiles(_root).ShouldBeEmpty();
    }

    private static int OwnedOpenCount(ScriptedFileSystemPrimitives primitives) =>
        primitives.Count("OpenFileHandle") + primitives.Count("OpenOwnedDescriptor");

    private static ScriptedFileSystemPrimitives SystemFiles()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(SystemFileSystemPrimitives.Instance.Platform);
        primitives.UseSystemFiles();
        return primitives;
    }
}
