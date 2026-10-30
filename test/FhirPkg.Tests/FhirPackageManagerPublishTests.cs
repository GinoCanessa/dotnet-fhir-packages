// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
using FhirPkg.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

[Collection("EnvironmentVariable")]
public sealed class FhirPackageManagerPublishTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"fhirpkg-manager-publish-{Guid.NewGuid():N}");

    public FhirPackageManagerPublishTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Theory]
    [InlineData(RegistryType.FhirNpm)]
    [InlineData(RegistryType.Npm)]
    public async Task Publish_ManagerContextCoversProbeLengthExtractionTargetAndCleanup(RegistryType type)
    {
        string path = CreateArchiveFile("example.package", "1.0.0");
        byte[] originalArchive = File.ReadAllBytes(path);
        CaptureHandler handler = new();
        using HttpClient http = new(handler);
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_tempRoot);
        using FhirPackageManager manager = CreateResolvedManager(files.CreateContext(retries: 1), http);
        files.Enqueue("Probe", ScriptedFileSystemPrimitives.NativeFailure<FileSystemProbeResult>(5, "publish probe", path));
        files.Enqueue("GetHandleLength", ScriptedFileSystemPrimitives.NativeFailure<long>(1117, "publish length", path));
        files.Enqueue("ReadAtAsync", ScriptedFileSystemPrimitives.NativeFailure<int>(1117, "publish read", path));
        files.Enqueue("DeleteEntry", ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "publish cleanup"));
        string[] variables =
        [
            FileSystemRetryOptions.RetryCountEnvironmentVariable,
            FileSystemRetryOptions.MinimumDelayMillisecondsEnvironmentVariable,
            FileSystemRetryOptions.MaximumJitterMillisecondsEnvironmentVariable,
        ];
        string?[] originals = variables.Select(Environment.GetEnvironmentVariable).ToArray();
        try
        {
            foreach (string variable in variables)
                Environment.SetEnvironmentVariable(variable, "invalid-after-manager-construction");

            PublishResult result = await manager.PublishAsync(
                path, CreateTargetEndpoint() with { Type = type }, TestContext.Current.CancellationToken);

            result.Success.ShouldBeTrue();
            handler.RequestCount.ShouldBe(1);
            handler.ContentType.ShouldBe(type == RegistryType.Npm ? "application/json" : "application/gzip");
            files.AsyncWaits.ShouldBe([10, 10, 10, 10]);
            files.Waits.ShouldBeEmpty();
            FileSystemPrimitiveCall[] opens = files.Calls.Where(call =>
                call.Operation == "OpenFileHandle" && call.Path == path).ToArray();
            opens.ShouldHaveSingleItem();
            opens[0].Share.ShouldBe(FileShare.Read);
            opens[0].Options.ShouldBe(FileOptions.Asynchronous | FileOptions.SequentialScan);
            files.Calls.Count(call => call.Operation == "GetHandleLength" && call.Path == path)
                .ShouldBeGreaterThanOrEqualTo(3);
            files.Count("GetTempPath").ShouldBe(type == RegistryType.Npm ? 2 : 1);
            files.Calls.ShouldContain(call => call.Operation == "DeleteEntry"
                && call.Path!.StartsWith(_tempRoot, StringComparison.Ordinal));
            Directory.GetDirectories(_tempRoot).ShouldBeEmpty();
            File.ReadAllBytes(path).ShouldBe(originalArchive);
        }
        finally
        {
            for (int index = 0; index < variables.Length; index++)
                Environment.SetEnvironmentVariable(variables[index], originals[index]);
        }
    }

    [Fact]
    public async Task Publish_TerminalTargetReadReturnsFinalContextWithoutAnotherRequestOrOpen()
    {
        string path = CreateArchiveFile("example.package", "1.0.0");
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_tempRoot);
        CaptureHandler handler = new(() =>
            files.Set<int>("ReadAtAsync", call =>
                ScriptedFileSystemPrimitives.NativeFailure<int>(1117, "publish body read", call.Path)));
        using HttpClient http = new(handler);
        using FhirPackageManager manager = CreateResolvedManager(files.CreateContext(retries: 2), http);

        PublishResult result = await manager.PublishAsync(
            path, CreateTargetEndpoint() with { Type = RegistryType.FhirNpm },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        FileSystemFailureContext.TryGetResult(result, out FileSystemFailureContext? failure).ShouldBeTrue();
        failure!.Path.ShouldBe(path);
        failure.AttemptCount.ShouldBe(3);
        failure.RetriesUsed.ShouldBe(2);
        string message = result.Message.ShouldNotBeNull();
        message.ShouldContain(path);
        message.ShouldContain("attempts 3, retries 2");
        handler.RequestCount.ShouldBe(1);
        files.Calls.Count(call => call.Operation == "OpenFileHandle" && call.Path == path).ShouldBe(1);
        files.AsyncWaits.ShouldBe([10, 10]);
        Directory.GetDirectories(_tempRoot).ShouldBeEmpty();
    }

    [Fact]
    public async Task Publish_MissingTarballDoesNotSpendFaultRetries()
    {
        string path = Path.Combine(_tempRoot, "missing.tgz");
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_tempRoot);
        CaptureHandler handler = new();
        using HttpClient http = new(handler);
        using FhirPackageManager manager = CreateResolvedManager(files.CreateContext(), http);

        FileNotFoundException exception = await Should.ThrowAsync<FileNotFoundException>(() =>
            manager.PublishAsync(path, CreateTargetEndpoint(), TestContext.Current.CancellationToken));

        exception.FileName.ShouldBe(path);
        files.Count("Probe").ShouldBe(1);
        files.Count("OpenFileHandle").ShouldBe(0);
        files.Count("GetTempPath").ShouldBe(0);
        files.AsyncWaits.ShouldBeEmpty();
        files.Waits.ShouldBeEmpty();
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task PublishAsync_UsesExactTargetInsteadOfMatchingReadComposite()
    {
        CaptureHandler handler = new();
        using HttpClient httpClient = new(handler)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        RegistryEndpoint configuredReadEndpoint =
            CreateConfiguredReadEndpoint();
        FhirPackageManagerOptions options = CreateOptions(
            configuredReadEndpoint);
        Mock<IPackageCache> cache = new();
        cache.SetupGet(value => value.CacheDirectory)
            .Returns(_tempRoot);
        Mock<IRegistryClient> readClient = new();
        readClient.SetupGet(value => value.Endpoint)
            .Returns(configuredReadEndpoint);
        FhirPackageManager manager =
            FhirPackageManager.CreateWithHttpClient(
                cache.Object,
                readClient.Object,
                new Mock<IVersionResolver>().Object,
                new Mock<IDependencyResolver>().Object,
                new Mock<IPackageIndexer>().Object,
                options,
                NullLogger<FhirPackageManager>.Instance,
                memoryCache: null,
                PackageInstallLimits.ResolveManager(
                    options.InstallLimits),
                httpClient,
                NullLoggerFactory.Instance,
                redirectsControlled: true);
        string archivePath = CreateArchiveFile(
            "example.package",
            "1.0.0");
        RegistryEndpoint target = CreateTargetEndpoint();

        PublishResult result = await manager.PublishAsync(
            archivePath,
            target,
            TestContext.Current.CancellationToken);

        AssertExactTargetRequest(handler);
        result.Success.ShouldBeTrue();
        readClient.Verify(value => value.PublishAsync(
            It.IsAny<PackageReference>(),
            It.IsAny<Stream>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PublishAsync_DependencyInjectionUsesExactTargetConfiguration()
    {
        CaptureHandler handler = new();
        RegistryEndpoint configuredReadEndpoint =
            CreateConfiguredReadEndpoint();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddFhirPackageManagement(options =>
        {
            options.CachePath = Path.Combine(
                _tempRoot,
                "cache");
            options.Registries.Add(
                configuredReadEndpoint);
            options.IncludeCiBuilds = false;
            options.IncludeHl7WebsiteFallback = false;
        });
        services.AddHttpClient("FhirPackages")
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using ServiceProvider provider =
            services.BuildServiceProvider();
        IFhirPackageManager manager =
            provider.GetRequiredService<IFhirPackageManager>();
        string archivePath = CreateArchiveFile(
            "example.package",
            "1.0.0");

        PublishResult result = await manager.PublishAsync(
            archivePath,
            CreateTargetEndpoint(),
            TestContext.Current.CancellationToken);

        AssertExactTargetRequest(handler);
        result.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task PublishAsync_FhirNpmUsesTheValidatedFileHandle()
    {
        string archivePath = CreateArchiveFile(
            "example.package",
            "1.0.0");
        byte[] validatedArchive =
            File.ReadAllBytes(archivePath);
        byte[] replacementArchive = CreateArchiveBytes(
            "replacement.package",
            "9.9.9");
        CaptureHandler handler = new(() =>
        {
            try
            {
                string movedPath = archivePath + ".validated";
                File.Move(archivePath, movedPath);
                File.WriteAllBytes(
                    archivePath,
                    replacementArchive);
            }
            catch (IOException)
            {
                // Windows sharing rules prevent the replacement while the
                // validated handle is open; Unix permits the path swap.
            }
        });
        using HttpClient httpClient = new(handler)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        RegistryEndpoint configuredReadEndpoint =
            CreateConfiguredReadEndpoint();
        FhirPackageManagerOptions options = CreateOptions(
            configuredReadEndpoint);
        Mock<IPackageCache> cache = new();
        cache.SetupGet(value => value.CacheDirectory)
            .Returns(_tempRoot);
        Mock<IRegistryClient> readClient = new();
        readClient.SetupGet(value => value.Endpoint)
            .Returns(configuredReadEndpoint);
        using FhirPackageManager manager =
            FhirPackageManager.CreateWithHttpClient(
                cache.Object,
                readClient.Object,
                new Mock<IVersionResolver>().Object,
                new Mock<IDependencyResolver>().Object,
                new Mock<IPackageIndexer>().Object,
                options,
                NullLogger<FhirPackageManager>.Instance,
                memoryCache: null,
                PackageInstallLimits.ResolveManager(
                    options.InstallLimits),
                httpClient,
                NullLoggerFactory.Instance,
                redirectsControlled: true);
        RegistryEndpoint target = CreateTargetEndpoint() with
        {
            Type = RegistryType.FhirNpm,
        };

        PublishResult result = await manager.PublishAsync(
            archivePath,
            target,
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        handler.ContentType.ShouldBe("application/gzip");
        handler.Body.ShouldBe(validatedArchive);
        readClient.Verify(value => value.PublishAsync(
            It.IsAny<PackageReference>(),
            It.IsAny<Stream>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(RegistryType.FhirNpm)]
    [InlineData(RegistryType.Npm)]
    public async Task Publish_ManagerAndNpmNormalizationUseAsyncPerChildWaits(RegistryType type)
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package/package.json", """{"name":"example.package","version":"1.0.0"}"""),
            ArbitraryTarBuilder.File("openapi/schema.json", "{}"),
            ArbitraryTarBuilder.File("other/data.txt", "payload"));
        string path = Path.Combine(_tempRoot, "ancillary.tgz");
        byte[] bytes = archive.ToArray();
        File.WriteAllBytes(path, bytes);
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_tempRoot);
        HashSet<string> failedDestinations = [];
        files.Set<bool>("MovePath", call =>
            Path.GetFileName(call.DestinationPath) == "openapi" && failedDestinations.Add(call.DestinationPath!)
                ? ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "publish normalization", call.Path)
                : SystemFileSystemPrimitives.Instance.MovePath(call.Path!, call.DestinationPath!, call.Flags == 1));
        CaptureHandler handler = new();
        using HttpClient http = new(handler);
        using FhirPackageManager manager = CreateResolvedManager(files.CreateContext(retries: 1), http);

        PublishResult result = await manager.PublishAsync(
            path, CreateTargetEndpoint() with { Type = type }, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        handler.RequestCount.ShouldBe(1);
        int normalizations = type == RegistryType.Npm ? 2 : 1;
        failedDestinations.Count.ShouldBe(normalizations);
        files.Count("MovePath").ShouldBe(3 * normalizations);
        files.Calls.Where(call => call.Operation == "MovePath" && Path.GetFileName(call.DestinationPath) == "other")
            .Select(call => call.Path).Distinct().Count().ShouldBe(normalizations);
        files.AsyncWaits.ShouldBe(type == RegistryType.Npm ? [10, 10] : [10]);
        files.Waits.ShouldBeEmpty();
        if (type == RegistryType.FhirNpm)
        {
            handler.Body.ShouldBe(bytes);
        }
        else
        {
            using JsonDocument document = JsonDocument.Parse(handler.Body);
            JsonElement attachment = document.RootElement.GetProperty("_attachments")
                .GetProperty("example.package-1.0.0.tgz");
            Convert.FromBase64String(attachment.GetProperty("data").GetString()!).ShouldBe(bytes);
            attachment.GetProperty("length").GetInt64().ShouldBe(bytes.Length);
        }
        Directory.GetDirectories(_tempRoot).ShouldBeEmpty();
        File.ReadAllBytes(path).ShouldBe(bytes);
    }

    [Fact]
    public async Task Publish_NormalizationExhaustionStopsBeforeAnyRegistryRequest()
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File("package/package.json", """{"name":"example.package","version":"1.0.0"}"""),
            ArbitraryTarBuilder.File("openapi/schema.json", "{}"));
        string path = Path.Combine(_tempRoot, "normalization-failure.tgz");
        File.WriteAllBytes(path, archive.ToArray());
        ScriptedFileSystemPrimitives files = CompositionTestSupport.SystemFiles(_tempRoot);
        files.Set<bool>("MovePath", call =>
            ScriptedFileSystemPrimitives.NativeFailure<bool>(5, "publish normalization", call.Path));
        CaptureHandler handler = new();
        using HttpClient http = new(handler);
        using FhirPackageManager manager = CreateResolvedManager(files.CreateContext(retries: 2), http);

        IOException exception = await Should.ThrowAsync<IOException>(() => manager.PublishAsync(
            path, CreateTargetEndpoint(), TestContext.Current.CancellationToken));

        FileSystemFailureContext.TryGetTerminal(exception, out FileSystemFailureContext? context).ShouldBeTrue();
        context!.RetriesUsed.ShouldBe(2);
        handler.RequestCount.ShouldBe(0);
        files.Count("MovePath").ShouldBe(3);
        files.Count("GetTempPath").ShouldBe(1);
        files.AsyncWaits.ShouldBe([10, 10]);
        files.Waits.ShouldBeEmpty();
        Directory.GetDirectories(_tempRoot).ShouldBeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static RegistryEndpoint CreateConfiguredReadEndpoint() =>
        new()
        {
            Url = "https://registry.example/custom/",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer read-token",
            CustomHeaders = [("X-Publish-Mode", "read")],
        };

    private FhirPackageManager CreateResolvedManager(FileSystemContext context, HttpClient http)
    {
        Mock<IPackageCache> cache = new();
        cache.SetupGet(value => value.CacheDirectory).Returns(_tempRoot);
        FhirPackageManagerConfiguration configuration = FhirPackageManagerConfiguration.CreateResolved(
            CreateOptions(CreateConfiguredReadEndpoint()), context);
        return FhirPackageManager.CreateWithConfiguration(
            cache.Object, new Mock<IRegistryClient>().Object, new Mock<IVersionResolver>().Object,
            new Mock<IDependencyResolver>().Object, new Mock<IPackageIndexer>().Object,
            configuration, NullLogger<FhirPackageManager>.Instance, null, http,
            NullLoggerFactory.Instance, redirectsControlled: true);
    }

    private static RegistryEndpoint CreateTargetEndpoint() =>
        new()
        {
            Url = "https://registry.example/custom/",
            Type = RegistryType.Npm,
            AuthHeaderValue = "Bearer target-token",
            CustomHeaders = [("X-Publish-Mode", "target")],
        };

    private FhirPackageManagerOptions CreateOptions(
        RegistryEndpoint readEndpoint) =>
        new()
        {
            CachePath = Path.Combine(_tempRoot, "cache"),
            Registries = [readEndpoint],
            IncludeCiBuilds = false,
            IncludeHl7WebsiteFallback = false,
        };

    private string CreateArchiveFile(
        string name,
        string version)
    {
        byte[] archive = CreateArchiveBytes(name, version);
        string path = Path.Combine(
            _tempRoot,
            $"{Guid.NewGuid():N}.tgz");
        File.WriteAllBytes(path, archive);
        return path;
    }

    private static byte[] CreateArchiveBytes(
        string name,
        string version)
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File(
                "package/package.json",
                $$"""
                  {
                    "name": "{{name}}",
                    "version": "{{version}}"
                  }
                  """));
        return archive.ToArray();
    }

    private static void AssertExactTargetRequest(
        CaptureHandler handler)
    {
        handler.RequestCount.ShouldBe(1);
        handler.RequestUri.ShouldBe(
            new Uri(
                "https://registry.example/custom/example.package"));
        handler.Authorization.ShouldBe(
            "Bearer target-token");
        handler.PublishMode.ShouldBe("target");
        handler.ContentType.ShouldBe("application/json");
        using JsonDocument document =
            JsonDocument.Parse(handler.Body);
        document.RootElement
            .GetProperty("_attachments")
            .TryGetProperty(
                "example.package-1.0.0.tgz",
                out _)
            .ShouldBeTrue();
    }

    private sealed class CaptureHandler(
        Action? beforeReadContent = null)
        : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        internal Uri? RequestUri { get; private set; }

        internal string? Authorization { get; private set; }

        internal string? PublishMode { get; private set; }

        internal string? ContentType { get; private set; }

        internal byte[] Body { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.TryGetValues(
                "Authorization",
                out IEnumerable<string>? authorizationValues)
                ? authorizationValues.Single()
                : null;
            PublishMode = request.Headers.TryGetValues(
                "X-Publish-Mode",
                out IEnumerable<string>? modeValues)
                ? modeValues.Single()
                : null;
            ContentType =
                request.Content?.Headers.ContentType?.MediaType;
            beforeReadContent?.Invoke();
            Body = request.Content is null
                ? []
                : await request.Content
                    .ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
            return new HttpResponseMessage(
                HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
        }
    }
}
