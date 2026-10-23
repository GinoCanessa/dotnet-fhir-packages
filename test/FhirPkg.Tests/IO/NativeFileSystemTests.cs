// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using FhirPkg.Cache;
using FhirPkg.IO;
using FhirPkg.Tests.Support;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.IO;

public sealed class NativeFileSystemTests
{
    private static string TestPath(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, .. parts]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rename_SyncFailureDoesNotRenameAgain(bool replace)
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.EnableDirectorySynchronization();
        primitives.Set("Probe", ScriptedFileSystemPrimitives.Missing());
        primitives.Set("MovePath", FileSystemReplayOutcome<bool>.Complete(true));
        primitives.Enqueue<FileSystemDirectorySyncOutcome>("SynchronizeDescriptor", _ =>
            ScriptedFileSystemPrimitives.NativeFailure<FileSystemDirectorySyncOutcome>(5, "fsync"));
        ResilientFileSystem fileSystem = primitives.CreateContext().FileSystem;
        string source = TestPath("source-parent", "source");
        string destination = TestPath("destination-parent", "destination");

        if (replace)
            await fileSystem.ReplaceFileAsync(source, destination, TestContext.Current.CancellationToken);
        else
            await fileSystem.MovePathAsync(source, destination, TestContext.Current.CancellationToken);

