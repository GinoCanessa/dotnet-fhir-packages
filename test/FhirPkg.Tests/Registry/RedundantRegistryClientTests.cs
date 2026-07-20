// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;
using FhirPkg.Registry;
using Shouldly;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FhirPkg.Tests.Registry;

public class RedundantRegistryClientTests
{
    private static readonly RegistryEndpoint TestEndpoint = new()
    {
        Url = "https://test.registry.org/",
        Type = RegistryType.FhirNpm
    };

    private static Mock<IRegistryClient> CreateMockClient(
        RegistryEndpoint? endpoint = null)
    {
        Mock<IRegistryClient> mock = new Mock<IRegistryClient>();
        mock.Setup(c => c.Endpoint).Returns(endpoint ?? TestEndpoint);
        mock.Setup(c => c.SupportedNameTypes).Returns(
            Enum.GetValues<PackageNameType>().ToList().AsReadOnly());
        mock.Setup(c => c.SupportedVersionTypes).Returns(
            Enum.GetValues<VersionType>().ToList().AsReadOnly());
        return mock;
    }

    private sealed class PublishingRegistryClient : RegistryClientBase
    {
        public PublishingRegistryClient(
            RegistryHttpTransport transport,
            RegistryEndpoint endpoint)
            : base(transport, endpoint, NullLogger.Instance)
        {
        }

        public override IReadOnlyList<PackageNameType> SupportedNameTypes => [];

        public override IReadOnlyList<VersionType> SupportedVersionTypes => [];

        public override async Task<PublishResult> PublishAsync(
            PackageReference reference,
            Stream tarballStream,
            CancellationToken cancellationToken = default)
        {
            using HttpResponseMessage response = await PutStreamAsync(
                Endpoint.Url,
                tarballStream,
                "application/gzip",
                cancellationToken);
            return new PublishResult
            {
                Success = true,
                StatusCode = response.StatusCode
            };
        }
    }

    private sealed class PublishSequenceHandler : HttpMessageHandler
    {
        private int _requestCount;

