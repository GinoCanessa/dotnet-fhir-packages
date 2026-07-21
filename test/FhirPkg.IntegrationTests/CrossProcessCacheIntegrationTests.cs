// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FhirPkg.Cache;
using FhirPkg.Models;
using FhirPkg.Utilities;
using Shouldly;
using Xunit;

namespace FhirPkg.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class CrossProcessCacheIntegrationTests
{
    private static readonly TimeSpan s_timeout =
        TimeSpan.FromSeconds(30);

    [Fact]
    public async Task SameIdentity_WaiterReturnsWinnerWithoutReadingSource()
    {
        using TestWorkspace workspace = new();
        string archive = workspace.CreateArchive(
            "same.package",
            "1.0.0");
        string barrier = workspace.File("owner.ready");
        string release = workspace.File("owner.release");
        string ownerCounter = workspace.File("owner.counter");
        string waiterCounter = workspace.File("waiter.counter");
        string waiterContention =
            workspace.File("waiter.contention");
        string ownerResult = workspace.File("owner.result.json");
        string waiterResult = workspace.File("waiter.result.json");
        using HostProcess owner = StartHost(
            "install",
            "--cache", workspace.CachePath,
            "--archive", archive,
            "--name", "same.package",
            "--version", "1.0.0",
            "--barrier", barrier,
            "--release", release,
            "--counter", ownerCounter,
            "--result", ownerResult);
        await WaitForFileAsync(
            barrier,
            TestContext.Current.CancellationToken,
            owner);
        using HostProcess waiter = StartHost(
            "install",
            "--cache", workspace.CachePath,
            "--archive", archive,
            "--name", "same.package",
            "--version", "1.0.0",
            "--counter", waiterCounter,
            "--contention", waiterContention,
            "--result", waiterResult);

        await WaitForContentAsync(
            waiterContention,
            "retry|1|identity:same.package#1.0.0",
            TestContext.Current.CancellationToken,
            waiter);
        File.WriteAllText(release, "release");

        HostExecution ownerExecution =
            await owner.WaitAsync(TestContext.Current.CancellationToken);
        HostExecution waiterExecution =
            await waiter.WaitAsync(TestContext.Current.CancellationToken);
        ownerExecution.ExitCode.ShouldBe(0, ownerExecution.StandardError);
        waiterExecution.ExitCode.ShouldBe(0, waiterExecution.StandardError);
        HostResult ownerData = ReadResult(ownerResult);
        HostResult waiterData = ReadResult(waiterResult);
        ownerData.BytesRead.ShouldBeGreaterThan(0);
        waiterData.BytesRead.ShouldBe(0);
        ReadCounter(waiterCounter).ShouldBe(0);
    }

    [Fact]
    public async Task SynchronousContentPath_WaitsForCrossProcessIdentityOwner()
    {
        using TestWorkspace workspace = new();
        string archive = workspace.CreateArchive(
            "sync.package",
            "1.0.0");
        string barrier = workspace.File("sync-owner.ready");
        string release = workspace.File("sync-owner.release");
        string contention = workspace.File("sync-reader.contention");
        string contentResult =
            workspace.File("sync-reader.result.json");
        using HostProcess owner = StartHost(
            "install",
            "--cache", workspace.CachePath,
            "--archive", archive,
            "--name", "sync.package",
            "--version", "1.0.0",
            "--barrier", barrier,
            "--release", release);
        await WaitForFileAsync(
            barrier,
            TestContext.Current.CancellationToken,
            owner);
        using HostProcess reader = StartHost(
            "content-path",
            "--cache", workspace.CachePath,
            "--name", "sync.package",
            "--version", "1.0.0",
            "--contention", contention,
            "--result", contentResult);

        await WaitForContentAsync(
            contention,
            "retry|1|identity:sync.package#1.0.0",
            TestContext.Current.CancellationToken,
            reader);
        File.WriteAllText(release, "release");

        HostExecution ownerExecution =
            await owner.WaitAsync(
                TestContext.Current.CancellationToken);
        HostExecution readerExecution =
            await reader.WaitAsync(
                TestContext.Current.CancellationToken);
        ownerExecution.ExitCode.ShouldBe(
            0,
            ownerExecution.StandardOutput +
            ownerExecution.StandardError);
        readerExecution.ExitCode.ShouldBe(
            0,
            readerExecution.StandardOutput +
            readerExecution.StandardError);
        HostResult readerData = ReadResult(contentResult);
        readerData.Success.ShouldBeTrue();
        readerData.ContentPath.ShouldNotBeNull();
        Directory.Exists(readerData.ContentPath!).ShouldBeTrue();
    }

