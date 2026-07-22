// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Resolution;

public class VersionResolverTests
{
    [Fact]
    public async Task ResolveVersionAsync_PartialExactCandidate_Succeeds()
    {
        Mock<IRegistryClient> registry = CreateRegistry(
            CreateListing(isComplete: false));
        VersionResolver resolver = new(
            registry.Object,
            NullLogger<VersionResolver>.Instance);

        FhirSemVer? result = await resolver.ResolveVersionAsync(
            "example.package",
            "1.0.0",
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(FhirSemVer.Parse("1.0.0"));
    }

    [Fact]
    public async Task ResolveVersionAsync_PartialRange_ThrowsAggregate()
    {
        Mock<IRegistryClient> registry = CreateRegistry(
            CreateListing(isComplete: false));
        VersionResolver resolver = new(
            registry.Object,
            NullLogger<VersionResolver>.Instance);

        RegistryOperationException exception =
            await Should.ThrowAsync<RegistryOperationException>(
                () => resolver.ResolveVersionAsync(
                    "example.package",
                    ">=1.0.0",
                    cancellationToken:
                        TestContext.Current.CancellationToken));

        exception.Operation.ShouldBe("resolve");
    }

    [Fact]
    public async Task ResolveVersionAsync_PartialExactMiss_ThrowsAggregate()
    {
        Mock<IRegistryClient> registry = CreateRegistry(
            CreateListing(isComplete: false));
        VersionResolver resolver = new(
            registry.Object,
            NullLogger<VersionResolver>.Instance);

        await Should.ThrowAsync<RegistryOperationException>(
            () => resolver.ResolveVersionAsync(
                "example.package",
                "2.0.0",
                cancellationToken:
                    TestContext.Current.CancellationToken));
    }

    private static Mock<IRegistryClient> CreateRegistry(PackageListing listing)
    {
        Mock<IRegistryClient> registry = new();
        registry.Setup(client => client.GetPackageListingAsync(
                "example.package",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(listing);
        return registry;
    }

    private static PackageListing CreateListing(bool isComplete)
    {
        RegistryEndpoint endpoint = new()
        {
            Url = "https://registry.example/",
            Type = RegistryType.FhirNpm,
        };
        PackageVersionInfo candidate = new()
        {
            Name = "example.package",
            Version = "1.0.0",
            SourceRegistry = endpoint,
            Distribution = new NpmDistribution(
                "sha",
                "https://registry.example/example.package-1.0.0.tgz"),
        };
        return new PackageListing
        {
            PackageId = "example.package",
            Versions = new Dictionary<string, PackageVersionInfo>
            {
                ["1.0.0"] = candidate,
            },
            VersionCandidates = [candidate],
            IsComplete = isComplete,
            QueryFailures = isComplete
                ? []
                :
                [
                    new RegistryAttemptFailure(
                        "https://failed.example/private?secret=value",
                        RegistryFailureCategory.Network)
                ],
        };
    }
}
