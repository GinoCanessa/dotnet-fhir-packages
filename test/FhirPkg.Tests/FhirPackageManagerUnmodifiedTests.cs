// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Formats.Tar;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
using FhirPkg.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;
using RecordingFileOperations = FhirPkg.Tests.Cache.DiskPackageCacheIndexTests.RecordingFileOperations;

namespace FhirPkg.Tests;

public sealed class FhirPackageManagerUnmodifiedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        $"fhir-manager-unmodified-{Guid.NewGuid():N}");

    private string CachePath => Path.Combine(_root, "cache");

    [Fact]
    public async Task Unmodified_RetryDoesNotIntroducePayloadOrIndexWrites()
    {
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_root);
        FileSystemContext context = files.CreateContext(retries: 1);
        using FhirPackageManager manager = FhirPackageManager.CreateResolved(
            FhirPackageManagerConfiguration.CreateResolved(
                new FhirPackageManagerOptions
                {
                    CachePath = CachePath,
                    Unmodified = true,
                    IncludeCiBuilds = false,
                    IncludeHl7WebsiteFallback = false,
                },
                context));
        Dictionary<string, string> payload = new()
        {
            ["package.json"] = """{"name":"unmodified.retry","version":"1.0.0"}""",
            ["patient.json"] = """{"resourceType":"Patient","id":"retained"}""",
            [".index.json"] = "invalid distributed index: preserve these bytes",
            ["other/payload.txt"] = "distributed sibling",
        };
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            payload.Select(pair => ArbitraryTarBuilder.File(pair.Key, pair.Value)).ToArray());
        files.Enqueue("WriteAtAsync", ScriptedFileSystemPrimitives.NativeFailure<bool>(1117, "spool write"));
        PackageRecord record = await manager.ImportAsync(
            archive, options: null, TestContext.Current.CancellationToken);
        record.ContentPath.ShouldBe(record.DirectoryPath);
        int writes = files.Count("WriteAtAsync");
        string resourcePath = Path.Combine(record.ContentPath, "patient.json");
        bool failed = false;
        files.Set<int>("ReadAtAsync", call =>
        {
            if (call.Path == resourcePath && !failed)
            {
                failed = true;
                return ScriptedFileSystemPrimitives.NativeFailure<int>(5, "unmodified index read", call.Path);
            }
            return SystemFileSystemPrimitives.Instance.ReadAt(
                call.Handle!, call.Buffer, call.Offset!.Value, call.Path!);
        });

        PackageIndex? index = await manager.IndexPackageAsync(
            record.Reference, new IndexingOptions { ForceReindex = true }, TestContext.Current.CancellationToken);
        IReadOnlyList<ResourceInfo> resources = await manager.FindByResourceTypeAsync(
            "Patient", record.Reference.FhirDirective, TestContext.Current.CancellationToken);

        index!.Files.Single().Id.ShouldBe("retained");
        resources.ShouldHaveSingleItem().Id.ShouldBe("retained");
        files.Count("WriteAtAsync").ShouldBe(writes);
        files.AsyncWaits.ShouldBe([10, 10]);
        files.Waits.ShouldBeEmpty();
        Directory.GetFiles(record.DirectoryPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(record.DirectoryPath, path).Replace('\\', '/'))
            .ShouldBe(payload.Keys, ignoreOrder: true);
        foreach ((string relativePath, string content) in payload)
        {
            (await File.ReadAllTextAsync(
                Path.Combine(record.DirectoryPath, relativePath), TestContext.Current.CancellationToken))
                .ShouldBe(content);
        }
        files.Calls.ShouldNotContain(call => (call.Operation == "WriteAt" || call.Operation == "WriteAtAsync")
            && call.Path!.StartsWith(record.DirectoryPath + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    public static TheoryData<bool, bool, string> SourceModes
    {
        get
        {
            TheoryData<bool, bool, string> cases = [];
            foreach (bool unmodified in new[] { false, true })
            {
                foreach (bool rootLayout in new[] { false, true })
                {
                    foreach (string source in new[]
                    {
                        "directive", "expected-stream", "discovered-stream",
                        "expected-uri", "discovered-uri", "local-archive", "batch",
                    })
                    {
                        cases.Add(unmodified, rootLayout, source);
                    }
                }
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(SourceModes))]
    public async Task AllSourceModes_ApplySnapshotToRootAndDependencies(
        bool unmodified,
        bool rootLayout,
        string source)
    {
        PackageFixture child = CreateFixture("child.package", "1.0.0", !rootLayout);
        PackageFixture root = CreateFixture(
            "root.package",
            "1.0.0",
            rootLayout,
            new Dictionary<string, string> { [child.Reference.Name] = "1.0.0" });
        PackageFixture batchSibling = CreateFixture("batch.package", "1.0.0", rootLayout);
        FixtureRegistry registry = new(root, child, batchSibling);
        RecordingFileOperations fileOperations = new();
        using DiskPackageCache cache = CreateCache(fileOperations);
        FixtureHttpHandler handler = new(root.ArchiveBytes);
        using HttpClient httpClient = new(handler);
        FhirPackageManagerOptions configured = new() { Unmodified = unmodified };
        BufferedExceptionLogger logger = new();
        using FhirPackageManager manager = CreateManager(cache, registry, configured, httpClient, logger);
        configured.Unmodified = !unmodified;
        PackageSourceInstallOptions options = new()
        {
            IncludeDependencies = true,
            ExpectedSha256 = Convert.ToHexString(SHA256.HashData(root.ArchiveBytes)),
        };
        using TrackingStream stream = new([0x10, 0x20, .. root.ArchiveBytes]);
        stream.Position = 2;
        Uri uri = new("https://source.example.test/package.tgz");
        PackageRecord? installed;
        try
        {
            switch (source)
            {
                case "directive":
                    installed = await manager.InstallAsync(
                        root.Reference.FhirDirective, options, TestContext.Current.CancellationToken);
                    break;
                case "expected-stream":
                    installed = await manager.InstallAsync(
                        root.Reference, stream, options, TestContext.Current.CancellationToken);
                    break;
                case "discovered-stream":
                    installed = await manager.ImportAsync(
                        stream, options, TestContext.Current.CancellationToken);
                    break;
                case "expected-uri":
                    installed = await manager.InstallAsync(
                        root.Reference, uri, options, TestContext.Current.CancellationToken);
                    break;
                case "discovered-uri":
                    installed = await manager.ImportAsync(
                        uri, options, TestContext.Current.CancellationToken);
                    break;
                case "local-archive":
                    string archivePath = Path.Combine(_root, "package.tgz");
                    await File.WriteAllBytesAsync(
                        archivePath, root.ArchiveBytes, TestContext.Current.CancellationToken);
                    await using (FileStream localArchive = File.OpenRead(archivePath))
                    {
                        installed = await manager.ImportAsync(
                            localArchive, options, TestContext.Current.CancellationToken);
                        localArchive.CanRead.ShouldBeTrue();
                        localArchive.Position.ShouldBe(localArchive.Length);
                    }
                    break;
                case "batch":
                    IReadOnlyList<PackageInstallResult> results = await manager.InstallManyAsync(
                        [root.Reference.FhirDirective, batchSibling.Reference.FhirDirective],
                        options,
                        TestContext.Current.CancellationToken);
                    if (results.Any(HasInstallationFailure))
                        WriteBatchFailureDiagnostics(results, logger, unmodified, rootLayout, source);
                    results.Count.ShouldBe(2);
                    results.ShouldAllBe(result => result.Status == PackageInstallStatus.Installed);
                    installed = results.Single(result => result.Directive == root.Reference.FhirDirective).Package;
                    await AssertFixtureAsync(cache, batchSibling, unmodified, fileOperations);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown source fixture '{source}'.");
            }
        }
        catch (DependencyInstallationException exception)
        {
            ITestOutputHelper? output = null;
            try
            {
                output = TestContext.Current.TestOutputHelper;
                if (output is not null)
                {
                    output.WriteLine(
                        $"Dependency installation exception in {nameof(AllSourceModes_ApplySnapshotToRootAndDependencies)}: " +
                        $"(unmodified: {unmodified}, rootLayout: {rootLayout}, source: \"{source}\")");
                    output.WriteLine(
                        $"UTC: {DateTimeOffset.UtcNow:O}; ProcessId: {Environment.ProcessId}; " +
                        $"Runtime: {RuntimeInformation.FrameworkDescription}");
                    output.WriteLine($"_root: {_root}; CachePath: {CachePath}");
                    output.WriteLine($"Exception.RootPackage.Reference: {exception.RootPackage.Reference}");
                    output.WriteLine(
                        "Diagnostics are best-effort: an unavailable output helper yields no output; " +
                        "formatting/output failures may leave partial output. Context is not native operation/handle attribution.");
                    output.WriteLine(exception.ToString());
                    WriteExceptionDetails(output, exception, "Exception");
                    output.WriteLine(
                        "Dependency failure snapshots do not retain original child exception objects, stacks or native codes; " +
                        "see buffered original manager exceptions below when available, not reconstructed exceptions. " +
                        "Registry failures retain only sanitized snapshots.");
                    WriteDependencyFailures(output, exception.DependencyFailures, "Exception.DependencyFailures");
                    WriteDependencyResolutionFailures(
                        output, exception.DependencyResolutionFailures, "Exception.DependencyResolutionFailures");
                    logger.WriteExceptions(output);
                }
            }
            catch (Exception)
            {
                // Best-effort diagnostics must not replace the installation exception.
                try
                {
                    output?.WriteLine(
                        "Diagnostic capture failed; output may be partial or absent. " +
                        "The original dependency installation exception is rethrown unchanged.");
                }
                catch (Exception)
                {
                    // The existing output helper may remain unavailable.
                }
            }

            throw;
        }

        installed.ShouldNotBeNull();
        installed.Reference.ShouldBe(root.Reference);
        installed.Index.ShouldNotBeNull();
        installed.Index.Files.Single().Id.ShouldBe(root.Reference.Name);
        stream.DisposeCount.ShouldBe(0);
        stream.CanRead.ShouldBeTrue();
        if (source is "expected-stream" or "discovered-stream")
        {
            stream.ReadCount.ShouldBeGreaterThan(0);
            stream.Position.ShouldBe(stream.Length);
        }
        handler.RequestCount.ShouldBe(source is "expected-uri" or "discovered-uri" ? 1 : 0);
        foreach (TrackingStream response in handler.ResponseStreams)
        {
            response.DisposeCount.ShouldBeGreaterThan(0);
            response.CanRead.ShouldBeFalse();
        }
        registry.DownloadedReferences.Count(reference => reference == child.Reference).ShouldBe(1);
        registry.DownloadedReferences.Count(reference => reference == root.Reference)
            .ShouldBe(source is "directive" or "batch" ? 1 : 0);
        foreach (TrackingStream download in registry.DownloadStreams)
            download.DisposeCount.ShouldBe(1);

        await AssertFixtureAsync(cache, root, unmodified, fileOperations);
        await AssertFixtureAsync(cache, child, unmodified, fileOperations);
        IReadOnlyList<ResourceInfo> resources = await manager.FindResourcesAsync(
            new ResourceSearchCriteria(),
            TestContext.Current.CancellationToken);
        resources.Count.ShouldBe(source == "batch" ? 3 : 2);
        foreach (ResourceInfo resource in resources)
        {
            (await manager.ReadResourceAsync(resource, TestContext.Current.CancellationToken))!["id"]!
                .GetValue<string>().ShouldBe(resource.PackageName);
        }
        if (unmodified)
        {
            fileOperations.AssertNoPayloadWrites(installed.DirectoryPath);
            fileOperations.AtomicDestinations.ShouldNotContain(
                path => Path.GetFileName(path) == ".index.json");
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task DependencyBootstrapClosureAndRestore_InheritMode(
        bool unmodified,
        bool rootLayout,
        bool restore)
    {
        PackageFixture leaf = CreateFixture("leaf.package", "1.0.0", rootLayout);
        PackageFixture ci = CreateFixture(
            "ci.package",
            "2.0.0",
            !rootLayout,
            new Dictionary<string, string> { [leaf.Reference.Name] = "1.0.0" });
        PackageFixture direct = CreateFixture("direct.package", "1.0.0", !rootLayout);
        PackageFixture root = CreateFixture(
            "root.package",
            "1.0.0",
            rootLayout,
            new Dictionary<string, string>
            {
                [ci.Reference.Name] = "current",
                [direct.Reference.Name] = "1.0.0",
            });
        PackageManifest project = new()
        {
            Name = "project.package",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string> { [root.Reference.Name] = "1.0.0" },
        };
        PackageReference alias = new(ci.Reference.Name, "current");
        FixtureRegistry registry = new(root, direct, ci, leaf);
        registry.OpaqueAliases.Add(ci.Reference.Name);
        RecordingFileOperations fileOperations = new();
        using DiskPackageCache cache = CreateCache(fileOperations);
        VersionResolver versionResolver = new(registry, NullLogger.Instance);
        DependencyResolver dependencyResolver = new(registry, versionResolver, cache, NullLogger.Instance);
        PackageClosure before = await dependencyResolver.ResolveAsync(
            restore ? project : root.Manifest,
            cancellationToken: TestContext.Current.CancellationToken);
        before.BootstrapInstallOrder.ShouldContain(alias);
        BufferedExceptionLogger logger = new();
        using FhirPackageManager manager = new(
            cache,
            registry,
            versionResolver,
            dependencyResolver,
            new PackageIndexer(NullLogger<PackageIndexer>.Instance),
            new FhirPackageManagerOptions { Unmodified = unmodified },
            logger);

        if (restore)
        {
            string projectPath = Path.Combine(_root, "project");
            Directory.CreateDirectory(projectPath);
            await File.WriteAllTextAsync(
                Path.Combine(projectPath, "package.json"),
                project.Serialize(),
                TestContext.Current.CancellationToken);

            PackageClosure closure = await manager.RestoreAsync(
                projectPath,
                cancellationToken: TestContext.Current.CancellationToken);

            closure.Failures.ShouldBeEmpty();
            closure.BootstrapInstallOrder.ShouldBeEmpty();
            closure.Resolved[ci.Reference.Name].ShouldBe(ci.Reference);
            Directory.GetFileSystemEntries(projectPath)
                .Select(Path.GetFileName).ShouldBe(["package.json"]);
        }
        else
        {
            PackageRecord? installed;
            try
            {
                installed = await manager.InstallAsync(
                    root.Reference.FhirDirective,
                    new InstallOptions { IncludeDependencies = true },
                    TestContext.Current.CancellationToken);
            }
            catch (DependencyInstallationException exception)
            {
                ITestOutputHelper? output = null;
                try
                {
                    output = TestContext.Current.TestOutputHelper;
                    if (output is not null)
                    {
                        output.WriteLine(
                            $"Dependency installation exception in {nameof(DependencyBootstrapClosureAndRestore_InheritMode)}: " +
                            $"(unmodified: {unmodified}, rootLayout: {rootLayout}, restore: {restore})");
                        output.WriteLine(
                            $"UTC: {DateTimeOffset.UtcNow:O}; ProcessId: {Environment.ProcessId}; " +
                            $"Runtime: {RuntimeInformation.FrameworkDescription}");
                        output.WriteLine($"_root: {_root}; CachePath: {CachePath}");
                        output.WriteLine($"Exception.RootPackage.Reference: {exception.RootPackage.Reference}");
                        output.WriteLine(
                            "Diagnostics are best-effort: an unavailable output helper yields no output; " +
                            "formatting/output failures may leave partial output. UTC is output time, not native-call timing. " +
                            "Context is not native operation/handle attribution.");
                        output.WriteLine(exception.ToString());
                        WriteExceptionDetails(output, exception, "Exception");
                        output.WriteLine(
                            "Dependency failure snapshots do not retain original child exception objects, stacks or native codes. " +
                            "Buffered original manager exceptions may be unavailable; missing details are not reconstructed. " +
                            "Zero logged exceptions is missing evidence, not evidence of successful installation. " +
                            "Registry failures retain only sanitized snapshots.");
                        WriteDependencyFailures(output, exception.DependencyFailures, "Exception.DependencyFailures");
                        WriteDependencyResolutionFailures(
                            output, exception.DependencyResolutionFailures, "Exception.DependencyResolutionFailures");
                        logger.WriteExceptions(output);
                    }
                }
                catch (Exception)
                {
                    // Best-effort diagnostics must not replace the installation exception.
                    try
                    {
                        output?.WriteLine(
                            "Diagnostic capture failed; output may be partial or absent. " +
                            "The original dependency installation exception is rethrown unchanged.");
                    }
                    catch (Exception)
                    {
                        // The existing output helper may remain unavailable.
                    }
                }

                throw;
            }

            installed.ShouldNotBeNull();
        }

        await AssertFixtureAsync(cache, root, unmodified, fileOperations);
        await AssertFixtureAsync(cache, direct, unmodified, fileOperations);
        await AssertFixtureAsync(cache, ci, unmodified, fileOperations, alias);
        await AssertFixtureAsync(cache, leaf, unmodified, fileOperations);
        registry.DownloadedReferences.ShouldBe(
            [root.Reference, direct.Reference, alias, leaf.Reference],
            ignoreOrder: true);
        registry.ResolvedReferences.Count(reference => reference == alias).ShouldBeGreaterThanOrEqualTo(2);
        Directory.Exists(PackageCacheKey.Create(ci.Reference).GetPackageDirectoryPath(CachePath))
            .ShouldBeFalse();
        ResourceInfo resource = (await manager.FindByResourceTypeAsync(
            "Patient", alias.FhirDirective, TestContext.Current.CancellationToken))
            .ShouldHaveSingleItem();
        resource.PackageVersion.ShouldBe("current");
        resource.FilePath.ShouldBe("patient.json");
        (await manager.ReadResourceAsync(resource, TestContext.Current.CancellationToken))!["id"]!
            .GetValue<string>().ShouldBe(ci.Reference.Name);
        if (unmodified)
            fileOperations.AtomicDestinations.ShouldNotContain(
                path => Path.GetFileName(path) == ".index.json");
    }

    public static TheoryData<bool, bool, string> InvalidSources
    {
        get
        {
            TheoryData<bool, bool, string> cases = [];
            foreach (bool unmodified in new[] { false, true })
            {
                foreach (bool discover in new[] { false, true })
                {
                    foreach (string failure in new[] { "manifest", "identity", "sha256", "sha1", "cancel" })
                        cases.Add(unmodified, discover, failure);
                }
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidSources))]
    public async Task InvalidSourceOrCancellation_PreservesPriorGenerationAndCallerStream(
        bool unmodified,
        bool discover,
        string failure)
    {
        PackageFixture fixture = CreateFixture("validated.package", "1.0.0", rootLayout: true);
        FixtureRegistry registry = new(fixture);
        RecordingFileOperations fileOperations = new();
        using DiskPackageCache cache = CreateCache(fileOperations);
        using FhirPackageManager manager = CreateManager(
            cache, registry, new FhirPackageManagerOptions { Unmodified = unmodified });
        using MemoryStream initial = new(fixture.ArchiveBytes);
        PackageRecord installed = await manager.ImportAsync(
            initial, options: null, TestContext.Current.CancellationToken);
        Dictionary<string, byte[]> before = ReadPayload(installed.DirectoryPath);
        string[] directoriesBefore = Directory.GetDirectories(
            installed.DirectoryPath, "*", SearchOption.AllDirectories);
        ArbitraryTarEntry[] replacementEntries = fixture.Entries.Select(entry =>
            entry.Name == "package.json" && failure is "manifest" or "identity"
                ? ArbitraryTarBuilder.File(
                    entry.Name,
                    failure == "manifest"
                        ? "{ malformed manifest"
                        : """{"name":"validated.package","version":"current"}""")
                : entry).ToArray();
        using MemoryStream replacement = ArbitraryTarBuilder.Create(replacementEntries);
        using TrackingStream source = new(replacement.ToArray());
        using CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        PackageSourceInstallOptions options = new()
        {
            OverwriteExisting = true,
            ExpectedSha256 = failure == "sha256" ? new string('0', 64) : null,
            ExpectedSha1 = failure == "sha1" ? new string('0', 40) : null,
            Progress = new InlineProgress(progress =>
            {
                if (failure == "cancel" && progress.Phase == PackageProgressPhase.Validating)
                    cancellation.Cancel();
            }),
        };
        fileOperations.Clear();

        Task<PackageRecord> installation = discover
            ? manager.ImportAsync(source, options, cancellation.Token)
            : manager.InstallAsync(fixture.Reference, source, options, cancellation.Token);
        if (failure == "cancel")
        {
            await Should.ThrowAsync<OperationCanceledException>(() => installation);
            cancellation.IsCancellationRequested.ShouldBeTrue();
        }
        else
        {
            PackageInstallException exception =
                await Should.ThrowAsync<PackageInstallException>(() => installation);
            exception.ErrorCode.ShouldBe(failure switch
            {
                "manifest" => PackageInstallErrorCode.InvalidArchive,
                "identity" => PackageInstallErrorCode.InvalidPackageIdentity,
                _ => PackageInstallErrorCode.ChecksumMismatch,
            });
            exception.Stage.ShouldBe(failure switch
            {
                "manifest" => PackageInstallStage.ArchiveValidation,
                "identity" => PackageInstallStage.IdentityValidation,
                _ => PackageInstallStage.ChecksumValidation,
            });
        }

        source.DisposeCount.ShouldBe(0);
        source.CanRead.ShouldBeTrue();
        source.ReadCount.ShouldBeGreaterThan(0);
        AssertSamePayload(before, installed.DirectoryPath);
        Directory.GetDirectories(installed.DirectoryPath, "*", SearchOption.AllDirectories)
            .ShouldBe(directoriesBefore, ignoreOrder: true);
        fileOperations.AssertNoPayloadWrites(installed.DirectoryPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unmodified_ExistingCacheHit_DoesNotClaimReconstruction(bool rootLayout)
    {
        PackageFixture fixture = CreateFixture("cached.package", "1.0.0", rootLayout);
        FixtureRegistry registry = new(fixture);
        RecordingFileOperations fileOperations = new();
        using DiskPackageCache cache = CreateCache(fileOperations);
        PackageRecord original;
        using (FhirPackageManager ordinary = CreateManager(cache, registry, new FhirPackageManagerOptions()))
        using (MemoryStream source = new(fixture.ArchiveBytes))
        {
            original = await ordinary.ImportAsync(
                source, options: null, TestContext.Current.CancellationToken);
        }
        Dictionary<string, byte[]> before = ReadPayload(original.DirectoryPath);
        using FhirPackageManager manager = CreateManager(
            cache, registry, new FhirPackageManagerOptions { Unmodified = true });
        using TrackingStream unreadSource = new([0x10, 0x20, 0x30]);
        fileOperations.Clear();

        PackageRecord? directiveHit = await manager.InstallAsync(
            fixture.Reference.FhirDirective,
            cancellationToken: TestContext.Current.CancellationToken);
        PackageRecord streamHit = await manager.InstallAsync(
            fixture.Reference,
            unreadSource,
            new PackageSourceInstallOptions { ExpectedSha256 = new string('0', 64) },
            TestContext.Current.CancellationToken);

        directiveHit!.ContentPath.ShouldBe(original.ContentPath);
        streamHit.ContentPath.ShouldBe(original.ContentPath);
        unreadSource.ReadCount.ShouldBe(0);
        unreadSource.DisposeCount.ShouldBe(0);
        registry.ResolvedReferences.ShouldBeEmpty();
        registry.DownloadedReferences.ShouldBeEmpty();
        AssertSamePayload(before, original.DirectoryPath);
        fileOperations.AssertNoPayloadWrites(original.DirectoryPath);

        using TrackingStream replacement = new(fixture.ArchiveBytes);
        PackageRecord reconstructed = await manager.InstallAsync(
            fixture.Reference,
            replacement,
            new PackageSourceInstallOptions { OverwriteExisting = true },
            TestContext.Current.CancellationToken);

        reconstructed.ContentGeneration.ShouldNotBe(original.ContentGeneration);
        replacement.ReadCount.ShouldBeGreaterThan(0);
        replacement.DisposeCount.ShouldBe(0);
        fileOperations.Clear();
        await AssertFixtureAsync(cache, fixture, unmodified: true, fileOperations);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("internal")]
    [InlineData("http")]
    public async Task Unmodified_UnsupportedCache_FailsBeforeSourceAccess(string constructor)
    {
        UnsupportedCache cache = new();
        FixtureRegistry registry = new();
        FixtureHttpHandler handler = new([0x10]);
        using HttpClient httpClient = new(handler);
        using TrackingStream source = new([0x10]);

        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(async () =>
        {
            using FhirPackageManager manager = CreateInjectedManager(
                constructor,
                cache,
                registry,
                new PackageIndexer(NullLogger<PackageIndexer>.Instance),
                httpClient);
            await manager.ImportAsync(source, options: null, TestContext.Current.CancellationToken);
        });

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.UnsupportedCacheCapability);
        exception.Stage.ShouldBe(PackageInstallStage.PolicyValidation);
        exception.Message.ShouldContain("Unmodified");
        cache.CallbackCount.ShouldBe(0);
        source.ReadCount.ShouldBe(0);
        source.DisposeCount.ShouldBe(0);
        handler.RequestCount.ShouldBe(0);
        registry.ResolvedReferences.ShouldBeEmpty();
        Directory.Exists(CachePath).ShouldBeFalse();
    }

    [Theory]
    [InlineData("public")]
    [InlineData("internal")]
    [InlineData("http")]
    public async Task Unmodified_UnsupportedIndexer_FailsBeforeCallbackOrSubscription(string constructor)
    {
        using SubscriptionTrackingCache cache = new(CachePath);
        Mock<IPackageIndexer> indexer = new(MockBehavior.Strict);
        FixtureRegistry registry = new();
        FixtureHttpHandler handler = new([0x10]);
        using HttpClient httpClient = new(handler);
        using TrackingStream source = new([0x10]);

        NotSupportedException exception = await Should.ThrowAsync<NotSupportedException>(async () =>
        {
            using FhirPackageManager manager = CreateInjectedManager(
                constructor, cache, registry, indexer.Object, httpClient);
            await manager.ImportAsync(source, options: null, TestContext.Current.CancellationToken);
        });

        exception.Message.ShouldContain("read-only managed package index builder");
        cache.SubscriptionCount.ShouldBe(0);
        indexer.Invocations.ShouldBeEmpty();
        source.ReadCount.ShouldBe(0);
        source.DisposeCount.ShouldBe(0);
        handler.RequestCount.ShouldBe(0);
        registry.ResolvedReferences.ShouldBeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static bool HasInstallationFailure(PackageInstallResult result) =>
        result.Status != PackageInstallStatus.Installed
        || result.DependencyFailures.Any(HasInstallationFailure);

    private void WriteBatchFailureDiagnostics(
        IReadOnlyList<PackageInstallResult> results,
        BufferedExceptionLogger logger,
        bool unmodified,
        bool rootLayout,
        string source)
    {
        ITestOutputHelper? output = null;
        try
        {
            output = TestContext.Current.TestOutputHelper;
            if (output is not null)
            {
                output.WriteLine(
                    $"Returned batch failure in {nameof(AllSourceModes_ApplySnapshotToRootAndDependencies)}: " +
                    $"(unmodified: {unmodified}, rootLayout: {rootLayout}, source: \"{source}\")");
                output.WriteLine(
                    $"UTC: {DateTimeOffset.UtcNow:O}; ProcessId: {Environment.ProcessId}; " +
                    $"Runtime: {RuntimeInformation.FrameworkDescription}");
                output.WriteLine($"_root: {_root}; CachePath: {CachePath}");
                output.WriteLine(
                    "Results are printed in returned order, including recursive dependency snapshots. " +
                    "Original exceptions are buffered separately from manager logging; missing details are not reconstructed. " +
                    "Diagnostics are best-effort and do not identify an obstructing handle's owner.");
                WriteDependencyFailures(output, results, "Results");
                logger.WriteExceptions(output);
            }
        }
        catch (Exception)
        {
            // Best-effort diagnostics must not replace the existing success assertion.
            try
            {
                output?.WriteLine(
                    "Batch diagnostic capture failed; output may be partial or absent. " +
                    "The original returned results will still be asserted.");
            }
            catch (Exception)
            {
                // The existing output helper may remain unavailable.
            }
        }
    }

    private static void WriteExceptionDetails(
        ITestOutputHelper output,
        Exception exception,
        string path)
    {
        output.WriteLine(
            $"{path}: {exception.GetType().FullName}; HResult: {exception.HResult} (0x{exception.HResult:X8})");
        if (exception is PackageInstallException installException)
        {
            output.WriteLine(
                $"{path}: ErrorCode: {installException.ErrorCode}; Stage: {installException.Stage}; " +
                $"Directive: {FormatDiagnosticValue(installException.Directive)}");
        }
        if (exception is Win32Exception win32Exception)
        {
            output.WriteLine($"{path}: NativeErrorCode: {win32Exception.NativeErrorCode}");
        }
        foreach (System.Collections.DictionaryEntry entry in exception.Data)
            output.WriteLine($"{path}.Data[{entry.Key}]: {entry.Value ?? "<null>"}");

        if (exception is AggregateException aggregateException)
        {
            for (int index = 0; index < aggregateException.InnerExceptions.Count; index++)
            {
                WriteExceptionDetails(
                    output,
                    aggregateException.InnerExceptions[index],
                    $"{path}.InnerExceptions[{index}]");
            }
        }
        else if (exception.InnerException is Exception innerException)
        {
            WriteExceptionDetails(output, innerException, $"{path}.InnerException");
        }
    }

    private static void WriteDependencyFailures(
        ITestOutputHelper output,
        IReadOnlyList<PackageInstallResult> failures,
        string path)
    {
        output.WriteLine($"{path}.Count: {failures.Count}");
        for (int index = 0; index < failures.Count; index++)
        {
            PackageInstallResult failure = failures[index];
            string entryPath = $"{path}[{index}]";
            output.WriteLine(
                $"{entryPath}: Directive: {FormatDiagnosticValue(failure.Directive)}; Status: {failure.Status}; " +
                $"ErrorCode: {failure.ErrorCode?.ToString() ?? "<null>"}; " +
                $"ErrorStage: {failure.ErrorStage?.ToString() ?? "<null>"}; " +
                $"ErrorMessage: {FormatDiagnosticValue(failure.ErrorMessage)}");
            WriteDependencyFailures(output, failure.DependencyFailures, $"{entryPath}.DependencyFailures");
        }
    }

    private static void WriteDependencyResolutionFailures(
        ITestOutputHelper output,
        IReadOnlyList<DependencyResolutionFailure> failures,
        string path)
    {
        output.WriteLine($"{path}.Count: {failures.Count}");
        for (int index = 0; index < failures.Count; index++)
        {
            DependencyResolutionFailure failure = failures[index];
            string entryPath = $"{path}[{index}]";
            output.WriteLine(
                $"{entryPath}: Code: {failure.Code}; PackageId: {FormatDiagnosticValue(failure.PackageId)}; " +
                $"Message: {FormatDiagnosticValue(failure.Message)}; " +
                $"VersionSpecifier: {FormatDiagnosticValue(failure.VersionSpecifier)}; " +
                $"SelectedVersion: {FormatDiagnosticValue(failure.SelectedVersion)}; " +
                $"ParentPackageId: {FormatDiagnosticValue(failure.ParentPackageId)}; " +
                $"ParentVersion: {FormatDiagnosticValue(failure.ParentVersion)}; " +
                $"Depth: {failure.Depth?.ToString() ?? "<null>"}; MaxDepth: {failure.MaxDepth?.ToString() ?? "<null>"}");
            output.WriteLine($"{entryPath}.RequestedVersions.Count: {failure.RequestedVersions.Count}");
            for (int versionIndex = 0; versionIndex < failure.RequestedVersions.Count; versionIndex++)
            {
                output.WriteLine(
                    $"{entryPath}.RequestedVersions[{versionIndex}]: " +
                    FormatDiagnosticValue(failure.RequestedVersions[versionIndex]));
            }
            output.WriteLine($"{entryPath}.RegistryFailures.Count: {failure.RegistryFailures.Count}");
            for (int registryIndex = 0; registryIndex < failure.RegistryFailures.Count; registryIndex++)
            {
                RegistryAttemptFailure registryFailure = failure.RegistryFailures[registryIndex];
                output.WriteLine(
                    $"{entryPath}.RegistryFailures[{registryIndex}]: " +
                    $"EndpointOrigin: {FormatDiagnosticValue(registryFailure.EndpointOrigin)}; " +
                    $"Category: {registryFailure.Category}; Message: {FormatDiagnosticValue(registryFailure.Message)}");
            }
        }
    }

    private static string FormatDiagnosticValue(string? value) =>
        value is null ? "<null>" : $"\"{value}\"";

    private DiskPackageCache CreateCache(RecordingFileOperations fileOperations) =>
        new(
            CachePath,
            logger: null,
            timeProvider: null,
            new PackageInstallLimits(),
            fileOperations,
            NullPackageCacheFaultObserver.Instance);

    private static FhirPackageManager CreateManager(
        DiskPackageCache cache,
        FixtureRegistry registry,
        FhirPackageManagerOptions options,
        HttpClient? httpClient = null,
        ILogger<FhirPackageManager>? logger = null)
    {
        VersionResolver versionResolver = new(registry, NullLogger.Instance);
        DependencyResolver dependencyResolver = new(registry, versionResolver, cache, NullLogger.Instance);
        PackageIndexer indexer = new(NullLogger<PackageIndexer>.Instance);
        logger ??= NullLogger<FhirPackageManager>.Instance;
        return httpClient is null
            ? new FhirPackageManager(
                cache, registry, versionResolver, dependencyResolver, indexer,
                options, logger, new MemoryResourceCache(10))
            : FhirPackageManager.CreateWithHttpClient(
                cache, registry, versionResolver, dependencyResolver, indexer,
                options, logger, new MemoryResourceCache(10),
                PackageInstallLimits.ResolveManager(options.InstallLimits), httpClient);
    }

    private sealed record LoggedException(
        DateTimeOffset Timestamp,
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception Exception);

    private sealed class BufferedExceptionLogger : ILogger<FhirPackageManager>
    {
        private readonly ConcurrentQueue<LoggedException> _exceptions = new();
        private int _captureFailures;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is null)
                return;

            try
            {
                string message;
                try
                {
                    message = formatter(state, exception);
                }
                catch (Exception)
                {
                    message = "<log message formatting unavailable>";
                    Interlocked.Increment(ref _captureFailures);
                }

                _exceptions.Enqueue(new LoggedException(
                    DateTimeOffset.UtcNow, logLevel, eventId, message, exception));
            }
            catch (Exception)
            {
                // Logging must not turn a failed install into a different outcome.
                Interlocked.Increment(ref _captureFailures);
            }
        }

        internal void WriteExceptions(ITestOutputHelper output)
        {
            LoggedException[] entries = _exceptions.ToArray();
            output.WriteLine(
                $"Original manager exceptions: {entries.Length}; " +
                $"Diagnostic capture/formatting failures: {Volatile.Read(ref _captureFailures)}");
            for (int index = 0; index < entries.Length; index++)
            {
                LoggedException entry = entries[index];
                string path = $"ManagerExceptions[{index}]";
                output.WriteLine(
                    $"{path}: UTC: {entry.Timestamp:O}; Level: {entry.Level}; " +
                    $"EventId: {entry.EventId}; Message: {entry.Message}");
                output.WriteLine(entry.Exception.ToString());
                WriteExceptionDetails(output, entry.Exception, path);
                if (entry.Exception is DependencyInstallationException dependencyException)
                {
                    WriteDependencyFailures(
                        output, dependencyException.DependencyFailures, $"{path}.DependencyFailures");
                    WriteDependencyResolutionFailures(
                        output, dependencyException.DependencyResolutionFailures, $"{path}.DependencyResolutionFailures");
                }
            }
        }
    }

    private static FhirPackageManager CreateInjectedManager(
        string constructor,
        IPackageCache cache,
        IRegistryClient registry,
        IPackageIndexer indexer,
        HttpClient httpClient)
    {
        FhirPackageManagerOptions options = new() { Unmodified = true };
        VersionResolver versionResolver = new(registry, NullLogger.Instance);
        DependencyResolver dependencyResolver = new(registry, versionResolver, cache, NullLogger.Instance);
        PackageInstallLimits limits = PackageInstallLimits.ResolveManager(options.InstallLimits);
        return constructor switch
        {
            "public" => new FhirPackageManager(
                cache, registry, versionResolver, dependencyResolver, indexer, options,
                NullLogger<FhirPackageManager>.Instance),
            "internal" => new FhirPackageManager(
                cache, registry, versionResolver, dependencyResolver, indexer, options,
                NullLogger<FhirPackageManager>.Instance, memoryCache: null, limits),
            "http" => FhirPackageManager.CreateWithHttpClient(
                cache, registry, versionResolver, dependencyResolver, indexer, options,
                NullLogger<FhirPackageManager>.Instance, memoryCache: null, limits, httpClient),
            _ => throw new InvalidOperationException(),
        };
    }

    private static PackageFixture CreateFixture(
        string name,
        string version,
        bool rootLayout,
        IReadOnlyDictionary<string, string>? dependencies = null)
    {
        PackageReference reference = new(name, version);
        PackageManifest manifest = new()
        {
            Name = name,
            Version = version,
            Dependencies = dependencies ?? new Dictionary<string, string>(),
        };
        string prefix = rootLayout ? string.Empty : "package/";
        List<ArbitraryTarEntry> entries =
        [
            ArbitraryTarBuilder.File($"{prefix}package.json", manifest.Serialize()),
            ArbitraryTarBuilder.File(
                $"{prefix}patient.json",
                $$"""{"resourceType":"Patient","id":"{{name}}","url":"https://example.test/{{name}}"}"""),
            ArbitraryTarBuilder.File("openapi/spec.json", "opaque specification"),
            ArbitraryTarBuilder.File("other/package.json", "{ opaque manifest"),
            ArbitraryTarBuilder.File("other/.index.json", "opaque index"),
            ArbitraryTarBuilder.File("packages.ini", "distributed payload, not cache metadata"),
            ArbitraryTarBuilder.File(".fhirpkg/payload.bin", [0x00, 0xff, 0x13]),
            ArbitraryTarBuilder.File("package/readme.txt", "distributed package child"),
            ArbitraryTarBuilder.Directory("empty/"),
        ];
        if (!rootLayout)
            entries.Add(ArbitraryTarBuilder.File(".index.json", "opaque root index"));
        using MemoryStream archive = ArbitraryTarBuilder.Create([.. entries]);
        return new PackageFixture(reference, rootLayout, manifest, entries, archive.ToArray());
    }

    private static async Task AssertFixtureAsync(
        DiskPackageCache cache,
        PackageFixture fixture,
        bool unmodified,
        RecordingFileOperations fileOperations,
        PackageReference? cacheReference = null)
    {
        PackageRecord? record = await cache.GetPackageAsync(
            cacheReference ?? fixture.Reference,
            TestContext.Current.CancellationToken);
        record.ShouldNotBeNull();
        record.Manifest.Name.ShouldBe(fixture.Reference.Name);
        record.Manifest.Version.ShouldBe(fixture.Reference.Version);
        record.ContentPath.ShouldBe(fixture.RootLayout && unmodified
            ? record.DirectoryPath
            : Path.Combine(record.DirectoryPath, "package"));
        string wrapper = fixture.RootLayout && !unmodified ? "package/" : string.Empty;
        Dictionary<string, byte[]> expectedFiles = new(StringComparer.Ordinal);
        HashSet<string> expectedDirectories = new(StringComparer.Ordinal);
        foreach (ArbitraryTarEntry entry in fixture.Entries)
        {
            string relativePath = wrapper + entry.Name.TrimEnd('/');
            string[] segments = relativePath.Split('/');
            int directoryCount = entry.EntryType == TarEntryType.Directory
                ? segments.Length
                : segments.Length - 1;
            for (int count = 1; count <= directoryCount; count++)
                expectedDirectories.Add(string.Join('/', segments.Take(count)));
            if (entry.Content is not null)
                expectedFiles.Add(relativePath, entry.Content);
        }

        Dictionary<string, byte[]> actualFiles = ReadPayload(record.DirectoryPath);
        foreach ((string path, byte[] bytes) in expectedFiles)
            actualFiles[path].ShouldBe(bytes);
        string indexRelativePath = fixture.RootLayout && unmodified ? ".index.json" : "package/.index.json";
        actualFiles.Keys.ShouldBe(unmodified
                ? expectedFiles.Keys
                : expectedFiles.Keys.Append(indexRelativePath),
            ignoreOrder: true);
        Directory.GetDirectories(record.DirectoryPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(record.DirectoryPath, path).Replace('\\', '/'))
            .ShouldBe(expectedDirectories, ignoreOrder: true);
        if (unmodified)
        {
            record.Index.ShouldBeNull();
            fileOperations.AssertNoPayloadWrites(record.DirectoryPath);
        }
        else
        {
            record.Index.ShouldNotBeNull();
            record.Index.Files.Single().Id.ShouldBe(fixture.Reference.Name);
        }
    }

    private static Dictionary<string, byte[]> ReadPayload(string directoryPath) =>
        Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(directoryPath, path).Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);

    private static void AssertSamePayload(Dictionary<string, byte[]> expected, string directoryPath)
    {
        Dictionary<string, byte[]> actual = ReadPayload(directoryPath);
        actual.Keys.ShouldBe(expected.Keys, ignoreOrder: true);
        foreach ((string path, byte[] bytes) in expected)
            actual[path].ShouldBe(bytes);
    }

    private sealed record PackageFixture(
        PackageReference Reference,
        bool RootLayout,
        PackageManifest Manifest,
        IReadOnlyList<ArbitraryTarEntry> Entries,
        byte[] ArchiveBytes);

    private sealed class FixtureRegistry(params PackageFixture[] packages) : IRegistryClient
    {
        private readonly Dictionary<string, PackageFixture> _packages =
            packages.ToDictionary(package => package.Reference.Name, StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<PackageReference> _resolved = new();
        private readonly ConcurrentQueue<PackageReference> _downloaded = new();
        private readonly ConcurrentQueue<TrackingStream> _downloadStreams = new();

        internal HashSet<string> OpaqueAliases { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal IReadOnlyList<PackageReference> ResolvedReferences => [.. _resolved];
        internal IReadOnlyList<PackageReference> DownloadedReferences => [.. _downloaded];
        internal IReadOnlyList<TrackingStream> DownloadStreams => [.. _downloadStreams];

        public RegistryEndpoint Endpoint { get; } = new()
        {
            Url = "https://registry.example.test/",
            Type = RegistryType.FhirNpm,
        };
        public IReadOnlyList<PackageNameType> SupportedNameTypes => [];
        public IReadOnlyList<VersionType> SupportedVersionTypes => [];

        public Task<PackageListing?> GetPackageListingAsync(
            string packageId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_packages.TryGetValue(packageId, out PackageFixture? package))
                return Task.FromResult<PackageListing?>(null);
            PackageVersionInfo version = new()
            {
                Name = package.Reference.Name,
                Version = package.Reference.Version!,
                Dependencies = package.Manifest.Dependencies,
            };
            return Task.FromResult<PackageListing?>(new PackageListing
            {
                PackageId = package.Reference.Name,
                Versions = new Dictionary<string, PackageVersionInfo> { [version.Version] = version },
            });
        }

        public Task<ResolvedDirective?> ResolveAsync(
            PackageDirective directive,
            VersionResolveOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _resolved.Enqueue(directive.ToReference());
            if (!_packages.TryGetValue(directive.PackageId, out PackageFixture? package))
                return Task.FromResult<ResolvedDirective?>(null);
            bool opaque = OpaqueAliases.Contains(directive.PackageId);
            return Task.FromResult<ResolvedDirective?>(new ResolvedDirective
            {
                Reference = opaque ? directive.ToReference() : package.Reference,
                TarballUri = new Uri($"https://registry.example.test/{directive.PackageId}.tgz"),
                Sha256Sum = Convert.ToHexString(SHA256.HashData(package.ArchiveBytes)),
                Dependencies = opaque ? null : package.Manifest.Dependencies,
            });
        }

        public Task<PackageDownloadResult?> DownloadAsync(
            ResolvedDirective resolved,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PackageFixture package = _packages[resolved.Reference.Name];
            TrackingStream stream = new(package.ArchiveBytes);
            _downloaded.Enqueue(resolved.Reference);
            _downloadStreams.Enqueue(stream);
            return Task.FromResult<PackageDownloadResult?>(new PackageDownloadResult
            {
                Content = stream,
                ContentLength = package.ArchiveBytes.LongLength,
                ContentType = "application/gzip",
            });
        }

        public Task<IReadOnlyList<CatalogEntry>> SearchAsync(
            PackageSearchCriteria criteria,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CatalogEntry>>([]);

        public Task<PublishResult> PublishAsync(
            PackageReference reference,
            Stream tarballStream,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixtureHttpHandler(byte[] content) : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }
        internal List<TrackingStream> ResponseStreams { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            TrackingStream stream = new(content);
            ResponseStreams.Add(stream);
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };
            response.Content.Headers.ContentLength = content.LongLength;
            return Task.FromResult(response);
        }
    }

    private sealed class TrackingStream(byte[] content) : MemoryStream(content)
    {
        internal int ReadCount { get; private set; }
        internal int DisposeCount { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            return base.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return base.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
    }

    private sealed class InlineProgress(Action<PackageProgress> callback) : IProgress<PackageProgress>
    {
        public void Report(PackageProgress value) => callback(value);
    }

    private sealed class SubscriptionTrackingCache(string path) :
        DiskPackageCache(path),
        IPackageCacheMutationPublisher
    {
        internal int SubscriptionCount { get; private set; }

        IDisposable IPackageCacheMutationPublisher.Subscribe(
            Action<PackageReference> packageInvalidated,
            Action cacheCleared)
        {
            SubscriptionCount++;
            throw new InvalidOperationException("An unsupported manager must not subscribe.");
        }
    }

    private sealed class UnsupportedCache : IHardenedPackageCache, IPackageCacheMutationPublisher
    {
        internal int CallbackCount { get; private set; }

        private T Unexpected<T>()
        {
            CallbackCount++;
            throw new InvalidOperationException("An unsupported cache must not be called.");
        }

        public string CacheDirectory => Unexpected<string>();
        public Task<bool> IsInstalledAsync(PackageReference reference, CancellationToken ct = default) =>
            Unexpected<Task<bool>>();
        public Task<PackageRecord?> GetPackageAsync(PackageReference reference, CancellationToken ct = default) =>
            Unexpected<Task<PackageRecord?>>();
        public Task<IReadOnlyList<PackageRecord>> ListPackagesAsync(
            string? packageIdFilter = null, string? versionFilter = null, CancellationToken ct = default) =>
            Unexpected<Task<IReadOnlyList<PackageRecord>>>();
        public Task<PackageRecord> InstallAsync(
            PackageReference reference, Stream tarballStream,
            InstallCacheOptions? options = null, CancellationToken ct = default) =>
            Unexpected<Task<PackageRecord>>();
        public Task<PackageRecord> ImportAsync(
            Stream tarballStream, InstallCacheOptions? options = null, CancellationToken ct = default) =>
            Unexpected<Task<PackageRecord>>();
        public Task<HardenedPackageCacheInspection> InspectAsync(
            PackageReference reference, CancellationToken cancellationToken = default) =>
            Unexpected<Task<HardenedPackageCacheInspection>>();
        public Task<bool> RemoveAsync(PackageReference reference, CancellationToken ct = default) =>
            Unexpected<Task<bool>>();
        public Task<int> ClearAsync(CancellationToken ct = default) => Unexpected<Task<int>>();
        public Task<PackageManifest?> ReadManifestAsync(PackageReference reference, CancellationToken ct = default) =>
            Unexpected<Task<PackageManifest?>>();
        public Task<PackageIndex?> GetIndexAsync(PackageReference reference, CancellationToken ct = default) =>
            Unexpected<Task<PackageIndex?>>();
        public Task<string?> GetFileContentAsync(
            PackageReference reference, string relativePath, CancellationToken ct = default) =>
            Unexpected<Task<string?>>();
        public string? GetPackageContentPath(PackageReference reference) => Unexpected<string?>();
        public Task<CacheMetadata> GetMetadataAsync(CancellationToken ct = default) =>
            Unexpected<Task<CacheMetadata>>();
        public Task UpdateMetadataAsync(
            PackageReference reference, CacheMetadataEntry entry, CancellationToken ct = default) =>
            Unexpected<Task>();
        public IDisposable Subscribe(Action<PackageReference> packageInvalidated, Action cacheCleared) =>
            Unexpected<IDisposable>();
        public void Dispose() => CallbackCount++;
    }
}