    [Fact]
    public async Task OperationOwner_TryAcquireReturnsNullForCrossProcessOwner()
    {
        using TestWorkspace workspace = new();
        string operationId = Guid.NewGuid().ToString("N");
        string barrier = workspace.File("operation-owner.ready");
        string release = workspace.File("operation-owner.release");
        string contention =
            workspace.File("operation-contender.contention");
        string ownerResult =
            workspace.File("operation-owner.result.json");
        string contenderResult =
            workspace.File("operation-contender.result.json");
        using HostProcess owner = StartHost(
            "hold-operation-owner",
            "--cache", workspace.CachePath,
            "--operation", operationId,
            "--barrier", barrier,
            "--release", release,
            "--result", ownerResult);
        await WaitForFileAsync(
            barrier,
            TestContext.Current.CancellationToken,
            owner);

        HostExecution contender = await RunHostAsync(
            "try-operation-owner",
            "--cache", workspace.CachePath,
            "--operation", operationId,
            "--contention", contention,
            "--result", contenderResult);

        contender.ExitCode.ShouldBe(
            0,
            contender.StandardOutput + contender.StandardError);
        HostResult contenderData = ReadResult(contenderResult);
        contenderData.Success.ShouldBeTrue();
        contenderData.OperationId.ShouldBe(operationId);
        contenderData.LockAcquired.ShouldBe(false);
        contenderData.ProcessLockCount.ShouldBe(0);
        File.Exists(contention).ShouldBeFalse();

        File.WriteAllText(release, "release");
        HostExecution ownerExecution =
            await owner.WaitAsync(
                TestContext.Current.CancellationToken);
        ownerExecution.ExitCode.ShouldBe(
            0,
            ownerExecution.StandardOutput +
            ownerExecution.StandardError);
        HostResult ownerData = ReadResult(ownerResult);
        ownerData.LockAcquired.ShouldBe(true);
        ownerData.ProcessLockCount.ShouldBe(0);
    }

    [Fact]
    public async Task DifferentIdentities_AcquireAndStageConcurrently()
    {
        using TestWorkspace workspace = new();
        string firstArchive = workspace.CreateArchive(
            "first.package",
            "1.0.0");
        string secondArchive = workspace.CreateArchive(
            "second.package",
            "1.0.0");
        string firstBarrier = workspace.File("first.ready");
        string secondBarrier = workspace.File("second.ready");
        string release = workspace.File("both.release");
        using HostProcess first = StartHost(
            "install",
            "--cache", workspace.CachePath,
            "--archive", firstArchive,
            "--name", "first.package",
            "--version", "1.0.0",
            "--barrier", firstBarrier,
            "--release", release);
        using HostProcess second = StartHost(
            "install",
            "--cache", workspace.CachePath,
            "--archive", secondArchive,
            "--name", "second.package",
            "--version", "1.0.0",
            "--barrier", secondBarrier,
            "--release", release);

        await WaitForFilesAsync(
            firstBarrier,
            first,
            secondBarrier,
            second,
            TestContext.Current.CancellationToken);
        File.WriteAllText(release, "release");
        HostExecution[] executions = await Task.WhenAll(
            first.WaitAsync(TestContext.Current.CancellationToken),
            second.WaitAsync(TestContext.Current.CancellationToken));

        executions.ShouldAllBe(
            execution => execution.ExitCode == 0);
        using DiskPackageCache cache = new(workspace.CachePath);
        IReadOnlyList<PackageRecord> records =
            await cache.ListPackagesAsync(
                ct: TestContext.Current.CancellationToken);
        records.Count.ShouldBe(2);
    }