        public List<byte[]> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            int requestNumber = Interlocked.Increment(ref _requestCount);
            return new HttpResponseMessage(
                requestNumber == 1
                    ? System.Net.HttpStatusCode.InternalServerError
                    : System.Net.HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task ResolveAsync_FirstClientSucceeds_ReturnsResult()
    {
        ResolvedDirective expected = new ResolvedDirective
        {
            Reference = new PackageReference("hl7.fhir.r4.core", "4.0.1"),
            TarballUri = new Uri("https://test.registry.org/package.tgz")
        };

        Mock<IRegistryClient> client1 = CreateMockClient();
        client1.Setup(c => c.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        Mock<IRegistryClient> client2 = CreateMockClient();

        RedundantRegistryClient sut = new RedundantRegistryClient(client1.Object, client2.Object);
        PackageDirective directive = PackageDirective.Parse("hl7.fhir.r4.core#4.0.1");

        ResolvedDirective? result = await sut.ResolveAsync(directive, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(expected);
        client2.Verify(c => c.ResolveAsync(
            It.IsAny<PackageDirective>(),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_FirstClientFails_FallsBackToSecond()
    {
        ResolvedDirective expected = new ResolvedDirective
        {
            Reference = new PackageReference("hl7.fhir.r4.core", "4.0.1"),
            TarballUri = new Uri("https://test2.registry.org/package.tgz")
        };

        Mock<IRegistryClient> client1 = CreateMockClient();
        client1.Setup(c => c.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection failed"));

        Mock<IRegistryClient> client2 = CreateMockClient();
        client2.Setup(c => c.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        RedundantRegistryClient sut = new RedundantRegistryClient(client1.Object, client2.Object);
        PackageDirective directive = PackageDirective.Parse("hl7.fhir.r4.core#4.0.1");

        ResolvedDirective? result = await sut.ResolveAsync(directive, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task ResolveAsync_AllClientsFail_ReturnsNull()
    {
        Mock<IRegistryClient> client1 = CreateMockClient();
        client1.Setup(c => c.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection failed"));

        Mock<IRegistryClient> client2 = CreateMockClient();
        client2.Setup(c => c.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection failed"));

        RedundantRegistryClient sut = new RedundantRegistryClient(client1.Object, client2.Object);
        PackageDirective directive = PackageDirective.Parse("hl7.fhir.r4.core#4.0.1");

        ResolvedDirective? result = await sut.ResolveAsync(directive, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_AllClientsTimeout_ThrowsTypedTimeout()
    {
        Mock<IRegistryClient> client1 = CreateMockClient();
        client1.Setup(c => c.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RegistryResponseTimeoutException("timed out"));
        Mock<IRegistryClient> client2 = CreateMockClient();
        client2.Setup(c => c.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RegistryResponseTimeoutException("timed out"));
        RedundantRegistryClient sut = new(client1.Object, client2.Object);
        PackageDirective directive =
            PackageDirective.Parse("hl7.fhir.r4.core#4.0.1");

        await Should.ThrowAsync<RegistryResponseTimeoutException>(
            () => sut.ResolveAsync(
                directive,
                cancellationToken:
                    TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_FirstReturnsNull_FallsBackToSecond()
    {
        ResolvedDirective expected = new ResolvedDirective
        {
            Reference = new PackageReference("hl7.fhir.r4.core", "4.0.1"),
            TarballUri = new Uri("https://test2.registry.org/package.tgz")
        };

        Mock<IRegistryClient> client1 = CreateMockClient();
        client1.Setup(c => c.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedDirective?)null);

        Mock<IRegistryClient> client2 = CreateMockClient();
        client2.Setup(c => c.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        RedundantRegistryClient sut = new RedundantRegistryClient(client1.Object, client2.Object);
        PackageDirective directive = PackageDirective.Parse("hl7.fhir.r4.core#4.0.1");

        ResolvedDirective? result = await sut.ResolveAsync(directive, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(expected);
    }

    [Fact]
    public void Constructor_EmptyClients_Throws()
    {
        Func<RedundantRegistryClient> act = () => new RedundantRegistryClient(Array.Empty<IRegistryClient>());

        Should.Throw<ArgumentException>(() => act());
    }

    [Fact]
    public async Task SearchAsync_QueriesAllClients_MergesResults()
    {
        Mock<IRegistryClient> client1 = CreateMockClient();
        client1.Setup(c => c.SearchAsync(It.IsAny<PackageSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CatalogEntry>
            {
                new() { Name = "package.a" },
                new() { Name = "package.b" }
            }.AsReadOnly());

        Mock<IRegistryClient> client2 = CreateMockClient();
        client2.Setup(c => c.SearchAsync(It.IsAny<PackageSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CatalogEntry>
            {
                new() { Name = "package.b" }, // duplicate
                new() { Name = "package.c" }
            }.AsReadOnly());

        RedundantRegistryClient sut = new RedundantRegistryClient(client1.Object, client2.Object);
        IReadOnlyList<CatalogEntry> results = await sut.SearchAsync(new PackageSearchCriteria { Name = "package" }, cancellationToken: TestContext.Current.CancellationToken);

        results.Count.ShouldBe(3);
        results.Select(r => r.Name).ShouldBe(new[] { "package.a", "package.b", "package.c" }, ignoreOrder: true);
    }

    [Fact]
    public async Task DownloadAsync_SourceRegistry_UsesOnlyProducingClient()
    {
        RegistryEndpoint firstEndpoint = new()
        {
            Url = "https://first.example.com/registry",
            Type = RegistryType.FhirNpm
        };
        RegistryEndpoint sourceEndpoint = new()
        {
            Url = "https://source.example.com/registry/",
            Type = RegistryType.FhirNpm
        };
        Mock<IRegistryClient> first = CreateMockClient(firstEndpoint);
        Mock<IRegistryClient> source = CreateMockClient(sourceEndpoint);
        PackageDownloadResult expected = new()
        {
            Content = new MemoryStream([1, 2, 3]),
            ContentType = "application/gzip",
            ContentLength = 3
        };
        source.Setup(client => client.DownloadAsync(
                It.IsAny<ResolvedDirective>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        RedundantRegistryClient sut = new(first.Object, source.Object);
        ResolvedDirective resolved = new()
        {
            Reference = new PackageReference("example.package", "1.0.0"),
            TarballUri = new Uri("https://cdn.example.com/package.tgz"),
            SourceRegistry = sourceEndpoint
        };

        PackageDownloadResult? actual = await sut.DownloadAsync(
            resolved,
            TestContext.Current.CancellationToken);

        actual.ShouldBeSameAs(expected);
        first.Verify(client => client.DownloadAsync(
            It.IsAny<ResolvedDirective>(),
            It.IsAny<CancellationToken>()), Times.Never);
        source.Verify(client => client.DownloadAsync(
            resolved,
            It.IsAny<CancellationToken>()), Times.Once);
        await expected.DisposeAsync();
    }

    [Fact]
    public async Task DownloadAsync_SourceRegistryFailure_DoesNotFallback()
    {
        RegistryEndpoint firstEndpoint = new()
        {
            Url = "https://first.example.com/",
            Type = RegistryType.FhirNpm
        };
        RegistryEndpoint sourceEndpoint = new()
        {
            Url = "https://source.example.com/",
            Type = RegistryType.FhirNpm
        };
        Mock<IRegistryClient> first = CreateMockClient(firstEndpoint);
        Mock<IRegistryClient> source = CreateMockClient(sourceEndpoint);
        source.Setup(client => client.DownloadAsync(
                It.IsAny<ResolvedDirective>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("source failed"));
        RedundantRegistryClient sut = new(first.Object, source.Object);
        ResolvedDirective resolved = new()
        {
            Reference = new PackageReference("example.package", "1.0.0"),
            TarballUri = new Uri("https://source.example.com/package.tgz"),
            SourceRegistry = sourceEndpoint
        };

        await Should.ThrowAsync<HttpRequestException>(
            () => sut.DownloadAsync(
                resolved,
                TestContext.Current.CancellationToken));

        first.Verify(client => client.DownloadAsync(
            It.IsAny<ResolvedDirective>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DownloadAsync_WithoutSourceRegistry_RetainsFallback()
    {
        Mock<IRegistryClient> first = CreateMockClient();
        Mock<IRegistryClient> second = CreateMockClient();
        first.Setup(client => client.DownloadAsync(
                It.IsAny<ResolvedDirective>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PackageDownloadResult?)null);
        PackageDownloadResult expected = new()
        {
            Content = new MemoryStream([1]),
            ContentType = "application/gzip",
            ContentLength = 1
        };
        second.Setup(client => client.DownloadAsync(
                It.IsAny<ResolvedDirective>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        RedundantRegistryClient sut = new(first.Object, second.Object);
        ResolvedDirective resolved = new()
        {
            Reference = new PackageReference("example.package", "1.0.0"),
            TarballUri = new Uri("https://cdn.example.com/package.tgz")
        };

        PackageDownloadResult? actual = await sut.DownloadAsync(
            resolved,
            TestContext.Current.CancellationToken);

        actual.ShouldBeSameAs(expected);
        await expected.DisposeAsync();
    }

    [Fact]
    public async Task PublishAsync_FirstHttpFailure_SecondReceivesFullOpenStream()
    {
        PublishSequenceHandler handler = new();
        using HttpClient httpClient = new(handler)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
        RegistryHttpTransport transport =
            RegistryHttpTransport.CreateRedirectControlled(
                httpClient,
                TimeSpan.FromSeconds(5),
                maxRedirects: 2);
        PublishingRegistryClient first = new(
            transport,
            new RegistryEndpoint
            {
                Url = "https://first.example.com/",
                Type = RegistryType.FhirNpm
            });
        PublishingRegistryClient second = new(
            transport,
            new RegistryEndpoint
            {
                Url = "https://second.example.com/",
                Type = RegistryType.FhirNpm
            });
        RedundantRegistryClient sut = new(first, second);
        byte[] payload = [1, 2, 3, 4, 5];
        using MemoryStream stream = new(payload);

        PublishResult result = await sut.PublishAsync(
            new PackageReference("example.package", "1.0.0"),
            stream,
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        handler.Bodies.Count.ShouldBe(2);
        handler.Bodies[0].ShouldBe(payload);
        handler.Bodies[1].ShouldBe(payload);
        stream.CanRead.ShouldBeTrue();
    }
}
