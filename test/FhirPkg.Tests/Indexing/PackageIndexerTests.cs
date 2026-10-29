// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Indexing;

public sealed class PackageIndexerTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        $"package-indexer-{Guid.NewGuid():N}");

    [Fact]
    public async Task ManagedBuild_BuildsWithoutRegistering()
    {
        string contentPath = CreatePackageContent(
            "example.package",
            "1.0.0",
            ("patient.json", """{"resourceType":"Patient","id":"one"}"""));
        PackageIndexer indexer = new(NullLogger.Instance);
        IManagedPackageIndexer managed = indexer;
        PackageRecord record = CreateRecord(
            new PackageReference("example.package", "1.0.0"),
            contentPath);

        PackageIndex index = await managed.BuildIndexAsync(
            record,
            TestContext.Current.CancellationToken);

        index.Files.Count.ShouldBe(1);
        indexer.FindResources(new ResourceSearchCriteria()).ShouldBeEmpty();
    }

    [Fact]
    public async Task LowLevelIndexing_InvalidExistingIndexIsRebuiltAndRegistered()
    {
        string contentPath = CreatePackageContent(
            "example.package",
            "1.0.0",
            ("patient.json", """{"resourceType":"Patient","id":"fresh"}"""));
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, ".index.json"),
            """{"index-version":1,"files":[{"filename":"patient.json","resourceType":"Patient","id":"stale"}]}""",
            TestContext.Current.CancellationToken);
        PackageIndexer indexer = new(NullLogger.Instance);

        PackageIndex index = await indexer.IndexPackageAsync(
            contentPath,
            cancellationToken:
                TestContext.Current.CancellationToken);

        index.IndexVersion.ShouldBe(2);
        index.Files.Single().Id.ShouldBe("fresh");
        indexer.FindByResourceType("Patient")
            .Single().Id.ShouldBe("fresh");
    }

    [Fact]
    public void ManagedRegistration_UsesExactScopedPackageIdentity()
    {
        PackageIndexer indexer = new(NullLogger.Instance);
        IManagedPackageIndexer managed = indexer;
        PackageReference first =
            PackageReference.Parse("@Example/Package@1.0.0");
        PackageReference second =
            PackageReference.Parse("@example/package@2.0.0");

        managed.RegisterPersistedIndex(
            first,
            CreateIndex(("first.json", "Patient", "first")));
        managed.RegisterPersistedIndex(
            second,
            CreateIndex(("second.json", "Patient", "second")));

        IReadOnlyList<ResourceInfo> firstResults =
            indexer.FindByResourceType(
                "Patient",
                "@example/package#1.0.0");
        IReadOnlyList<ResourceInfo> secondResults =
            indexer.FindByResourceType(
                "Patient",
                "@example/package#2.0.0");

        firstResults.Select(result => result.Id)
            .ShouldBe(["first"]);
        secondResults.Select(result => result.Id)
            .ShouldBe(["second"]);
        firstResults[0].PackageName.ShouldBe("@example/package");
    }

    [Fact]
    public void Searches_AreDeterministicAcrossRegistrationAndFileOrder()
    {
        PackageIndexer indexer = new(NullLogger.Instance);
        IManagedPackageIndexer managed = indexer;
        managed.RegisterPersistedIndex(
            new PackageReference("z.package", "1.0.0"),
            CreateIndex(
                ("z-second.json", "Patient", "z-second"),
                ("z-first.json", "Patient", "z-first")));
        managed.RegisterPersistedIndex(
            new PackageReference("a.package", "1.0.0"),
            CreateIndex(
                ("a-second.json", "Patient", "a-second"),
                ("a-first.json", "Patient", "a-first")));

        IReadOnlyList<ResourceInfo> results =
            indexer.FindResources(new ResourceSearchCriteria
            {
                ResourceTypes = ["Patient"]
            });

        results.Select(result => result.Id).ShouldBe(
        [
            "a-first",
            "a-second",
            "z-first",
            "z-second"
        ]);
        indexer.FindByCanonicalUrl("https://example/a-first")!
            .Id.ShouldBe("a-first");
    }

    [Fact]
    public void UnregisterAndClear_RemoveOnlyRequestedRegistrations()
    {
        PackageIndexer indexer = new(NullLogger.Instance);
        IManagedPackageIndexer managed = indexer;
        PackageReference first = new("example.package", "1.0.0");
        PackageReference second = new("example.package", "2.0.0");
        managed.RegisterPersistedIndex(
            first,
            CreateIndex(("first.json", "Patient", "first")));
        managed.RegisterPersistedIndex(
            second,
            CreateIndex(("second.json", "Patient", "second")));

        managed.Unregister(first).ShouldBeTrue();
        managed.Unregister(first).ShouldBeFalse();
        indexer.FindByResourceType("Patient")
            .Select(result => result.Id)
            .ShouldBe(["second"]);

        managed.Clear();

        indexer.FindByResourceType("Patient").ShouldBeEmpty();
    }

    [Fact]
    public async Task ManagedSearchAndClear_DoNotAffectLegacyRegistrations()
    {
        string contentPath = CreatePackageContent(
            "legacy.package",
            "1.0.0",
            ("legacy.json", """{"resourceType":"Patient","id":"legacy"}"""));
        PackageIndexer indexer = new(NullLogger.Instance);
        IManagedPackageIndexer managed = indexer;
        _ = await indexer.IndexPackageAsync(
            contentPath,
            cancellationToken:
                TestContext.Current.CancellationToken);
        managed.RegisterPersistedIndex(
            new PackageReference(
                "managed.package",
                "1.0.0"),
            CreateIndex(
                ("managed.json", "Patient", "managed")));

        managed.FindManagedByResourceType("Patient")
            .Select(resource => resource.Id)
            .ShouldBe(["managed"]);

        managed.Clear();

        managed.FindManagedResources(
                new ResourceSearchCriteria())
            .ShouldBeEmpty();
        indexer.FindByResourceType("Patient")
            .Select(resource => resource.Id)
            .ShouldBe(["legacy"]);
    }

    [Fact]
    public async Task PublicSearch_ReplacesDuplicateIdentityAndPrefersManagedRegistration()
    {
        string firstPath = CreatePackageContent(
            "example.package",
            "1.0.0",
            ("patient.json", """{"resourceType":"Patient","id":"first"}"""));
        string secondPath = Path.Combine(
            _testRoot,
            "alternate",
            "example.package#1.0.0",
            "package");
        Directory.CreateDirectory(secondPath);
        File.WriteAllText(
            Path.Combine(
                secondPath,
                "package.json"),
            """{"name":"example.package","version":"1.0.0"}""");
        File.WriteAllText(
            Path.Combine(
                secondPath,
                "patient.json"),
            """{"resourceType":"Patient","id":"second"}""");
        PackageIndexer indexer = new(NullLogger.Instance);
        IManagedPackageIndexer managed = indexer;

        _ = await indexer.IndexPackageAsync(
            firstPath,
            cancellationToken:
                TestContext.Current.CancellationToken);
        _ = await indexer.IndexPackageAsync(
            secondPath,
            cancellationToken:
                TestContext.Current.CancellationToken);
        indexer.FindByResourceType(
                "Patient",
                "example.package#1.0.0")
            .Select(resource => resource.Id)
            .ShouldBe(["second"]);

        managed.RegisterPersistedIndex(
            new PackageReference(
                "example.package",
                "1.0.0"),
            CreateIndex(
                ("patient.json", "Patient", "managed")));

        indexer.FindByResourceType(
                "Patient",
                "example.package#1.0.0")
            .Select(resource => resource.Id)
            .ShouldBe(["managed"]);
    }

    [Fact]
    public void ManagedRegistration_InvalidStructureIsRejected()
    {
        PackageIndexer indexer = new(NullLogger.Instance);
        IManagedPackageIndexer managed = indexer;
        PackageIndex invalid = new()
        {
            IndexVersion = 1,
            Files = []
        };

        Should.Throw<InvalidDataException>(
            () => managed.RegisterPersistedIndex(
                new PackageReference("example.package", "1.0.0"),
                invalid));
    }

    [Fact]
    public async Task BuildIndex_EnumerationRetryDoesNotDuplicateResources()
    {
        string contentPath = CreatePackageContent(
            "example.package",
            "1.0.0",
            ("a.json", """{"resourceType":"Patient","id":"a"}"""),
            ("b.json", """{"resourceType":"Patient","id":"b"}"""),
            ("c.json", """{"resourceType":"Patient","id":"c"}"""));
        ScriptedFileSystemPrimitives primitives = new(SystemFileSystemPrimitives.Instance.Platform);
        primitives.UseSystemFiles();
        int created = 0;
        primitives.Set<IEnumerator<string>>("CreateDirectoryEnumerator", call =>
        {
            IEnumerator<string> inner = Directory.EnumerateFileSystemEntries(call.Path!).GetEnumerator();
            // The first attempt fails after yielding two entries.
            IEnumerator<string> result = created++ == 0 ? new FailingEnumerator(inner, 2) : inner;
            return FileSystemReplayOutcome<IEnumerator<string>>.Complete(result);
        });
        PackageIndexer indexer = new(NullLogger.Instance, primitives.CreateContext());
        PackageRecord record = CreateRecord(
            new PackageReference("example.package", "1.0.0"),
            contentPath);

        PackageIndex index = await ((IManagedPackageIndexer)indexer).BuildIndexAsync(
            record,
            TestContext.Current.CancellationToken);

        index.Files.Select(file => file.Filename).ShouldBe(["a.json", "b.json", "c.json"]);
        primitives.Count("CreateDirectoryEnumerator").ShouldBe(2);
        primitives.AsyncWaits.ShouldBe([10]);
    }

    [Fact]
    public async Task ReadExistingIndex_ExhaustedIoRetainsRegenerationBehavior()
    {
        string contentPath = CreatePackageContent(
            "example.package",
            "1.0.0",
            ("patient.json", """{"resourceType":"Patient","id":"fresh"}"""));
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, ".index.json"),
            """{"index-version":2,"files":[{"filename":"patient.json","resourceType":"Patient","id":"stale"}]}""",
            TestContext.Current.CancellationToken);
        ScriptedFileSystemPrimitives primitives = new(SystemFileSystemPrimitives.Instance.Platform);
        primitives.UseSystemFiles();
        primitives.Set<FileSystemProbeResult>("Probe", call =>
            call.Path!.EndsWith(".index.json", StringComparison.Ordinal)
                ? ScriptedFileSystemPrimitives.NativeFailure<FileSystemProbeResult>(5, "Probe", call.Path)
                : SystemFileSystemPrimitives.Instance.Probe(call.Path!));
        primitives.Set<IEnumerator<string>>("CreateDirectoryEnumerator", call =>
            FileSystemReplayOutcome<IEnumerator<string>>.Complete(
                Directory.EnumerateFileSystemEntries(call.Path!).GetEnumerator()));
        CountingLogger logger = new();
        PackageIndexer indexer = new(logger, primitives.CreateContext());

        PackageIndex index = await indexer.IndexPackageAsync(
            contentPath,
            cancellationToken: TestContext.Current.CancellationToken);

        index.Files.Single().Id.ShouldBe("fresh");
        primitives.Calls.Count(call => call.Operation == "Probe"
            && call.Path!.EndsWith(".index.json", StringComparison.Ordinal)).ShouldBe(6);
        primitives.AsyncWaits.Count.ShouldBe(5);
        logger.Warnings.ShouldBe(1);
        indexer.FindByResourceType("Patient").Single().Id.ShouldBe("fresh");
    }

    [Fact]
    public async Task ReadExistingIndex_LinkedIndexUsesFollowingPositionedReads()
    {
        string contentPath = CreatePackageContent(
            "example.package",
            "1.0.0",
            ("patient.json", """{"resourceType":"Patient","id":"fresh"}"""));
        string targetPath = Path.Combine(_testRoot, "existing-index.json");
        await File.WriteAllTextAsync(
            targetPath,
            """{"index-version":2,"files":[{"filename":"patient.json","resourceType":"Patient","id":"stored"}]}""",
            TestContext.Current.CancellationToken);
        string indexPath = Path.Combine(contentPath, ".index.json");
        CreateSymbolicLink(indexPath, targetPath, isDirectory: false);
        ScriptedFileSystemPrimitives primitives = CreateFilePrimitives();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            primitives.Enqueue<int>("ReadAtAsync", call =>
            {
                call.Path.ShouldBe(indexPath);
                return ScriptedFileSystemPrimitives.NativeFailure<int>(5, "ReadAtAsync", call.Path);
            });
        }
        CountingLogger logger = new();
        PackageIndexer indexer = new(logger, primitives.CreateContext(retries: 2));

        PackageIndex index = await indexer.IndexPackageAsync(
            contentPath,
            cancellationToken: TestContext.Current.CancellationToken);

        index.Files.Single().Id.ShouldBe("stored");
        indexer.FindByResourceType("Patient").Single().Id.ShouldBe("stored");
        FileSystemPrimitiveCall opened = primitives.Calls.Single(
            call => call.Operation == "OpenFileHandle" && call.Path == indexPath);
        opened.Mode.ShouldBe(FileMode.Open);
        opened.Access.ShouldBe(FileAccess.Read);
        opened.Share.ShouldBe(FileShare.Read);
        opened.Options.ShouldBe(FileOptions.None);
        primitives.Calls.Single(call => call.Operation == "Probe" && call.Path == indexPath)
            .Flags.ShouldBe(1);
        primitives.Count("CreateDirectoryEnumerator").ShouldBe(0);
        primitives.AsyncWaits.ShouldBe([10, 10]);
        primitives.Waits.ShouldBeEmpty();
        logger.Warnings.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildIndex_LinkedContentAndResourcesExcludeDirectoriesInOrdinalOrder(bool managed)
    {
        string contentPath = CreatePackageContent(
            "physical.package",
            "1.0.0",
            ("a.json", """{"resourceType":"Patient","id":"a"}"""),
            ("Z.json", """{"resourceType":"Patient","id":"z"}"""));
        string directoryPath = Path.Combine(contentPath, "B-directory.json");
        Directory.CreateDirectory(directoryPath);
        await File.WriteAllTextAsync(
            Path.Combine(directoryPath, "nested.json"),
            """{"resourceType":"Patient","id":"nested"}""",
            TestContext.Current.CancellationToken);
        string resourceTarget = Path.Combine(_testRoot, "resource-target.json");
        await File.WriteAllTextAsync(
            resourceTarget,
            """{"resourceType":"Patient","id":"linked"}""",
            TestContext.Current.CancellationToken);
        CreateSymbolicLink(
            Path.Combine(contentPath, "M-linked.json"), resourceTarget, isDirectory: false);
        CreateSymbolicLink(
            Path.Combine(contentPath, "C-directory-link.json"), directoryPath, isDirectory: true);
        string linkedContentPath = Path.Combine(_testRoot, "alias.package#2.0.0", "package");
        Directory.CreateDirectory(Path.GetDirectoryName(linkedContentPath)!);
        CreateSymbolicLink(linkedContentPath, contentPath, isDirectory: true);
        ScriptedFileSystemPrimitives primitives = CreateFilePrimitives();
        primitives.Enqueue<int>("ReadAtAsync", call =>
        {
            Path.GetFileName(call.Path).ShouldBe("M-linked.json");
            return ScriptedFileSystemPrimitives.NativeFailure<int>(5, "ReadAtAsync", call.Path);
        });
        CountingLogger logger = new();
        PackageIndexer indexer = new(logger, primitives.CreateContext(retries: 1));

        PackageIndex index = managed
            ? await ((IManagedPackageIndexer)indexer).BuildIndexAsync(
                CreateRecord(new PackageReference("alias.package", "2.0.0"), linkedContentPath),
                TestContext.Current.CancellationToken)
            : await indexer.IndexPackageAsync(
                linkedContentPath,
                new IndexingOptions { ForceReindex = true },
                TestContext.Current.CancellationToken);

        index.Files.Select(file => file.Filename).ShouldBe(["M-linked.json", "Z.json", "a.json"]);
        index.Files.Select(file => file.Id).ShouldBe(["linked", "z", "a"]);
        primitives.Calls.Where(call => call.Operation == "OpenFileHandle")
            .Select(call => Path.GetFileName(call.Path))
            .ShouldBe(["M-linked.json", "Z.json", "a.json"]);
        foreach (FileSystemPrimitiveCall opened in primitives.Calls.Where(call => call.Operation == "OpenFileHandle"))
        {
            opened.Mode.ShouldBe(FileMode.Open);
            opened.Access.ShouldBe(FileAccess.Read);
            opened.Share.ShouldBe(FileShare.Read);
            opened.Options.ShouldBe(FileOptions.None);
        }
        primitives.Calls.Where(call => call.Operation == "Probe").ShouldAllBe(call => call.Flags == 1);
        primitives.AsyncWaits.ShouldBe([10]);
        primitives.Waits.ShouldBeEmpty();
        logger.Warnings.ShouldBe(0);
        if (!managed)
        {
            indexer.FindByResourceType("Patient").Select(resource => resource.FilePath)
                .ShouldBe(["M-linked.json", "Z.json", "a.json"]);
            indexer.FindByResourceType("Patient").ShouldAllBe(
                resource => resource.PackageName == "alias.package" && resource.PackageVersion == "2.0.0");
        }
    }

    [Fact]
    public async Task ReadExistingIndex_ReferencedFileLinkIsStillRejectedAndRegenerated()
    {
        string contentPath = CreatePackageContent("example.package", "1.0.0");
        string targetPath = Path.Combine(_testRoot, "patient-target.json");
        await File.WriteAllTextAsync(
            targetPath,
            """{"resourceType":"Patient","id":"fresh"}""",
            TestContext.Current.CancellationToken);
        string resourcePath = Path.Combine(contentPath, "patient.json");
        CreateSymbolicLink(resourcePath, targetPath, isDirectory: false);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, ".index.json"),
            """{"index-version":2,"files":[{"filename":"patient.json","resourceType":"Patient","id":"stale"}]}""",
            TestContext.Current.CancellationToken);
        ScriptedFileSystemPrimitives primitives = CreateFilePrimitives();
        CountingLogger logger = new();
        PackageIndexer indexer = new(logger, primitives.CreateContext(retries: 0));

        PackageIndex index = await indexer.IndexPackageAsync(
            contentPath,
            cancellationToken: TestContext.Current.CancellationToken);

        index.Files.Single().Id.ShouldBe("fresh");
        logger.WarningMessages.ShouldHaveSingleItem().ShouldContain("Ignoring invalid .index.json");
        primitives.Calls.ShouldContain(
            call => call.Operation == "Probe" && call.Path == resourcePath && call.Flags == 0);
        primitives.Calls.ShouldContain(
            call => call.Operation == "Probe" && call.Path == resourcePath && call.Flags == 1);
        primitives.AsyncWaits.ShouldBeEmpty();
        primitives.Waits.ShouldBeEmpty();
        File.ReadAllText(targetPath).ShouldContain("\"id\":\"fresh\"");
    }

    [Fact]
    public async Task ReadExistingIndex_RelativeRootUsesRetainedContextAndOriginalRegistrationSpelling()
    {
        string contentPath = CreatePackageContent(
            "physical.package",
            "1.0.0",
            ("patient.json", """{"resourceType":"Patient","id":"fresh"}"""));
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, ".index.json"),
            """{"index-version":2,"files":[{"filename":"patient.json","resourceType":"Patient","id":"stored"}]}""",
            TestContext.Current.CancellationToken);
        string relativeRoot = Path.Combine("Alias.Package#2.0.0", "package");
        ScriptedFileSystemPrimitives primitives = CreateFilePrimitives();
        int rootAttempts = 0;
        primitives.Set<string>("GetFullPath", call =>
        {
            if (call.Path == relativeRoot)
            {
                int attempt = rootAttempts++;
                // Directory discovery succeeds; the later validation root resolution
                // fails twice and must retain the indexer's context and budget.
                return attempt is 1 or 2
                    ? ScriptedFileSystemPrimitives.NativeFailure<string>(5, "GetFullPath", call.Path)
                    : FileSystemReplayOutcome<string>.Complete(contentPath);
            }

            call.Path.ShouldBe(Path.Combine(relativeRoot, ".index.json"));
            return FileSystemReplayOutcome<string>.Complete(Path.Combine(contentPath, ".index.json"));
        });
        PackageIndexer indexer = new(NullLogger.Instance, primitives.CreateContext(retries: 2));

        PackageIndex index = await indexer.IndexPackageAsync(
            relativeRoot,
            cancellationToken: TestContext.Current.CancellationToken);

        index.Files.Single().Id.ShouldBe("stored");
        ResourceInfo resource = indexer.FindByResourceType("Patient").Single();
        resource.PackageName.ShouldBe("Alias.Package");
        resource.PackageVersion.ShouldBe("2.0.0");
        rootAttempts.ShouldBe(4);
        primitives.Count("GetFullPath").ShouldBe(5);
        primitives.Count("CreateDirectoryEnumerator").ShouldBe(0);
        primitives.Waits.ShouldBe([10, 10]);
        primitives.AsyncWaits.ShouldBeEmpty();
    }

    [Fact]
    public void ReferencedFiles_RelativeRootExhaustionUsesOnlyRetainedBudget()
    {
        string relativeRoot = Path.Combine("relative.package#1.0.0", "package");
        ScriptedFileSystemPrimitives primitives = CreateFilePrimitives();
        IOException[] failures = Enumerable.Range(0, 3)
            .Select(attempt => new IOException($"Root resolution failure {attempt}."))
            .ToArray();
        foreach (IOException failure in failures)
        {
            primitives.Enqueue("GetFullPath",
                ScriptedFileSystemPrimitives.Fail<string>(failure, "GetFullPath", relativeRoot));
        }
        FileSystemContext context = primitives.CreateContext(retries: 2);

        IOException exception = Should.Throw<IOException>(() =>
            PackageIndexValidation.TryValidateReferencedFiles(
                CreateIndex(("patient.json", "Patient", "one")),
                relativeRoot,
                context,
                out _));

        exception.ShouldBeSameAs(failures[2]);
        FileSystemFailureContext.FormatMessage(exception).ShouldContain("attempts 3, retries 2, termination Exhausted");
        primitives.Count("GetFullPath").ShouldBe(3);
        primitives.Count("Probe").ShouldBe(0);
        primitives.Count("OpenFileHandle").ShouldBe(0);
        primitives.Waits.ShouldBe([10, 10]);
        primitives.AsyncWaits.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReferencedFile_UsesSamePolicyWithoutRewrapping(bool unauthorized)
    {
        string contentPath = CreatePackageContent(
            "example.package",
            "1.0.0",
            ("patient.json", """{"resourceType":"Patient","id":"one"}"""));
        ScriptedFileSystemPrimitives primitives = new(SystemFileSystemPrimitives.Instance.Platform);
        primitives.UseSystemFiles();
        int attempts = 0;
        primitives.Set<FileSystemProbeResult>("Probe", call =>
        {
            string message = $"Reference failure {attempts++}.";
            Exception exception = unauthorized
                ? new UnauthorizedAccessException(message)
                : new IOException(message);
            return FileSystemReplayOutcome<FileSystemProbeResult>.Failed(
                FileSystemFailureContext.CapturePrimitiveFailure(
                    exception,
                    FileSystemReplaySafety.Safe,
                    "Probe",
                    call.Path,
                    nativeErrorCode: 5,
                    nativeFlags: 0x123));
        });
        FileSystemContext context = primitives.CreateContext();

        bool valid = PackageIndexValidation.TryValidateReferencedFiles(
            CreateIndex(("patient.json", "Patient", "one")),
            contentPath,
            context,
            out string? failureReason);

        valid.ShouldBeFalse();
        failureReason.ShouldNotBeNull().ShouldStartWith("Reference failure 5.");
        failureReason.ShouldContain("Filesystem operation 'Probe', phase 'Probe'");
        failureReason.ShouldContain("attempts 6, retries 5, termination Exhausted");
        failureReason.ShouldContain($"path '{Path.Combine(contentPath, "patient.json")}'");
        failureReason.ShouldContain("native error 5, native flags 0x123");
        for (int attempt = 0; attempt < 5; attempt++)
            failureReason.ShouldNotContain($"Reference failure {attempt}.");
        // One budget for the probe: six executions and five waits, no nested loop.
        primitives.Count("Probe").ShouldBe(6);
        primitives.Waits.ShouldBe([10, 10, 10, 10, 10]);
        primitives.AsyncWaits.ShouldBeEmpty();
    }

    [Fact]
    public void StandaloneIndexer_SnapshotsExplicitSettings()
    {
        FileSystemRetryOptions options = new()
        {
            RetryCount = 2,
            MinimumDelayMilliseconds = 3,
            MaximumJitterMilliseconds = 0,
        };
        PackageIndexer indexer = new(NullLogger.Instance, options);
        options.RetryCount = 9;
        options.MinimumDelayMilliseconds = 99;

        FileSystemContext context = indexer.FileSystemContext;
        context.Policy.RetryCount.ShouldBe(2);
        context.Policy.MinimumDelayMilliseconds.ShouldBe(3);
        context.Policy.MaximumJitterMilliseconds.ShouldBe(0);
        context.HasExplicitValues.ShouldBeTrue();
        Should.Throw<PackageInstallException>(
            () => new PackageIndexer(NullLogger.Instance, (FileSystemRetryOptions)null!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private static ScriptedFileSystemPrimitives CreateFilePrimitives()
    {
        ScriptedFileSystemPrimitives primitives = new(SystemFileSystemPrimitives.Instance.Platform);
        primitives.UseSystemFiles();
        primitives.Set<IEnumerator<string>>("CreateDirectoryEnumerator",
            call => SystemFileSystemPrimitives.Instance.CreateDirectoryEnumerator(call.Path!));
        primitives.Set<FileStream>("OpenFile", call => SystemFileSystemPrimitives.Instance.OpenFile(
            call.Path!, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.SequentialScan));
        return primitives;
    }

    private static void CreateSymbolicLink(string linkPath, string targetPath, bool isDirectory)
    {
        try
        {
            if (isDirectory)
                Directory.CreateSymbolicLink(linkPath, targetPath);
            else
                File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or PlatformNotSupportedException
            || (exception is IOException && (exception.HResult & 0xFFFF) is 50 or 95 or 1314))
        {
            Assert.Skip($"Symbolic links are unavailable on this host: {exception.Message}");
        }
    }

    private sealed class FailingEnumerator(IEnumerator<string> inner, int failAfter) : IEnumerator<string>
    {
        private int _moves;

        public string Current => inner.Current;

        object System.Collections.IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_moves++ == failAfter)
                throw new IOException("Scripted enumeration failure.");
            return inner.MoveNext();
        }

        public void Reset() => inner.Reset();

        public void Dispose() => inner.Dispose();
    }

    private sealed class CountingLogger : ILogger
    {
        internal int Warnings { get; private set; }

        internal List<string> WarningMessages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings++;
                WarningMessages.Add(formatter(state, exception));
            }
        }
    }

    private string CreatePackageContent(
        string name,
        string version,
        params (string Filename, string Json)[] resources)
    {
        string contentPath = Path.Combine(
            _testRoot,
            $"{name}#{version}",
            "package");
        Directory.CreateDirectory(contentPath);
        File.WriteAllText(
            Path.Combine(contentPath, "package.json"),
            $$"""{"name":"{{name}}","version":"{{version}}"}""");
        foreach ((string filename, string json) in resources)
        {
            File.WriteAllText(
                Path.Combine(contentPath, filename),
                json);
        }

        return contentPath;
    }

    private static PackageRecord CreateRecord(
        PackageReference reference,
        string contentPath) =>
        new()
        {
            Reference = reference,
            DirectoryPath = Directory.GetParent(contentPath)!.FullName,
            ContentPath = contentPath,
            Manifest = new PackageManifest
            {
                Name = reference.Name,
                Version = reference.Version!
            }
        };

    private static PackageIndex CreateIndex(
        params (string Filename, string ResourceType, string Id)[] entries) =>
        new()
        {
            IndexVersion = 2,
            Files = entries
                .Select(entry => new ResourceIndexEntry
                {
                    Filename = entry.Filename,
                    ResourceType = entry.ResourceType,
                    Id = entry.Id,
                    Url = $"https://example/{entry.Id}"
                })
                .ToList()
        };
}