    [Fact]
    public async Task DiscoveryRace_MayStageTwiceButConvergesOnOneIdentity()
    {
        using TestWorkspace workspace = new();
        string archive = workspace.CreateArchive(
            "discovery.package",
            "1.0.0");
        string firstBarrier = workspace.File("first.ready");
        string secondBarrier = workspace.File("second.ready");
        string release = workspace.File("discovery.release");
        string firstResult = workspace.File("first.result.json");
        string secondResult = workspace.File("second.result.json");
        using HostProcess first = StartHost(
            "import",
            "--cache", workspace.CachePath,
            "--archive", archive,
            "--barrier", firstBarrier,
            "--release", release,
            "--result", firstResult);
        using HostProcess second = StartHost(
            "import",
            "--cache", workspace.CachePath,
            "--archive", archive,
            "--barrier", secondBarrier,
            "--release", release,
            "--result", secondResult);

        await WaitForFilesAsync(
            firstBarrier,
            first,
            secondBarrier,
            second,
            TestContext.Current.CancellationToken);
        File.WriteAllText(release, "release");
        HostExecution[] executions = await Task.WhenAll(
            first.WaitAsync(TestContext.Current.CancellationToken),
            second.WaitAsync(TestContext.Current.CancellationToken));

        executions.ShouldAllBe(
            execution => execution.ExitCode == 0);
        ReadResult(firstResult).BytesRead.ShouldBeGreaterThan(0);
        ReadResult(secondResult).BytesRead.ShouldBeGreaterThan(0);
        using DiskPackageCache cache = new(workspace.CachePath);
        (await cache.ListPackagesAsync(
            ct: TestContext.Current.CancellationToken))
            .ShouldHaveSingleItem()
            .Reference.ShouldBe(
                new PackageReference(
                    "discovery.package",
                    "1.0.0"));
    }

