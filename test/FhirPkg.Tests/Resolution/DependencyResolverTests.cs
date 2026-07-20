// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
using FhirPkg.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Resolution;

public class DependencyResolverTests
{
    [Fact]
    public async Task ResolveAsync_AppliesConfiguredFixupToTransitiveDirective()
    {
        Mock<IVersionResolver> versionResolver = new();
        string? capturedPackage = null;
        string? capturedVersion = null;
        versionResolver
            .Setup(resolver => resolver.ResolveVersionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, VersionResolveOptions?, CancellationToken>(
                (package, version, _, _) =>
                {
                    capturedPackage = package;
                    capturedVersion = version;
                })
            .ReturnsAsync((FhirSemVer?)null);

        PackageFixupPolicy fixupPolicy = PackageFixupPolicy.Create(
            new Dictionary<string, string>
            {
                ["@scope/dependency@1.0.0"] = "1.0.1",
            });
        DependencyResolver resolver = new(
            new Mock<IRegistryClient>().Object,
            versionResolver.Object,
            new Mock<IPackageCache>().Object,
            NullLogger.Instance);
        PackageManifest root = new()
        {
            Name = "root.package",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["@scope/dependency"] = "1.0.0",
            },
        };

        PackageClosure result = await resolver.ResolveAsync(
            root,
            new DependencyResolveOptions { FixupPolicy = fixupPolicy },
            cancellationToken: TestContext.Current.CancellationToken);

        capturedPackage.ShouldBe("@scope/dependency");
        capturedVersion.ShouldBe("1.0.1");
        result.Missing.ContainsKey("@scope/dependency").ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_UsesLaterRegistryDependencyCandidate()
    {
        Mock<IVersionResolver> versionResolver = new();
        versionResolver
            .Setup(resolver => resolver.ResolveVersionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                string packageId,
                string _,
                VersionResolveOptions? _,
                CancellationToken _) =>
                packageId == "example.dependency"
                    ? FhirSemVer.Parse("1.0.0")
                    : null);
        RegistryEndpoint primary = new()
        {
            Url = "https://primary.example/",
            Type = RegistryType.FhirNpm,
        };
        RegistryEndpoint secondary = new()
        {
            Url = "https://secondary.example/",
            Type = RegistryType.FhirNpm,
        };
        RegistryEndpoint tertiary = new()
        {
            Url = "https://tertiary.example/",
            Type = RegistryType.FhirNpm,
        };
        PackageVersionInfo primaryCandidate = new()
        {
            Name = "example.dependency",
            Version = "1.0.0",
            SourceRegistry = primary,
            FhirVersions = ["4.0.1"],
        };
        PackageVersionInfo secondaryCandidate = new()
        {
            Name = "example.dependency",
            Version = "1.0.0",
            SourceRegistry = secondary,
            FhirVersions = ["5.0.0"],
            Dependencies = new Dictionary<string, string>
            {
                ["wrong.metadata"] = "3.0.0",
            },
        };
        PackageVersionInfo tertiaryCandidate = new()
        {
            Name = "example.dependency",
            Version = "1.0.0",
            SourceRegistry = tertiary,
            FhirVersions = ["4.0.1"],
            Dependencies = new Dictionary<string, string>
            {
                ["later.metadata"] = "2.0.0",
            },
        };
        PackageListing listing = new()
        {
            PackageId = "example.dependency",
            Versions = new Dictionary<string, PackageVersionInfo>
            {
                ["1.0.0"] = primaryCandidate,
            },
            VersionCandidates =
                [primaryCandidate, secondaryCandidate, tertiaryCandidate],
        };
        Mock<IRegistryClient> registry = new();
        registry.Setup(client => client.GetPackageListingAsync(
                "example.dependency",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(listing);
        Mock<IPackageCache> cache = new();
        cache.Setup(value => value.ReadManifestAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PackageManifest?)null);
        DependencyResolver resolver = new(
            registry.Object,
            versionResolver.Object,
            cache.Object,
            NullLogger.Instance);
        PackageManifest root = new()
        {
            Name = "root.package",
            Version = "1.0.0",
            Dependencies = new Dictionary<string, string>
            {
                ["example.dependency"] = "1.0.0",
            },
        };

        await resolver.ResolveAsync(
            root,
            new DependencyResolveOptions
            {
                PreferredFhirRelease = FhirRelease.R4,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        versionResolver.Verify(value => value.ResolveVersionAsync(
            "later.metadata",
            "2.0.0",
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        versionResolver.Verify(value => value.ResolveVersionAsync(
            "wrong.metadata",
            It.IsAny<string>(),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
