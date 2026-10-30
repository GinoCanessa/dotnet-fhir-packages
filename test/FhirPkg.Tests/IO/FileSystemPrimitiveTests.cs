// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Collections;
using System.ComponentModel;
using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.IO;
using FhirPkg.Tests.Support;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.IO;

public sealed class FileSystemPrimitiveTests
{
    private static string TestPath(string name) => Path.Combine(AppContext.BaseDirectory, name);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Probe_AccessDeniedIsNotMissing(bool asynchronous)
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        UnauthorizedAccessException denied = new UnauthorizedAccessException("denied");
        primitives.Set<FileSystemProbeResult>("Probe", call =>
            ScriptedFileSystemPrimitives.Fail<FileSystemProbeResult>(denied, "Probe", call.Path));
        FileSystemContext context = primitives.CreateContext();

        UnauthorizedAccessException final = asynchronous
            ? await Should.ThrowAsync<UnauthorizedAccessException>(
                () => context.FileSystem.ProbeAsync(TestPath("denied"), TestContext.Current.CancellationToken).AsTask())
            : Should.Throw<UnauthorizedAccessException>(() =>
                context.FileSystem.Probe(TestPath("denied"), TestContext.Current.CancellationToken));

        final.ShouldBeSameAs(denied);
        primitives.Count("Probe").ShouldBe(6);
        (asynchronous ? primitives.AsyncWaits : primitives.Waits).ShouldBe([10, 10, 10, 10, 10]);
        (asynchronous ? primitives.Waits : primitives.AsyncWaits).ShouldBeEmpty();
        FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? failure).ShouldBeTrue();
        failure!.Path.ShouldBe(TestPath("denied"));
    }

    [Fact]
    public void Probe_MissingIsSemanticButRequiredOpenIsNotAbsence()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(FileSystemPlatform.Windows);
        primitives.Set("Probe", ScriptedFileSystemPrimitives.Missing());
        primitives.Set<FileAttributes>("GetAttributes", call =>
            ScriptedFileSystemPrimitives.Fail<FileAttributes>(
                new FileNotFoundException("required file", call.Path), "GetAttributes", call.Path));
        FileSystemContext context = primitives.CreateContext();

        context.FileSystem.Probe(TestPath("missing"), TestContext.Current.CancellationToken).Exists.ShouldBeFalse();
        primitives.Waits.ShouldBeEmpty();
        Should.Throw<FileNotFoundException>(() => context.FileSystem.OpenRegularRead(TestPath("missing")));

        primitives.Count("Probe").ShouldBe(1);
        primitives.Count("GetAttributes").ShouldBe(6);
        primitives.Count("OpenFile").ShouldBe(0);
        primitives.Waits.Count.ShouldBe(5);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1117)]
    [InlineData(123456)]
    public void Probe_DeviceAndUnknownErrorsAreNotMissing(int error)
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.Set<FileSystemProbeResult>("Probe", call =>
            ScriptedFileSystemPrimitives.NativeFailure<FileSystemProbeResult>(error, "Probe", call.Path));

        IOException exception = Should.Throw<IOException>(() =>
            primitives.CreateContext(retries: 2).FileSystem.Probe(TestPath("device")));

        exception.InnerException.ShouldBeOfType<Win32Exception>().NativeErrorCode.ShouldBe(error);
        primitives.Count("Probe").ShouldBe(3);
        primitives.Waits.ShouldBe([10, 10]);
    }

    [Fact]
    public void ThrownApplicationIOException_DoesNotBecomePrimitiveEvidence()
    {
        IOException callbackFailure = new IOException("observer, not an OS result");
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.Set<FileSystemProbeResult>("Probe", _ => throw callbackFailure);

        Should.Throw<IOException>(() => primitives.CreateContext().FileSystem.Probe(TestPath("file")))
            .ShouldBeSameAs(callbackFailure);

        primitives.Count("Probe").ShouldBe(1);
        primitives.Waits.ShouldBeEmpty();
        FileSystemFailureContext.TryGetOrigin(callbackFailure, out _).ShouldBeFalse();
    }

    [Fact]
    public void RegularOpen_RetryRechecksKindBeforeOpening()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(FileSystemPlatform.Windows);
        primitives.Enqueue("GetAttributes", FileSystemReplayOutcome<FileAttributes>.Complete(FileAttributes.Normal));
        primitives.Enqueue("GetAttributes", FileSystemReplayOutcome<FileAttributes>.Complete(FileAttributes.ReparsePoint));
        primitives.Set<FileStream>("OpenFile", call =>
            ScriptedFileSystemPrimitives.NativeFailure<FileStream>(5, "OpenFile", call.Path));

        IOException exception = Should.Throw<IOException>(() =>
            primitives.CreateContext().FileSystem.OpenRegularRead(TestPath("changed-kind")));

        exception.Message.ShouldBe("The package manifest is not a regular file.");
        primitives.Count("GetAttributes").ShouldBe(2);
        primitives.Count("OpenFile").ShouldBe(1);
        primitives.Waits.ShouldBe([10]);
        FileSystemFailureContext.TryGetOrigin(exception, out _).ShouldBeFalse();
    }

    [Fact]
    public void Enumeration_FailedAdvanceDiscardsPartialSnapshot()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        TestEnumerator failed = new TestEnumerator(["stale", "never"], "advance");
        TestEnumerator succeeded = new TestEnumerator(["second", "first"]);
        primitives.Enqueue("CreateDirectoryEnumerator", FileSystemReplayOutcome<IEnumerator<string>>.Complete(failed));
        primitives.Enqueue("CreateDirectoryEnumerator", FileSystemReplayOutcome<IEnumerator<string>>.Complete(succeeded));

        string[] snapshot = primitives.CreateContext().FileSystem.SnapshotDirectory(
            TestPath("directory"), TestContext.Current.CancellationToken);

        snapshot.ShouldBe(["second", "first"]);
        failed.DisposeCount.ShouldBe(1);
        succeeded.DisposeCount.ShouldBe(1);
        primitives.Count("CreateDirectoryEnumerator").ShouldBe(2);
        primitives.Waits.ShouldBe([10]);
    }

    [Theory]
    [InlineData("creation")]
    [InlineData("current")]
    [InlineData("dispose")]
    public async Task Enumeration_AllLazyBoundariesUseTheSameSnapshotBudget(string failurePoint)
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        TestEnumerator failed = new TestEnumerator(["discard"], failurePoint);
        TestEnumerator succeeded = new TestEnumerator(["kept"]);
        if (failurePoint == "creation")
        {
            primitives.Enqueue<IEnumerator<string>>("CreateDirectoryEnumerator", call =>
                ScriptedFileSystemPrimitives.NativeFailure<IEnumerator<string>>(5, "EnumerateDirectory", call.Path));
        }
        else
        {
            primitives.Enqueue("CreateDirectoryEnumerator", FileSystemReplayOutcome<IEnumerator<string>>.Complete(failed));
        }
        primitives.Enqueue("CreateDirectoryEnumerator", FileSystemReplayOutcome<IEnumerator<string>>.Complete(succeeded));

        string[] snapshot = await primitives.CreateContext().FileSystem.SnapshotDirectoryAsync(
            TestPath("directory"), TestContext.Current.CancellationToken);

        snapshot.ShouldBe(["kept"]);
        failed.DisposeCount.ShouldBe(failurePoint == "creation" ? 0 : 1);
        succeeded.DisposeCount.ShouldBe(1);
        primitives.AsyncWaits.ShouldBe([10]);
        primitives.Waits.ShouldBeEmpty();
    }

    [Fact]
    public void Enumeration_DisposalFailureDoesNotReplacePrimaryFailure()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        IOException primary = new IOException("advancement");
        TestEnumerator enumerator = new TestEnumerator(["partial"], "dispose");
        primitives.Enqueue("CreateDirectoryEnumerator", FileSystemReplayOutcome<IEnumerator<string>>.Complete(enumerator));
        primitives.Set<bool>("MoveNext", call => ScriptedFileSystemPrimitives.Fail<bool>(primary, "MoveNext", call.Path));

        IOException final = Should.Throw<IOException>(() =>
            primitives.CreateContext(retries: 0).FileSystem.SnapshotDirectory(TestPath("directory")));

        final.ShouldBeSameAs(primary);
        enumerator.DisposeCount.ShouldBe(1);
        primitives.Count("CreateDirectoryEnumerator").ShouldBe(1);
    }

    [Fact]
    public void Enumeration_CancellationDisposesOnceWithoutRetry()
    {
        using CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        TestEnumerator enumerator = new TestEnumerator(["partial", "never"], onAdvance: source.Cancel);
        primitives.Enqueue("CreateDirectoryEnumerator", FileSystemReplayOutcome<IEnumerator<string>>.Complete(enumerator));

        Should.Throw<OperationCanceledException>(() =>
            primitives.CreateContext().FileSystem.SnapshotDirectory(TestPath("directory"), source.Token));

        enumerator.DisposeCount.ShouldBe(1);
        primitives.Count("CreateDirectoryEnumerator").ShouldBe(1);
        primitives.Count("MoveNext").ShouldBe(1);
        primitives.Waits.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("GetCurrentDirectory", false)]
    [InlineData("GetTempPath", false)]
    [InlineData("GetHomeDirectory", false)]
    [InlineData("GetCurrentDirectory", true)]
    [InlineData("GetTempPath", true)]
    [InlineData("GetHomeDirectory", true)]
    public async Task Discovery_FailedOperatingSystemCallUsesPolicy(string operation, bool asynchronous)
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.Enqueue<string>(operation, _ => ScriptedFileSystemPrimitives.NativeFailure<string>(5, operation));
        primitives.Set(operation, FileSystemReplayOutcome<string>.Complete(TestPath("discovered")));
        ResilientFileSystem fileSystem = primitives.CreateContext().FileSystem;

        string discovered = asynchronous
            ? await (operation switch
            {
                "GetCurrentDirectory" => fileSystem.GetCurrentDirectoryAsync(TestContext.Current.CancellationToken),
                "GetTempPath" => fileSystem.GetTempPathAsync(TestContext.Current.CancellationToken),
                _ => fileSystem.GetHomeDirectoryAsync(TestContext.Current.CancellationToken),
            })
            : operation switch
            {
                "GetCurrentDirectory" => fileSystem.GetCurrentDirectory(TestContext.Current.CancellationToken),
                "GetTempPath" => fileSystem.GetTempPath(TestContext.Current.CancellationToken),
                _ => fileSystem.GetHomeDirectory(TestContext.Current.CancellationToken),
            };

        discovered.ShouldBe(TestPath("discovered"));
        primitives.Count(operation).ShouldBe(2);
        (asynchronous ? primitives.AsyncWaits : primitives.Waits).ShouldBe([10]);
        (asynchronous ? primitives.Waits : primitives.AsyncWaits).ShouldBeEmpty();
    }

    [Fact]
    public void RelativeResolution_AndProbeShareTheRemainingBudget()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.Enqueue<string>("GetFullPath", call =>
            ScriptedFileSystemPrimitives.NativeFailure<string>(5, "GetFullPath", call.Path));
        primitives.Set("GetFullPath", FileSystemReplayOutcome<string>.Complete(TestPath("resolved")));
        primitives.Set<FileSystemProbeResult>("Probe", call =>
            ScriptedFileSystemPrimitives.NativeFailure<FileSystemProbeResult>(5, "Probe", call.Path));

        Should.Throw<IOException>(() => primitives.CreateContext(retries: 1).FileSystem.Probe("relative"));

        primitives.Count("GetFullPath").ShouldBe(2);
        primitives.Count("Probe").ShouldBe(1);
        primitives.Waits.ShouldBe([10]);
    }

    [Fact]
    public void InvalidNonIoPath_IsNotRetried()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.Set<string>("GetFullPath", call => SystemFileSystemPrimitives.Instance.GetFullPath(call.Path!));
        FileSystemContext context = primitives.CreateContext();

        ArgumentException exception = Should.Throw<ArgumentException>(() => context.FileSystem.Probe("bad\0path"));

        primitives.Count("GetFullPath").ShouldBe(1);
        primitives.Count("Probe").ShouldBe(0);
        primitives.Waits.ShouldBeEmpty();
        FileSystemFailureContext.TryGetOrigin(exception, out _).ShouldBeFalse();
    }

    [Fact]
    public void WindowsPathResolution_PreservesDriveRelativeAndUncSemantics()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Windows drive-relative and UNC path semantics require Windows.");

        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives(FileSystemPlatform.Windows);
        primitives.Set<string>("GetFullPath", call => SystemFileSystemPrimitives.Instance.GetFullPath(call.Path!));
        ResilientFileSystem fileSystem = primitives.CreateContext().FileSystem;
        string driveRelative = Path.GetPathRoot(AppContext.BaseDirectory)![..2] + "relative";

        fileSystem.GetFullPath(driveRelative, TestContext.Current.CancellationToken).ShouldBe(Path.GetFullPath(driveRelative));
        fileSystem.GetFullPath(@"\root-relative", TestContext.Current.CancellationToken).ShouldBe(Path.GetFullPath(@"\root-relative"));
        fileSystem.GetFullPath(@"\\server\share\folder\..\file", TestContext.Current.CancellationToken).ShouldBe(@"\\server\share\file");
        primitives.Count("GetFullPath").ShouldBe(2);
        primitives.Waits.ShouldBeEmpty();
    }

    [Fact]
    public void AttributesAndLength_AreIndependentNarrowOperations()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.Enqueue<FileAttributes>("GetAttributes", call =>
            ScriptedFileSystemPrimitives.NativeFailure<FileAttributes>(5, "GetAttributes", call.Path));
        primitives.Set("GetAttributes", FileSystemReplayOutcome<FileAttributes>.Complete(FileAttributes.ReadOnly));
        primitives.Enqueue<long>("GetLength", call =>
            ScriptedFileSystemPrimitives.NativeFailure<long>(5, "FileInfo.Length", call.Path));
        primitives.Set("GetLength", FileSystemReplayOutcome<long>.Complete(123));
        ResilientFileSystem fileSystem = primitives.CreateContext(retries: 1).FileSystem;

        fileSystem.GetAttributes(TestPath("file"), TestContext.Current.CancellationToken).ShouldBe(FileAttributes.ReadOnly);
        fileSystem.GetLength(TestPath("file"), TestContext.Current.CancellationToken).ShouldBe(123);
        primitives.Waits.ShouldBe([10, 10]);
    }

    [Fact]
    public void Context_ValidatesBeforeDiscoveryAndRetainsImmutableSettings()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        FileSystemRetryOptions invalid = new FileSystemRetryOptions { RetryCount = -1 };
        Should.Throw<PackageInstallException>(() =>
            new FileSystemContext(invalid, primitives, primitives.CreateExecutor()))
            .ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
        primitives.Calls.ShouldBeEmpty();

        FileSystemRetryOptions options = new FileSystemRetryOptions
        {
            RetryCount = 1,
            MinimumDelayMilliseconds = 0,
            MaximumJitterMilliseconds = 0,
        };
        FileSystemContext context = new FileSystemContext(options, primitives, primitives.CreateExecutor());
        options.RetryCount = 99;
        context.ToOptionsSnapshot().RetryCount = 98;
        context.Policy.RetryCount.ShouldBe(1);
        context.HasExplicitValues.ShouldBeTrue();

        FileSystemRetryPolicy implicitPolicy = FileSystemRetryPolicy.Resolve(
            new FileSystemRetryOptions(), _ => null, out bool explicitValues);
        FileSystemContext implicitContext = new FileSystemContext(
            implicitPolicy, explicitValues, primitives, primitives.CreateExecutor());
        implicitContext.ToOptionsSnapshot().RetryCount.ShouldBe(5);
        implicitContext.HasExplicitValues.ShouldBeFalse();
    }

    [Fact]
    public void ContextBoundAdapter_DoesNotAddASecondBudget()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.Set<FileSystemProbeResult>("Probe", call =>
            ScriptedFileSystemPrimitives.NativeFailure<FileSystemProbeResult>(5, "Probe", call.Path));
        SystemPackageCacheFileOperations operations = new SystemPackageCacheFileOperations(primitives.CreateContext(retries: 1));

        Should.Throw<IOException>(() => operations.FileExists(TestPath("file")));

        primitives.Count("Probe").ShouldBe(2);
        primitives.Waits.ShouldBe([10]);
        SystemPackageCacheFileOperations.Instance.ShouldNotBeSameAs(SystemPackageCacheFileOperations.Instance);
    }

    [Fact]
    public void Adapter_ExistenceSemanticsDoNotChangeArtifactNoFollowInspection()
    {
        ScriptedFileSystemPrimitives primitives = new ScriptedFileSystemPrimitives();
        primitives.Set<FileSystemProbeResult>("Probe", call => ScriptedFileSystemPrimitives.Entry(
            call.Flags == 1 ? FileSystemEntryKind.Directory : FileSystemEntryKind.SymbolicLink));
        SystemPackageCacheFileOperations operations = new SystemPackageCacheFileOperations(primitives.CreateContext());

        operations.DirectoryExists(TestPath("directory-link")).ShouldBeTrue();
        operations.GetArtifactKind(TestPath("directory-link")).ShouldBe(PackageCacheArtifactKind.SymbolicLink);
        operations.ArtifactExists(TestPath("directory-link"), PackageCacheArtifactKind.SymbolicLink).ShouldBeTrue();

        primitives.Calls.Select(call => call.Flags).ShouldBe(new long?[] { 1, 0, 0 });
        primitives.Waits.ShouldBeEmpty();
    }

    [Fact]
    public void ProductionPrimitives_PerformOneAttemptAndReturnOrigin()
    {
        string missing = TestPath($"missing-{Guid.NewGuid():N}");
        FileSystemReplayOutcome<FileAttributes> attributes = SystemFileSystemPrimitives.Instance.GetAttributes(missing);

        attributes.Kind.ShouldBe(FileSystemReplayOutcomeKind.PrimitiveFailure);
        attributes.PrimitiveFailure!.Exception.ShouldBeOfType<FileNotFoundException>();
        FileSystemFailureContext.TryGetOrigin(attributes.PrimitiveFailure.Exception, out _).ShouldBeTrue();
        FileSystemFailureContext.TryGetTerminal(attributes.PrimitiveFailure.Exception, out _).ShouldBeFalse();
        SystemFileSystemPrimitives.Instance.Probe(missing).Kind.ShouldBe(FileSystemReplayOutcomeKind.Missing);
    }

    private sealed class TestEnumerator : IEnumerator<string>
    {
        private readonly string[] _entries;
        private readonly string? _failurePoint;
        private readonly Action? _onAdvance;
        private int _index = -1;

        internal TestEnumerator(string[] entries, string? failurePoint = null, Action? onAdvance = null)
        {
            _entries = entries;
            _failurePoint = failurePoint;
            _onAdvance = onAdvance;
        }

        internal int DisposeCount { get; private set; }

        public string Current => _failurePoint == "current"
            ? throw new IOException("current failed")
            : _entries[_index];

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            _index++;
            _onAdvance?.Invoke();
            if (_failurePoint == "advance" && _index == 1)
                throw new IOException("advance failed");
            return _index < _entries.Length;
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose()
        {
            DisposeCount++;
            if (_failurePoint == "dispose")
                throw new IOException("dispose failed");
        }
    }
}
