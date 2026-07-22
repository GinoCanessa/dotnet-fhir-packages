// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using FhirPkg.Cache;
using FhirPkg.Indexing;
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