    [Fact]
    public async Task CancelledSameIdentityWaiter_DoesNotReadSource()
    {
        using TestWorkspace workspace = new();
        string archive = workspace.CreateArchive(
            "cancel.package",
            "1.0.0");
        string barrier = workspace.File("owner.ready");
        string release = workspace.File("owner.release");
        string waiterResult = workspace.File("waiter.result.json");
        string waiterCounter = workspace.File("waiter.counter");
        using HostProcess owner = StartHost(
            "install",
            "--cache", workspace.CachePath,
            "--archive", archive,
            "--name", "cancel.package",
            "--version", "1.0.0",
            "--barrier", barrier,
            "--release", release);
        await WaitForFileAsync(
            barrier,
            TestContext.Current.CancellationToken,
            owner);
        using HostProcess waiter = StartHost(
            "install",
            "--cache", workspace.CachePath,
            "--archive", archive,
            "--name", "cancel.package",
            "--version", "1.0.0",
            "--cancel-after-ms", "100",
            "--counter", waiterCounter,
            "--result", waiterResult);

        HostExecution waiterExecution =
            await waiter.WaitAsync(TestContext.Current.CancellationToken);
        waiterExecution.ExitCode.ShouldBe(2);
        HostResult waiterData = ReadResult(waiterResult);
        waiterData.Cancelled.ShouldBeTrue();
        waiterData.BytesRead.ShouldBe(0);
        ReadCounter(waiterCounter).ShouldBe(0);
        File.WriteAllText(release, "release");
        (await owner.WaitAsync(
            TestContext.Current.CancellationToken))
            .ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task TerminatedOwner_ReleasesLockAndAbandonedStagingIsRemoved()
    {
        using TestWorkspace workspace = new();
        string archive = workspace.CreateArchive(
            "killed.package",
            "1.0.0");
        string barrier = workspace.File("owner.ready");
        string unreleased = workspace.File("never.release");
        using HostProcess owner = StartHost(
            "install",
            "--cache", workspace.CachePath,
            "--archive", archive,
            "--name", "killed.package",
            "--version", "1.0.0",
            "--barrier", barrier,
            "--release", unreleased);
        await WaitForFileAsync(
            barrier,
            TestContext.Current.CancellationToken,
            owner);
        owner.Kill();
        await owner.WaitForExitAfterKillAsync(
            TestContext.Current.CancellationToken);

        using HostProcess successor = StartHost(
            "install",
            "--cache", workspace.CachePath,
            "--archive", archive,
            "--name", "killed.package",
            "--version", "1.0.0");
        HostExecution execution =
            await successor.WaitAsync(
                TestContext.Current.CancellationToken);

        execution.ExitCode.ShouldBe(0, execution.StandardError);
        string stagingRoot = Path.Combine(
            workspace.CachePath,
            ".fhirpkg",
            "staging");
        if (Directory.Exists(stagingRoot))
        {
            Directory.GetDirectories(stagingRoot)
                .ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task OverwriteAndRemove_AreSerializedByIdentity()
    {
        using TestWorkspace workspace = new();
        string firstArchive = workspace.CreateArchive(
            "conflict.package",
            "1.0.0",
            "generation.txt",
            "first");
        string secondArchive = workspace.CreateArchive(
            "conflict.package",
            "1.0.0",
            "generation.txt",
            "second");
        (await RunHostAsync(
            "install",
            "--cache", workspace.CachePath,
            "--archive", firstArchive,
            "--name", "conflict.package",
            "--version", "1.0.0"))
            .ExitCode.ShouldBe(0);
        string barrier = workspace.File("overwrite.ready");
        string release = workspace.File("overwrite.release");
        string contention = workspace.File("remove.contention");
        using HostProcess overwrite = StartHost(
            "install",
            "--cache", workspace.CachePath,
            "--archive", secondArchive,
            "--name", "conflict.package",
            "--version", "1.0.0",
            "--overwrite",
            "--barrier", barrier,
            "--release", release);
        await WaitForFileAsync(
            barrier,
            TestContext.Current.CancellationToken,
            overwrite);
        using HostProcess remove = StartHost(
            "remove",
            "--cache", workspace.CachePath,
            "--name", "conflict.package",
            "--version", "1.0.0",
            "--contention", contention);
        await WaitForContentAsync(
            contention,
            "retry|1|identity:conflict.package#1.0.0",
            TestContext.Current.CancellationToken,
            remove);

        File.WriteAllText(release, "release");
        (await overwrite.WaitAsync(
            TestContext.Current.CancellationToken))
            .ExitCode.ShouldBe(0);
        (await remove.WaitAsync(
            TestContext.Current.CancellationToken))
            .ExitCode.ShouldBe(0);
        using DiskPackageCache cache = new(workspace.CachePath);
        (await cache.IsInstalledAsync(
            new PackageReference("conflict.package", "1.0.0"),
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task DifferentIdentityCommits_PreserveBothMetadataEntries()
    {
        using TestWorkspace workspace = new();
        string firstArchive = workspace.CreateArchive(
            "metadata.first",
            "1.0.0");
        string secondArchive = workspace.CreateArchive(
            "metadata.second",
            "1.0.0");

        HostExecution[] executions = await Task.WhenAll(
            RunHostAsync(
                "install",
                "--cache", workspace.CachePath,
                "--archive", firstArchive,
                "--name", "metadata.first",
                "--version", "1.0.0"),
            RunHostAsync(
                "install",
                "--cache", workspace.CachePath,
                "--archive", secondArchive,
                "--name", "metadata.second",
                "--version", "1.0.0"));

        executions.ShouldAllBe(
            execution => execution.ExitCode == 0);
        IReadOnlyDictionary<string,
            IReadOnlyDictionary<string, string>> metadata =
            IniParser.ParseFile(
                Path.Combine(
                    workspace.CachePath,
                    "packages.ini"));
        metadata["packages"].Keys.ShouldContain(
            "metadata.first#1.0.0");
        metadata["packages"].Keys.ShouldContain(
            "metadata.second#1.0.0");
    }

    [Fact]
    public async Task CrashAfterPreparedJournal_ClearRecoversSnapshotIdentity()
    {
        using TestWorkspace workspace = new();
        string archive = workspace.CreateArchive(
            "journal.package",
            "1.0.0");
        string progress = workspace.File("fault.progress");
        string neverRelease = workspace.File("fault.release");
        using HostProcess owner = StartHost(
            "install",
            "--cache", workspace.CachePath,
            "--archive", archive,
            "--name", "journal.package",
            "--version", "1.0.0",
            "--progress", progress,
            "--pause-fault", "JournalWritten:Prepared",
            "--release", neverRelease);
        await WaitForContentAsync(
            progress,
            "fault|JournalWritten|Prepared",
            TestContext.Current.CancellationToken,
            owner);
        owner.Kill();
        await owner.WaitForExitAfterKillAsync(
            TestContext.Current.CancellationToken);

        HostExecution clear = await RunHostAsync(
            "clear",
            "--cache", workspace.CachePath);

        clear.ExitCode.ShouldBe(0, clear.StandardError);
        using DiskPackageCache cache = new(workspace.CachePath);
        (await cache.ListPackagesAsync(
            ct: TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
        string transactionDirectory = Path.Combine(
            workspace.CachePath,
            ".fhirpkg",
            "transactions");
        if (Directory.Exists(transactionDirectory))
        {
            Directory.GetFiles(
                transactionDirectory,
                "*.json",
                SearchOption.TopDirectoryOnly)
                .ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task CrashAfterMetadataCommit_WaiterRecoversWinnerWithoutReading()
    {
        using TestWorkspace workspace = new();
        string archive = workspace.CreateArchive(
            "forward.package",
            "1.0.0");
        string progress = workspace.File("forward.progress");
        string neverRelease = workspace.File("forward.release");
        using HostProcess owner = StartHost(
            "install",
            "--cache", workspace.CachePath,
            "--archive", archive,
            "--name", "forward.package",
            "--version", "1.0.0",
            "--progress", progress,
            "--pause-fault",
            "MetadataReplaced:MetadataCommitted",
            "--release", neverRelease);
        await WaitForContentAsync(
            progress,
            "fault|MetadataReplaced|MetadataCommitted",
            TestContext.Current.CancellationToken,
            owner);
        owner.Kill();
        await owner.WaitForExitAfterKillAsync(
            TestContext.Current.CancellationToken);
        string counter = workspace.File("waiter.counter");
        string result = workspace.File("waiter.result.json");

        HostExecution waiter = await RunHostAsync(
            "install",
            "--cache", workspace.CachePath,
            "--archive", archive,
            "--name", "forward.package",
            "--version", "1.0.0",
            "--counter", counter,
            "--result", result);

        waiter.ExitCode.ShouldBe(0, waiter.StandardError);
        ReadResult(result).BytesRead.ShouldBe(0);
        ReadCounter(counter).ShouldBe(0);
    }

    private static HostProcess StartHost(
        string command,
        params string[] arguments)
    {
        string hostPath = GetHostPath();
        ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(hostPath);
        startInfo.ArgumentList.Add(command);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Process process = new()
        {
            StartInfo = startInfo
        };
        process.Start();
        return new HostProcess(process);
    }

    private static async Task<HostExecution> RunHostAsync(
        string command,
        params string[] arguments)
    {
        using HostProcess process = StartHost(
            command,
            arguments);
        return await process.WaitAsync(
                TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
    }

    private static string GetHostPath()
    {
        DirectoryInfo targetFrameworkDirectory =
            new(AppContext.BaseDirectory);
        string configuration =
            targetFrameworkDirectory.Parent?.Name
            ?? "Debug";
        string targetFramework =
            $"net{Environment.Version.Major}.0";
        string testRoot = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                ".."));
        string hostPath = Path.Combine(
            testRoot,
            "FhirPkg.ProcessTestHost",
            "bin",
            configuration,
            targetFramework,
            "FhirPkg.ProcessTestHost.dll");
        File.Exists(hostPath).ShouldBeTrue(
            $"Process test host was not built at '{hostPath}'.");
        return hostPath;
    }

    private static async Task WaitForFileAsync(
        string path,
        CancellationToken cancellationToken,
        HostProcess process)
    {
        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeoutSource.CancelAfter(s_timeout);
        while (!File.Exists(path))
        {
            if (process.HasExited)
            {
                HostExecution execution =
                    await process.WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                throw new InvalidOperationException(
                    "The process host exited before creating marker " +
                    $"'{path}'. Standard output: " +
                    $"{execution.StandardOutput} Standard error: " +
                    execution.StandardError);
            }

            await Task.Delay(
                    TimeSpan.FromMilliseconds(20),
                    timeoutSource.Token)
                .ConfigureAwait(false);
        }
    }

    private static async Task WaitForFilesAsync(
        string firstPath,
        HostProcess firstProcess,
        string secondPath,
        HostProcess secondProcess,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource siblingCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        Task firstWait = WaitForFileAsync(
            firstPath,
            siblingCancellation.Token,
            firstProcess);
        Task secondWait = WaitForFileAsync(
            secondPath,
            siblingCancellation.Token,
            secondProcess);
        Task completed = await Task.WhenAny(
                firstWait,
                secondWait)
            .ConfigureAwait(false);
        if (!completed.IsCompletedSuccessfully)
            siblingCancellation.Cancel();

        await Task.WhenAll(firstWait, secondWait)
            .ConfigureAwait(false);
    }

    private static async Task WaitForContentAsync(
        string path,
        string expected,
        CancellationToken cancellationToken,
        HostProcess? process = null)
    {
        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeoutSource.CancelAfter(s_timeout);
        while (true)
        {
            if (process?.HasExited == true)
            {
                HostExecution execution =
                    await process.WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                throw new InvalidOperationException(
                    "The process host exited before reaching the expected " +
                    $"marker '{expected}'. Standard output: " +
                    $"{execution.StandardOutput} Standard error: " +
                    execution.StandardError);
            }

            if (File.Exists(path))
            {
                string content = await ReadSharedTextAsync(
                        path,
                        timeoutSource.Token)
                    .ConfigureAwait(false);
                if (content.Contains(
                    expected,
                    StringComparison.Ordinal))
                {
                    return;
                }
            }

            await Task.Delay(
                    TimeSpan.FromMilliseconds(20),
                    timeoutSource.Token)
                .ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadSharedTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(stream);
        return await reader.ReadToEndAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static HostResult ReadResult(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<HostResult>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })
            ?? throw new InvalidDataException(
                "The process host result was empty.");
    }

    private static long ReadCounter(string path)
    {
        if (!File.Exists(path))
            return 0;

        return long.Parse(
            File.ReadAllText(path),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class TestWorkspace : IDisposable
    {
        internal TestWorkspace()
        {
            RootPath = Path.Combine(
                AppContext.BaseDirectory,
                "phase5-process-integration",
                Guid.NewGuid().ToString("N"));
            CachePath = Path.Combine(RootPath, "cache");
            Directory.CreateDirectory(CachePath);
        }

        internal string RootPath { get; }
        internal string CachePath { get; }

        internal string File(string name) =>
            Path.Combine(RootPath, name);

        internal string CreateArchive(
            string name,
            string version,
            string? extraName = null,
            string? extraContent = null)
        {
            string archivePath = File(
                $"{name}-{Guid.NewGuid():N}.tgz");
            using FileStream file = new(
                archivePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            using GZipStream gzip = new(
                file,
                CompressionLevel.Fastest,
                leaveOpen: false);
            using TarWriter writer = new(
                gzip,
                TarEntryFormat.Pax,
                leaveOpen: false);
            writer.WriteEntry(new PaxTarEntry(
                TarEntryType.Directory,
                "package/"));
            WriteTarEntry(
                writer,
                "package/package.json",
                $$"""{"name":"{{name}}","version":"{{version}}"}""");
            if (extraName is not null)
            {
                WriteTarEntry(
                    writer,
                    $"package/{extraName}",
                    extraContent ?? string.Empty);
            }

            return archivePath;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }

        private static void WriteTarEntry(
            TarWriter writer,
            string path,
            string content)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            writer.WriteEntry(new PaxTarEntry(
                TarEntryType.RegularFile,
                path)
            {
                DataStream = new MemoryStream(bytes)
            });
        }
    }

    private sealed class HostProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _standardOutput;
        private readonly Task<string> _standardError;

        internal HostProcess(Process process)
        {
            _process = process;
            _standardOutput =
                process.StandardOutput.ReadToEndAsync();
            _standardError =
                process.StandardError.ReadToEndAsync();
        }

        internal bool HasExited => _process.HasExited;

        internal void Kill()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }

        internal async Task WaitForExitAfterKillAsync(
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            timeoutSource.CancelAfter(s_timeout);
            await _process.WaitForExitAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            await Task.WhenAll(
                    _standardOutput,
                    _standardError)
                .ConfigureAwait(false);
        }

        internal async Task<HostExecution> WaitAsync(
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            timeoutSource.CancelAfter(s_timeout);
            await _process.WaitForExitAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            string[] output = await Task.WhenAll(
                    _standardOutput,
                    _standardError)
                .ConfigureAwait(false);
            return new HostExecution(
                _process.ExitCode,
                output[0],
                output[1]);
        }

        public void Dispose()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);

            _process.Dispose();
        }
    }

    private sealed record HostExecution(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed record HostResult
    {
        public bool Success { get; init; }
        public bool Cancelled { get; init; }
        public long BytesRead { get; init; }
        public string? ContentPath { get; init; }
        public string? ErrorDetail { get; init; }
        public string? OperationId { get; init; }
        public bool? LockAcquired { get; init; }
        public int? ProcessLockCount { get; init; }
    }
}