        primitives.Count("MovePath").ShouldBe(1);
        primitives.Calls.Where(call => call.Operation == "OpenDescriptor").Select(call => call.Path)
            .ShouldBe([Path.GetDirectoryName(source), Path.GetDirectoryName(destination)]);
        primitives.Calls.Where(call => call.Operation == "SynchronizeDescriptor").Select(call => call.Descriptor)
            .ShouldBe(new int?[] { 40, 40, 41 });
        primitives.Count("CloseDescriptor").ShouldBe(2);
        primitives.AsyncWaits.ShouldBe([10]);
        primitives.Waits.ShouldBeEmpty();
    }

    [Fact]
    public void Rename_MutationAndBarrierShareRemainingBudget()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.EnableDirectorySynchronization();
        primitives.Set("Probe", ScriptedFileSystemPrimitives.Missing());
        primitives.Enqueue<bool>("MovePath", call =>
            ScriptedFileSystemPrimitives.NativeFailure<bool>(13, "rename", call.Path));
        primitives.Set("MovePath", FileSystemReplayOutcome<bool>.Complete(true));
        primitives.Set<FileSystemDirectorySyncOutcome>("SynchronizeDescriptor", call =>
            ScriptedFileSystemPrimitives.NativeFailure<FileSystemDirectorySyncOutcome>(5, "fsync", call.Path));

        IOException final = Should.Throw<IOException>(() =>
            primitives.CreateContext(retries: 1).FileSystem.MovePath(TestPath("source"), TestPath("destination")));

        primitives.Count("MovePath").ShouldBe(2);
        primitives.Count("SynchronizeDescriptor").ShouldBe(1);
        primitives.Count("CloseDescriptor").ShouldBe(1);
        primitives.Waits.ShouldBe([10]);
        FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? failure).ShouldBeTrue();
        failure!.Operation.ShouldBe("fsync");
        failure.RetriesUsed.ShouldBe(1);
    }

    [Fact]
    public void CreateDirectory_RetryRetainsPendingBarriers()
    {
        string parent = TestPath("existing");
        string child = Path.Combine(parent, "child");
        string leaf = Path.Combine(child, "leaf");
        HashSet<string> existing = [parent];
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.EnableDirectorySynchronization();
        primitives.Set<FileSystemProbeResult>("Probe", call => existing.Contains(call.Path!)
            ? ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.Directory)
            : ScriptedFileSystemPrimitives.Missing());
        primitives.Set<bool>("CreateDirectory", call =>
        {
            existing.Add(call.Path!).ShouldBeTrue();
            return FileSystemReplayOutcome<bool>.Complete(true);
        });
        primitives.Enqueue<FileSystemDirectorySyncOutcome>("SynchronizeDescriptor", _ =>
            ScriptedFileSystemPrimitives.NativeFailure<FileSystemDirectorySyncOutcome>(5, "fsync"));

        primitives.CreateContext().FileSystem.CreateDirectory(leaf, TestContext.Current.CancellationToken);

        primitives.Calls.Where(call => call.Operation == "CreateDirectory").Select(call => call.Path)
            .ShouldBe([child, leaf]);
        primitives.Calls.Where(call => call.Operation == "OpenDescriptor").Select(call => call.Path)
            .ShouldBe([child, parent, leaf, child]);
        primitives.Count("Probe").ShouldBe(3);
        primitives.Count("SynchronizeDescriptor").ShouldBe(5);
        primitives.Count("CloseDescriptor").ShouldBe(4);
        primitives.Waits.ShouldBe([10]);
    }

    [Fact]
    public void Delete_SyncFailureIsNotMissingSuccess()
    {
        bool exists = true;
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.EnableDirectorySynchronization();
        primitives.Set<FileSystemProbeResult>("Probe", _ => exists
            ? ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.File)
            : ScriptedFileSystemPrimitives.Missing());
        primitives.Set<bool>("DeleteEntry", _ =>
        {
            exists.ShouldBeTrue();
            exists = false;
            return FileSystemReplayOutcome<bool>.Complete(true);
        });
        primitives.Set<FileSystemDirectorySyncOutcome>("SynchronizeDescriptor", call =>
            ScriptedFileSystemPrimitives.NativeFailure<FileSystemDirectorySyncOutcome>(5, "fsync", call.Path));

        IOException final = Should.Throw<IOException>(() =>
            primitives.CreateContext(retries: 2).FileSystem.DeleteFile(TestPath("removed")));

        exists.ShouldBeFalse();
        primitives.Count("DeleteEntry").ShouldBe(1);
        primitives.Count("Probe").ShouldBe(1);
        primitives.Count("SynchronizeDescriptor").ShouldBe(3);
        primitives.Count("OpenDescriptor").ShouldBe(1);
        primitives.Count("CloseDescriptor").ShouldBe(1);
        primitives.Waits.ShouldBe([10, 10]);
        FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? failure).ShouldBeTrue();
        failure!.Phase.ShouldBe("StandardDirectorySync");
        failure.Path.ShouldBe(Path.GetDirectoryName(TestPath("removed")));
        failure.Termination.ShouldBe(FileSystemFailureTermination.Exhausted);
    }

    [Fact]
    public void UnknownNativeFailure_UsesBudgetWhenReplayIsSafe()
    {
        int nativeCalls = 0;
        int errorReads = 0;
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.EnableDirectorySynchronization();
        primitives.Set<FileSystemDirectorySyncOutcome>("SynchronizeDescriptor", _ =>
            NativeFileSystemPrimitives.SynchronizeStandard(
                () =>
                {
                    nativeCalls++;
                    return -1;
                },
                () =>
                {
                    errorReads++;
                    errorReads.ShouldBe(nativeCalls);
                    return 123456;
                },
                macOS: false));

        IOException final = Should.Throw<IOException>(() =>
            primitives.CreateContext().FileSystem.SynchronizeDirectory(TestPath("directory")));

        nativeCalls.ShouldBe(6);
        errorReads.ShouldBe(6);
        final.InnerException.ShouldBeOfType<Win32Exception>().NativeErrorCode.ShouldBe(123456);
        primitives.Waits.ShouldBe([10, 10, 10, 10, 10]);
        primitives.Count("CloseDescriptor").ShouldBe(1);
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("create")]
    [InlineData("delete")]
    public void AmbiguousMutation_FailsWithoutDestructiveReplay(string mutation)
    {
        bool changed = false;
        string path = TestPath("existing", "entry");
        string operation = mutation switch
        {
            "rename" => "MovePath",
            "create" => "CreateDirectory",
            _ => "DeleteEntry",
        };
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.EnableDirectorySynchronization();
        primitives.Set<FileSystemProbeResult>("Probe", call =>
        {
            if (call.Path == Path.GetDirectoryName(path))
                return ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.Directory);
            return mutation == "delete" && !changed
                ? ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.File)
                : changed
                    ? ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.Directory)
                    : ScriptedFileSystemPrimitives.Missing();
        });
        primitives.Set<bool>(operation, call =>
        {
            changed = true;
            return ScriptedFileSystemPrimitives.NativeFailure<bool>(
                5, operation, call.Path, FileSystemReplaySafety.Unsafe);
        });
        ResilientFileSystem fileSystem = primitives.CreateContext().FileSystem;

        IOException final = Should.Throw<IOException>(() =>
        {
            if (mutation == "rename")
                fileSystem.MovePath(path, TestPath("destination"));
            else if (mutation == "create")
                fileSystem.CreateDirectory(path);
            else
                fileSystem.DeleteFile(path);
        });

        changed.ShouldBeTrue();
        primitives.Count(operation).ShouldBe(1);
        primitives.Count("Probe").ShouldBe(mutation == "create" ? 2 : 1);
        primitives.Count("SynchronizeDescriptor").ShouldBe(0);
        primitives.Waits.ShouldBeEmpty();
        FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? failure).ShouldBeTrue();
        failure!.Termination.ShouldBe(FileSystemFailureTermination.Unsafe);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloseFailure_DoesNotCloseReusedDescriptor(bool macOS)
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(
            macOS ? FileSystemPlatform.MacOS : FileSystemPlatform.Linux);
        primitives.Set("Probe", ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.File));
        primitives.Set("OpenDescriptor", FileSystemReplayOutcome<int>.Complete(0));
        primitives.Set("InspectDescriptor", ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.File));
        primitives.Set("DuplicateDescriptor", FileSystemReplayOutcome<int>.Complete(4));
        List<int> closed = [];
        primitives.Set<bool>("CloseDescriptor", call => NativeFileSystemPrimitives.CloseDescriptor(
            call.Descriptor!.Value,
            descriptor =>
            {
                closed.ShouldNotContain(descriptor);
                closed.Add(descriptor);
                // Descriptor zero has already been released/reused even though close
                // reports EINTR. A repeated close would affect a different owner.
                return descriptor == 0 ? -1 : 0;
            },
            () => 4));

        IOException final = Should.Throw<IOException>(() =>
            primitives.CreateContext().FileSystem.OpenRegularRead(TestPath("manifest")));

        closed.ShouldBe([0, 4]);
        primitives.Count("OpenDescriptor").ShouldBe(1);
        primitives.Count("InspectDescriptor").ShouldBe(1);
        primitives.Count("DuplicateDescriptor").ShouldBe(1);
        primitives.Calls.Single(call => call.Operation == "DuplicateDescriptor").Flags
            .ShouldBe(macOS ? 67L : 1030L);
        primitives.Waits.ShouldBeEmpty();
        FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? failure).ShouldBeTrue();
        failure!.Termination.ShouldBe(FileSystemFailureTermination.Unsafe);
    }

    [Fact]
    public void DirectorySync_CloseFailureDoesNotReopenOrResynchronize()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.EnableDirectorySynchronization();
        primitives.Set("OpenDescriptor", FileSystemReplayOutcome<int>.Complete(0));
        primitives.Set<bool>("CloseDescriptor", call => NativeFileSystemPrimitives.CloseDescriptor(
            call.Descriptor!.Value, _ => -1, () => 4, call.Path));

        IOException final = Should.Throw<IOException>(() =>
            primitives.CreateContext().FileSystem.SynchronizeDirectory(TestPath("directory")));

        primitives.Count("OpenDescriptor").ShouldBe(1);
        primitives.Count("SynchronizeDescriptor").ShouldBe(1);
        primitives.Count("CloseDescriptor").ShouldBe(1);
        primitives.Waits.ShouldBeEmpty();
        FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? failure).ShouldBeTrue();
        failure!.Operation.ShouldBe("close");
        failure.Path.ShouldBe(TestPath("directory"));
        failure.Termination.ShouldBe(FileSystemFailureTermination.Unsafe);
    }

    [Theory]
    [InlineData(87, true)]
    [InlineData(1117, false)]
    [InlineData(123456, false)]
    public void WindowsNativeMutation_DistinguishesRejectionFromAmbiguousFailure(int error, bool safe)
    {
        int calls = 0;
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(FileSystemPlatform.Windows);
        FileSystemContext context = primitives.CreateContext(retries: 2);

        IOException final = Should.Throw<IOException>(() => context.FileSystem.Execute(
            "Replace",
            () => NativeFileSystemPrimitives.MoveWindowsPath(
                "source", "destination", 9,
                (_, _, _) =>
                {
                    calls++;
                    return false;
                },
                () => error)));

        calls.ShouldBe(safe ? 3 : 1);
        primitives.Waits.Count.ShouldBe(safe ? 2 : 0);
        final.InnerException.ShouldBeOfType<Win32Exception>().NativeErrorCode.ShouldBe(error);
        final.Data["SourcePath"].ShouldBe("source");
        final.Data["DestinationPath"].ShouldBe("destination");
        final.Data["NativeFlags"].ShouldBe(9u);
        FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? failure).ShouldBeTrue();
        failure!.Termination.ShouldBe(safe ? FileSystemFailureTermination.Exhausted : FileSystemFailureTermination.Unsafe);
    }

    [Fact]
    public void RecursiveDelete_RetainsEachChildAndDoesNotFollowLinks()
    {
        string root = TestPath("tree");
        string first = Path.Combine(root, "first");
        string link = Path.Combine(root, "outside-link");
        string last = Path.Combine(root, "last");
        Dictionary<string, FileSystemEntryKind> entries = new Dictionary<string, FileSystemEntryKind>
        {
            [root] = FileSystemEntryKind.Directory,
            [first] = FileSystemEntryKind.File,
            [link] = FileSystemEntryKind.SymbolicLink,
            [last] = FileSystemEntryKind.File,
        };
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.EnableDirectorySynchronization();
        primitives.Set<FileSystemProbeResult>("Probe", call => ScriptedFileSystemPrimitives.Entry(entries[call.Path!]));
        primitives.Enqueue("CreateDirectoryEnumerator", FileSystemReplayOutcome<IEnumerator<string>>.Complete(
            ((IEnumerable<string>)[first, link, last]).GetEnumerator()));
        bool rootRejected = false;
        primitives.Set<bool>("DeleteEntry", call =>
        {
            if (call.Path == root && !rootRejected)
            {
                rootRejected = true;
                return ScriptedFileSystemPrimitives.NativeFailure<bool>(13, "rmdir", call.Path);
            }
            entries.Remove(call.Path!).ShouldBeTrue();
            return FileSystemReplayOutcome<bool>.Complete(true);
        });
        primitives.Enqueue<FileSystemDirectorySyncOutcome>("SynchronizeDescriptor", _ =>
            ScriptedFileSystemPrimitives.NativeFailure<FileSystemDirectorySyncOutcome>(5, "fsync"));

        primitives.CreateContext(retries: 2).FileSystem.DeleteDirectory(
            root, recursive: true, TestContext.Current.CancellationToken);

        entries.ShouldBeEmpty();
        primitives.Count("CreateDirectoryEnumerator").ShouldBe(1);
        primitives.Calls.Where(call => call.Operation == "DeleteEntry").Select(call => call.Path)
            .ShouldBe([first, link, last, root, root]);
        primitives.Calls.Single(call => call.Operation == "DeleteEntry" && call.Path == link)
            .Kind.ShouldBe(FileSystemEntryKind.SymbolicLink);
        primitives.Waits.ShouldBe([10, 10]);
    }

    [Fact]
    public void RecursiveDelete_ChildrenShareRemainingBudget()
    {
        string root = TestPath("tree");
        string first = Path.Combine(root, "first");
        string second = Path.Combine(root, "second");
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.EnableDirectorySynchronization();
        primitives.Set<FileSystemProbeResult>("Probe", call =>
            ScriptedFileSystemPrimitives.Entry(call.Path == root ? FileSystemEntryKind.Directory : FileSystemEntryKind.File));
        primitives.Enqueue("CreateDirectoryEnumerator", FileSystemReplayOutcome<IEnumerator<string>>.Complete(
            ((IEnumerable<string>)[first, second]).GetEnumerator()));
        int firstAttempts = 0;
        primitives.Set<bool>("DeleteEntry", call => call.Path == first && ++firstAttempts == 2
            ? FileSystemReplayOutcome<bool>.Complete(true)
            : ScriptedFileSystemPrimitives.NativeFailure<bool>(13, "unlink", call.Path));

        Should.Throw<IOException>(() => primitives.CreateContext(retries: 1).FileSystem.DeleteDirectory(root, recursive: true));

        primitives.Calls.Where(call => call.Operation == "DeleteEntry").Select(call => call.Path)
            .ShouldBe([first, first, second]);
        primitives.Waits.ShouldBe([10]);
        primitives.Count("CreateDirectoryEnumerator").ShouldBe(1);
    }

    [Fact]
    public void DeleteDirectory_SymbolicRootAndTrailingSeparatorDoNotFollowTarget()
    {
        string path = TestPath("directory-link");
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.EnableDirectorySynchronization();
        primitives.Set("Probe", ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.SymbolicLink));
        primitives.Set("DeleteEntry", FileSystemReplayOutcome<bool>.Complete(true));

        primitives.CreateContext().FileSystem.DeleteDirectory(
            path + Path.DirectorySeparatorChar, recursive: true, TestContext.Current.CancellationToken);

        primitives.Calls.Single(call => call.Operation == "Probe").Path.ShouldBe(path);
        FileSystemPrimitiveCall deletion = primitives.Calls.Single(call => call.Operation == "DeleteEntry");
        deletion.Path.ShouldBe(path);
        deletion.Kind.ShouldBe(FileSystemEntryKind.SymbolicLink);
        primitives.Count("CreateDirectoryEnumerator").ShouldBe(0);
        primitives.Waits.ShouldBeEmpty();
    }

    [Fact]
    public void CreateDirectory_ForeignCollisionIsNotOwnedOrDeleted()
    {
        string path = TestPath("parent", "collision");
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.Enqueue("Probe", ScriptedFileSystemPrimitives.Missing());
        primitives.Enqueue("Probe", ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.Directory));
        primitives.Enqueue("Probe", ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.File));
        primitives.Set("CreateDirectory", FileSystemReplayOutcome<bool>.KindRejected(false));

        IOException exception = Should.Throw<IOException>(() => primitives.CreateContext().FileSystem.CreateDirectory(path));

        primitives.Count("CreateDirectory").ShouldBe(1);
        primitives.Count("DeleteEntry").ShouldBe(0);
        primitives.Count("SynchronizeDescriptor").ShouldBe(0);
        primitives.Waits.ShouldBeEmpty();
        FileSystemFailureContext.TryGetOrigin(exception, out _).ShouldBeFalse();
    }

    [Fact]
    public void CreateNewFile_CollisionDoesNotAdoptOrDeleteForeignFile()
    {
        string path = TestPath($"foreign-{Guid.NewGuid():N}");
        File.WriteAllText(path, "foreign");
        try
        {
            ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
            primitives.Set<FileStream>("OpenFile", call => SystemFileSystemPrimitives.Instance.OpenFile(
                call.Path!, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, FileOptions.WriteThrough));

            IOException final = Should.Throw<IOException>(() => primitives.CreateContext().FileSystem.OpenFile(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384, FileOptions.WriteThrough));

            File.ReadAllText(path).ShouldBe("foreign");
            primitives.Count("OpenFile").ShouldBe(1);
            primitives.Count("DeleteEntry").ShouldBe(0);
            primitives.Waits.ShouldBeEmpty();
            FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? failure).ShouldBeTrue();
            failure!.Termination.ShouldBe(FileSystemFailureTermination.Unsafe);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RegularOpen_InspectionRetriesRetainedDescriptorAndPreservesPrimaryFailure()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.Set("OpenDescriptor", FileSystemReplayOutcome<int>.Complete(73));
        primitives.Enqueue<FileSystemProbeResult>("InspectDescriptor", _ =>
            ScriptedFileSystemPrimitives.NativeFailure<FileSystemProbeResult>(5, "statx"));
        primitives.Set("InspectDescriptor", ScriptedFileSystemPrimitives.Entry(FileSystemEntryKind.Other));
        primitives.Set<bool>("CloseDescriptor", _ =>
            ScriptedFileSystemPrimitives.NativeFailure<bool>(4, "close", safety: FileSystemReplaySafety.Unsafe));

        IOException exception = Should.Throw<IOException>(() =>
            primitives.CreateContext().FileSystem.OpenRegularRead(TestPath("not-regular")));

        exception.Message.ShouldBe("The package manifest is not a regular file.");
        primitives.Count("OpenDescriptor").ShouldBe(1);
        primitives.Count("InspectDescriptor").ShouldBe(2);
        primitives.Count("CloseDescriptor").ShouldBe(1);
        primitives.Calls.Single(call => call.Operation == "OpenDescriptor").Flags
            .ShouldBe((long)NativeFileSystemPrimitives.LinuxManifestOpenFlags);
        primitives.Calls.Where(call => call.Operation == "InspectDescriptor").ShouldAllBe(call => call.Descriptor == 73);
        primitives.Waits.ShouldBe([10]);
        FileSystemFailureContext.TryGetOrigin(exception, out _).ShouldBeFalse();
    }

    [Fact]
    public void LinuxStatx_ReturnedUnsupportedIsRetriedWithItsOriginalExceptionType()
    {
        int calls = 0;
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.Set<FileSystemProbeResult>("Probe", call => NativeFileSystemPrimitives.InspectLinux(
            -100, call.Path!, 0x100, optional: true,
            (_, _, _, _, _) =>
            {
                calls++;
                return -1;
            },
            () => 38));

        PlatformNotSupportedException final = Should.Throw<PlatformNotSupportedException>(() =>
            primitives.CreateContext().FileSystem.Probe(TestPath("statx")));

        calls.ShouldBe(6);
        final.InnerException.ShouldBeOfType<Win32Exception>().NativeErrorCode.ShouldBe(38);
        primitives.Waits.Count.ShouldBe(5);
        FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? failure).ShouldBeTrue();
        failure!.NativeFlags.ShouldBe(0x100);
    }

    [Fact]
    public void LinuxStatx_MissingEntryPointIsNotAReturnedNativeFailure()
    {
        int calls = 0;
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.Set<FileSystemProbeResult>("Probe", call => NativeFileSystemPrimitives.InspectLinux(
            -100, call.Path!, 0x100, optional: true,
            (_, _, _, _, _) =>
            {
                calls++;
                throw new EntryPointNotFoundException("statx");
            },
            () => throw new InvalidOperationException("There was no native return.")));

        PlatformNotSupportedException final = Should.Throw<PlatformNotSupportedException>(() =>
            primitives.CreateContext().FileSystem.Probe(TestPath("statx")));

        calls.ShouldBe(1);
        final.InnerException.ShouldBeOfType<EntryPointNotFoundException>();
        primitives.Waits.ShouldBeEmpty();
        FileSystemFailureContext.TryGetOrigin(final, out _).ShouldBeFalse();
    }

    [Fact]
    public void LinuxStatx_FixedLayoutAndNoFollowFlagsArePreserved()
    {
        FileSystemReplayOutcome<FileSystemProbeResult> result = NativeFileSystemPrimitives.InspectLinux(
            -100, "link", 0x100, optional: true,
            (descriptor, path, flags, mask, buffer) =>
            {
                descriptor.ShouldBe(-100);
                path.ShouldBe("link");
                flags.ShouldBe(0x100);
                mask.ShouldBe(1u);
                Marshal.WriteInt32(buffer, 0, 1);
                Marshal.WriteInt16(buffer, 28, unchecked((short)0xA000));
                return 0;
            },
            () => 13);

        result.Value.Kind.ShouldBe(FileSystemEntryKind.SymbolicLink);
        result.Value.Mode.ShouldBe(0xA000);
    }

    [Fact]
    public void MacFullSync_FallbackRetainsStandardPhaseAcrossFailures()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(FileSystemPlatform.MacOS);
        primitives.EnableDirectorySynchronization();
        int standardCalls = 0;
        primitives.Set<FileSystemDirectorySyncOutcome>("SynchronizeDescriptor", call => call.Flags == 1
            ? NativeFileSystemPrimitives.SynchronizeMacFull(() => -1, () => 25)
            : NativeFileSystemPrimitives.SynchronizeStandard(
                () => ++standardCalls == 2 ? 0 : -1, () => 5, macOS: true));

        primitives.CreateContext().FileSystem.SynchronizeDirectory(
            TestPath("mac-directory"), TestContext.Current.CancellationToken);

        primitives.Calls.Where(call => call.Operation == "SynchronizeDescriptor").Select(call => call.Flags)
            .ShouldBe(new long?[] { 1, 0, 0 });
        primitives.Count("OpenDescriptor").ShouldBe(1);
        primitives.Count("CloseDescriptor").ShouldBe(1);
        primitives.Waits.ShouldBe([10]);
    }

    [Fact]
    public void MacFullSync_RealFailureIsRetriedWithoutCapabilityFallback()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(FileSystemPlatform.MacOS);
        primitives.EnableDirectorySynchronization();
        int calls = 0;
        primitives.Set<FileSystemDirectorySyncOutcome>("SynchronizeDescriptor", call =>
        {
            call.Flags.ShouldBe(1);
            return NativeFileSystemPrimitives.SynchronizeMacFull(() => ++calls == 2 ? 0 : -1, () => 5);
        });

        primitives.CreateContext().FileSystem.SynchronizeDirectory(
            TestPath("mac-directory"), TestContext.Current.CancellationToken);

        calls.ShouldBe(2);
        primitives.Waits.ShouldBe([10]);
    }

    [Fact]
    public void MacStandardSync_DocumentedUnsupportedOutcomeIsNotAFault()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(FileSystemPlatform.MacOS);
        primitives.EnableDirectorySynchronization();
        primitives.Set<FileSystemDirectorySyncOutcome>("SynchronizeDescriptor", call => call.Flags == 1
            ? NativeFileSystemPrimitives.SynchronizeMacFull(() => -1, () => 45)
            : NativeFileSystemPrimitives.SynchronizeStandard(() => -1, () => 22, macOS: true));

        primitives.CreateContext().FileSystem.SynchronizeDirectory(
            TestPath("mac-directory"), TestContext.Current.CancellationToken);

        primitives.Count("SynchronizeDescriptor").ShouldBe(2);
        primitives.Count("CloseDescriptor").ShouldBe(1);
        primitives.Waits.ShouldBeEmpty();
    }

    [Fact]
    public async Task WindowsNative_DurableMutationsAndExtendedPaths()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Real MoveFileExW/CreateDirectoryW/DeleteFileW checks require Windows.");

        string root = TestPath($"native-{Guid.NewGuid():N}");
        FileSystemContext context = RealContext();
        try
        {
            string source = Path.Combine(root, "source");
            string destination = Path.Combine(root, "destination");
            context.FileSystem.CreateDirectory(Path.Combine(source, "nested"), TestContext.Current.CancellationToken);
            string value = Path.Combine(source, "value");
            File.WriteAllText(value, "old");
            context.FileSystem.MovePath(source, destination, TestContext.Current.CancellationToken);
            string replacement = Path.Combine(root, "replacement");
            File.WriteAllText(replacement, "new");
            await context.FileSystem.ReplaceFileAsync(
                replacement, Path.Combine(destination, "value"), TestContext.Current.CancellationToken);

            File.ReadAllText(Path.Combine(destination, "value")).ShouldBe("new");
            context.FileSystem.Probe(source, TestContext.Current.CancellationToken).Exists.ShouldBeFalse();
            NativeFileSystemPrimitives.ToWindowsExtendedPath(destination).ShouldStartWith(@"\\?\");
            NativeFileSystemPrimitives.ToWindowsExtendedPath(@"\\server\share\entry")
                .ShouldBe(@"\\?\UNC\server\share\entry");
            context.FileSystem.DeleteDirectory(root, recursive: true, TestContext.Current.CancellationToken);
            context.FileSystem.Probe(root, TestContext.Current.CancellationToken).Exists.ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Linux")]
    [InlineData("macOS")]
    public void UnixNative_RegularOpenAndRecursiveDeleteDoNotFollowLinks(string platform)
    {
        if (platform == "Linux" ? !OperatingSystem.IsLinux() : !OperatingSystem.IsMacOS())
            Assert.Skip($"Real {platform} descriptor/no-follow/barrier checks require {platform}.");

        string root = TestPath($"native-{Guid.NewGuid():N}");
        string outside = TestPath($"outside-{Guid.NewGuid():N}");
        FileSystemContext context = RealContext();
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            string file = Path.Combine(root, "regular");
            File.WriteAllText(file, "content");
            File.WriteAllText(Path.Combine(outside, "keep"), "foreign");
            string link = Path.Combine(root, "file-link");
            File.CreateSymbolicLink(link, "regular");
            string directoryLink = Path.Combine(root, "directory-link");
            Directory.CreateSymbolicLink(directoryLink, outside);
            SystemPackageCacheFileOperations operations = new SystemPackageCacheFileOperations(context);
            operations.FileExists(link).ShouldBeTrue();
            operations.DirectoryExists(directoryLink).ShouldBeTrue();
            operations.CreateDirectory(directoryLink);
            context.FileSystem.Probe(directoryLink, TestContext.Current.CancellationToken).IsLink.ShouldBeTrue();
            using (FileStream stream = context.FileSystem.OpenRegularRead(file, TestContext.Current.CancellationToken))
                stream.ReadByte().ShouldBe((int)'c');
            Should.Throw<IOException>(() => context.FileSystem.OpenRegularRead(link));

            context.FileSystem.DeleteDirectory(root, recursive: true, TestContext.Current.CancellationToken);

            File.ReadAllText(Path.Combine(outside, "keep")).ShouldBe("foreign");
            context.FileSystem.Probe(root, TestContext.Current.CancellationToken).Exists.ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            if (Directory.Exists(outside))
                Directory.Delete(outside, recursive: true);
        }
    }

    private static FileSystemContext RealContext() =>
        new FileSystemContext(new FileSystemRetryOptions
        {
            RetryCount = 1,
            MinimumDelayMilliseconds = 0,
            MaximumJitterMilliseconds = 0,
        });
}
