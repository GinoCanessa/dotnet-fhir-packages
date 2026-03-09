// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;
using FhirPkg.Registry;
using FluentAssertions;
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
        var mock = new Mock<IRegistryClient>();
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
        var expected = new ResolvedDirective
        {
            Reference = new PackageReference("hl7.fhir.r4.core", "4.0.1"),
            TarballUri = new Uri("https://test.registry.org/package.tgz")
        };

        var client1 = CreateMockClient();
        client1.Setup(c => c.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var client2 = CreateMockClient();

        var sut = new RedundantRegistryClient(client1.Object, client2.Object);
        var directive = PackageDirective.Parse("hl7.fhir.r4.core#4.0.1");

        var result = await sut.ResolveAsync(directive);

        result.Should().Be(expected);
        client2.Verify(c => c.ResolveAsync(
            It.IsAny<PackageDirective>(),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_FirstClientFails_FallsBackToSecond()
    {
        var expected = new ResolvedDirective
        {
            Reference = new PackageReference("hl7.fhir.r4.core", "4.0.1"),
            TarballUri = new Uri("https://test2.registry.org/package.tgz")
        };

        var client1 = CreateMockClient();
        client1.Setup(c => c.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection failed"));

        var client2 = CreateMockClient();
        client2.Setup(c => c.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var sut = new RedundantRegistryClient(client1.Object, client2.Object);
        var directive = PackageDirective.Parse("hl7.fhir.r4.core#4.0.1");

        var result = await sut.ResolveAsync(directive);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task ResolveAsync_AllClientsFail_ReturnsNull()
    {
        var client1 = CreateMockClient();
        client1.Setup(c => c.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection failed"));

        var client2 = CreateMockClient();
        client2.Setup(c => c.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection failed"));

        var sut = new RedundantRegistryClient(client1.Object, client2.Object);
        var directive = PackageDirective.Parse("hl7.fhir.r4.core#4.0.1");

        var result = await sut.ResolveAsync(directive);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_FirstReturnsNull_FallsBackToSecond()
    {
        var expected = new ResolvedDirective
        {
            Reference = new PackageReference("hl7.fhir.r4.core", "4.0.1"),
            TarballUri = new Uri("https://test2.registry.org/package.tgz")
        };

        var client1 = CreateMockClient();
        client1.Setup(c => c.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedDirective?)null);

        var client2 = CreateMockClient();
        client2.Setup(c => c.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var sut = new RedundantRegistryClient(client1.Object, client2.Object);
        var directive = PackageDirective.Parse("hl7.fhir.r4.core#4.0.1");

        var result = await sut.ResolveAsync(directive);

        result.Should().Be(expected);
    }

    [Fact]
    public void Constructor_EmptyClients_Throws()
    {
        var act = () => new RedundantRegistryClient(Array.Empty<IRegistryClient>());

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task SearchAsync_QueriesAllClients_MergesResults()
    {
        var client1 = CreateMockClient();
        client1.Setup(c => c.SearchAsync(It.IsAny<PackageSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CatalogEntry>
            {
                new() { Name = "package.a" },
                new() { Name = "package.b" }
            }.AsReadOnly());

        var client2 = CreateMockClient();
        client2.Setup(c => c.SearchAsync(It.IsAny<PackageSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CatalogEntry>
            {
                new() { Name = "package.b" }, // duplicate
                new() { Name = "package.c" }
            }.AsReadOnly());

        var sut = new RedundantRegistryClient(client1.Object, client2.Object);
        var results = await sut.SearchAsync(new PackageSearchCriteria { Name = "package" });

        results.Should().HaveCount(3);
        results.Select(r => r.Name).Should().Contain(["package.a", "package.b", "package.c"]);
    }
}
