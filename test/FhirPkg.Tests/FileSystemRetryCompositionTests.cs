// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
using FhirPkg.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

[Collection("EnvironmentVariable")]
public sealed class FileSystemRetryCompositionTests : IDisposable
{
    private static readonly string[] s_variables =
    [
        FileSystemRetryOptions.RetryCountEnvironmentVariable,
        FileSystemRetryOptions.MinimumDelayMillisecondsEnvironmentVariable,
        FileSystemRetryOptions.MaximumJitterMillisecondsEnvironmentVariable,
    ];

    private readonly string?[] _originals = s_variables.Select(Environment.GetEnvironmentVariable).ToArray();
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory, $"fhirpkg-composition-{Guid.NewGuid():N}");

    public FileSystemRetryCompositionTests()
    {
        Directory.CreateDirectory(_root);
        foreach (string variable in s_variables)
            Environment.SetEnvironmentVariable(variable, null);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("count")]
    [InlineData("minimum")]
    [InlineData("jitter")]
    [InlineData("sum")]
    [InlineData("environment")]
    public void DirectAndDi_InvalidPolicyPrecedesCacheCreation(string invalid)
    {
        FhirPackageManagerOptions options = Options("must-not-exist");
        switch (invalid)
        {
            case "null":
                options.FileSystemRetry = null!;
                break;
            case "count":
                options.FileSystemRetry.RetryCount = -1;
                break;
            case "minimum":
                options.FileSystemRetry.MinimumDelayMilliseconds = -1;
                break;
            case "jitter":
                options.FileSystemRetry.MaximumJitterMilliseconds = -1;
                break;
            case "sum":
                options.FileSystemRetry.MinimumDelayMilliseconds = int.MaxValue;
                options.FileSystemRetry.MaximumJitterMilliseconds = 1;
                break;
            case "environment":
                Environment.SetEnvironmentVariable(s_variables[0], "invalid");
                break;
        }

        AssertInvalid(() => new FhirPackageManager(options));
        Directory.Exists(options.CachePath).ShouldBeFalse();

        int factories = 0;
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddSingleton<IPackageCache>(_ =>
        {
            factories++;
            throw new InvalidOperationException("The cache factory must not run.");
        });
        services.AddSingleton<IDependencyResolver>(_ =>
        {
            factories++;
            throw new InvalidOperationException("The resolver factory must not run.");
        });
        services.AddFhirPackageManagement();
        using ServiceProvider provider = services.BuildServiceProvider();

        AssertInvalid(() => provider.GetRequiredService<IFhirPackageManager>());
        factories.ShouldBe(0);
        Directory.Exists(options.CachePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Di_ResolvedSnapshotDoesNotBecomeExplicitByCopying()
    {
        SetEnvironment("2", "7", "0");
        using HttpClient http = new(new RejectingHandler());
        using DiskPackageCache cache = Cache("injected", Retry(0));
        FhirNpmRegistryClient registry = Registry(http, Retry(0));
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IPackageCache>(cache);
        services.AddSingleton<IRegistryClient>(registry);
        services.AddFhirPackageManagement(options =>
        {
            options.CachePath = Path.Combine(_root, "unused-cache");
            options.IncludeCiBuilds = false;
            options.IncludeHl7WebsiteFallback = false;
        });
        using ServiceProvider provider = services.BuildServiceProvider();
        FhirPackageManagerConfiguration configuration =
            provider.GetRequiredService<FhirPackageManagerConfiguration>();
        FhirPackageManagerOptions exposed = provider.GetRequiredService<FhirPackageManagerOptions>();
        configuration.HasExplicitFileSystemRetry.ShouldBeFalse();
        configuration.Options.FileSystemRetry.HasExplicitValues.ShouldBeTrue();
        exposed.FileSystemRetry.RetryCount.ShouldBe(2);
        exposed.FileSystemRetry.RetryCount = 99;
        SetEnvironment("invalid", "invalid", "invalid");

        IFhirPackageManager manager = provider.GetRequiredService<IFhirPackageManager>();
        (await manager.ListCachedSummariesAsync(
            cancellationToken: TestContext.Current.CancellationToken)).ShouldBeEmpty();

        configuration.HasExplicitFileSystemRetry.ShouldBeFalse();
        configuration.FileSystemContext.Policy.RetryCount.ShouldBe(2);
        configuration.FileSystemContext.Policy.MinimumDelayMilliseconds.ShouldBe(7);
        ((PackageIndexer)provider.GetRequiredService<IPackageIndexer>())
            .FileSystemContext.ShouldBeSameAs(configuration.FileSystemContext);
        provider.GetRequiredService<IPackageCache>().ShouldBeSameAs(cache);
        provider.GetRequiredService<IRegistryClient>().ShouldBeSameAs(registry);
        Directory.Exists(configuration.Options.CachePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Managers_WithDifferentPoliciesRemainIndependent()
    {
        string project = CreateProject();
        string manifestPath = Path.Combine(project, "package.json");
        ScriptedFileSystemPrimitives firstFiles = CompositionTestSupport.SystemFiles(_root);
        ScriptedFileSystemPrimitives secondFiles = CompositionTestSupport.SystemFiles(_root);
        using FhirPackageManager first = FhirPackageManager.CreateResolved(
            FhirPackageManagerConfiguration.CreateResolved(Options("first"), firstFiles.CreateContext(retries: 1)));
        using FhirPackageManager second = FhirPackageManager.CreateResolved(
            FhirPackageManagerConfiguration.CreateResolved(Options("second"), secondFiles.CreateContext(retries: 3)));
        firstFiles.Set<int>("ReadAtAsync", call =>
            ScriptedFileSystemPrimitives.NativeFailure<int>(1117, "manifest read", call.Path));
        secondFiles.Set<int>("ReadAtAsync", call =>
            ScriptedFileSystemPrimitives.NativeFailure<int>(5, "manifest read", call.Path));

        Task<IOException> firstFailure = Should.ThrowAsync<IOException>(() =>
            first.RestoreAsync(project, cancellationToken: TestContext.Current.CancellationToken));
        Task<IOException> secondFailure = Should.ThrowAsync<IOException>(() =>
            second.RestoreAsync(project, cancellationToken: TestContext.Current.CancellationToken));
        IOException[] failures = await Task.WhenAll(firstFailure, secondFailure);

        firstFiles.Count("ReadAtAsync").ShouldBe(2);
        secondFiles.Count("ReadAtAsync").ShouldBe(4);
        firstFiles.AsyncWaits.ShouldBe([10]);
        secondFiles.AsyncWaits.ShouldBe([10, 10, 10]);
        firstFiles.Waits.ShouldBeEmpty();
        secondFiles.Waits.ShouldBeEmpty();
        FileSystemFailureContext.TryGetTerminal(failures[0], out FileSystemFailureContext? firstContext).ShouldBeTrue();
        FileSystemFailureContext.TryGetTerminal(failures[1], out FileSystemFailureContext? secondContext).ShouldBeTrue();
        firstContext!.AttemptCount.ShouldBe(2);
        secondContext!.AttemptCount.ShouldBe(4);
        firstContext.Path.ShouldBe(manifestPath);
        secondContext.Path.ShouldBe(manifestPath);
        firstFiles.Calls.ShouldContain(call => call.Operation == "CreateDirectory" && call.Path == Path.Combine(_root, "first"));
        secondFiles.Calls.ShouldContain(call => call.Operation == "CreateDirectory" && call.Path == Path.Combine(_root, "second"));
    }

    [Fact]
    public async Task OptionsAndEnvironmentMutation_DoesNotReconfigureGraph()
    {
        SetEnvironment("2", "7", "0");
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_root);
        FhirPackageManagerOptions options = Options("fixed");
        FileSystemContext context = new(options.FileSystemRetry, files, files.CreateExecutor());
        FhirPackageManagerConfiguration configuration =
            FhirPackageManagerConfiguration.CreateResolved(options, context);
        using FhirPackageManager manager = FhirPackageManager.CreateResolved(configuration);
        options.CachePath = Path.Combine(_root, "mutated");
        options.FileSystemRetry.RetryCount = 0;
        options.FileSystemRetry.MinimumDelayMilliseconds = 0;
        options.FileSystemRetry.MaximumJitterMilliseconds = 0;
        options.IncludeCiBuilds = true;
        SetEnvironment("invalid", "invalid", "invalid");
        files.Enqueue("WriteAtAsync", ScriptedFileSystemPrimitives.NativeFailure<bool>(1117, "spool write"));
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package/package.json", """{"name":"fixed.package","version":"1.0.0"}"""),
            ArbitraryTarBuilder.File("package/patient.json", """{"resourceType":"Patient","id":"fixed"}"""));

        PackageRecord record = await manager.ImportAsync(
            archive, options: null, TestContext.Current.CancellationToken);
        PackageIndex? rebuilt = await manager.IndexPackageAsync(
            record.Reference,
            new IndexingOptions { ForceReindex = true },
            TestContext.Current.CancellationToken);

        record.DirectoryPath.ShouldStartWith(configuration.Options.CachePath!);
        rebuilt!.Files.Single().Id.ShouldBe("fixed");
        Directory.Exists(options.CachePath).ShouldBeFalse();
        archive.CanRead.ShouldBeTrue();
        files.AsyncWaits.ShouldBe([7]);
        context.HasExplicitValues.ShouldBeFalse();
        files.Calls.ShouldContain(call => call.Operation == "WriteAtAsync"
            && call.Path!.EndsWith("archive.tgz", StringComparison.Ordinal));
        files.Calls.ShouldContain(call => call.Operation == "ReadAtAsync"
            && call.Path!.EndsWith("patient.json", StringComparison.Ordinal));
        files.Calls.ShouldContain(call => call.Operation == "DeleteEntry");
    }

    [Fact]
    public void ExplicitPolicy_RejectsNestedBuiltInMismatch()
    {
        using HttpClient http = new(new RejectingHandler());
        using DiskPackageCache cache = Cache("matching", Retry(1));
        PackageIndexer indexer = new(NullLogger.Instance, Retry(1));
        FhirNpmRegistryClient matching = Registry(http, Retry(1));
        FhirNpmRegistryClient different = Registry(http, Retry(2));
        RedundantRegistryClient flattenedAway = RedundantRegistryClient.CreateWithFileSystemRetry(
            [matching], Retry(99));
        RedundantRegistryClient healthy = RedundantRegistryClient.CreateWithFileSystemRetry(
            [flattenedAway, matching], Retry(1));
        FhirPackageManagerOptions options = Options("not-created", Retry(1));
        using (FhirPackageManager manager = Inject(cache, healthy, indexer, options))
        {
            healthy.BuiltInClients.ShouldBe([matching, matching]);
        }
        RedundantRegistryClient nested = RedundantRegistryClient.CreateWithFileSystemRetry(
            [different], Retry(1));
        RedundantRegistryClient actual = RedundantRegistryClient.CreateWithFileSystemRetry(
            [matching, nested], Retry(1));

        AssertInvalid(() => Inject(cache, actual, indexer, options));
        Directory.Exists(options.CachePath).ShouldBeFalse();
    }

    [Fact]
    public void DirectExplicitPolicy_RejectsMismatchedResolverCache()
    {
        using HttpClient http = new(new RejectingHandler());
        using DiskPackageCache cache = Cache("top-cache", Retry(1));
        using DiskPackageCache hiddenCache = Cache("hidden-cache", Retry(2));
        FhirNpmRegistryClient registry = Registry(http, Retry(1));
        VersionResolver versionResolver = new(registry, NullLogger.Instance);
        DependencyResolver dependencyResolver = new(registry, versionResolver, hiddenCache, NullLogger.Instance);
        PackageIndexer indexer = new(NullLogger.Instance, Retry(1));
        FhirPackageManagerOptions options = Options("not-created", Retry(1));

        AssertInvalid(() => new FhirPackageManager(
            cache, registry, versionResolver, dependencyResolver, indexer, options,
            NullLogger<FhirPackageManager>.Instance));

        Directory.Exists(options.CachePath).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiExplicitPolicy_RejectsMismatchedResolverCache(bool registerAfterDefaults)
    {
        using HttpClient http = new(new RejectingHandler());
        using DiskPackageCache cache = Cache("top-cache", Retry(1));
        using DiskPackageCache hiddenCache = Cache("hidden-cache", Retry(2));
        FhirNpmRegistryClient registry = Registry(http, Retry(1));
        VersionResolver versionResolver = new(registry, NullLogger.Instance);
        DependencyResolver dependencyResolver = new(registry, versionResolver, hiddenCache, NullLogger.Instance);
        PackageIndexer indexer = new(NullLogger.Instance, Retry(1));
        FhirPackageManagerOptions options = Options("not-created", Retry(1));
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(options);
        if (registerAfterDefaults)
            services.AddFhirPackageManagement();
        services.AddSingleton<IPackageCache>(cache);
        services.AddSingleton<IRegistryClient>(registry);
        services.AddSingleton<IVersionResolver>(versionResolver);
        services.AddSingleton<IDependencyResolver>(dependencyResolver);
        services.AddSingleton<IPackageIndexer>(indexer);
        if (!registerAfterDefaults)
            services.AddFhirPackageManagement();
        int httpFactories = 0;
        services.AddHttpClient("FhirPackages").ConfigurePrimaryHttpMessageHandler(() =>
        {
            httpFactories++;
            return new RejectingHandler();
        });
        using ServiceProvider provider = services.BuildServiceProvider();

        AssertInvalid(() => provider.GetRequiredService<IFhirPackageManager>());

        httpFactories.ShouldBe(0);
        Directory.Exists(options.CachePath).ShouldBeFalse();
    }

    [Theory]
    [InlineData("registry")]
    [InlineData("version")]
    [InlineData("indexer")]
    public void DiExplicitPolicy_RejectsLateMismatchBeforeDefaultCacheCreation(string collaborator)
    {
        using HttpClient http = new(new RejectingHandler());
        FhirNpmRegistryClient different = Registry(http, Retry(2));
        FhirPackageManagerOptions options = Options("not-created", Retry(1));
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddFhirPackageManagement();
        switch (collaborator)
        {
            case "registry":
                services.AddSingleton<IRegistryClient>(different);
                break;
            case "version":
                services.AddSingleton<IVersionResolver>(new VersionResolver(different, NullLogger.Instance));
                break;
            case "indexer":
                services.AddSingleton<IPackageIndexer>(new PackageIndexer(NullLogger.Instance, Retry(2)));
                break;
        }
        using ServiceProvider provider = services.BuildServiceProvider();

        AssertInvalid(() => provider.GetRequiredService<IFhirPackageManager>());

        Directory.Exists(options.CachePath).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiExplicitPolicy_RejectsLateResolverMismatchBeforeDefaultCacheCreation(
        bool removeRegistrationAfterBuild)
    {
        using HttpClient http = new(new RejectingHandler());
        using DiskPackageCache hiddenCache = Cache("hidden-cache", Retry(2));
        FhirNpmRegistryClient registry = Registry(http, Retry(1));
        VersionResolver version = new(registry, NullLogger.Instance);
        DependencyResolver dependencies = new(registry, version, hiddenCache, NullLogger.Instance);
        FhirPackageManagerOptions options = Options("not-created", Retry(1));
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(options);
        services.AddFhirPackageManagement();
        services.AddSingleton<IDependencyResolver>(dependencies);
        ServiceDescriptor registration = services.Last();
        using ServiceProvider provider = services.BuildServiceProvider();
        if (removeRegistrationAfterBuild)
            services.Remove(registration);

        AssertInvalid(() => provider.GetRequiredService<IFhirPackageManager>());

        provider.GetRequiredService<IDependencyResolver>().ShouldBeSameAs(dependencies);
        Directory.Exists(options.CachePath).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitPolicy_ValidatesVersionResolverHeldRegistry(bool heldByDependencyResolver)
    {
        using HttpClient http = new(new RejectingHandler());
        using DiskPackageCache cache = Cache("matching", Retry(1));
        FhirNpmRegistryClient registry = Registry(http, Retry(1));
        FhirNpmRegistryClient hiddenRegistry = Registry(http, Retry(2));
        VersionResolver matching = new(registry, NullLogger.Instance);
        VersionResolver different = new(hiddenRegistry, NullLogger.Instance);
        DependencyResolver dependencies = new(
            registry, heldByDependencyResolver ? different : matching, cache, NullLogger.Instance);
        FhirPackageManagerOptions options = Options("not-created", Retry(1));

        AssertInvalid(() => new FhirPackageManager(
            cache, registry, heldByDependencyResolver ? matching : different, dependencies,
            new PackageIndexer(NullLogger.Instance, Retry(1)), options,
            NullLogger<FhirPackageManager>.Instance));

        Directory.Exists(options.CachePath).ShouldBeFalse();
    }

    [Theory]
    [InlineData(2, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 0, 1)]
    public void ExplicitPolicy_ValidatesDependencyResolverHeldRegistry(
        int retries, int minimum, int jitter)
    {
        using HttpClient http = new(new RejectingHandler());
        using DiskPackageCache cache = Cache("matching", Retry(1));
        FhirNpmRegistryClient registry = Registry(http, Retry(1));
        FhirNpmRegistryClient hiddenRegistry = Registry(http, new FileSystemRetryOptions
        {
            RetryCount = retries,
            MinimumDelayMilliseconds = minimum,
            MaximumJitterMilliseconds = jitter,
        });
        VersionResolver version = new(registry, NullLogger.Instance);
        DependencyResolver dependencies = new(hiddenRegistry, version, cache, NullLogger.Instance);

        AssertInvalid(() => new FhirPackageManager(
            cache, registry, version, dependencies, new PackageIndexer(NullLogger.Instance, Retry(1)),
            Options("not-created", Retry(1)), NullLogger<FhirPackageManager>.Instance));
    }

    [Fact]
    public void SharedBuiltInCollaborators_AreVisitedOnce()
    {
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_root);
        FileSystemContext context = files.CreateContext(retries: 1);
        FhirPackageManagerConfiguration configuration =
            FhirPackageManagerConfiguration.CreateResolved(Options("matching"), context);
        using DiskPackageCache cache = new(
            configuration.Options.CachePath, null, null, configuration.InstallLimits, context);
        using HttpClient http = new(new RejectingHandler());
        FhirNpmRegistryClient leaf = Registry(http, context.ToOptionsSnapshot());
        RedundantRegistryClient first = RedundantRegistryClient.CreateWithFileSystemRetry([leaf], context.ToOptionsSnapshot());
        RedundantRegistryClient second = RedundantRegistryClient.CreateWithFileSystemRetry([leaf], context.ToOptionsSnapshot());
        VersionResolver version = new(second, NullLogger.Instance);
        DependencyResolver dependencies = new(first, version, cache, NullLogger.Instance);
        PackageIndexer indexer = new(NullLogger.Instance, context.ToOptionsSnapshot());
        RecordingVisitedSet visited = new();
        files.Calls.Clear();

        configuration.ValidateFileSystemCollaborators(
            [cache, first, version, dependencies, indexer, second, first, version, dependencies, leaf],
            visited);

        visited.Count.ShouldBe(7);
        visited.Accepted.ShouldBe([cache, first, leaf, version, second, dependencies, indexer]);
        visited.Attempts[cache].ShouldBe(2);
        visited.Attempts[first].ShouldBe(3);
        visited.Attempts[leaf].ShouldBe(3);
        visited.Attempts[version].ShouldBe(3);
        visited.Attempts[second].ShouldBe(2);
        visited.Attempts[dependencies].ShouldBe(2);
        visited.Attempts[indexer].ShouldBe(1);
        files.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnspecifiedPolicy_PreservesLegacyInjectedGraph(bool environmentOnly)
    {
        if (environmentOnly)
            SetEnvironment("3", "0", "0");
        using HttpClient http = new(new RejectingHandler());
        using DiskPackageCache cache = Cache("legacy", Retry(0));
        using DiskPackageCache hiddenCache = Cache("hidden", Retry(2));
        FhirNpmRegistryClient registry = Registry(http, Retry(1));
        VersionResolver version = new(Registry(http, Retry(2)), NullLogger.Instance);
        DependencyResolver dependencies = new(registry, version, hiddenCache, NullLogger.Instance);
        FhirPackageManagerOptions options = Options("unused");
        using FhirPackageManager manager = new(
            cache, registry, version, dependencies, new PackageIndexer(NullLogger.Instance, Retry(4)),
            options, NullLogger<FhirPackageManager>.Instance);

        (await manager.ListCachedSummariesAsync(
            cancellationToken: TestContext.Current.CancellationToken)).ShouldBeEmpty();

        Directory.Exists(options.CachePath).ShouldBeFalse();
        cache.FileSystemContext.Policy.RetryCount.ShouldBe(0);
    }

    [Fact]
    public async Task ThirdPartyCollaborators_RequireNoNewCapability()
    {
        using HttpClient http = new(new RejectingHandler());
        ExternalRegistry registry = new(http);
        Mock<IPackageCache> cache = new(MockBehavior.Strict);
        cache.Setup(value => value.ListPackageSummariesAsync(
                null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        Mock<IVersionResolver> version = new(MockBehavior.Strict);
        Mock<IDependencyResolver> dependencies = new(MockBehavior.Strict);
        Mock<IPackageIndexer> indexer = new(MockBehavior.Strict);
        FhirPackageManagerOptions options = Options("unused", Retry(0));
        using FhirPackageManager manager = new(
            cache.Object, registry, version.Object, dependencies.Object, indexer.Object,
            options, NullLogger<FhirPackageManager>.Instance);

        (await manager.GetPackageListingAsync("external.package", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await manager.ListCachedSummariesAsync(
            cancellationToken: TestContext.Current.CancellationToken)).ShouldBeEmpty();

        registry.ListingCalls.ShouldBe(1);
        registry.FileSystemContext.Policy.RetryCount.ShouldBe(99);
        cache.VerifyAll();
        version.VerifyNoOtherCalls();
        dependencies.VerifyNoOtherCalls();
        indexer.VerifyNoOtherCalls();
        Directory.Exists(options.CachePath).ShouldBeFalse();
    }

    [Fact]
    public void LegacyConstructors_NullAndDefaultCallsStillCompile()
    {
        // Deliberately invalid calls are compiled, not invoked against a real cache.
        Func<FhirPackageManager>[] constructors =
        [
            () => new FhirPackageManager(),
            () => new FhirPackageManager(null!),
            () => new FhirPackageManager(default!, default),
            () => new FhirPackageManager(null!, null),
            () => new FhirPackageManager(null!, null!, null!, null!, null!, null!, null!),
            () => new FhirPackageManager(default!, default!, default!, default!, default!, default!, default!, default),
        ];

        constructors.Length.ShouldBe(6);
    }

    [Fact]
    public void PublicRegistryFactory_ResolvesManagerOptionsOnceForWholeChain()
    {
        SetEnvironment("invalid", "invalid", "invalid");
        FhirPackageManagerOptions options = Options("unused", Retry(2));
        options.IncludeCiBuilds = true;
        options.IncludeHl7WebsiteFallback = true;
        using HttpClient http = new(new RejectingHandler());

        RedundantRegistryClient composite = (RedundantRegistryClient)RegistryClientFactory.BuildRegistryClient(
            options, http, NullLoggerFactory.Instance);

        composite.BuiltInClients.Count().ShouldBe(4);
        foreach (RegistryClientBase leaf in composite.BuiltInClients)
            leaf.FileSystemContext.ShouldBeSameAs(composite.FileSystemContext);
        composite.FileSystemContext.Policy.RetryCount.ShouldBe(2);
        composite.FileSystemContext.Policy.MinimumDelayMilliseconds.ShouldBe(0);
        composite.FileSystemContext.Policy.MaximumJitterMilliseconds.ShouldBe(0);
        options.FileSystemRetry.RetryCount = 0;
        composite.FileSystemContext.Policy.RetryCount.ShouldBe(2);
        Directory.Exists(options.CachePath).ShouldBeFalse();
    }

    private FhirPackageManagerOptions Options(string cache, FileSystemRetryOptions? retry = null) =>
        new()
        {
            CachePath = Path.Combine(_root, cache),
            FileSystemRetry = retry ?? new FileSystemRetryOptions(),
            IncludeCiBuilds = false,
            IncludeHl7WebsiteFallback = false,
            ResourceCacheSize = 0,
        };

    private DiskPackageCache Cache(string name, FileSystemRetryOptions retry) =>
        new(Path.Combine(_root, name), null, null, retry);

    private static FileSystemRetryOptions Retry(int retries) =>
        new() { RetryCount = retries, MinimumDelayMilliseconds = 0, MaximumJitterMilliseconds = 0 };

    private static FhirNpmRegistryClient Registry(HttpClient http, FileSystemRetryOptions retry) =>
        new(http, new RegistryEndpoint { Url = "https://offline.example/", Type = RegistryType.FhirNpm },
            NullLogger<FhirNpmRegistryClient>.Instance, retry);

    private static FhirPackageManager Inject(
        IPackageCache cache, IRegistryClient registry, IPackageIndexer indexer, FhirPackageManagerOptions options)
    {
        VersionResolver version = new(registry, NullLogger.Instance);
        DependencyResolver dependencies = new(registry, version, cache, NullLogger.Instance);
        return new FhirPackageManager(
            cache, registry, version, dependencies, indexer, options, NullLogger<FhirPackageManager>.Instance);
    }

    private static void AssertInvalid(Func<object> create)
    {
        PackageInstallException exception = Should.Throw<PackageInstallException>(() => create());
        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
        exception.Stage.ShouldBe(PackageInstallStage.PolicyValidation);
    }

    private string CreateProject()
    {
        string path = Path.Combine(_root, "project");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "package.json"), """{"name":"project.package","version":"1.0.0"}""");
        return path;
    }

    private static void SetEnvironment(string retries, string minimum, string jitter)
    {
        Environment.SetEnvironmentVariable(s_variables[0], retries);
        Environment.SetEnvironmentVariable(s_variables[1], minimum);
        Environment.SetEnvironmentVariable(s_variables[2], jitter);
    }

    public void Dispose()
    {
        for (int index = 0; index < s_variables.Length; index++)
            Environment.SetEnvironmentVariable(s_variables[index], _originals[index]);
        Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingVisitedSet : HashSet<object>, ISet<object>
    {
        internal RecordingVisitedSet() : base(ReferenceEqualityComparer.Instance)
        {
        }

        internal Dictionary<object, int> Attempts { get; } = new(ReferenceEqualityComparer.Instance);

        internal List<object> Accepted { get; } = [];

        bool ISet<object>.Add(object value)
        {
            Attempts[value] = Attempts.GetValueOrDefault(value) + 1;
            bool added = Add(value);
            if (added)
                Accepted.Add(value);
            return added;
        }
    }

    private sealed class ExternalRegistry(HttpClient http) : RegistryClientBase(
        http,
        new RegistryEndpoint { Url = "https://external.example/", Type = RegistryType.FhirNpm },
        NullLogger.Instance,
        Retry(99))
    {
        internal int ListingCalls { get; private set; }

        public override IReadOnlyList<PackageNameType> SupportedNameTypes => [];

        public override IReadOnlyList<VersionType> SupportedVersionTypes => [];

        public override Task<PackageListing?> GetPackageListingAsync(
            string packageId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListingCalls++;
            return Task.FromResult<PackageListing?>(null);
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This test must not make an HTTP request.");
    }
}

internal static class CompositionTestSupport
{
    internal static ScriptedFileSystemPrimitives SystemFiles(string tempRoot)
    {
        ScriptedFileSystemPrimitives files = new(SystemFileSystemPrimitives.Instance.Platform);
        files.UseSystemFiles();
        files.Set("GetTempPath", FileSystemReplayOutcome<string>.Complete(tempRoot));
        files.Set<IEnumerator<string>>("CreateDirectoryEnumerator",
            call => SystemFileSystemPrimitives.Instance.CreateDirectoryEnumerator(call.Path!));
        files.Set<FileStream>("OpenFile", call => SystemFileSystemPrimitives.Instance.OpenFile(
            call.Path!, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            OperatingSystem.IsMacOS() ? FileShare.None : FileShare.ReadWrite, 1, FileOptions.None));
        files.Set("TryLock", FileSystemReplayOutcome<bool>.Complete(true));
        files.Set("Unlock", FileSystemReplayOutcome<bool>.Complete(true));
        return files;
    }
}
