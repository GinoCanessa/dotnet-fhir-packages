// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;
using FhirPkg.Registry;
using Shouldly;
using Moq;
using Xunit;

namespace FhirPkg.Tests.Registry;

public class RedundantRegistryClientTests
{
    private static readonly RegistryEndpoint TestEndpoint = new()
    {
        Url = "https://test.registry.org/",
        Type = RegistryType.FhirNpm
    };

    private static Mock<IRegistryClient> CreateMockClient()
    {
        Mock<IRegistryClient> mock = new Mock<IRegistryClient>();
        mock.Setup(c => c.Endpoint).Returns(TestEndpoint);
        mock.Setup(c => c.SupportedNameTypes).Returns(
            Enum.GetValues<PackageNameType>().ToList().AsReadOnly());
        mock.Setup(c => c.SupportedVersionTypes).Returns(
            Enum.GetValues<VersionType>().ToList().AsReadOnly());
        return mock;
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

        ResolvedDirective? result = await sut.ResolveAsync(directive);

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

        ResolvedDirective? result = await sut.ResolveAsync(directive);

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

        ResolvedDirective? result = await sut.ResolveAsync(directive);

        result.ShouldBeNull();
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

        ResolvedDirective? result = await sut.ResolveAsync(directive);

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
        IReadOnlyList<CatalogEntry> results = await sut.SearchAsync(new PackageSearchCriteria { Name = "package" });

        results.Count.ShouldBe(3);
        results.Select(r => r.Name).ShouldBe(new[] { "package.a", "package.b", "package.c" }, ignoreOrder: true);
    }
}
