// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;

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

    [Fact]
    public async Task ResolveAsync_NegativeMaxDepth_IsRejected()
    {
        DependencyResolver resolver = CreateResolver(
            CreateExactVersionResolver(),
            CreateRegistry(
                new Dictionary<string, PackageListing>(
                    StringComparer.OrdinalIgnoreCase)),
            CreateCache());
        PackageManifest root = CreateRoot(Dependencies());

        ArgumentOutOfRangeException exception =
            await Should.ThrowAsync<ArgumentOutOfRangeException>(
                () => resolver.ResolveAsync(
                    root,
                    new DependencyResolveOptions
                    {
                        MaxDepth = -1,
                    },
                    TestContext.Current.CancellationToken));

        exception.ParamName.ShouldBe("MaxDepth");
    }

    [Fact]
    public async Task ResolveAsync_MaxDepthZero_ResolvesDirectAndReportsGrandchild()
    {
        Dictionary<string, PackageListing> listings = new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["direct.package"] = CreateListing(
                CreateVersion(
                    "direct.package",
                    "1.0.0",
                    Dependencies(("grandchild.package", "1.0.0")))),
        };
        DependencyResolver resolver = CreateResolver(
            CreateExactVersionResolver(),
            CreateRegistry(listings),
            CreateCache());
        PackageManifest root = CreateRoot(
            Dependencies(("direct.package", "1.0.0")));

        PackageClosure result = await resolver.ResolveAsync(
            root,
            new DependencyResolveOptions
            {
                MaxDepth = 0,
            },
            TestContext.Current.CancellationToken);

        result.Resolved["direct.package"].Version.ShouldBe("1.0.0");
        result.Resolved.ContainsKey("grandchild.package").ShouldBeFalse();
        DependencyResolutionFailure failure = result.Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBe(
            DependencyResolutionFailureCode.DepthLimitExceeded);
        failure.PackageId.ShouldBe("grandchild.package");
        failure.Depth.ShouldBe(1);
        failure.MaxDepth.ShouldBe(0);
        result.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_HighestWinner_PrunesLosingSubgraphAndFailures()
    {
        Dictionary<string, PackageListing> listings =
            CreateConflictGraphListings();
        DependencyResolver resolver = CreateResolver(
            CreateExactVersionResolver("stale.missing"),
            CreateRegistry(listings),
            CreateCache());
        PackageManifest root = CreateRoot(
            Dependencies(
                ("low.parent", "1.0.0"),
                ("high.parent", "1.0.0")));

        PackageClosure result = await resolver.ResolveAsync(
            root,
            new DependencyResolveOptions
            {
                ConflictStrategy =
                    ConflictResolutionStrategy.HighestWins,
            },
            TestContext.Current.CancellationToken);

        result.Resolved["pivot.package"].Version.ShouldBe("2.0.0");
        result.Resolved.ContainsKey("winning.child").ShouldBeTrue();
        result.Resolved.ContainsKey("shared.child").ShouldBeTrue();
        result.Resolved.ContainsKey("losing.child").ShouldBeFalse();
        result.Missing.ContainsKey("stale.missing").ShouldBeFalse();
        result.Failures.ShouldBeEmpty();
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_FirstWinner_UsesWinningVersionMetadata()
    {
        Dictionary<string, PackageListing> listings =
            CreateConflictGraphListings();
        DependencyResolver resolver = CreateResolver(
            CreateExactVersionResolver("stale.missing"),
            CreateRegistry(listings),
            CreateCache());
        PackageManifest root = CreateRoot(
            Dependencies(
                ("low.parent", "1.0.0"),
                ("high.parent", "1.0.0")));

        PackageClosure result = await resolver.ResolveAsync(
            root,
            new DependencyResolveOptions
            {
                ConflictStrategy =
                    ConflictResolutionStrategy.FirstWins,
            },
            TestContext.Current.CancellationToken);

        result.Resolved["pivot.package"].Version.ShouldBe("1.0.0");
        result.Resolved.ContainsKey("losing.child").ShouldBeTrue();
        result.Resolved.ContainsKey("winning.child").ShouldBeFalse();
        result.Missing.ContainsKey("stale.missing").ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_ErrorConflict_ReturnsTypedActiveFailure()
    {
        Dictionary<string, PackageListing> listings =
            CreateConflictGraphListings();
        DependencyResolver resolver = CreateResolver(
            CreateExactVersionResolver("stale.missing"),
            CreateRegistry(listings),
            CreateCache());
        PackageManifest root = CreateRoot(
            Dependencies(
                ("low.parent", "1.0.0"),
                ("high.parent", "1.0.0")));

        PackageClosure result = await resolver.ResolveAsync(
            root,
            new DependencyResolveOptions
            {
                ConflictStrategy = ConflictResolutionStrategy.Error,
            },
            TestContext.Current.CancellationToken);

        DependencyResolutionFailure conflict = result.Failures.Single(
            failure =>
                failure.Code
                    == DependencyResolutionFailureCode.VersionConflict);
        conflict.PackageId.ShouldBe("pivot.package");
        conflict.RequestedVersions.ShouldBe(["1.0.0", "2.0.0"]);
        conflict.SelectedVersion.ShouldBe("1.0.0");
        result.Missing.ContainsKey("pivot.package").ShouldBeTrue();
        result.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_SharedDagAndCycle_AreNotGloballySuppressed()
    {
        Dictionary<string, PackageListing> listings = new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["first.parent"] = CreateListing(
                CreateVersion(
                    "first.parent",
                    "1.0.0",
                    Dependencies(("shared.package", "1.0.0")))),
            ["second.parent"] = CreateListing(
                CreateVersion(
                    "second.parent",
                    "1.0.0",
                    Dependencies(("shared.package", "1.0.0")))),
            ["shared.package"] = CreateListing(
                CreateVersion(
                    "shared.package",
                    "1.0.0",
                    Dependencies(("cycle.package", "1.0.0")))),
            ["cycle.package"] = CreateListing(
                CreateVersion(
                    "cycle.package",
                    "1.0.0",
                    Dependencies(("shared.package", "1.0.0")))),
        };
        Mock<IRegistryClient> registry = CreateRegistry(listings);
        DependencyResolver resolver = CreateResolver(
            CreateExactVersionResolver(),
            registry,
            CreateCache());
        PackageManifest root = CreateRoot(
            Dependencies(
                ("first.parent", "1.0.0"),
                ("second.parent", "1.0.0")));

        PackageClosure result = await resolver.ResolveAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Resolved.Keys.ShouldContain("first.parent");
        result.Resolved.Keys.ShouldContain("second.parent");
        result.Resolved.Keys.ShouldContain("shared.package");
        result.Resolved.Keys.ShouldContain("cycle.package");
        result.Failures.ShouldBeEmpty();
        registry.Verify(client => client.GetPackageListingAsync(
            "shared.package",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_IncompleteListing_ReturnsTypedMetadataFailure()
    {
        RegistryAttemptFailure registryFailure = new(
            "https://offline.example/private/path",
            RegistryFailureCategory.Network);
        Dictionary<string, PackageListing> listings = new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["example.package"] = new PackageListing
            {
                PackageId = "example.package",
                Versions =
                    new Dictionary<string, PackageVersionInfo>(
                        StringComparer.OrdinalIgnoreCase),
                IsComplete = false,
                QueryFailures = [registryFailure],
            },
        };
        DependencyResolver resolver = CreateResolver(
            CreateExactVersionResolver(),
            CreateRegistry(listings),
            CreateCache());
        PackageManifest root = CreateRoot(
            Dependencies(("example.package", "1.0.0")));

        PackageClosure result = await resolver.ResolveAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        DependencyResolutionFailure failure =
            result.Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBe(
            DependencyResolutionFailureCode.MetadataUnavailable);
        failure.PackageId.ShouldBe("example.package");
        failure.SelectedVersion.ShouldBe("1.0.0");
        failure.RegistryFailures.ShouldHaveSingleItem()
            .EndpointOrigin.ShouldBe("https://offline.example");
        result.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_IncompleteExactListing_TraversesButRemainsIncomplete()
    {
        RegistryAttemptFailure registryFailure = new(
            "https://offline.example/",
            RegistryFailureCategory.Network);
        PackageVersionInfo parentVersion = CreateVersion(
            "parent.package",
            "1.0.0",
            Dependencies(("known.child", "1.0.0")));
        Dictionary<string, PackageListing> listings = new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["parent.package"] = new PackageListing
            {
                PackageId = "parent.package",
                Versions =
                    new Dictionary<string, PackageVersionInfo>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["1.0.0"] = parentVersion,
                    },
                VersionCandidates = [parentVersion],
                IsComplete = false,
                QueryFailures = [registryFailure],
            },
            ["known.child"] = CreateListing(
                CreateVersion(
                    "known.child",
                    "1.0.0",
                    Dependencies())),
        };
        DependencyResolver resolver = CreateResolver(
            CreateExactVersionResolver(),
            CreateRegistry(listings),
            CreateCache());
        PackageManifest root = CreateRoot(
            Dependencies(("parent.package", "1.0.0")));

        PackageClosure result = await resolver.ResolveAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Resolved.ContainsKey("known.child").ShouldBeTrue();
        DependencyResolutionFailure failure =
            result.Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBe(
            DependencyResolutionFailureCode.MetadataUnavailable);
        failure.PackageId.ShouldBe("parent.package");
        failure.RegistryFailures.ShouldHaveSingleItem();
        result.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_OscillatingHighestGraph_ReturnsTypedFailure()
    {
        Dictionary<string, PackageListing> listings = new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["cycle.a"] = CreateListing(
                CreateVersion(
                    "cycle.a",
                    "1.0.0",
                    Dependencies(("cycle.b", "2.0.0"))),
                CreateVersion(
                    "cycle.a",
                    "2.0.0",
                    Dependencies())),
            ["cycle.b"] = CreateListing(
                CreateVersion(
                    "cycle.b",
                    "2.0.0",
                    Dependencies(("cycle.a", "2.0.0")))),
        };
        DependencyResolver resolver = CreateResolver(
            CreateExactVersionResolver(),
            CreateRegistry(listings),
            CreateCache());
        PackageManifest root = CreateRoot(
            Dependencies(("cycle.a", "1.0.0")));

        PackageClosure result = await resolver.ResolveAsync(
            root,
            new DependencyResolveOptions
            {
                ConflictStrategy =
                    ConflictResolutionStrategy.HighestWins,
            },
            TestContext.Current.CancellationToken);

        DependencyResolutionFailure failure = result.Failures.Single(
            candidate =>
                candidate.Code
                    == DependencyResolutionFailureCode.UnstableResolution);
        failure.PackageId.ShouldBeOneOf("cycle.a", "cycle.b");
        result.Resolved.ContainsKey(failure.PackageId).ShouldBeFalse();
        if (failure.PackageId == "cycle.a")
        {
            result.Resolved.ContainsKey("cycle.b").ShouldBeFalse();
        }
        result.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_ShallowRouteAndCyclePath_DoNotDiverge()
    {
        Dictionary<string, PackageListing> listings = new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["driver.package"] = CreateListing(
                CreateVersion(
                    "driver.package",
                    "1.0.0",
                    Dependencies(("cycle.a", "1.0.0"))),
                CreateVersion(
                    "driver.package",
                    "2.0.0",
                    Dependencies())),
            ["cycle.a"] = CreateListing(
                CreateVersion(
                    "cycle.a",
                    "1.0.0",
                    Dependencies(("cycle.b", "1.0.0")))),
            ["cycle.b"] = CreateListing(
                CreateVersion(
                    "cycle.b",
                    "1.0.0",
                    Dependencies(
                        ("cycle.a", "1.0.0"),
                        ("driver.package", "2.0.0")))),
        };
        DependencyResolver resolver = CreateResolver(
            CreateExactVersionResolver(),
            CreateRegistry(listings),
            CreateCache());
        PackageManifest root = CreateRoot(
            Dependencies(
                ("driver.package", "1.0.0"),
                ("cycle.a", "1.0.0")));

        PackageClosure result = await resolver.ResolveAsync(
            root,
            new DependencyResolveOptions
            {
                ConflictStrategy =
                    ConflictResolutionStrategy.HighestWins,
            },
            TestContext.Current.CancellationToken);

        result.Resolved["driver.package"].Version.ShouldBe("2.0.0");
        result.Resolved["cycle.a"].Version.ShouldBe("1.0.0");
        result.Resolved["cycle.b"].Version.ShouldBe("1.0.0");
        result.Failures.ShouldBeEmpty();
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_RegistryMetadata_PrecedesCacheFallback()
    {
        Dictionary<string, PackageListing> listings = new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["parent.package"] = CreateListing(
                CreateVersion(
                    "parent.package",
                    "1.0.0",
                    Dependencies(("registry.child", "1.0.0")))),
            ["registry.child"] = CreateListing(
                CreateVersion(
                    "registry.child",
                    "1.0.0",
                    Dependencies())),
        };
        Mock<IPackageCache> cache = CreateCache(
            reference =>
                reference.Name == "parent.package"
                    ? CreateManifest(
                        "parent.package",
                        "1.0.0",
                        Dependencies(("cache.child", "1.0.0")))
                    : null);
        DependencyResolver resolver = CreateResolver(
            CreateExactVersionResolver(),
            CreateRegistry(listings),
            cache);
        PackageManifest root = CreateRoot(
            Dependencies(("parent.package", "1.0.0")));

        PackageClosure result = await resolver.ResolveAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Resolved.ContainsKey("registry.child").ShouldBeTrue();
        result.Resolved.ContainsKey("cache.child").ShouldBeFalse();
        cache.Verify(value => value.ReadManifestAsync(
            It.Is<PackageReference>(
                reference => reference.Name == "parent.package"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_CacheFallback_FollowsExhaustedRegistryCandidates()
    {
        Dictionary<string, PackageListing> listings = new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["parent.package"] = CreateListing(
                CreateVersion(
                    "parent.package",
                    "1.0.0",
                    dependencies: null)),
            ["cache.child"] = CreateListing(
                CreateVersion(
                    "cache.child",
                    "1.0.0",
                    Dependencies())),
        };
        Mock<IPackageCache> cache = CreateCache(
            reference =>
                reference.Name == "parent.package"
                    ? CreateManifest(
                        "parent.package",
                        "1.0.0",
                        Dependencies(("cache.child", "1.0.0")))
                    : null);
        DependencyResolver resolver = CreateResolver(
            CreateExactVersionResolver(),
            CreateRegistry(listings),
            cache);
        PackageManifest root = CreateRoot(
            Dependencies(("parent.package", "1.0.0")));

        PackageClosure result = await resolver.ResolveAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Resolved.ContainsKey("cache.child").ShouldBeTrue();
        result.Failures.ShouldBeEmpty();
        cache.Verify(value => value.ReadManifestAsync(
            It.Is<PackageReference>(
                reference => reference.Name == "parent.package"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_MissingVersion_ReturnsTypedFailure()
    {
        DependencyResolver resolver = CreateResolver(
            CreateExactVersionResolver("missing.package"),
            CreateRegistry(
                new Dictionary<string, PackageListing>(
                    StringComparer.OrdinalIgnoreCase)),
            CreateCache());
        PackageManifest root = CreateRoot(
            Dependencies(("missing.package", "2.0.0")));

        PackageClosure result = await resolver.ResolveAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        DependencyResolutionFailure failure =
            result.Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBe(
            DependencyResolutionFailureCode.PackageNotFound);
        failure.PackageId.ShouldBe("missing.package");
        failure.VersionSpecifier.ShouldBe("2.0.0");
        failure.ParentPackageId.ShouldBe("root.package");
        result.Missing.ContainsKey("missing.package").ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_InvalidRegistryResponse_ReturnsTypedFailure()
    {
        Mock<IVersionResolver> versionResolver = new();
        versionResolver
            .Setup(resolver => resolver.ResolveVersionAsync(
                "invalid.package",
                "1.0.0",
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new JsonException("sensitive response details"));
        DependencyResolver resolver = CreateResolver(
            versionResolver,
            CreateRegistry(
                new Dictionary<string, PackageListing>(
                    StringComparer.OrdinalIgnoreCase)),
            CreateCache());
        PackageManifest root = CreateRoot(
            Dependencies(("invalid.package", "1.0.0")));

        PackageClosure result = await resolver.ResolveAsync(
            root,
            cancellationToken: TestContext.Current.CancellationToken);

        DependencyResolutionFailure failure =
            result.Failures.ShouldHaveSingleItem();
        failure.Code.ShouldBe(
            DependencyResolutionFailureCode.RegistryUnavailable);
        RegistryAttemptFailure attempt =
            failure.RegistryFailures.ShouldHaveSingleItem();
        attempt.Category.ShouldBe(
            RegistryFailureCategory.InvalidResponse);
        failure.Message.ShouldNotContain("sensitive");
    }

    private static DependencyResolver CreateResolver(
        Mock<IVersionResolver> versionResolver,
        Mock<IRegistryClient> registry,
        Mock<IPackageCache> cache) =>
        new(
            registry.Object,
            versionResolver.Object,
            cache.Object,
            NullLogger.Instance);

    private static Mock<IVersionResolver> CreateExactVersionResolver(
        params string[] missingPackageIds)
    {
        HashSet<string> missing = new(
            missingPackageIds,
            StringComparer.OrdinalIgnoreCase);
        Mock<IVersionResolver> versionResolver = new();
        versionResolver
            .Setup(resolver => resolver.ResolveVersionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                string packageId,
                string versionSpecifier,
                VersionResolveOptions? _,
                CancellationToken _) =>
            {
                if (missing.Contains(packageId))
                    return null;

                return FhirSemVer.TryParse(
                    versionSpecifier,
                    out FhirSemVer? version)
                    ? version
                    : null;
            });
        return versionResolver;
    }

    private static Mock<IRegistryClient> CreateRegistry(
        IReadOnlyDictionary<string, PackageListing> listings)
    {
        Mock<IRegistryClient> registry = new();
        registry.SetupGet(client => client.Endpoint)
            .Returns(new RegistryEndpoint
            {
                Url = "https://registry.example/",
                Type = RegistryType.FhirNpm,
            });
        registry.Setup(client => client.GetPackageListingAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                string packageId,
                CancellationToken _) =>
                listings.TryGetValue(
                    packageId,
                    out PackageListing? listing)
                    ? listing
                    : null);
        return registry;
    }

    private static Mock<IPackageCache> CreateCache(
        Func<PackageReference, PackageManifest?>? readManifest = null)
    {
        Mock<IPackageCache> cache = new();
        cache.Setup(value => value.ReadManifestAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                PackageReference reference,
                CancellationToken _) =>
                readManifest?.Invoke(reference));
        return cache;
    }

    private static PackageManifest CreateRoot(
        IReadOnlyDictionary<string, string> dependencies) =>
        CreateManifest(
            "root.package",
            "1.0.0",
            dependencies);

    private static PackageManifest CreateManifest(
        string packageId,
        string version,
        IReadOnlyDictionary<string, string>? dependencies) =>
        new()
        {
            Name = packageId,
            Version = version,
            Dependencies = dependencies,
        };

    private static Dictionary<string, PackageListing>
        CreateConflictGraphListings()
    {
        Dictionary<string, PackageListing> listings = new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["low.parent"] = CreateListing(
                CreateVersion(
                    "low.parent",
                    "1.0.0",
                    Dependencies(("pivot.package", "1.0.0")))),
            ["high.parent"] = CreateListing(
                CreateVersion(
                    "high.parent",
                    "1.0.0",
                    Dependencies(("bridge.package", "1.0.0")))),
            ["bridge.package"] = CreateListing(
                CreateVersion(
                    "bridge.package",
                    "1.0.0",
                    Dependencies(("pivot.package", "2.0.0")))),
            ["pivot.package"] = CreateListing(
                CreateVersion(
                    "pivot.package",
                    "1.0.0",
                    Dependencies(
                        ("losing.child", "1.0.0"),
                        ("stale.missing", "1.0.0"),
                        ("shared.child", "1.0.0"))),
                CreateVersion(
                    "pivot.package",
                    "2.0.0",
                    Dependencies(
                        ("winning.child", "1.0.0"),
                        ("shared.child", "1.0.0")))),
            ["losing.child"] = CreateListing(
                CreateVersion(
                    "losing.child",
                    "1.0.0",
                    Dependencies())),
            ["winning.child"] = CreateListing(
                CreateVersion(
                    "winning.child",
                    "1.0.0",
                    Dependencies())),
            ["shared.child"] = CreateListing(
                CreateVersion(
                    "shared.child",
                    "1.0.0",
                    Dependencies())),
        };
        return listings;
    }

    private static PackageListing CreateListing(
        params PackageVersionInfo[] versions)
    {
        string packageId = versions[0].Name;
        Dictionary<string, PackageVersionInfo> versionMap =
            versions.ToDictionary(
                version => version.Version,
                StringComparer.OrdinalIgnoreCase);
        return new PackageListing
        {
            PackageId = packageId,
            Versions = versionMap,
            VersionCandidates = versions,
        };
    }

    private static PackageVersionInfo CreateVersion(
        string packageId,
        string version,
        IReadOnlyDictionary<string, string>? dependencies) =>
        new()
        {
            Name = packageId,
            Version = version,
            Dependencies = dependencies,
        };

    private static IReadOnlyDictionary<string, string> Dependencies(
        params (string PackageId, string Version)[] dependencies)
    {
        Dictionary<string, string> result =
            new(StringComparer.OrdinalIgnoreCase);
        foreach ((string packageId, string version) in dependencies)
        {
            result.Add(packageId, version);
        }

        return result;
    }
}
