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

public class DependencyResolverFixupTests
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
}
