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

        result.ShouldNotBeNull();
        result.Reference.ShouldBe(expected.Reference);
        result.TarballUri.ShouldBe(expected.TarballUri);
        result.SourceRegistry.ShouldBe(client1.Object.Endpoint);
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

        result.ShouldNotBeNull();
        result.Reference.ShouldBe(expected.Reference);
        result.TarballUri.ShouldBe(expected.TarballUri);
        result.SourceRegistry.ShouldBe(client2.Object.Endpoint);
    }

    [Fact]
    public async Task ResolveAsync_AllClientsFail_ThrowsAggregate()
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

        RegistryOperationException exception =
            await Should.ThrowAsync<RegistryOperationException>(
                () => sut.ResolveAsync(
                    directive,
                    cancellationToken: TestContext.Current.CancellationToken));

        exception.Operation.ShouldBe("resolve");
        exception.Failures.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ResolveAsync_AllClientsTimeout_ThrowsAggregateWithTimeoutFailures()
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

        RegistryOperationException exception =
            await Should.ThrowAsync<RegistryOperationException>(
            () => sut.ResolveAsync(
                directive,
                cancellationToken:
                    TestContext.Current.CancellationToken));

        exception.Failures.ShouldAllBe(failure =>
            failure.Category == RegistryFailureCategory.Timeout);
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

        result.ShouldNotBeNull();
        result.Reference.ShouldBe(expected.Reference);
        result.TarballUri.ShouldBe(expected.TarballUri);
        result.SourceRegistry.ShouldBe(client2.Object.Endpoint);
    }

    [Fact]
    public async Task GetPackageListingAsync_AllNull_ReturnsNull()
    {
        Mock<IRegistryClient> first = CreateMockClient(CreateEndpoint("first"));
        Mock<IRegistryClient> second = CreateMockClient(CreateEndpoint("second"));
        first.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PackageListing?)null);
        second.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PackageListing?)null);
        RedundantRegistryClient sut = new(first.Object, second.Object);

        PackageListing? listing = await sut.GetPackageListingAsync(
            "example.package",
            TestContext.Current.CancellationToken);

        listing.ShouldBeNull();
    }

    [Fact]
    public async Task GetPackageListingAsync_RespectsConfiguredParallelism()
    {
        int active = 0;
        int maxActive = 0;
        async Task<PackageListing?> QueryAsync()
        {
            int current = Interlocked.Increment(ref active);
            int observed;
            do
            {
                observed = Volatile.Read(ref maxActive);
            }
            while (current > observed
                && Interlocked.CompareExchange(
                    ref maxActive,
                    current,
                    observed) != observed);

            await Task.Delay(20, TestContext.Current.CancellationToken);
            Interlocked.Decrement(ref active);
            return null;
        }

        Mock<IRegistryClient> first = CreateMockClient(CreateEndpoint("first"));
        Mock<IRegistryClient> second = CreateMockClient(CreateEndpoint("second"));
        first.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .Returns(QueryAsync);
        second.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .Returns(QueryAsync);
        RedundantRegistryClient sut = new(
            [first.Object, second.Object],
            maxParallelRegistryQueries: 1);

        await sut.GetPackageListingAsync(
            "example.package",
            TestContext.Current.CancellationToken);

        maxActive.ShouldBe(1);
    }

    [Fact]
    public async Task GetPackageListingAsync_NestedCompositeUsesOneGlobalLimit()
    {
        int active = 0;
        int maxActive = 0;
        async Task<PackageListing?> QueryAsync()
        {
            int current = Interlocked.Increment(ref active);
            int observed;
            do
            {
                observed = Volatile.Read(ref maxActive);
            }
            while (current > observed
                && Interlocked.CompareExchange(
                    ref maxActive,
                    current,
                    observed) != observed);

            await Task.Delay(20, TestContext.Current.CancellationToken);
            Interlocked.Decrement(ref active);
            return null;
        }

        Mock<IRegistryClient>[] clients =
        [
            CreateMockClient(CreateEndpoint("first")),
            CreateMockClient(CreateEndpoint("second")),
            CreateMockClient(CreateEndpoint("third")),
            CreateMockClient(CreateEndpoint("fourth")),
        ];
        foreach (Mock<IRegistryClient> client in clients)
        {
            client.Setup(value => value.GetPackageListingAsync(
                    "example.package",
                    It.IsAny<CancellationToken>()))
                .Returns(QueryAsync);
        }

        RedundantRegistryClient firstInner =
            new(clients[0].Object, clients[1].Object);
        RedundantRegistryClient secondInner =
            new(clients[2].Object, clients[3].Object);
        RedundantRegistryClient sut = new(
            [firstInner, secondInner],
            maxParallelRegistryQueries: 2);

        await sut.GetPackageListingAsync(
            "example.package",
            TestContext.Current.CancellationToken);

        maxActive.ShouldBe(2);
    }

    [Fact]
    public async Task GetPackageListingAsync_MixedNullAndError_ThrowsAggregate()
    {
        Mock<IRegistryClient> first = CreateMockClient(CreateEndpoint("first"));
        Mock<IRegistryClient> second = CreateMockClient(CreateEndpoint("second"));
        first.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PackageListing?)null);
        second.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        RedundantRegistryClient sut = new(first.Object, second.Object);

        RegistryOperationException exception =
            await Should.ThrowAsync<RegistryOperationException>(
                () => sut.GetPackageListingAsync(
                    "example.package",
                    TestContext.Current.CancellationToken));

        exception.Operation.ShouldBe("get-package-listing");
        exception.Failures.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetPackageListingAsync_DisjointVersions_MergesWithProvenance()
    {
        RegistryEndpoint firstEndpoint = CreateEndpoint("first");
        RegistryEndpoint secondEndpoint = CreateEndpoint("second");
        Mock<IRegistryClient> first = CreateMockClient(firstEndpoint);
        Mock<IRegistryClient> second = CreateMockClient(secondEndpoint);
        first.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing(
                "example.package",
                "1.0.0",
                CreateVersion("example.package", "1.0.0", firstEndpoint)));
        second.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing(
                "example.package",
                "2.0.0",
                CreateVersion("example.package", "2.0.0", secondEndpoint)));
        RedundantRegistryClient sut = new(first.Object, second.Object);

        PackageListing? listing = await sut.GetPackageListingAsync(
            "example.package",
            TestContext.Current.CancellationToken);

        listing.ShouldNotBeNull();
        listing.IsComplete.ShouldBeTrue();
        listing.Versions.Keys.ShouldBe(
            ["1.0.0", "2.0.0"],
            ignoreOrder: true);
        listing.VersionCandidates.Count.ShouldBe(2);
        listing.VersionCandidates.Select(candidate => candidate.SourceRegistry)
            .ShouldBe([firstEndpoint, secondEndpoint]);
    }

    [Fact]
    public async Task GetPackageListingAsync_ProvenanceDoesNotRetainCredentials()
    {
        RegistryEndpoint privateEndpoint = new()
        {
            Url =
                "https://user:password@private.example/secret-path?token=query-secret",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer header-secret",
            CustomHeaders = [("X-Secret", "custom-secret")],
            TrustedHeaderOrigins = ["https://trusted.example"],
            UserAgent = "secret-agent",
        };
        PackageVersionInfo version = new()
        {
            Name = "example.package",
            Version = "1.0.0",
        };
        Mock<IRegistryClient> client = CreateMockClient(privateEndpoint);
        client.Setup(value => value.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing(
                "example.package",
                "1.0.0",
                version));
        RedundantRegistryClient sut = new(client.Object);

        PackageListing listing = (await sut.GetPackageListingAsync(
            "example.package",
            TestContext.Current.CancellationToken))!;

        RegistryEndpoint provenance = listing.SourceRegistry!;
        provenance.ShouldNotBeNull();
        provenance.Url.ShouldBe("https://private.example/");
        provenance.AuthHeaderValue.ShouldBeNull();
        provenance.CustomHeaders.ShouldBeNull();
        provenance.TrustedHeaderOrigins.ShouldBeEmpty();
        provenance.UserAgent.ShouldBeNull();
        listing.VersionCandidates[0].SourceRegistry
            .ShouldBe(provenance);
        string publicState = listing.ToString();
        publicState.ShouldNotContain("password");
        publicState.ShouldNotContain("query-secret");
        publicState.ShouldNotContain("header-secret");
        publicState.ShouldNotContain("custom-secret");
        publicState.ShouldNotContain("secret-path");
        publicState.ShouldNotContain("secret-agent");
    }

    [Fact]
    public async Task GetPackageListingAsync_PositiveAndError_ReturnsIncompleteListing()
    {
        RegistryEndpoint firstEndpoint = CreateEndpoint("first");
        Mock<IRegistryClient> first = CreateMockClient(firstEndpoint);
        Mock<IRegistryClient> second = CreateMockClient(CreateEndpoint("second"));
        first.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing(
                "example.package",
                "1.0.0",
                CreateVersion("example.package", "1.0.0", firstEndpoint)));
        second.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        RedundantRegistryClient sut = new(first.Object, second.Object);

        PackageListing? listing = await sut.GetPackageListingAsync(
            "example.package",
            TestContext.Current.CancellationToken);

        listing.ShouldNotBeNull();
        listing.IsComplete.ShouldBeFalse();
        listing.QueryFailures.Count.ShouldBe(1);
        listing.Versions.ContainsKey("1.0.0").ShouldBeTrue();
    }

    [Fact]
    public async Task GetPackageListingAsync_DuplicateVersion_PreservesAtomicCandidates()
    {
        RegistryEndpoint firstEndpoint = CreateEndpoint("first");
        RegistryEndpoint secondEndpoint = CreateEndpoint("second");
        PackageVersionInfo firstVersion = CreateVersion(
            "example.package",
            "1.0.0",
            firstEndpoint,
            shaSum: "aaaa",
            integrity: "sha512-first",
            description: "primary",
            dependencies: new Dictionary<string, string>
            {
                ["primary.dependency"] = "1.0.0",
            });
        PackageVersionInfo secondVersion = CreateVersion(
            "example.package",
            "1.0.0",
            secondEndpoint,
            shaSum: "bbbb",
            integrity: "sha512-second",
            description: "secondary",
            dependencies: new Dictionary<string, string>
            {
                ["secondary.dependency"] = "2.0.0",
            });
        Mock<IRegistryClient> first = CreateMockClient(firstEndpoint);
        Mock<IRegistryClient> second = CreateMockClient(secondEndpoint);
        first.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing("example.package", "1.0.0", firstVersion));
        second.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing("example.package", "1.0.0", secondVersion));
        RedundantRegistryClient sut = new(first.Object, second.Object);

        PackageListing listing = (await sut.GetPackageListingAsync(
            "example.package",
            TestContext.Current.CancellationToken))!;

        listing.Versions["1.0.0"].Description.ShouldBe("primary");
        listing.VersionCandidates.Count.ShouldBe(2);
        listing.VersionCandidates[0].Distribution!.ShaSum.ShouldBe("aaaa");
        listing.VersionCandidates[0].Distribution!.Integrity
            .ShouldBe("sha512-first");
        listing.VersionCandidates[0].Dependencies!
            .ContainsKey("primary.dependency")
            .ShouldBeTrue();
        listing.VersionCandidates[1].Distribution!.ShaSum.ShouldBe("bbbb");
        listing.VersionCandidates[1].Distribution!.Integrity
            .ShouldBe("sha512-second");
        listing.VersionCandidates[1].Dependencies!
            .ContainsKey("secondary.dependency")
            .ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_PartialExactCandidate_Succeeds()
    {
        RegistryEndpoint firstEndpoint = CreateEndpoint("first");
        Mock<IRegistryClient> first = CreateMockClient(firstEndpoint);
        Mock<IRegistryClient> second = CreateMockClient(CreateEndpoint("second"));
        first.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing(
                "example.package",
                "1.0.0",
                CreateVersion("example.package", "1.0.0", firstEndpoint)));
        second.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        RedundantRegistryClient sut = new(first.Object, second.Object);

        ResolvedDirective? resolved = await sut.ResolveAsync(
            PackageDirective.Parse("example.package#1.0.0"),
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.ShouldNotBeNull();
        resolved.Reference.Version.ShouldBe("1.0.0");
        resolved.SourceRegistry.ShouldBe(firstEndpoint);
    }

    [Fact]
    public async Task ResolveAsync_PartialRange_ThrowsAggregate()
    {
        RegistryEndpoint firstEndpoint = CreateEndpoint("first");
        Mock<IRegistryClient> first = CreateMockClient(firstEndpoint);
        Mock<IRegistryClient> second = CreateMockClient(CreateEndpoint("second"));
        first.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing(
                "example.package",
                "1.0.0",
                CreateVersion("example.package", "1.0.0", firstEndpoint)));
        second.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        RedundantRegistryClient sut = new(first.Object, second.Object);

        RegistryOperationException exception =
            await Should.ThrowAsync<RegistryOperationException>(
                () => sut.ResolveAsync(
                    PackageDirective.Parse("example.package#>=1.0.0"),
                    cancellationToken: TestContext.Current.CancellationToken));

        exception.Operation.ShouldBe("resolve");
    }

    [Fact]
    public async Task ResolveAsync_UsesHighestEligibleSourceLatest()
    {
        RegistryEndpoint firstEndpoint = CreateEndpoint("first");
        RegistryEndpoint secondEndpoint = CreateEndpoint("second");
        Mock<IRegistryClient> first = CreateMockClient(firstEndpoint);
        Mock<IRegistryClient> second = CreateMockClient(secondEndpoint);
        first.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing(
                "example.package",
                "1.0.0",
                CreateVersion("example.package", "1.0.0", firstEndpoint),
                CreateVersion("example.package", "3.0.0", firstEndpoint)));
        second.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing(
                "example.package",
                "2.0.0",
                CreateVersion("example.package", "2.0.0", secondEndpoint)));
        RedundantRegistryClient sut = new(first.Object, second.Object);

        ResolvedDirective? resolved = await sut.ResolveAsync(
            PackageDirective.Parse("example.package#latest"),
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.ShouldNotBeNull();
        resolved.Reference.Version.ShouldBe("2.0.0");
        resolved.SourceRegistry.ShouldBe(secondEndpoint);
    }

    [Fact]
    public async Task ResolveAsync_LaterRegistryRangeCandidate_UsesCoherentSource()
    {
        RegistryEndpoint firstEndpoint = CreateEndpoint("first");
        RegistryEndpoint secondEndpoint = CreateEndpoint("second");
        Mock<IRegistryClient> first = CreateMockClient(firstEndpoint);
        Mock<IRegistryClient> second = CreateMockClient(secondEndpoint);
        first.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing(
                "example.package",
                "1.0.0",
                CreateVersion("example.package", "1.0.0", firstEndpoint)));
        PackageVersionInfo laterVersion = CreateVersion(
            "example.package",
            "2.0.0",
            secondEndpoint,
            shaSum: "second-sha",
            dependencies: new Dictionary<string, string>
            {
                ["later.dependency"] = "1.0.0",
            });
        second.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing(
                "example.package",
                "2.0.0",
                laterVersion));
        RedundantRegistryClient sut = new(first.Object, second.Object);

        ResolvedDirective? resolved = await sut.ResolveAsync(
            PackageDirective.Parse("example.package#>=2.0.0"),
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.ShouldNotBeNull();
        resolved.Reference.Version.ShouldBe("2.0.0");
        resolved.SourceRegistry.ShouldBe(secondEndpoint);
        resolved.ShaSum.ShouldBe("second-sha");
        resolved.Dependencies!.ContainsKey("later.dependency").ShouldBeTrue();
        resolved.TarballUri.Host.ShouldBe("second.example");
    }

    [Fact]
    public async Task ResolveAsync_NestedComposite_MaterializesFromInnerLeafSource()
    {
        RegistryEndpoint firstEndpoint = CreateEndpoint("first");
        RegistryEndpoint sourceEndpoint = CreateEndpoint("source");
        RegistryEndpoint outerEndpoint = CreateEndpoint("outer");
        PackageVersionInfo sourceVersion = new()
        {
            Name = "example.package",
            Version = "1.0.0",
            SourceRegistry = sourceEndpoint,
            PublicationDate = "2025-01-02T03:04:05Z",
            Distribution = new NpmDistribution(
                "candidate-sha",
                null)
            {
                Integrity = "sha512-candidate",
            },
            Dependencies = new Dictionary<string, string>
            {
                ["candidate.dependency"] = "2.0.0",
            },
            FhirVersions = ["4.0.1"],
        };
        Mock<IRegistryClient> first = CreateMockClient(firstEndpoint);
        Mock<IRegistryClient> source = CreateMockClient(sourceEndpoint);
        Mock<IRegistryClient> outer = CreateMockClient(outerEndpoint);
        first.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PackageListing?)null);
        source.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing(
                "example.package",
                "1.0.0",
                sourceVersion));
        outer.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PackageListing?)null);
        source.Setup(client => client.ResolveAsync(
                It.Is<PackageDirective>(directive =>
                    directive.RequestedVersion == "1.0.0"),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedDirective
            {
                Reference = new PackageReference(
                    "example.package",
                    "1.0.0"),
                TarballUri = new Uri(
                    "https://source.example/example.package-1.0.0.tgz"),
                SourceRegistry = sourceEndpoint,
            });
        RedundantRegistryClient inner = new(first.Object, source.Object);
        RedundantRegistryClient sut = new(inner, outer.Object);

        ResolvedDirective? resolved = await sut.ResolveAsync(
            PackageDirective.Parse("example.package#1.0.0"),
            cancellationToken: TestContext.Current.CancellationToken);

        resolved.ShouldNotBeNull();
        resolved.SourceRegistry.ShouldBe(sourceEndpoint);
        resolved.TarballUri.Host.ShouldBe("source.example");
        resolved.ShaSum.ShouldBe("candidate-sha");
        resolved.Integrity.ShouldBe("sha512-candidate");
        resolved.PublicationDate.ShouldBe(
            new DateTime(
                2025,
                1,
                2,
                3,
                4,
                5,
                DateTimeKind.Utc));
        resolved.Dependencies!.ContainsKey("candidate.dependency")
            .ShouldBeTrue();
        resolved.FhirVersions.ShouldBe(["4.0.1"]);
        source.Verify(client => client.ResolveAsync(
            It.IsAny<PackageDirective>(),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        first.Verify(client => client.ResolveAsync(
            It.IsAny<PackageDirective>(),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        outer.Verify(client => client.ResolveAsync(
            It.IsAny<PackageDirective>(),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_DuplicateVersion_DoesNotMixSourceMetadata()
    {
        RegistryEndpoint firstEndpoint = CreateEndpoint("first");
        RegistryEndpoint secondEndpoint = CreateEndpoint("second");
        PackageVersionInfo firstVersion = CreateVersion(
            "example.package",
            "1.0.0",
            firstEndpoint,
            shaSum: "first-sha",
            integrity: "sha512-first",
            dependencies: new Dictionary<string, string>
            {
                ["first.dependency"] = "1.0.0",
            });
        PackageVersionInfo secondVersion = CreateVersion(
            "example.package",
            "1.0.0",
            secondEndpoint,
            shaSum: "second-sha",
            integrity: "sha512-second",
            dependencies: new Dictionary<string, string>
            {
                ["second.dependency"] = "2.0.0",
            });
        Mock<IRegistryClient> first = CreateMockClient(firstEndpoint);
        Mock<IRegistryClient> second = CreateMockClient(secondEndpoint);
        first.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing("example.package", "1.0.0", firstVersion));
        second.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing("example.package", "1.0.0", secondVersion));
        RedundantRegistryClient sut = new(first.Object, second.Object);

        ResolvedDirective resolved = (await sut.ResolveAsync(
            PackageDirective.Parse("example.package#1.0.0"),
            cancellationToken: TestContext.Current.CancellationToken))!;

        resolved.SourceRegistry.ShouldBe(firstEndpoint);
        resolved.ShaSum.ShouldBe("first-sha");
        resolved.Integrity.ShouldBe("sha512-first");
        resolved.Dependencies!.ContainsKey("first.dependency").ShouldBeTrue();
        resolved.Dependencies.ContainsKey("second.dependency").ShouldBeFalse();
        resolved.TarballUri.Host.ShouldBe("first.example");
    }

    [Fact]
    public async Task ResolveAsync_PrimaryMetadataOmitted_SelectsWholeLaterCandidate()
    {
        RegistryEndpoint firstEndpoint = CreateEndpoint("first");
        RegistryEndpoint secondEndpoint = CreateEndpoint("second");
        PackageVersionInfo firstVersion = CreateVersion(
            "example.package",
            "1.0.0",
            firstEndpoint,
            shaSum: "first-sha",
            integrity: "sha512-first");
        PackageVersionInfo secondVersion = CreateVersion(
            "example.package",
            "1.0.0",
            secondEndpoint,
            shaSum: "second-sha",
            integrity: "sha512-second",
            dependencies: new Dictionary<string, string>
            {
                ["later.dependency"] = "2.0.0",
            });
        Mock<IRegistryClient> first = CreateMockClient(firstEndpoint);
        Mock<IRegistryClient> second = CreateMockClient(secondEndpoint);
        first.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing("example.package", "1.0.0", firstVersion));
        second.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing("example.package", "1.0.0", secondVersion));
        RedundantRegistryClient sut = new(first.Object, second.Object);

        ResolvedDirective resolved = (await sut.ResolveAsync(
            PackageDirective.Parse("example.package#1.0.0"),
            cancellationToken: TestContext.Current.CancellationToken))!;

        resolved.SourceRegistry.ShouldBe(secondEndpoint);
        resolved.ShaSum.ShouldBe("second-sha");
        resolved.Integrity.ShouldBe("sha512-second");
        resolved.Dependencies!.ContainsKey("later.dependency").ShouldBeTrue();
        resolved.TarballUri.Host.ShouldBe("second.example");
    }

    [Fact]
    public async Task ResolveAndDownload_SameOriginPaths_RetainsPrivateRoute()
    {
        RegistryEndpoint firstEndpoint = new()
        {
            Url = "https://registry.example/first",
            Type = RegistryType.FhirNpm,
        };
        RegistryEndpoint secondEndpoint = new()
        {
            Url = "https://registry.example/second",
            Type = RegistryType.FhirNpm,
        };
        Mock<IRegistryClient> first = CreateMockClient(firstEndpoint);
        Mock<IRegistryClient> second = CreateMockClient(secondEndpoint);
        first.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing(
                "example.package",
                "1.0.0",
                CreateVersion(
                    "example.package",
                    "1.0.0",
                    firstEndpoint)));
        second.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing(
                "example.package",
                "1.0.0",
                CreateVersion(
                    "example.package",
                    "1.0.0",
                    secondEndpoint,
                    dependencies: new Dictionary<string, string>())));
        PackageDownloadResult expected = new()
        {
            Content = new MemoryStream([1]),
            ContentType = "application/gzip",
        };
        second.Setup(client => client.DownloadAsync(
                It.IsAny<ResolvedDirective>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        RedundantRegistryClient sut = new(first.Object, second.Object);

        ResolvedDirective resolved = (await sut.ResolveAsync(
            PackageDirective.Parse("example.package#1.0.0"),
            cancellationToken: TestContext.Current.CancellationToken))!;
        PackageDownloadResult? downloaded = await sut.DownloadAsync(
            resolved,
            TestContext.Current.CancellationToken);

        resolved.SourceRegistry.ShouldNotBeNull();
        resolved.SourceRegistry.Url.ShouldBe("https://registry.example/");
        downloaded.ShouldBeSameAs(expected);
        second.Verify(client => client.DownloadAsync(
            resolved,
            It.IsAny<CancellationToken>()), Times.Once);
        first.Verify(client => client.DownloadAsync(
            It.IsAny<ResolvedDirective>(),
            It.IsAny<CancellationToken>()), Times.Never);
        await expected.DisposeAsync();
    }

    [Fact]
    public async Task ResolveAsync_DuplicateVersion_ChoosesPolicyCompatibleSource()
    {
        RegistryEndpoint firstEndpoint = CreateEndpoint("first");
        RegistryEndpoint secondEndpoint = CreateEndpoint("second");
        PackageVersionInfo firstVersion = CreateVersion(
            "example.package",
            "1.0.0",
            firstEndpoint,
            fhirVersion: "5.0.0");
        PackageVersionInfo secondVersion = CreateVersion(
            "example.package",
            "1.0.0",
            secondEndpoint,
            fhirVersion: "4.0.1");
        Mock<IRegistryClient> first = CreateMockClient(firstEndpoint);
        Mock<IRegistryClient> second = CreateMockClient(secondEndpoint);
        first.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing("example.package", "1.0.0", firstVersion));
        second.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateListing("example.package", "1.0.0", secondVersion));
        RedundantRegistryClient sut = new(first.Object, second.Object);

        ResolvedDirective resolved = (await sut.ResolveAsync(
            PackageDirective.Parse("example.package#1.0.0"),
            new VersionResolveOptions { FhirRelease = FhirRelease.R4 },
            TestContext.Current.CancellationToken))!;

        resolved.SourceRegistry.ShouldBe(secondEndpoint);
        resolved.FhirVersions.ShouldBe(["4.0.1"]);
        resolved.TarballUri.Host.ShouldBe("second.example");
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
    public async Task DownloadAsync_NestedComposite_RoutesToInnerLeafSource()
    {
        RegistryEndpoint firstEndpoint = CreateEndpoint("first");
        RegistryEndpoint sourceEndpoint = CreateEndpoint("source");
        RegistryEndpoint outerEndpoint = CreateEndpoint("outer");
        Mock<IRegistryClient> first = CreateMockClient(firstEndpoint);
        Mock<IRegistryClient> source = CreateMockClient(sourceEndpoint);
        Mock<IRegistryClient> outer = CreateMockClient(outerEndpoint);
        PackageDownloadResult expected = new()
        {
            Content = new MemoryStream([1, 2, 3]),
            ContentType = "application/gzip",
        };
        source.Setup(client => client.DownloadAsync(
                It.IsAny<ResolvedDirective>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        RedundantRegistryClient inner = new(first.Object, source.Object);
        RedundantRegistryClient sut = new(inner, outer.Object);
        ResolvedDirective resolved = new()
        {
            Reference = new PackageReference("example.package", "1.0.0"),
            TarballUri = new Uri("https://source.example/package.tgz"),
            SourceRegistry = sourceEndpoint,
        };

        PackageDownloadResult? result = await sut.DownloadAsync(
            resolved,
            TestContext.Current.CancellationToken);

        result.ShouldBeSameAs(expected);
        source.Verify(client => client.DownloadAsync(
            resolved,
            It.IsAny<CancellationToken>()), Times.Once);
        first.Verify(client => client.DownloadAsync(
            It.IsAny<ResolvedDirective>(),
            It.IsAny<CancellationToken>()), Times.Never);
        outer.Verify(client => client.DownloadAsync(
            It.IsAny<ResolvedDirective>(),
            It.IsAny<CancellationToken>()), Times.Never);
        await expected.DisposeAsync();
    }

    [Fact]
    public async Task DownloadAsync_UnmatchedSource_ThrowsSanitizedAggregate()
    {
        Mock<IRegistryClient> configured =
            CreateMockClient(CreateEndpoint("configured"));
        RedundantRegistryClient sut = new(configured.Object);
        ResolvedDirective resolved = new()
        {
            Reference = new PackageReference("example.package", "1.0.0"),
            TarballUri = new Uri("https://packages.example/package.tgz"),
            SourceRegistry = new RegistryEndpoint
            {
                Url = "https://user:secret@missing.example/registry?token=hidden",
                Type = RegistryType.FhirNpm,
            },
        };

        RegistryOperationException exception =
            await Should.ThrowAsync<RegistryOperationException>(
                () => sut.DownloadAsync(
                    resolved,
                    TestContext.Current.CancellationToken));

        exception.Failures.Count.ShouldBe(1);
        exception.Failures[0].EndpointOrigin.ShouldBe(
            "https://missing.example");
        exception.ToString().ShouldNotContain("secret");
        exception.ToString().ShouldNotContain("hidden");
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

        RegistryOperationException exception =
            await Should.ThrowAsync<RegistryOperationException>(
            () => sut.DownloadAsync(
                resolved,
                TestContext.Current.CancellationToken));

        exception.Operation.ShouldBe("download");
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

    private static RegistryEndpoint CreateEndpoint(string name) =>
        new()
        {
            Url = $"https://{name}.example/",
            Type = RegistryType.FhirNpm,
        };

    private static PackageVersionInfo CreateVersion(
        string packageId,
        string version,
        RegistryEndpoint endpoint,
        string? shaSum = null,
        string? integrity = null,
        string? description = null,
        IReadOnlyDictionary<string, string>? dependencies = null,
        string fhirVersion = "4.0.1") =>
        new()
        {
            Name = packageId,
            Version = version,
            Description = description,
            FhirVersion = fhirVersion,
            FhirVersions = [fhirVersion],
            Distribution = new NpmDistribution(
                shaSum,
                $"{endpoint.Url.TrimEnd('/')}/{packageId}-{version}.tgz")
            {
                Integrity = integrity,
            },
            Dependencies = dependencies,
        };

    private static PackageListing CreateListing(
        string packageId,
        string latest,
        params PackageVersionInfo[] versions) =>
        new()
        {
            PackageId = packageId,
            DistTags = new Dictionary<string, string>
            {
                ["latest"] = latest,
            },
            Versions = versions.ToDictionary(
                version => version.Version,
                StringComparer.OrdinalIgnoreCase),
        };
}
