// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
using FhirPkg.Utilities;
using Shouldly;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;

namespace FhirPkg.Tests;

[Collection("EnvironmentVariable")]
public class FhirPackageManagerTests
{
    private readonly Mock<IHardenedPackageCache> _cacheMock = new();
    private readonly Mock<IRegistryClient> _registryMock = new();
    private readonly Mock<IVersionResolver> _versionResolverMock = new();
    private readonly Mock<IDependencyResolver> _dependencyResolverMock = new();
    private readonly Mock<IPackageIndexer> _indexerMock = new();

    public FhirPackageManagerTests()
    {
        _cacheMock.Setup(cache => cache.InspectAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HardenedPackageCacheInspection
            {
                State = HardenedPackageCacheState.Missing
            });
    }

    private FhirPackageManager CreateManager(FhirPackageManagerOptions? options = null)
    {
        return new FhirPackageManager(
            _cacheMock.Object,
            _registryMock.Object,
            _versionResolverMock.Object,
            _dependencyResolverMock.Object,
            _indexerMock.Object,
            options ?? new FhirPackageManagerOptions(),
            NullLogger<FhirPackageManager>.Instance);
    }

    [Fact]
    public void PublicConstructors_PreserveExistingSignatures()
    {
        ConstructorInfo[] constructors = typeof(FhirPackageManager).GetConstructors();
        Type[][] signatures = constructors
            .Select(constructor => constructor.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .ToArray())
            .ToArray();

        signatures.Length.ShouldBe(3);
        signatures.Any(signature => signature.Length == 0).ShouldBeTrue();
        signatures.Any(signature => signature.SequenceEqual(
        [
            typeof(FhirPackageManagerOptions),
            typeof(ILoggerFactory)
        ])).ShouldBeTrue();
        signatures.Any(signature => signature.SequenceEqual(
        [
            typeof(IPackageCache),
            typeof(IRegistryClient),
            typeof(IVersionResolver),
            typeof(IDependencyResolver),
            typeof(IPackageIndexer),
            typeof(FhirPackageManagerOptions),
            typeof(ILogger<FhirPackageManager>),
            typeof(MemoryResourceCache)
        ])).ShouldBeTrue();
    }

    [Fact]
    public async Task InstallAsync_CachedPackage_ReturnsWithoutDownload()
    {
        PackageRecord expectedRecord = new PackageRecord
        {
            Reference = new PackageReference("hl7.fhir.r4.core", "4.0.1"),
            DirectoryPath = "/cache/hl7.fhir.r4.core#4.0.1",
            ContentPath = "/cache/hl7.fhir.r4.core#4.0.1/package",
            Manifest = new PackageManifest { Name = "hl7.fhir.r4.core", Version = "4.0.1" }
        };

        _cacheMock.Setup(c => c.IsInstalledAsync(
                It.Is<PackageReference>(r => r.Name == "hl7.fhir.r4.core" && r.Version == "4.0.1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _cacheMock.Setup(c => c.GetPackageAsync(
                It.Is<PackageReference>(r => r.Name == "hl7.fhir.r4.core" && r.Version == "4.0.1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedRecord);

        using FhirPackageManager manager = CreateManager();

        PackageRecord? result = await manager.InstallAsync("hl7.fhir.r4.core#4.0.1", cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Reference.Name.ShouldBe("hl7.fhir.r4.core");
        result.Reference.Version.ShouldBe("4.0.1");

        // Verify no download was attempted
        _registryMock.Verify(r => r.DownloadAsync(
            It.IsAny<ResolvedDirective>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_UsesVersionFixupSnapshotCapturedAtConstruction()
    {
        FhirPackageManagerOptions options = new()
        {
            VersionFixups = new Dictionary<string, string>
            {
                ["example.package@1.0.0"] = "1.0.1",
            },
        };
        PackageDirective? captured = null;
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<PackageDirective, VersionResolveOptions?, CancellationToken>(
                (directive, _, _) => captured = directive)
            .ReturnsAsync((ResolvedDirective?)null);

        using FhirPackageManager manager = CreateManager(options);
        options.VersionFixups["example.package@1.0.0"] = "2.0.0";

        await manager.ResolveAsync(
            "example.package#1.0.0",
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured.RequestedVersion.ShouldBe("1.0.1");
    }

    [Theory]
    [InlineData("2.0", "2.0")]
    [InlineData("4.x?", "4.1.0")]
    [InlineData("6.1?", "6.1.0")]
    [InlineData("4.*.*", "4.1.0")]
    [InlineData("6.0.x-*", "6.0.0-ballot")]
    public async Task ResolveAsync_DefinedWildcardGrammar_Succeeds(
        string specifier,
        string expectedVersion)
    {
        PackageListing listing = CreateWildcardGrammarListing();
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                PackageDirective directive,
                VersionResolveOptions? options,
                CancellationToken _) =>
                CreateResolvedDirective(
                    PackageVersionSelector.Select(
                        directive,
                        listing,
                        options)));
        using FhirPackageManager manager = CreateManager();

        ResolvedDirective? result = await manager.ResolveAsync(
            $"example.package#{specifier}",
            TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Reference.Version.ShouldBe(expectedVersion);
    }

    [Fact]
    public async Task InstallAsync_DisabledPrereleaseWildcard_DoesNotDownload()
    {
        PackageListing listing = CreateWildcardGrammarListing();
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                PackageDirective directive,
                VersionResolveOptions? options,
                CancellationToken _) =>
                CreateResolvedDirective(
                    PackageVersionSelector.Select(
                        directive,
                        listing,
                        options)));
        using FhirPackageManager manager = CreateManager();

        PackageRecord? result = await manager.InstallAsync(
            "example.package#6.0.x-*",
            new InstallOptions { AllowPreRelease = false },
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        _registryMock.Verify(registry => registry.DownloadAsync(
            It.IsAny<ResolvedDirective>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task InstallAsync_InvalidDirective_Throws(string? directive)
    {
        using FhirPackageManager manager = CreateManager();

        Func<Task<PackageRecord?>> act = () => manager.InstallAsync(directive!);

        await Should.ThrowAsync<ArgumentException>(act);
    }

    [Theory]
    [InlineData("../escape#1.0.0")]
    [InlineData("example.package#current$../escape")]
    public async Task InstallAsync_UnsafeIdentityFailsBeforeCacheOrRegistryAccess(
        string directive)
    {
        using FhirPackageManager manager = CreateManager();

        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
            () => manager.InstallAsync(
                directive,
                cancellationToken: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPackageIdentity);
        _cacheMock.Verify(cache => cache.IsInstalledAsync(
            It.IsAny<PackageReference>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _registryMock.Verify(registry => registry.ResolveAsync(
            It.IsAny<PackageDirective>(),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListCachedAsync_DelegatesToCache()
    {
        ReadOnlyCollection<PackageRecord> expectedRecords = new List<PackageRecord>
        {
            new()
            {
                Reference = new PackageReference("hl7.fhir.r4.core", "4.0.1"),
                DirectoryPath = "/cache/hl7.fhir.r4.core#4.0.1",
                ContentPath = "/cache/hl7.fhir.r4.core#4.0.1/package",
                Manifest = new PackageManifest { Name = "hl7.fhir.r4.core", Version = "4.0.1" }
            }
        }.AsReadOnly();

        _cacheMock.Setup(c => c.ListPackagesAsync(
                "hl7",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedRecords);

        using FhirPackageManager manager = CreateManager();

        IReadOnlyList<PackageRecord> result = await manager.ListCachedAsync("hl7", cancellationToken: TestContext.Current.CancellationToken);

        result.Count.ShouldBe(1);
        result[0].Reference.Name.ShouldBe("hl7.fhir.r4.core");
        _cacheMock.Verify(cache => cache.ListPackagesAsync(
            "hl7",
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListCachedSummariesAsync_DelegatesToCacheSummaryPath()
    {
        ReadOnlyCollection<PackageRecord> expectedRecords =
            new List<PackageRecord>
            {
                new()
                {
                    Reference = new PackageReference(
                        "hl7.fhir.r4.core",
                        "4.0.1"),
                    DirectoryPath =
                        "/cache/hl7.fhir.r4.core#4.0.1",
                    ContentPath =
                        "/cache/hl7.fhir.r4.core#4.0.1/package",
                    Manifest = new PackageManifest
                    {
                        Name = "hl7.fhir.r4.core",
                        Version = "4.0.1"
                    }
                }
            }.AsReadOnly();
        _cacheMock.Setup(cache => cache.ListPackageSummariesAsync(
                "hl7",
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedRecords);
        using FhirPackageManager manager = CreateManager();

        IReadOnlyList<PackageRecord> result =
            await manager.ListCachedSummariesAsync(
                "hl7",
                TestContext.Current.CancellationToken);

        result.ShouldBeSameAs(expectedRecords);
        _cacheMock.Verify(cache => cache.ListPackageSummariesAsync(
            "hl7",
            null,
            It.IsAny<CancellationToken>()), Times.Once);
        _cacheMock.Verify(cache => cache.ListPackagesAsync(
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InstallAsync_NotCached_ResolvesAndDownloads()
    {
        ResolvedDirective resolvedDirective = new ResolvedDirective
        {
            Reference = new PackageReference("hl7.fhir.r4.core", "4.0.1"),
            TarballUri = new Uri("https://packages.fhir.org/hl7.fhir.r4.core/4.0.1")
        };

        PackageDownloadResult downloadResult = new PackageDownloadResult
        {
            Content = new MemoryStream([1, 2, 3]),
            ContentType = "application/gzip"
        };

        PackageRecord installedRecord = new PackageRecord
        {
            Reference = new PackageReference("hl7.fhir.r4.core", "4.0.1"),
            DirectoryPath = "/cache/hl7.fhir.r4.core#4.0.1",
            ContentPath = "/cache/hl7.fhir.r4.core#4.0.1/package",
            Manifest = new PackageManifest { Name = "hl7.fhir.r4.core", Version = "4.0.1" }
        };

        _cacheMock.Setup(c => c.IsInstalledAsync(It.IsAny<PackageReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _registryMock.Setup(r => r.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedDirective);

        _registryMock.Setup(r => r.DownloadAsync(
                It.IsAny<ResolvedDirective>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(downloadResult);
        _cacheMock.Setup(c => c.InstallAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(installedRecord);

        using FhirPackageManager manager = CreateManager();

        PackageRecord? result = await manager.InstallAsync("hl7.fhir.r4.core#4.0.1", cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Reference.Version.ShouldBe("4.0.1");
    }

    [Fact]
    public async Task InstallAsync_ResolveReturnsNull_ReturnsNull()
    {
        _cacheMock.Setup(c => c.IsInstalledAsync(It.IsAny<PackageReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _registryMock.Setup(r => r.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedDirective?)null);

        using FhirPackageManager manager = CreateManager();

        PackageRecord? result = await manager.InstallAsync("hl7.fhir.r4.core#4.0.1", cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task InstallAsync_ChecksumMismatch_ThrowsInvalidOperation()
    {
        ResolvedDirective resolvedDirective = new ResolvedDirective
        {
            Reference = new PackageReference("hl7.fhir.r4.core", "4.0.1"),
            TarballUri = new Uri("https://packages.fhir.org/hl7.fhir.r4.core/4.0.1"),
            Sha256Sum = "0000000000000000000000000000000000000000000000000000000000000000"
        };

        PackageDownloadResult downloadResult = new PackageDownloadResult
        {
            Content = new MemoryStream([1, 2, 3]),
            ContentType = "application/gzip"
        };

        _cacheMock.Setup(c => c.IsInstalledAsync(It.IsAny<PackageReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _registryMock.Setup(r => r.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedDirective);

        _registryMock.Setup(r => r.DownloadAsync(
                It.IsAny<ResolvedDirective>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(downloadResult);
        _cacheMock.Setup(cache => cache.InstallAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<Stream>(),
                It.Is<InstallCacheOptions?>(options =>
                    options != null
                    && options.ExpectedSha256Sum == resolvedDirective.Sha256Sum),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PackageInstallException(
                PackageInstallErrorCode.ChecksumMismatch,
                PackageInstallStage.ChecksumValidation,
                "Checksum mismatch.",
                resolvedDirective.Reference.FhirDirective));

        using FhirPackageManager manager = CreateManager();

        Func<Task<PackageRecord?>> act = () => manager.InstallAsync("hl7.fhir.r4.core#4.0.1");

        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(act);

        exception.ShouldBeAssignableTo<InvalidOperationException>();
        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.ChecksumMismatch);
    }

    [Fact]
    public async Task InstallAsync_OverwriteExisting_Succeeds()
    {
        ResolvedDirective resolvedDirective = new ResolvedDirective
        {
            Reference = new PackageReference("hl7.fhir.r4.core", "4.0.1"),
            TarballUri = new Uri("https://packages.fhir.org/hl7.fhir.r4.core/4.0.1")
        };

        PackageDownloadResult downloadResult = new PackageDownloadResult
        {
            Content = new MemoryStream([1, 2, 3]),
            ContentType = "application/gzip"
        };

        PackageRecord installedRecord = new PackageRecord
        {
            Reference = new PackageReference("hl7.fhir.r4.core", "4.0.1"),
            DirectoryPath = "/cache/hl7.fhir.r4.core#4.0.1",
            ContentPath = "/cache/hl7.fhir.r4.core#4.0.1/package",
            Manifest = new PackageManifest { Name = "hl7.fhir.r4.core", Version = "4.0.1" }
        };

        _cacheMock.Setup(c => c.IsInstalledAsync(It.IsAny<PackageReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _registryMock.Setup(r => r.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedDirective);

        _registryMock.Setup(r => r.DownloadAsync(
                It.IsAny<ResolvedDirective>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(downloadResult);

        _cacheMock.Setup(c => c.InstallAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(installedRecord);

        using FhirPackageManager manager = CreateManager();

        PackageRecord? result = await manager.InstallAsync(
            "hl7.fhir.r4.core#4.0.1",
            new InstallOptions { OverwriteExisting = true },
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Reference.Version.ShouldBe("4.0.1");

        // Verify download was attempted despite being already installed
        _registryMock.Verify(r => r.DownloadAsync(
            It.IsAny<ResolvedDirective>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstallAsync_Current_PreservesRequestedAliasAsCacheReference()
    {
        ResolvedDirective resolvedDirective = new ResolvedDirective
        {
            Reference = new PackageReference("example.package", "2.0.0"),
            TarballUri = new Uri("https://example.test/example.package.tgz"),
            PublicationDate = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc)
        };
        PackageReference? installedReference = null;
        InstallCacheOptions? capturedOptions = null;
        PackageRecord installedRecord = CreatePackageRecord("example.package", "current");

        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedDirective);
        _registryMock.Setup(registry => registry.DownloadAsync(
                resolvedDirective,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1, 2, 3]),
                ContentType = "application/gzip"
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<PackageReference, Stream, InstallCacheOptions?, CancellationToken>(
                (reference, _, options, _) =>
                {
                    installedReference = reference;
                    capturedOptions = options;
                })
            .ReturnsAsync(installedRecord);

        using FhirPackageManager manager = CreateManager();

        PackageRecord? result = await manager.InstallAsync(
            "example.package#current",
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(installedRecord);
        installedReference.ShouldBe(new PackageReference("example.package", "current"));
        capturedOptions.ShouldNotBeNull();
        capturedOptions!.ArchiveSha256.ShouldBeNull();
        capturedOptions.SkipIfArchiveUnchanged.ShouldBeTrue();
        capturedOptions.SourcePublicationDate.ShouldBe(
            new DateTimeOffset(resolvedDirective.PublicationDate.Value));
    }

    [Fact]
    public async Task InstallAsync_CurrentBranch_PreservesFullRequestedAlias()
    {
        ResolvedDirective resolvedDirective = new ResolvedDirective
        {
            Reference = new PackageReference("example.package", "2.0.0"),
            TarballUri = new Uri("https://example.test/example.package.tgz")
        };
        PackageReference? installedReference = null;

        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedDirective);
        _registryMock.Setup(registry => registry.DownloadAsync(
                resolvedDirective,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1]),
                ContentType = "application/gzip"
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<PackageReference, Stream, InstallCacheOptions?, CancellationToken>(
                (reference, _, _, _) => installedReference = reference)
            .ReturnsAsync(CreatePackageRecord("example.package", "current$feature/fix"));

        using FhirPackageManager manager = CreateManager();

        await manager.InstallAsync(
            "example.package#current$feature/fix",
            cancellationToken: TestContext.Current.CancellationToken);

        installedReference.ShouldBe(
            new PackageReference("example.package", "current$feature/fix"));
    }

    [Fact]
    public async Task InstallAsync_DevMissing_DoesNotResolveFromRegistries()
    {
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        using FhirPackageManager manager = CreateManager();

        PackageRecord? result = await manager.InstallAsync(
            "example.package#dev",
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        _registryMock.Verify(registry => registry.ResolveAsync(
            It.IsAny<PackageDirective>(),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InstallAsync_DevCached_RemainsAuthoritativeWhenOverwriteRequested()
    {
        PackageReference aliasReference = new PackageReference("example.package", "dev");
        PackageRecord cachedRecord = CreatePackageRecord("example.package", "dev");
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                aliasReference,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _cacheMock.Setup(cache => cache.GetPackageAsync(
                aliasReference,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedRecord);
        using FhirPackageManager manager = CreateManager();

        PackageRecord? result = await manager.InstallAsync(
            "example.package#dev",
            new InstallOptions { OverwriteExisting = true },
            TestContext.Current.CancellationToken);

        result.ShouldBe(cachedRecord);
        _registryMock.Verify(registry => registry.ResolveAsync(
            It.IsAny<PackageDirective>(),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _cacheMock.Verify(cache => cache.InstallAsync(
            It.IsAny<PackageReference>(),
            It.IsAny<Stream>(),
            It.IsAny<InstallCacheOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InstallAsync_CurrentWithSamePublication_ReturnsCachedPackage()
    {
        DateTime publicationDate = new DateTime(
            2026,
            7,
            17,
            12,
            0,
            0,
            DateTimeKind.Utc);
        PackageReference aliasReference = new PackageReference("example.package", "current");
        PackageRecord cachedRecord = CreateAliasPackageRecord(
            "example.package",
            "current",
            "2.0.0");
        ResolvedDirective resolvedDirective = new ResolvedDirective
        {
            Reference = new PackageReference("example.package", "2.0.0"),
            TarballUri = new Uri("https://example.test/example.package.tgz"),
            PublicationDate = publicationDate
        };

        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                aliasReference,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _cacheMock.Setup(cache => cache.GetPackageAsync(
                aliasReference,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedRecord);
        _cacheMock.Setup(cache => cache.GetMetadataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CacheMetadata
            {
                Packages = new Dictionary<string, CacheMetadataEntry>
                {
                    ["example.package#current"] = new CacheMetadataEntry
                    {
                        DownloadDateTime = publicationDate,
                        SourcePublicationDate = new DateTimeOffset(publicationDate)
                    }
                }
            });
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedDirective);

        using FhirPackageManager manager = CreateManager();

        PackageRecord? result = await manager.InstallAsync(
            "example.package#current",
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(cachedRecord);
        _registryMock.Verify(registry => registry.DownloadAsync(
            It.IsAny<ResolvedDirective>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InstallAsync_CurrentMovedAtSamePublication_RefreshesCache()
    {
        DateTime publicationDate = new(
            2026,
            7,
            17,
            12,
            0,
            0,
            DateTimeKind.Utc);
        PackageReference aliasReference =
            new("example.package", "current");
        PackageRecord cachedRecord = CreateAliasPackageRecord(
            "example.package",
            "current",
            "1.0.0");
        PackageRecord replacementRecord = CreateAliasPackageRecord(
            "example.package",
            "current",
            "2.0.0");
        ResolvedDirective resolvedDirective = new()
        {
            Reference =
                new PackageReference(
                    "example.package",
                    "2.0.0"),
            TarballUri =
                new Uri("https://example.test/example.package.tgz"),
            PublicationDate = publicationDate,
        };
        InstallCacheOptions? capturedOptions = null;

        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                aliasReference,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _cacheMock.Setup(cache => cache.GetPackageAsync(
                aliasReference,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedRecord);
        _cacheMock.Setup(cache => cache.GetMetadataAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CacheMetadata
            {
                Packages =
                    new Dictionary<string, CacheMetadataEntry>
                    {
                        ["example.package#current"] =
                            new CacheMetadataEntry
                            {
                                DownloadDateTime =
                                    publicationDate,
                                SourcePublicationDate =
                                    new DateTimeOffset(
                                        publicationDate),
                            },
                    },
            });
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedDirective);
        _registryMock.Setup(registry => registry.DownloadAsync(
                resolvedDirective,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1]),
                ContentType = "application/gzip",
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                aliasReference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<
                PackageReference,
                Stream,
                InstallCacheOptions?,
                CancellationToken>(
                (_, _, options, _) =>
                    capturedOptions = options)
            .ReturnsAsync(replacementRecord);
        using FhirPackageManager manager = CreateManager();

        PackageRecord? result = await manager.InstallAsync(
            "example.package#current",
            cancellationToken:
                TestContext.Current.CancellationToken);

        result.ShouldBe(replacementRecord);
        capturedOptions.ShouldNotBeNull();
        capturedOptions!.SkipIfArchiveUnchanged.ShouldBeFalse();
        capturedOptions.IdentityExpectation.ShouldNotBeNull();
        capturedOptions.IdentityExpectation!
            .ExpectedManifestReference.ShouldBe(
                new PackageReference(
                    "example.package",
                    "2.0.0"));
    }

    [Fact]
    public async Task InstallAsync_CurrentWithSameArchiveHash_DoesNotReplace()
    {
        byte[] content = [1, 2, 3];
        string archiveSha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        PackageReference aliasReference = new PackageReference("example.package", "current");
        PackageRecord cachedRecord = CreateAliasPackageRecord(
            "example.package",
            "current",
            "2.0.0");
        CacheMetadataEntry metadataEntry = new CacheMetadataEntry
        {
            DownloadDateTime = DateTime.UtcNow,
            ArchiveSha256 = archiveSha256
        };
        ResolvedDirective resolvedDirective = new ResolvedDirective
        {
            Reference = new PackageReference("example.package", "2.0.0"),
            TarballUri = new Uri("https://example.test/example.package.tgz")
        };

        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                aliasReference,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _cacheMock.Setup(cache => cache.GetPackageAsync(
                aliasReference,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedRecord);
        _cacheMock.Setup(cache => cache.GetMetadataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CacheMetadata
            {
                Packages = new Dictionary<string, CacheMetadataEntry>
                {
                    ["example.package#current"] = metadataEntry
                }
            });
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedDirective);
        _registryMock.Setup(registry => registry.DownloadAsync(
                resolvedDirective,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream(content),
                ContentType = "application/gzip"
            });
        InstallCacheOptions? capturedOptions = null;
        _cacheMock.Setup(cache => cache.InstallAsync(
                aliasReference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<PackageReference, Stream, InstallCacheOptions?, CancellationToken>(
                (_, _, options, _) => capturedOptions = options)
            .ReturnsAsync(cachedRecord);

        using FhirPackageManager manager = CreateManager();

        PackageRecord? result = await manager.InstallAsync(
            "example.package#current",
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(cachedRecord);
        capturedOptions.ShouldNotBeNull();
        capturedOptions!.SkipIfArchiveUnchanged.ShouldBeTrue();
        _cacheMock.Verify(cache => cache.InstallAsync(
            It.IsAny<PackageReference>(),
            It.IsAny<Stream>(),
            It.IsAny<InstallCacheOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstallAsync_CurrentWithChangedArchiveHash_ReplacesAlias()
    {
        PackageReference aliasReference = new PackageReference("example.package", "current");
        PackageRecord cachedRecord = CreateAliasPackageRecord(
            "example.package",
            "current",
            "2.0.0");
        PackageRecord replacementRecord = CreateAliasPackageRecord(
            "example.package",
            "current",
            "2.0.0");
        InstallCacheOptions? capturedOptions = null;
        ResolvedDirective resolvedDirective = new ResolvedDirective
        {
            Reference = new PackageReference("example.package", "2.0.0"),
            TarballUri = new Uri("https://example.test/example.package.tgz")
        };

        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                aliasReference,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _cacheMock.Setup(cache => cache.GetPackageAsync(
                aliasReference,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedRecord);
        _cacheMock.Setup(cache => cache.GetMetadataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CacheMetadata
            {
                Packages = new Dictionary<string, CacheMetadataEntry>
                {
                    ["example.package#current"] = new CacheMetadataEntry
                    {
                        DownloadDateTime = DateTime.UtcNow,
                        ArchiveSha256 = "different"
                    }
                }
            });
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedDirective);
        _registryMock.Setup(registry => registry.DownloadAsync(
                resolvedDirective,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1, 2, 3]),
                ContentType = "application/gzip"
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                aliasReference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<PackageReference, Stream, InstallCacheOptions?, CancellationToken>(
                (_, _, options, _) => capturedOptions = options)
            .ReturnsAsync(replacementRecord);

        using FhirPackageManager manager = CreateManager();

        PackageRecord? result = await manager.InstallAsync(
            "example.package#current",
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBe(replacementRecord);
        capturedOptions.ShouldNotBeNull();
        capturedOptions!.OverwriteExisting.ShouldBeTrue();
    }

    [Theory]
    [InlineData("current")]
    [InlineData("current$main")]
    public async Task InstallManyAsync_CurrentFirstInstallReportsInstalled(
        string alias)
    {
        PackageReference aliasReference =
            new PackageReference("example.package", alias);
        PackageRecord installedRecord = CreateAliasPackageRecord(
            "example.package",
            alias,
            "2.0.0",
            "20260721");
        ResolvedDirective resolvedDirective = new()
        {
            Reference =
                new PackageReference("example.package", "2.0.0"),
            TarballUri =
                new Uri("https://example.test/example.package.tgz")
        };
        SetupMutableCiScenario(
            alias,
            cachedRecord: null,
            resolvedDirective,
            installedRecord,
            new PackageCacheInstallOutcome(
                PackageCacheInstallEffect.Created,
                null));
        using FhirPackageManager manager = CreateManager();

        IReadOnlyList<PackageInstallResult> results =
            await manager.InstallManyAsync(
                [$"example.package#{alias}"],
                cancellationToken:
                    TestContext.Current.CancellationToken);

        PackageInstallResult result = results.ShouldHaveSingleItem();
        AssertMutableCiResult(
            result,
            PackageInstallDisposition.Installed,
            previousManifestDate: null,
            manifestDate: "20260721");
        _registryMock.Verify(
            registry => registry.DownloadAsync(
                resolvedDirective,
                It.IsAny<CancellationToken>()),
            Times.Once);
        _cacheMock.Verify(
            cache => cache.InstallAsync(
                aliasReference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("current$main")]
    public async Task InstallManyAsync_CurrentNewerBuildReportsUpdated(
        string alias)
    {
        PackageRecord cachedRecord = CreateAliasPackageRecord(
            "example.package",
            alias,
            "1.0.0",
            "20260720");
        PackageRecord installedRecord = CreateAliasPackageRecord(
            "example.package",
            alias,
            "2.0.0",
            "20260721");
        ResolvedDirective resolvedDirective = new()
        {
            Reference =
                new PackageReference("example.package", "2.0.0"),
            TarballUri =
                new Uri("https://example.test/example.package.tgz")
        };
        SetupMutableCiScenario(
            alias,
            cachedRecord,
            resolvedDirective,
            installedRecord,
            new PackageCacheInstallOutcome(
                PackageCacheInstallEffect.Replaced,
                "20260720"));
        using FhirPackageManager manager = CreateManager();

        IReadOnlyList<PackageInstallResult> results =
            await manager.InstallManyAsync(
                [$"example.package#{alias}"],
                cancellationToken:
                    TestContext.Current.CancellationToken);

        AssertMutableCiResult(
            results.ShouldHaveSingleItem(),
            PackageInstallDisposition.Updated,
            "20260720",
            "20260721");
    }

    [Theory]
    [InlineData("current")]
    [InlineData("current$main")]
    public async Task InstallManyAsync_CurrentSourceMetadataMatchReportsAlreadyCurrent(
        string alias)
    {
        DateTime publicationDate = new(
            2026,
            7,
            21,
            10,
            0,
            0,
            DateTimeKind.Utc);
        PackageReference aliasReference =
            new PackageReference("example.package", alias);
        PackageRecord cachedRecord = CreateAliasPackageRecord(
            "example.package",
            alias,
            "2.0.0",
            "20260721");
        ResolvedDirective resolvedDirective = new()
        {
            Reference =
                new PackageReference("example.package", "2.0.0"),
            TarballUri =
                new Uri("https://example.test/example.package.tgz"),
            PublicationDate = publicationDate
        };
        CacheMetadata metadata = new()
        {
            Packages =
                new Dictionary<string, CacheMetadataEntry>
                {
                    [PackageCacheKey.Create(aliasReference).MetadataKey] =
                        new CacheMetadataEntry
                        {
                            DownloadDateTime = publicationDate,
                            SourcePublicationDate =
                                new DateTimeOffset(publicationDate)
                        }
                }
        };
        SetupMutableCiScenario(
            alias,
            cachedRecord,
            resolvedDirective,
            installedRecord: null,
            installOutcome: null,
            metadata);
        using FhirPackageManager manager = CreateManager();

        IReadOnlyList<PackageInstallResult> results =
            await manager.InstallManyAsync(
                [$"example.package#{alias}"],
                cancellationToken:
                    TestContext.Current.CancellationToken);

        AssertMutableCiResult(
            results.ShouldHaveSingleItem(),
            PackageInstallDisposition.AlreadyCurrent,
            "20260721",
            "20260721");
        _registryMock.Verify(
            registry => registry.DownloadAsync(
                It.IsAny<ResolvedDirective>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _cacheMock.Verify(
            cache => cache.InstallAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("current$main")]
    public async Task InstallManyAsync_CurrentMatchingArchiveReportsAlreadyCurrent(
        string alias)
    {
        PackageReference aliasReference =
            new PackageReference("example.package", alias);
        PackageRecord cachedRecord = CreateAliasPackageRecord(
            "example.package",
            alias,
            "2.0.0",
            "20260721");
        ResolvedDirective resolvedDirective = new()
        {
            Reference =
                new PackageReference("example.package", "2.0.0"),
            TarballUri =
                new Uri("https://example.test/example.package.tgz")
        };
        SetupMutableCiScenario(
            alias,
            cachedRecord,
            resolvedDirective,
            cachedRecord,
            new PackageCacheInstallOutcome(
                PackageCacheInstallEffect.Unchanged,
                "20260721"));
        using FhirPackageManager manager = CreateManager();

        IReadOnlyList<PackageInstallResult> results =
            await manager.InstallManyAsync(
                [$"example.package#{alias}"],
                cancellationToken:
                    TestContext.Current.CancellationToken);

        AssertMutableCiResult(
            results.ShouldHaveSingleItem(),
            PackageInstallDisposition.AlreadyCurrent,
            "20260721",
            "20260721");
        _cacheMock.Verify(
            cache => cache.InstallAsync(
                aliasReference,
                It.IsAny<Stream>(),
                It.Is<InstallCacheOptions?>(
                    options =>
                        options != null
                        && options.SkipIfArchiveUnchanged),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("current$main")]
    public async Task InstallManyAsync_CurrentOverwriteSameArchiveReportsRefreshed(
        string alias)
    {
        PackageRecord cachedRecord = CreateAliasPackageRecord(
            "example.package",
            alias,
            "2.0.0",
            "20260721");
        ResolvedDirective resolvedDirective = new()
        {
            Reference =
                new PackageReference("example.package", "2.0.0"),
            TarballUri =
                new Uri("https://example.test/example.package.tgz")
        };
        SetupMutableCiScenario(
            alias,
            cachedRecord,
            resolvedDirective,
            cachedRecord,
            new PackageCacheInstallOutcome(
                PackageCacheInstallEffect.Unchanged,
                "20260721"));
        using FhirPackageManager manager = CreateManager();

        IReadOnlyList<PackageInstallResult> results =
            await manager.InstallManyAsync(
                [$"example.package#{alias}"],
                new InstallOptions { OverwriteExisting = true },
                TestContext.Current.CancellationToken);

        AssertMutableCiResult(
            results.ShouldHaveSingleItem(),
            PackageInstallDisposition.Refreshed,
            "20260721",
            "20260721");
    }

    [Fact]
    public async Task InstallManyAsync_CurrentReplacedArchiveWithEqualDatesReportsUpdated()
    {
        PackageRecord cachedRecord = CreateAliasPackageRecord(
            "example.package",
            "current",
            "1.0.0",
            "20260721");
        PackageRecord installedRecord = CreateAliasPackageRecord(
            "example.package",
            "current",
            "2.0.0",
            "20260721");
        ResolvedDirective resolvedDirective = new()
        {
            Reference =
                new PackageReference("example.package", "2.0.0"),
            TarballUri =
                new Uri("https://example.test/example.package.tgz")
        };
        SetupMutableCiScenario(
            "current",
            cachedRecord,
            resolvedDirective,
            installedRecord,
            new PackageCacheInstallOutcome(
                PackageCacheInstallEffect.Replaced,
                "20260721"));
        using FhirPackageManager manager = CreateManager();

        IReadOnlyList<PackageInstallResult> results =
            await manager.InstallManyAsync(
                ["example.package#current"],
                cancellationToken:
                    TestContext.Current.CancellationToken);

        AssertMutableCiResult(
            results.ShouldHaveSingleItem(),
            PackageInstallDisposition.Updated,
            "20260721",
            "20260721");
    }

    [Fact]
    public async Task InstallManyAsync_CurrentReplacedArchiveWithoutDatesReportsUpdated()
    {
        PackageRecord cachedRecord = CreateAliasPackageRecord(
            "example.package",
            "current",
            "1.0.0");
        PackageRecord installedRecord = CreateAliasPackageRecord(
            "example.package",
            "current",
            "2.0.0");
        ResolvedDirective resolvedDirective = new()
        {
            Reference =
                new PackageReference("example.package", "2.0.0"),
            TarballUri =
                new Uri("https://example.test/example.package.tgz")
        };
        SetupMutableCiScenario(
            "current",
            cachedRecord,
            resolvedDirective,
            installedRecord,
            new PackageCacheInstallOutcome(
                PackageCacheInstallEffect.Replaced,
                null));
        using FhirPackageManager manager = CreateManager();

        IReadOnlyList<PackageInstallResult> results =
            await manager.InstallManyAsync(
                ["example.package#current"],
                cancellationToken:
                    TestContext.Current.CancellationToken);

        AssertMutableCiResult(
            results.ShouldHaveSingleItem(),
            PackageInstallDisposition.Updated,
            previousManifestDate: null,
            manifestDate: null);
    }

    [Fact]
    public async Task InstallManyAsync_CurrentUnknownCacheOutcomeLeavesDispositionNull()
    {
        PackageRecord installedRecord = CreateAliasPackageRecord(
            "example.package",
            "current",
            "2.0.0",
            "20260721");
        ResolvedDirective resolvedDirective = new()
        {
            Reference =
                new PackageReference("example.package", "2.0.0"),
            TarballUri =
                new Uri("https://example.test/example.package.tgz")
        };
        SetupMutableCiScenario(
            "current",
            cachedRecord: null,
            resolvedDirective,
            installedRecord,
            PackageCacheInstallOutcome.Unknown);
        using FhirPackageManager manager = CreateManager();

        IReadOnlyList<PackageInstallResult> results =
            await manager.InstallManyAsync(
                ["example.package#current"],
                cancellationToken:
                    TestContext.Current.CancellationToken);

        PackageInstallResult result = results.ShouldHaveSingleItem();
        result.Status.ShouldBe(PackageInstallStatus.Installed);
        result.Disposition.ShouldBeNull();
        result.PreviousManifestDate.ShouldBeNull();
        result.ManifestDate.ShouldBeNull();
    }

    [Theory]
    [InlineData("current")]
    [InlineData("current$main")]
    public async Task InstallManyAsync_LockedCurrentAliasReportsAlreadyCurrent(
        string alias)
    {
        PackageReference aliasReference =
            new PackageReference("example.package", alias);
        PackageReference lockedReference =
            new PackageReference("example.package", "2.0.0");
        PackageRecord cachedRecord = CreateAliasPackageRecord(
            "example.package",
            alias,
            "2.0.0",
            "20260721");
        _cacheMock.Setup(
                cache => cache.IsInstalledAsync(
                    aliasReference,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _cacheMock.Setup(
                cache => cache.GetPackageAsync(
                    aliasReference,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedRecord);
        using FhirPackageManager manager = CreateManager();

        PackageInstallResult result = await InvokeInstallResultAsync(
            manager,
            $"example.package#{alias}",
            lockedReference);

        AssertMutableCiResult(
            result,
            PackageInstallDisposition.AlreadyCurrent,
            "20260721",
            "20260721");
        _registryMock.Verify(
            registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InstallManyAsync_ExactCachePathsKeepNullDisposition()
    {
        PackageReference reference =
            new PackageReference("example.package", "1.0.0");
        PackageRecord cachedRecord = CreatePackageRecord(
            "example.package",
            "1.0.0",
            manifestDate: "20260721");
        _cacheMock.Setup(
                cache => cache.IsInstalledAsync(
                    reference,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _cacheMock.Setup(
                cache => cache.GetPackageAsync(
                    reference,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedRecord);
        using FhirPackageManager manager = CreateManager();

        IReadOnlyList<PackageInstallResult> results =
            await manager.InstallManyAsync(
                ["example.package#1.0.0"],
                cancellationToken:
                    TestContext.Current.CancellationToken);

        PackageInstallResult result = results.ShouldHaveSingleItem();
        result.Status.ShouldBe(PackageInstallStatus.Installed);
        result.Disposition.ShouldBeNull();
        result.PreviousManifestDate.ShouldBeNull();
        result.ManifestDate.ShouldBeNull();
    }

    [Fact]
    public async Task InstallAsync_DownloadFailure_ThrowsTypedFailure()
    {
        ResolvedDirective resolvedDirective = new ResolvedDirective
        {
            Reference = new PackageReference("example.package", "1.0.0"),
            TarballUri = new Uri("https://example.test/example.package.tgz")
        };
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedDirective);
        _registryMock.Setup(registry => registry.DownloadAsync(
                resolvedDirective,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PackageDownloadResult?)null);
        using FhirPackageManager manager = CreateManager();

        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
            () => manager.InstallAsync(
                "example.package#1.0.0",
                cancellationToken: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.DownloadFailed);
        exception.Stage.ShouldBe(PackageInstallStage.Acquisition);
    }

    [Fact]
    public async Task InstallAsync_RegistryBodyTimeout_MapsToAcquisitionFailure()
    {
        ResolvedDirective resolvedDirective = new()
        {
            Reference = new PackageReference("example.package", "1.0.0"),
            TarballUri = new Uri("https://example.test/example.package.tgz")
        };
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedDirective);
        _registryMock.Setup(registry => registry.DownloadAsync(
                resolvedDirective,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new TimeoutReadStream(),
                ContentType = "application/gzip"
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns<PackageReference, Stream, InstallCacheOptions?, CancellationToken>(
                async (_, stream, _, cancellationToken) =>
                {
                    byte[] buffer = new byte[1];
                    await stream.ReadExactlyAsync(
                        buffer,
                        cancellationToken);
                    return CreatePackageRecord(
                        "example.package",
                        "1.0.0");
                });
        using FhirPackageManager manager = CreateManager();

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.InstallAsync(
                    "example.package#1.0.0",
                    cancellationToken:
                        TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.DownloadFailed);
        exception.Stage.ShouldBe(PackageInstallStage.Acquisition);
        exception.InnerException.ShouldBeOfType<RegistryResponseTimeoutException>();
    }

    [Fact]
    public async Task InstallManyAsync_UnresolvedDirective_MapsResolutionErrorCode()
    {
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedDirective?)null);
        using FhirPackageManager manager = CreateManager();

        IReadOnlyList<PackageInstallResult> results = await manager.InstallManyAsync(
            ["missing.package#1.0.0"],
            cancellationToken: TestContext.Current.CancellationToken);

        results.Count.ShouldBe(1);
        results[0].Status.ShouldBe(PackageInstallStatus.NotFound);
        results[0].ErrorCode.ShouldBe(PackageInstallErrorCode.ResolutionFailed);
        results[0].ErrorStage.ShouldBe(PackageInstallStage.Resolution);
    }

    [Fact]
    public async Task InstallManyAsync_AllRegistryTransportsFail_ReturnsFailedResult()
    {
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RegistryOperationException(
                "resolve",
                "missing.package",
                [
                    new RegistryAttemptFailure(
                        "https://registry.example/private?secret=value",
                        RegistryFailureCategory.Network)
                ]));
        using FhirPackageManager manager = CreateManager();

        IReadOnlyList<PackageInstallResult> results = await manager.InstallManyAsync(
            ["missing.package#1.0.0"],
            cancellationToken: TestContext.Current.CancellationToken);

        results.Count.ShouldBe(1);
        results[0].Status.ShouldBe(PackageInstallStatus.Failed);
        results[0].ErrorCode.ShouldBe(PackageInstallErrorCode.ResolutionFailed);
        results[0].ErrorStage.ShouldBe(PackageInstallStage.Resolution);
    }

    [Fact]
    public async Task InstallAsync_CancellationIsNotWrapped()
    {
        using CancellationTokenSource source = new CancellationTokenSource();
        source.Cancel();
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(source.Token));
        using FhirPackageManager manager = CreateManager();

        await Should.ThrowAsync<OperationCanceledException>(
            () => manager.InstallAsync(
                "example.package#1.0.0",
                cancellationToken: source.Token));
    }

    [Fact]
    public async Task InstallAsync_DependencyUsesResolvedPerCallPolicy()
    {
        ResolvedDirective rootResolved = new ResolvedDirective
        {
            Reference = new PackageReference("root.package", "1.0.0"),
            TarballUri = new Uri("https://example.test/root.tgz")
        };
        ResolvedDirective dependencyResolved = new ResolvedDirective
        {
            Reference = new PackageReference("dependency.package", "1.0.0"),
            TarballUri = new Uri("https://example.test/dependency.tgz")
        };
        PackageRecord rootRecord = CreatePackageRecord(
            "root.package",
            "1.0.0",
            new Dictionary<string, string>
            {
                ["dependency.package"] = "1.0.0"
            });
        PackageReference dependencyReference =
            dependencyResolved.Reference;
        PackageClosure closure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [dependencyReference.Name] = dependencyReference,
                },
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase),
            InstallOrder = [dependencyReference],
            InstallOrderIsComplete = true,
        };

        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.Is<PackageDirective>(directive => directive.PackageId == "root.package"),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootResolved);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.Is<PackageDirective>(directive => directive.PackageId == "dependency.package"),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(dependencyResolved);
        _dependencyResolverMock.Setup(resolver => resolver.ResolveAsync(
                It.IsAny<PackageManifest>(),
                It.IsAny<DependencyResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(closure);
        _registryMock.Setup(registry => registry.DownloadAsync(
                rootResolved,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1]),
                ContentType = "application/gzip",
                ContentLength = 1
            });
        _registryMock.Setup(registry => registry.DownloadAsync(
                dependencyResolved,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1, 2, 3, 4]),
                ContentType = "application/gzip",
                ContentLength = 4
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                It.Is<PackageReference>(reference => reference.Name == "root.package"),
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootRecord);
        _cacheMock.Setup(cache => cache.InstallAsync(
                It.Is<PackageReference>(reference => reference.Name == "dependency.package"),
                It.IsAny<Stream>(),
                It.Is<InstallCacheOptions?>(options =>
                    options != null
                    && options.Limits != null
                    && options.Limits.MaxCompressedBytes == 3),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PackageInstallException(
                PackageInstallErrorCode.CompressedSizeLimitExceeded,
                PackageInstallStage.Acquisition,
                "Compressed package exceeds the configured limit.",
                dependencyResolved.Reference.FhirDirective));
        FhirPackageManagerOptions managerOptions = new FhirPackageManagerOptions
        {
            InstallLimits = new PackageInstallLimits
            {
                MaxCompressedBytes = 10
            }
        };
        using FhirPackageManager manager = CreateManager(managerOptions);

        DependencyInstallationException exception =
            await Should.ThrowAsync<DependencyInstallationException>(
                () => manager.InstallAsync(
                    "root.package#1.0.0",
                    new InstallOptions
                    {
                        IncludeDependencies = true,
                        InstallLimits = new PackageInstallLimits
                        {
                            MaxCompressedBytes = 3
                        }
                    },
                    TestContext.Current.CancellationToken));

        exception.RootPackage.ShouldBe(rootRecord);
        PackageInstallResult failure =
            exception.DependencyFailures.ShouldHaveSingleItem();
        failure.Directive.ShouldBe("dependency.package#1.0.0");
        failure.ErrorCode.ShouldBe(
            PackageInstallErrorCode.CompressedSizeLimitExceeded);
        failure.ErrorStage.ShouldBe(PackageInstallStage.Acquisition);
        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.DependencyInstallationFailed);
        exception.Stage.ShouldBe(
            PackageInstallStage.DependencyInstallation);
        _cacheMock.Verify(cache => cache.InstallAsync(
            It.IsAny<PackageReference>(),
            It.IsAny<Stream>(),
            It.IsAny<InstallCacheOptions?>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task InstallAsync_OpaqueCiDependency_BootstrapsThenReresolves()
    {
        PackageRecord rootRecord = CreatePackageRecord(
            "root.package",
            "1.0.0",
            new Dictionary<string, string>
            {
                ["ci.package"] = "current",
            });
        PackageRecord ciRecord =
            CreatePackageRecord("ci.package", "2.0.0");
        PackageReference ciAlias =
            new("ci.package", "current");
        DependencyResolutionFailure bootstrapFailure = new()
        {
            Code =
                DependencyResolutionFailureCode.MetadataUnavailable,
            PackageId = "ci.package",
            VersionSpecifier = "current",
            Message =
                "The CI package must be installed before its manifest can be resolved.",
        };
        PackageClosure bootstrapClosure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase),
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["ci.package"] = bootstrapFailure.Message,
                },
            Failures = [bootstrapFailure],
            BootstrapInstallOrder = [ciAlias],
            InstallOrderIsComplete = true,
        };
        PackageClosure completeClosure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["ci.package"] =
                        new PackageReference(
                            "ci.package",
                            "2.0.0"),
                },
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase),
            InstallOrderIsComplete = true,
        };
        ResolvedDirective ciResolved = new()
        {
            Reference = ciAlias,
            TarballUri =
                new Uri("https://example.test/ci.package.tgz"),
        };

        _cacheMock.Setup(cache => cache.InstallAsync(
                rootRecord.Reference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootRecord);
        _dependencyResolverMock.SetupSequence(
                resolver => resolver.ResolveAsync(
                    rootRecord.Manifest,
                    It.IsAny<DependencyResolveOptions?>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(bootstrapClosure)
            .ReturnsAsync(completeClosure);
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                ciAlias,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.Is<PackageDirective>(
                    directive =>
                        directive.PackageId == "ci.package"
                        && directive.RequestedVersion == "current"),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ciResolved);
        _registryMock.Setup(registry => registry.DownloadAsync(
                ciResolved,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1, 2, 3]),
                ContentType = "application/gzip",
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                ciAlias,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ciRecord);
        using FhirPackageManager manager = CreateManager();

        PackageRecord? result = await manager.InstallAsync(
            rootRecord.Reference,
            new MemoryStream([4, 5, 6]),
            new PackageSourceInstallOptions
            {
                IncludeDependencies = true,
            },
            TestContext.Current.CancellationToken);

        result.ShouldBe(rootRecord);
        _dependencyResolverMock.Verify(resolver => resolver.ResolveAsync(
            rootRecord.Manifest,
            It.IsAny<DependencyResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        _registryMock.Verify(registry => registry.ResolveAsync(
            It.Is<PackageDirective>(
                directive =>
                    directive.PackageId == "ci.package"
                    && directive.RequestedVersion == "2.0.0"),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InstallAsync_PartialBootstrap_InstallsUnlockedClosure()
    {
        PackageRecord rootRecord = CreatePackageRecord(
            "root.package",
            "1.0.0",
            new Dictionary<string, string>
            {
                ["good.ci"] = "current",
                ["bad.ci"] = "current",
                ["pruned.ci"] = "current",
            });
        PackageReference goodAlias =
            new("good.ci", "current");
        PackageReference badAlias =
            new("bad.ci", "current");
        PackageReference prunedAlias =
            new("pruned.ci", "current");
        PackageReference childReference =
            new("child.package", "1.0.0");
        DependencyResolutionFailure goodBootstrapFailure = new()
        {
            Code =
                DependencyResolutionFailureCode.MetadataUnavailable,
            PackageId = "good.ci",
            VersionSpecifier = "current",
            Message = "Install good.ci to inspect its manifest.",
        };
        DependencyResolutionFailure badBootstrapFailure = new()
        {
            Code =
                DependencyResolutionFailureCode.MetadataUnavailable,
            PackageId = "bad.ci",
            VersionSpecifier = "current",
            Message = "Install bad.ci to inspect its manifest.",
        };
        DependencyResolutionFailure prunedBootstrapFailure = new()
        {
            Code =
                DependencyResolutionFailureCode.MetadataUnavailable,
            PackageId = "pruned.ci",
            VersionSpecifier = "current",
            Message =
                "Install pruned.ci to inspect its manifest.",
        };
        PackageClosure bootstrapClosure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase),
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["good.ci"] = goodBootstrapFailure.Message,
                    ["bad.ci"] = badBootstrapFailure.Message,
                    ["pruned.ci"] =
                        prunedBootstrapFailure.Message,
                },
            Failures =
                [
                    goodBootstrapFailure,
                    badBootstrapFailure,
                    prunedBootstrapFailure,
                ],
            BootstrapInstallOrder =
                [goodAlias, badAlias, prunedAlias],
            InstallOrderIsComplete = true,
        };
        PackageClosure partialClosure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["good.ci"] =
                        new PackageReference(
                            "good.ci",
                            "2.0.0"),
                    [childReference.Name] =
                        childReference,
                },
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["bad.ci"] = badBootstrapFailure.Message,
                },
            Failures = [badBootstrapFailure],
            BootstrapInstallOrder = [badAlias],
            InstallOrder = [childReference],
            InstallOrderIsComplete = true,
        };
        ResolvedDirective goodResolved = new()
        {
            Reference = goodAlias,
            TarballUri =
                new Uri("https://example.test/good.ci.tgz"),
        };
        ResolvedDirective childResolved = new()
        {
            Reference = childReference,
            TarballUri =
                new Uri("https://example.test/child.package.tgz"),
        };

        _cacheMock.Setup(cache => cache.InstallAsync(
                rootRecord.Reference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootRecord);
        _dependencyResolverMock.SetupSequence(
                resolver => resolver.ResolveAsync(
                    rootRecord.Manifest,
                    It.IsAny<DependencyResolveOptions?>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(bootstrapClosure)
            .ReturnsAsync(partialClosure);
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                PackageDirective directive,
                VersionResolveOptions? _,
                CancellationToken _) =>
                directive.PackageId switch
                {
                    "good.ci" => goodResolved,
                    "child.package" => childResolved,
                    _ => null,
                });
        _registryMock.Setup(registry => registry.DownloadAsync(
                goodResolved,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1]),
                ContentType = "application/gzip",
            });
        _registryMock.Setup(registry => registry.DownloadAsync(
                childResolved,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([2]),
                ContentType = "application/gzip",
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                goodAlias,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAliasPackageRecord(
                "good.ci",
                "current",
                "2.0.0"));
        _cacheMock.Setup(cache => cache.InstallAsync(
                childReference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                CreatePackageRecord(
                    "child.package",
                    "1.0.0"));
        using FhirPackageManager manager = CreateManager();

        DependencyInstallationException exception =
            await Should.ThrowAsync<DependencyInstallationException>(
                () => manager.InstallAsync(
                    rootRecord.Reference,
                    new MemoryStream([3]),
                    new PackageSourceInstallOptions
                    {
                        IncludeDependencies = true,
                    },
                    TestContext.Current.CancellationToken));

        PackageInstallResult failure =
            exception.DependencyFailures.ShouldHaveSingleItem();
        failure.Directive.ShouldBe("bad.ci#current");
        _cacheMock.Verify(cache => cache.InstallAsync(
            childReference,
            It.IsAny<Stream>(),
            It.IsAny<InstallCacheOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _dependencyResolverMock.Verify(resolver => resolver.ResolveAsync(
            rootRecord.Manifest,
            It.IsAny<DependencyResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("current")]
    public async Task InstallAsync_CiDependency_PreservesPinnedAlias(
        string resolvedVersion)
    {
        PackageRecord rootRecord = CreatePackageRecord(
            "root.package",
            "1.0.0",
            new Dictionary<string, string>
            {
                ["ci.package"] = "current",
            });
        PackageReference ciAlias =
            new("ci.package", "current");
        PackageClosure closure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["ci.package"] =
                        new PackageReference(
                            "ci.package",
                            "2.0.0"),
                },
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase),
            InstallOrder = [ciAlias],
            InstallOrderIsComplete = true,
        };
        ResolvedDirective ciResolved = new()
        {
            Reference =
                new PackageReference(
                    "ci.package",
                    resolvedVersion),
            TarballUri =
                new Uri("https://example.test/ci.package.tgz"),
        };

        _cacheMock.Setup(cache => cache.InstallAsync(
                rootRecord.Reference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootRecord);
        _dependencyResolverMock.Setup(resolver => resolver.ResolveAsync(
                rootRecord.Manifest,
                It.IsAny<DependencyResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(closure);
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                ciAlias,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.Is<PackageDirective>(
                    directive =>
                        directive.PackageId == "ci.package"
                        && directive.RequestedVersion == "current"),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ciResolved);
        _registryMock.Setup(registry => registry.DownloadAsync(
                ciResolved,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1]),
                ContentType = "application/gzip",
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                ciAlias,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageRecord
            {
                Reference = ciAlias,
                DirectoryPath = "/cache/ci.package#current",
                ContentPath =
                    "/cache/ci.package#current/package",
                Manifest = new PackageManifest
                {
                    Name = "ci.package",
                    Version = "2.0.0",
                },
            });
        using FhirPackageManager manager = CreateManager();

        PackageRecord? result = await manager.InstallAsync(
            rootRecord.Reference,
            new MemoryStream([2]),
            new PackageSourceInstallOptions
            {
                IncludeDependencies = true,
            },
            TestContext.Current.CancellationToken);

        result.ShouldBe(rootRecord);
        _cacheMock.Verify(cache => cache.InstallAsync(
            ciAlias,
            It.IsAny<Stream>(),
            It.Is<InstallCacheOptions?>(
                options =>
                    options != null
                    && options.IdentityExpectation != null
                    && options.IdentityExpectation
                        .ExpectedManifestReference
                        == new PackageReference(
                            "ci.package",
                            "2.0.0")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstallAsync_TwoMutableAliasesUseTheirMappedExactIdentities()
    {
        PackageRecord rootRecord = CreatePackageRecord(
            "root.package",
            "1.0.0",
            new Dictionary<string, string>
            {
                ["shared.package"] = "current$one",
            });
        PackageReference firstAlias =
            new("shared.package", "current$one");
        PackageReference secondAlias =
            new("shared.package", "current$two");
        PackageReference firstExact =
            new("shared.package", "1.0.0");
        PackageReference secondExact =
            new("shared.package", "2.0.0");
        PackageClosure closure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["shared.package"] = secondExact,
                },
            ResolvedPackages =
                [firstExact, secondExact],
            InstallationIdentities =
            [
                new PackageInstallationIdentity
                {
                    InstallationReference = firstAlias,
                    ResolvedReference = firstExact,
                },
                new PackageInstallationIdentity
                {
                    InstallationReference = secondAlias,
                    ResolvedReference = secondExact,
                },
            ],
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase),
            InstallOrder = [firstAlias, secondAlias],
            InstallOrderIsComplete = true,
        };
        ResolvedDirective firstResolved = new()
        {
            Reference = firstAlias,
            TarballUri =
                new Uri("https://example.test/shared-one.tgz"),
        };
        ResolvedDirective secondResolved = new()
        {
            Reference = secondAlias,
            TarballUri =
                new Uri("https://example.test/shared-two.tgz"),
        };

        _cacheMock.Setup(cache => cache.InstallAsync(
                rootRecord.Reference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootRecord);
        _dependencyResolverMock.Setup(resolver => resolver.ResolveAsync(
                rootRecord.Manifest,
                It.IsAny<DependencyResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(closure);
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.Is<PackageDirective>(
                    directive =>
                        directive.PackageId
                            == "shared.package"),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                PackageDirective directive,
                VersionResolveOptions? _,
                CancellationToken _) =>
                directive.RequestedVersion
                    == firstAlias.Version
                    ? firstResolved
                    : secondResolved);
        _registryMock.Setup(registry => registry.DownloadAsync(
                It.IsAny<ResolvedDirective>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1]),
                ContentType = "application/gzip",
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                firstAlias,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                CreateAliasPackageRecord(
                    firstAlias.Name,
                    firstAlias.Version!,
                    firstExact.Version!));
        _cacheMock.Setup(cache => cache.InstallAsync(
                secondAlias,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                CreateAliasPackageRecord(
                    secondAlias.Name,
                    secondAlias.Version!,
                    secondExact.Version!));
        using FhirPackageManager manager = CreateManager();

        PackageRecord result = await manager.InstallAsync(
            rootRecord.Reference,
            new MemoryStream([2]),
            new PackageSourceInstallOptions
            {
                IncludeDependencies = true,
            },
            TestContext.Current.CancellationToken);

        result.ShouldBe(rootRecord);
        _cacheMock.Verify(cache => cache.InstallAsync(
            firstAlias,
            It.IsAny<Stream>(),
            It.Is<InstallCacheOptions?>(options =>
                options != null
                && options.IdentityExpectation != null
                && options.IdentityExpectation
                    .ExpectedManifestReference
                    == firstExact),
            It.IsAny<CancellationToken>()), Times.Once);
        _cacheMock.Verify(cache => cache.InstallAsync(
            secondAlias,
            It.IsAny<Stream>(),
            It.Is<InstallCacheOptions?>(options =>
                options != null
                && options.IdentityExpectation != null
                && options.IdentityExpectation
                    .ExpectedManifestReference
                    == secondExact),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstallAsync_AmbiguousMutableAliasWithoutMapping_Fails()
    {
        PackageRecord rootRecord = CreatePackageRecord(
            "root.package",
            "1.0.0",
            new Dictionary<string, string>
            {
                ["shared.package"] = "current",
            });
        PackageReference alias =
            new("shared.package", "current");
        PackageClosure closure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["shared.package"] =
                        new PackageReference(
                            "shared.package",
                            "2.0.0"),
                },
            ResolvedPackages =
            [
                new PackageReference(
                    "shared.package",
                    "1.0.0"),
                new PackageReference(
                    "shared.package",
                    "2.0.0"),
            ],
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase),
            InstallOrder = [alias],
            InstallOrderIsComplete = true,
        };
        _cacheMock.Setup(cache => cache.InstallAsync(
                rootRecord.Reference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootRecord);
        _dependencyResolverMock.Setup(resolver => resolver.ResolveAsync(
                rootRecord.Manifest,
                It.IsAny<DependencyResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(closure);
        using FhirPackageManager manager = CreateManager();

        PackageInstallException exception =
            await Should.ThrowAsync<PackageInstallException>(
                () => manager.InstallAsync(
                    rootRecord.Reference,
                    new MemoryStream([1]),
                    new PackageSourceInstallOptions
                    {
                        IncludeDependencies = true,
                    },
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(
            PackageInstallErrorCode.DependencyInstallationFailed);
        exception.Stage.ShouldBe(
            PackageInstallStage.DependencyInstallation);
        exception.Directive.ShouldBe(alias.FhirDirective);
        _registryMock.Verify(registry => registry.ResolveAsync(
            It.IsAny<PackageDirective>(),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InstallAsync_CiDependency_RejectsMovedAlias()
    {
        PackageRecord rootRecord = CreatePackageRecord(
            "root.package",
            "1.0.0",
            new Dictionary<string, string>
            {
                ["ci.package"] = "current",
            });
        PackageReference ciAlias =
            new("ci.package", "current");
        PackageClosure closure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["ci.package"] =
                        new PackageReference(
                            "ci.package",
                            "2.0.0"),
                },
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase),
            InstallOrder = [ciAlias],
            InstallOrderIsComplete = true,
        };
        ResolvedDirective movedAlias = new()
        {
            Reference =
                new PackageReference("ci.package", "2.0.1"),
            TarballUri =
                new Uri("https://example.test/ci.package.tgz"),
        };

        _cacheMock.Setup(cache => cache.InstallAsync(
                rootRecord.Reference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootRecord);
        _dependencyResolverMock.Setup(resolver => resolver.ResolveAsync(
                rootRecord.Manifest,
                It.IsAny<DependencyResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(closure);
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                ciAlias,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(movedAlias);
        using FhirPackageManager manager = CreateManager();

        DependencyInstallationException exception =
            await Should.ThrowAsync<DependencyInstallationException>(
                () => manager.InstallAsync(
                    rootRecord.Reference,
                    new MemoryStream([1]),
                    new PackageSourceInstallOptions
                    {
                        IncludeDependencies = true,
                    },
                    TestContext.Current.CancellationToken));

        PackageInstallResult failure =
            exception.DependencyFailures.ShouldHaveSingleItem();
        failure.ErrorCode.ShouldBe(
            PackageInstallErrorCode.InvalidPackageIdentity);
        failure.ErrorStage.ShouldBe(
            PackageInstallStage.IdentityValidation);
        _registryMock.Verify(registry => registry.DownloadAsync(
            It.IsAny<ResolvedDirective>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InstallAsync_DependencyNotFound_ThrowsDependencyInstallationException()
    {
        PackageRecord rootRecord = CreatePackageRecord(
            "root.package",
            "1.0.0",
            new Dictionary<string, string>
            {
                ["missing.package"] = "1.0.0"
            });
        DependencyResolutionFailure resolutionFailure = new()
        {
            Code = DependencyResolutionFailureCode.PackageNotFound,
            PackageId = "missing.package",
            VersionSpecifier = "1.0.0",
            ParentPackageId = "root.package",
            ParentVersion = "1.0.0",
            Message = "Could not resolve version '1.0.0'.",
        };
        PackageClosure closure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase),
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["missing.package"] = resolutionFailure.Message,
                },
            Failures = [resolutionFailure],
        };
        _cacheMock.Setup(cache => cache.InstallAsync(
                It.Is<PackageReference>(
                    reference => reference.Name == "root.package"),
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootRecord);
        _dependencyResolverMock.Setup(resolver => resolver.ResolveAsync(
                rootRecord.Manifest,
                It.IsAny<DependencyResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(closure);
        using FhirPackageManager manager = CreateManager();

        DependencyInstallationException exception =
            await Should.ThrowAsync<DependencyInstallationException>(
                () => manager.InstallAsync(
                    rootRecord.Reference,
                    new MemoryStream([1, 2, 3]),
                    new PackageSourceInstallOptions
                    {
                        IncludeDependencies = true,
                    },
                    TestContext.Current.CancellationToken));

        exception.RootPackage.ShouldBe(rootRecord);
        exception.DependencyResolutionFailures.ShouldHaveSingleItem()
            .ShouldBe(resolutionFailure);
        PackageInstallResult failure =
            exception.DependencyFailures.ShouldHaveSingleItem();
        failure.Status.ShouldBe(PackageInstallStatus.NotFound);
        failure.Directive.ShouldBe("missing.package#1.0.0");
        failure.ErrorCode.ShouldBe(
            PackageInstallErrorCode.DependencyInstallationFailed);
    }

    [Fact]
    public async Task InstallManyAsync_DependencyFailure_RetainsCommittedRoot()
    {
        ResolvedDirective rootResolved = new()
        {
            Reference = new PackageReference(
                "root.package",
                "1.0.0"),
            TarballUri =
                new Uri("https://example.test/root.tgz"),
        };
        PackageRecord rootRecord = CreatePackageRecord(
            "root.package",
            "1.0.0",
            new Dictionary<string, string>
            {
                ["missing.package"] = "1.0.0"
            });
        DependencyResolutionFailure resolutionFailure = new()
        {
            Code = DependencyResolutionFailureCode.PackageNotFound,
            PackageId = "missing.package",
            VersionSpecifier = "1.0.0",
            Message = "Could not resolve version '1.0.0'.",
        };
        PackageClosure closure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase),
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["missing.package"] = resolutionFailure.Message,
                },
            Failures = [resolutionFailure],
        };
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootResolved);
        _registryMock.Setup(registry => registry.DownloadAsync(
                rootResolved,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1]),
                ContentType = "application/gzip",
                ContentLength = 1,
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                rootResolved.Reference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootRecord);
        _dependencyResolverMock.Setup(resolver => resolver.ResolveAsync(
                rootRecord.Manifest,
                It.IsAny<DependencyResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(closure);
        using FhirPackageManager manager = CreateManager();

        PackageInstallResult result = (await manager.InstallManyAsync(
            ["root.package#1.0.0"],
            new InstallOptions
            {
                IncludeDependencies = true,
            },
            TestContext.Current.CancellationToken)).ShouldHaveSingleItem();

        result.Status.ShouldBe(PackageInstallStatus.Failed);
        result.Package.ShouldBe(rootRecord);
        result.ErrorCode.ShouldBe(
            PackageInstallErrorCode.DependencyInstallationFailed);
        result.ErrorStage.ShouldBe(
            PackageInstallStage.DependencyInstallation);
        result.DependencyFailures.ShouldHaveSingleItem()
            .Directive.ShouldBe("missing.package#1.0.0");
    }

    [Fact]
    public async Task InstallAsync_DependencyFailures_AttemptEntireActiveSet()
    {
        PackageRecord rootRecord = CreatePackageRecord(
            "root.package",
            "1.0.0",
            new Dictionary<string, string>
            {
                ["missing.package"] = "1.0.0",
                ["good.package"] = "1.0.0",
            });
        PackageReference missingReference =
            new("missing.package", "1.0.0");
        PackageReference goodReference =
            new("good.package", "1.0.0");
        PackageClosure closure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [missingReference.Name] = missingReference,
                    [goodReference.Name] = goodReference,
                },
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase),
            InstallOrder = [missingReference, goodReference],
            InstallOrderIsComplete = true,
        };
        ResolvedDirective goodResolved = new()
        {
            Reference = goodReference,
            TarballUri =
                new Uri("https://example.test/good.tgz"),
        };
        PackageRecord goodRecord =
            CreatePackageRecord("good.package", "1.0.0");
        _cacheMock.Setup(cache => cache.InstallAsync(
                rootRecord.Reference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootRecord);
        _dependencyResolverMock.Setup(resolver => resolver.ResolveAsync(
                rootRecord.Manifest,
                It.IsAny<DependencyResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(closure);
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.Is<PackageDirective>(
                    directive =>
                        directive.PackageId == "missing.package"),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedDirective?)null);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.Is<PackageDirective>(
                    directive =>
                        directive.PackageId == "good.package"),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(goodResolved);
        _registryMock.Setup(registry => registry.DownloadAsync(
                goodResolved,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1]),
                ContentType = "application/gzip",
                ContentLength = 1,
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                goodReference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(goodRecord);
        using FhirPackageManager manager = CreateManager();

        DependencyInstallationException exception =
            await Should.ThrowAsync<DependencyInstallationException>(
                () => manager.InstallAsync(
                    rootRecord.Reference,
                    new MemoryStream([1]),
                    new PackageSourceInstallOptions
                    {
                        IncludeDependencies = true,
                    },
                    TestContext.Current.CancellationToken));

        exception.DependencyFailures.ShouldHaveSingleItem()
            .Directive.ShouldBe("missing.package#1.0.0");
        _cacheMock.Verify(cache => cache.InstallAsync(
            goodReference,
            It.IsAny<Stream>(),
            It.IsAny<InstallCacheOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstallAsync_DependencyCancellation_IsNotWrapped()
    {
        using CancellationTokenSource source = new();
        PackageRecord rootRecord = CreatePackageRecord(
            "root.package",
            "1.0.0",
            new Dictionary<string, string>
            {
                ["dependency.package"] = "1.0.0"
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                rootRecord.Reference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootRecord);
        _dependencyResolverMock.Setup(resolver => resolver.ResolveAsync(
                rootRecord.Manifest,
                It.IsAny<DependencyResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback(source.Cancel)
            .ThrowsAsync(new OperationCanceledException(source.Token));
        using FhirPackageManager manager = CreateManager();

        await Should.ThrowAsync<OperationCanceledException>(
            () => manager.InstallAsync(
                rootRecord.Reference,
                new MemoryStream([1]),
                new PackageSourceInstallOptions
                {
                    IncludeDependencies = true,
                },
                source.Token));
    }

    [Fact]
    public async Task InstallAsync_ActiveClosure_SkipsCycleRootAndSupersededNodes()
    {
        PackageRecord rootRecord = CreatePackageRecord(
            "root.package",
            "1.0.0",
            new Dictionary<string, string>
            {
                ["winner.package"] = "2.0.0"
            });
        PackageReference winnerReference =
            new("winner.package", "2.0.0");
        PackageClosure closure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [winnerReference.Name] = winnerReference,
                    [rootRecord.Reference.Name] =
                        rootRecord.Reference,
                },
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase),
            InstallOrder =
                [winnerReference, rootRecord.Reference],
            InstallOrderIsComplete = true,
        };
        ResolvedDirective winnerResolved = new()
        {
            Reference = winnerReference,
            TarballUri =
                new Uri("https://example.test/winner.tgz"),
        };
        _cacheMock.Setup(cache => cache.InstallAsync(
                rootRecord.Reference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootRecord);
        _dependencyResolverMock.Setup(resolver => resolver.ResolveAsync(
                rootRecord.Manifest,
                It.IsAny<DependencyResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(closure);
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.Is<PackageDirective>(
                    directive =>
                        directive.PackageId == "winner.package"),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(winnerResolved);
        _registryMock.Setup(registry => registry.DownloadAsync(
                winnerResolved,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1]),
                ContentType = "application/gzip",
                ContentLength = 1,
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                winnerReference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                CreatePackageRecord(
                    "winner.package",
                    "2.0.0"));
        using FhirPackageManager manager = CreateManager();

        PackageRecord result = await manager.InstallAsync(
            rootRecord.Reference,
            new MemoryStream([1]),
            new PackageSourceInstallOptions
            {
                IncludeDependencies = true,
            },
            TestContext.Current.CancellationToken);

        result.ShouldBe(rootRecord);
        _registryMock.Verify(registry => registry.ResolveAsync(
            It.Is<PackageDirective>(
                directive => directive.PackageId == "root.package"),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _registryMock.Verify(registry => registry.ResolveAsync(
            It.Is<PackageDirective>(
                directive => directive.PackageId == "losing.package"),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _registryMock.Verify(registry => registry.ResolveAsync(
            It.Is<PackageDirective>(
                directive => directive.PackageId == "winner.package"),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstallAsync_DifferentRootVersion_IsInstalled()
    {
        PackageRecord rootRecord = CreatePackageRecord(
            "root.package",
            "1.0.0-Alpha",
            new Dictionary<string, string>
            {
                ["child.package"] = "1.0.0"
            });
        PackageReference conflictingRoot =
            new("root.package", "1.0.0-alpha");
        PackageClosure closure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [conflictingRoot.Name] = conflictingRoot,
                },
            ResolvedPackages = [conflictingRoot],
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase),
            InstallOrder = [conflictingRoot],
            InstallOrderIsComplete = true,
        };
        _cacheMock.Setup(cache => cache.InstallAsync(
                rootRecord.Reference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootRecord);
        _dependencyResolverMock.Setup(resolver => resolver.ResolveAsync(
                rootRecord.Manifest,
                It.IsAny<DependencyResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(closure);
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                conflictingRoot,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        ResolvedDirective resolvedDependency = new()
        {
            Reference = conflictingRoot,
            TarballUri =
                new Uri("https://example.test/root-v2.tgz"),
        };
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.Is<PackageDirective>(
                    directive =>
                        directive.PackageId
                            == conflictingRoot.Name
                        && directive.RequestedVersion
                            == conflictingRoot.Version),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedDependency);
        _registryMock.Setup(registry => registry.DownloadAsync(
                resolvedDependency,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1]),
                ContentType = "application/gzip",
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                conflictingRoot,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                CreatePackageRecord(
                    conflictingRoot.Name,
                    conflictingRoot.Version!));
        using FhirPackageManager manager = CreateManager();

        PackageRecord result = await manager.InstallAsync(
            rootRecord.Reference,
            new MemoryStream([1]),
            new PackageSourceInstallOptions
            {
                IncludeDependencies = true,
            },
            TestContext.Current.CancellationToken);

        result.ShouldBe(rootRecord);
        _cacheMock.Verify(cache => cache.InstallAsync(
            conflictingRoot,
            It.IsAny<Stream>(),
            It.IsAny<InstallCacheOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstallAsync_PartialInstallOrder_FallsBackToAllResolvedPackages()
    {
        PackageRecord rootRecord = CreatePackageRecord(
            "root.package",
            "1.0.0",
            new Dictionary<string, string>
            {
                ["shared.package"] = "2.0.0",
            });
        PackageReference firstReference =
            new("shared.package", "1.0.0");
        PackageReference secondReference =
            new("shared.package", "2.0.0");
        PackageClosure closure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [secondReference.Name] = secondReference,
                },
            ResolvedPackages =
                [firstReference, secondReference],
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase),
            InstallOrder = [firstReference],
            InstallOrderIsComplete = false,
        };
        ResolvedDirective firstResolved = new()
        {
            Reference = firstReference,
            TarballUri =
                new Uri("https://example.test/first.tgz"),
        };
        ResolvedDirective secondResolved = new()
        {
            Reference = secondReference,
            TarballUri =
                new Uri("https://example.test/second.tgz"),
        };

        _cacheMock.Setup(cache => cache.InstallAsync(
                rootRecord.Reference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootRecord);
        _dependencyResolverMock.Setup(resolver => resolver.ResolveAsync(
                rootRecord.Manifest,
                It.IsAny<DependencyResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(closure);
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                PackageDirective directive,
                VersionResolveOptions? _,
                CancellationToken _) =>
                directive.RequestedVersion
                    == firstReference.Version
                    ? firstResolved
                    : secondResolved);
        _registryMock.Setup(registry => registry.DownloadAsync(
                It.IsAny<ResolvedDirective>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1]),
                ContentType = "application/gzip",
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                It.Is<PackageReference>(
                    reference =>
                        reference.Name
                            != rootRecord.Reference.Name),
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                PackageReference reference,
                Stream _,
                InstallCacheOptions? _,
                CancellationToken _) =>
                CreatePackageRecord(
                    reference.Name,
                    reference.Version!));
        using FhirPackageManager manager = CreateManager();

        PackageRecord? result = await manager.InstallAsync(
            rootRecord.Reference,
            new MemoryStream([2]),
            new PackageSourceInstallOptions
            {
                IncludeDependencies = true,
            },
            TestContext.Current.CancellationToken);

        result.ShouldBe(rootRecord);
        _cacheMock.Verify(cache => cache.InstallAsync(
            firstReference,
            It.IsAny<Stream>(),
            It.IsAny<InstallCacheOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        _cacheMock.Verify(cache => cache.InstallAsync(
            secondReference,
            It.IsAny<Stream>(),
            It.IsAny<InstallCacheOptions?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstallAsync_RealResolver_InstallsAllExactVersionsAndSubgraphs()
    {
        PackageRecord rootRecord = CreatePackageRecord(
            "root.package",
            "1.0.0",
            CreateDependencies(
                ("low.parent", "1.0.0"),
                ("high.parent", "1.0.0")));
        Dictionary<string, PackageListing> listings = new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["low.parent"] = CreateDependencyListing(
                CreateDependencyVersion(
                    "low.parent",
                    "1.0.0",
                    CreateDependencies(
                        ("shared.package", "3.1.1")))),
            ["high.parent"] = CreateDependencyListing(
                CreateDependencyVersion(
                    "high.parent",
                    "1.0.0",
                    CreateDependencies(
                        ("shared.package", "6.1.0")))),
            ["shared.package"] = CreateDependencyListing(
                CreateDependencyVersion(
                    "shared.package",
                    "3.1.1",
                    CreateDependencies(
                        ("child.v3", "1.0.0"))),
                CreateDependencyVersion(
                    "shared.package",
                    "6.1.0",
                    CreateDependencies(
                        ("child.v6", "1.0.0")))),
            ["child.v3"] = CreateDependencyListing(
                CreateDependencyVersion(
                    "child.v3",
                    "1.0.0",
                    CreateDependencies())),
            ["child.v6"] = CreateDependencyListing(
                CreateDependencyVersion(
                    "child.v6",
                    "1.0.0",
                    CreateDependencies())),
        };

        _versionResolverMock.Setup(resolver => resolver.ResolveVersionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                string _,
                string versionSpecifier,
                VersionResolveOptions? _,
                CancellationToken _) =>
                FhirSemVer.TryParse(
                    versionSpecifier,
                    out FhirSemVer? version)
                    ? version
                    : null);
        _registryMock.Setup(registry => registry.GetPackageListingAsync(
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
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                PackageDirective directive,
                VersionResolveOptions? _,
                CancellationToken _) =>
                new ResolvedDirective
                {
                    Reference = new PackageReference(
                        directive.PackageId,
                        directive.RequestedVersion),
                    TarballUri = new Uri(
                        $"https://example.test/{Uri.EscapeDataString(directive.PackageId)}-{Uri.EscapeDataString(directive.RequestedVersion ?? string.Empty)}.tgz"),
                });
        _registryMock.Setup(registry => registry.DownloadAsync(
                It.IsAny<ResolvedDirective>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1]),
                ContentType = "application/gzip",
            });
        _cacheMock.Setup(cache => cache.ReadManifestAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PackageManifest?)null);
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _cacheMock.Setup(cache => cache.InstallAsync(
                It.Is<PackageReference>(
                    reference =>
                        reference.Name != rootRecord.Reference.Name),
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                PackageReference reference,
                Stream _,
                InstallCacheOptions? _,
                CancellationToken _) =>
                CreatePackageRecord(
                    reference.Name,
                    reference.Version!));
        _cacheMock.Setup(cache => cache.InstallAsync(
                rootRecord.Reference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootRecord);

        DependencyResolver dependencyResolver = new(
            _registryMock.Object,
            _versionResolverMock.Object,
            _cacheMock.Object,
            NullLogger.Instance);
        using FhirPackageManager manager = new(
            _cacheMock.Object,
            _registryMock.Object,
            _versionResolverMock.Object,
            dependencyResolver,
            _indexerMock.Object,
            new FhirPackageManagerOptions(),
            NullLogger<FhirPackageManager>.Instance);

        PackageRecord result = await manager.InstallAsync(
            rootRecord.Reference,
            new MemoryStream([2]),
            new PackageSourceInstallOptions
            {
                IncludeDependencies = true,
            },
            TestContext.Current.CancellationToken);

        result.ShouldBe(rootRecord);
        PackageReference[] expectedReferences =
        [
            new PackageReference("shared.package", "3.1.1"),
            new PackageReference("shared.package", "6.1.0"),
            new PackageReference("child.v3", "1.0.0"),
            new PackageReference("child.v6", "1.0.0"),
        ];
        foreach (PackageReference reference in expectedReferences)
        {
            _cacheMock.Verify(cache => cache.InstallAsync(
                reference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task InstallAsync_ActiveClosure_DoesNotApplyVersionFixupTwice()
    {
        PackageRecord rootRecord = CreatePackageRecord(
            "root.package",
            "1.0.0",
            new Dictionary<string, string>
            {
                ["dependency.package"] = "1.0.0"
            });
        PackageReference dependencyReference =
            new("dependency.package", "1.0.1");
        PackageClosure closure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [dependencyReference.Name] =
                        dependencyReference,
                },
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase),
            InstallOrder = [dependencyReference],
            InstallOrderIsComplete = true,
        };
        ResolvedDirective dependencyResolved = new()
        {
            Reference = dependencyReference,
            TarballUri =
                new Uri("https://example.test/dependency.tgz"),
        };
        _cacheMock.Setup(cache => cache.InstallAsync(
                rootRecord.Reference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(rootRecord);
        _dependencyResolverMock.Setup(resolver => resolver.ResolveAsync(
                rootRecord.Manifest,
                It.IsAny<DependencyResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(closure);
        _cacheMock.Setup(cache => cache.IsInstalledAsync(
                dependencyReference,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _registryMock.Setup(registry => registry.ResolveAsync(
                It.Is<PackageDirective>(
                    directive =>
                        directive.PackageId == "dependency.package"
                        && directive.RequestedVersion == "1.0.1"),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(dependencyResolved);
        _registryMock.Setup(registry => registry.DownloadAsync(
                dependencyResolved,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1]),
                ContentType = "application/gzip",
                ContentLength = 1,
            });
        _cacheMock.Setup(cache => cache.InstallAsync(
                dependencyReference,
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                CreatePackageRecord(
                    "dependency.package",
                    "1.0.1"));
        FhirPackageManagerOptions managerOptions = new()
        {
            VersionFixups =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["dependency.package@1.0.0"] = "1.0.1",
                    ["dependency.package@1.0.1"] = "1.0.2",
                },
        };
        using FhirPackageManager manager =
            CreateManager(managerOptions);

        PackageRecord result = await manager.InstallAsync(
            rootRecord.Reference,
            new MemoryStream([1]),
            new PackageSourceInstallOptions
            {
                IncludeDependencies = true,
            },
            TestContext.Current.CancellationToken);

        result.ShouldBe(rootRecord);
        _registryMock.Verify(registry => registry.ResolveAsync(
            It.Is<PackageDirective>(
                directive =>
                    directive.PackageId == "dependency.package"
                    && directive.RequestedVersion == "1.0.2"),
            It.IsAny<VersionResolveOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RestoreAsync_RealResolver_IgnoresExistingLockAndInstallsAllExactVersions()
    {
        string projectPath = Path.Combine(
            Path.GetTempPath(),
            $"fhirpkg-restore-real-resolver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectPath);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(projectPath, "package.json"),
                """{"name":"root.package","version":"1.0.0","dependencies":{"low.parent":"1.0.0","high.parent":"1.0.0"}}""",
                TestContext.Current.CancellationToken);
            string lockPath = Path.Combine(
                projectPath,
                "fhirpkg.lock.json");
            byte[] sentinelBytes = [0xff, 0x00, 0xc3, 0x28];
            await File.WriteAllBytesAsync(
                lockPath,
                sentinelBytes,
                TestContext.Current.CancellationToken);
            Dictionary<string, PackageListing> listings = new(
                StringComparer.OrdinalIgnoreCase)
            {
                ["low.parent"] = CreateDependencyListing(
                    CreateDependencyVersion(
                        "low.parent",
                        "1.0.0",
                        CreateDependencies(
                            ("shared.package", "3.1.1")))),
                ["high.parent"] = CreateDependencyListing(
                    CreateDependencyVersion(
                        "high.parent",
                        "1.0.0",
                        CreateDependencies(
                            ("shared.package", "6.1.0")))),
                ["shared.package"] = CreateDependencyListing(
                    CreateDependencyVersion(
                        "shared.package",
                        "3.1.1",
                        CreateDependencies(
                            ("child.v3", "1.0.0"))),
                    CreateDependencyVersion(
                        "shared.package",
                        "6.1.0",
                        CreateDependencies(
                            ("child.v6", "1.0.0")))),
                ["child.v3"] = CreateDependencyListing(
                    CreateDependencyVersion(
                        "child.v3",
                        "1.0.0",
                        CreateDependencies())),
                ["child.v6"] = CreateDependencyListing(
                    CreateDependencyVersion(
                        "child.v6",
                        "1.0.0",
                        CreateDependencies())),
            };

            _versionResolverMock.Setup(resolver => resolver.ResolveVersionAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<VersionResolveOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((
                    string _,
                    string versionSpecifier,
                    VersionResolveOptions? _,
                    CancellationToken _) =>
                    FhirSemVer.TryParse(
                        versionSpecifier,
                        out FhirSemVer? version)
                        ? version
                        : null);
            _registryMock.Setup(registry => registry.GetPackageListingAsync(
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
            _registryMock.Setup(registry => registry.ResolveAsync(
                    It.IsAny<PackageDirective>(),
                    It.IsAny<VersionResolveOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((
                    PackageDirective directive,
                    VersionResolveOptions? _,
                    CancellationToken _) =>
                    new ResolvedDirective
                    {
                        Reference = new PackageReference(
                            directive.PackageId,
                            directive.RequestedVersion),
                        TarballUri = new Uri(
                            $"https://example.test/{Uri.EscapeDataString(directive.PackageId)}-{Uri.EscapeDataString(directive.RequestedVersion ?? string.Empty)}.tgz"),
                    });
            _registryMock.Setup(registry => registry.DownloadAsync(
                    It.IsAny<ResolvedDirective>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageDownloadResult
                {
                    Content = new MemoryStream([1]),
                    ContentType = "application/gzip",
                });
            _cacheMock.Setup(cache => cache.ReadManifestAsync(
                    It.IsAny<PackageReference>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((PackageManifest?)null);
            _cacheMock.Setup(cache => cache.IsInstalledAsync(
                    It.IsAny<PackageReference>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            _cacheMock.Setup(cache => cache.InstallAsync(
                    It.IsAny<PackageReference>(),
                    It.IsAny<Stream>(),
                    It.IsAny<InstallCacheOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((
                    PackageReference reference,
                    Stream _,
                    InstallCacheOptions? _,
                    CancellationToken _) =>
                    CreatePackageRecord(
                        reference.Name,
                        reference.Version!));

            DependencyResolver dependencyResolver = new(
                _registryMock.Object,
                _versionResolverMock.Object,
                _cacheMock.Object,
                NullLogger.Instance);
            using FhirPackageManager manager = new(
                _cacheMock.Object,
                _registryMock.Object,
                _versionResolverMock.Object,
                dependencyResolver,
                _indexerMock.Object,
                new FhirPackageManagerOptions(),
                NullLogger<FhirPackageManager>.Instance);

            await manager.RestoreAsync(
                projectPath,
                cancellationToken:
                    TestContext.Current.CancellationToken);

            PackageReference[] expectedReferences =
            [
                new PackageReference("shared.package", "3.1.1"),
                new PackageReference("shared.package", "6.1.0"),
                new PackageReference("child.v3", "1.0.0"),
                new PackageReference("child.v6", "1.0.0"),
            ];
            foreach (PackageReference reference in expectedReferences)
            {
                _cacheMock.Verify(cache => cache.InstallAsync(
                    reference,
                    It.IsAny<Stream>(),
                    It.IsAny<InstallCacheOptions?>(),
                    It.IsAny<CancellationToken>()), Times.Once);
            }

            byte[] persistedBytes = await File.ReadAllBytesAsync(
                lockPath,
                TestContext.Current.CancellationToken);
            persistedBytes.SequenceEqual(sentinelBytes).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(projectPath))
                Directory.Delete(projectPath, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_DoesNotCreateProjectLockArtifacts()
    {
        string projectPath = Path.Combine(
            Path.GetTempPath(),
            $"fhirpkg-restore-no-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectPath);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(projectPath, "package.json"),
                """{"name":"root.package","version":"1.0.0","dependencies":{}}""",
                TestContext.Current.CancellationToken);
            _dependencyResolverMock.Setup(resolver => resolver.ResolveAsync(
                    It.IsAny<PackageManifest>(),
                    It.IsAny<DependencyResolveOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageClosure
                {
                    Timestamp = DateTime.UtcNow,
                    Resolved =
                        new Dictionary<string, PackageReference>(
                            StringComparer.OrdinalIgnoreCase),
                    Missing =
                        new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase),
                    InstallOrderIsComplete = true,
                });
            using FhirPackageManager manager = CreateManager();

            await manager.RestoreAsync(
                projectPath,
                cancellationToken:
                    TestContext.Current.CancellationToken);

            File.Exists(
                    Path.Combine(
                        projectPath,
                        "fhirpkg.lock.json"))
                .ShouldBeFalse();
            File.Exists(
                    Path.Combine(
                        projectPath,
                        ".fhirpkg-restore.lock"))
                .ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(projectPath))
                Directory.Delete(projectPath, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_FailedClosureInstallThrows()
    {
        string projectPath = Path.Combine(
            Path.GetTempPath(),
            $"fhirpkg-restore-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectPath);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(projectPath, "package.json"),
                """{"name":"root.package","version":"1.0.0","dependencies":{"dependency.package":"1.0.0"}}""",
                TestContext.Current.CancellationToken);

            Dictionary<string, string> missing = [];
            PackageClosure closure = new PackageClosure
            {
                Timestamp = DateTime.UtcNow,
                Resolved = new Dictionary<string, PackageReference>
                {
                    ["dependency.package"] = new PackageReference(
                        "dependency.package",
                        "1.0.0")
                },
                Missing = missing
            };
            ResolvedDirective resolvedDirective = new ResolvedDirective
            {
                Reference = new PackageReference("dependency.package", "1.0.0"),
                TarballUri = new Uri("https://example.test/dependency.tgz")
            };
            _dependencyResolverMock.Setup(resolver => resolver.ResolveAsync(
                    It.IsAny<PackageManifest>(),
                    It.IsAny<DependencyResolveOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(closure);
            _cacheMock.Setup(cache => cache.IsInstalledAsync(
                    It.IsAny<PackageReference>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            _registryMock.Setup(registry => registry.ResolveAsync(
                    It.IsAny<PackageDirective>(),
                    It.IsAny<VersionResolveOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(resolvedDirective);
            _registryMock.Setup(registry => registry.DownloadAsync(
                    resolvedDirective,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageDownloadResult
                {
                    Content = new MemoryStream([1, 2, 3, 4]),
                    ContentType = "application/gzip",
                    ContentLength = 4
                });
            _cacheMock.Setup(cache => cache.InstallAsync(
                    It.IsAny<PackageReference>(),
                    It.IsAny<Stream>(),
                    It.Is<InstallCacheOptions?>(options =>
                        options != null
                        && options.Limits != null
                        && options.Limits.MaxCompressedBytes == 3),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new PackageInstallException(
                    PackageInstallErrorCode.CompressedSizeLimitExceeded,
                    PackageInstallStage.Acquisition,
                    "Compressed package exceeds the configured limit.",
                    resolvedDirective.Reference.FhirDirective));
            FhirPackageManagerOptions managerOptions = new FhirPackageManagerOptions
            {
                InstallLimits = new PackageInstallLimits
                {
                    MaxCompressedBytes = 10
                }
            };
            using FhirPackageManager manager = CreateManager(managerOptions);

            PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
                () => manager.RestoreAsync(
                    projectPath,
                    new RestoreOptions
                    {
                        InstallLimits = new PackageInstallLimits
                        {
                            MaxCompressedBytes = 3
                        }
                    },
                    TestContext.Current.CancellationToken));

            exception.ErrorCode.ShouldBe(
                PackageInstallErrorCode.CompressedSizeLimitExceeded);
            exception.Stage.ShouldBe(PackageInstallStage.Acquisition);
            _cacheMock.Verify(cache => cache.InstallAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<Stream>(),
                It.IsAny<InstallCacheOptions?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(projectPath))
                Directory.Delete(projectPath, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_OpaqueCiDependency_BootstrapsAndReresolves()
    {
        string projectPath = Path.Combine(
            Path.GetTempPath(),
            $"fhirpkg-restore-bootstrap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectPath);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(projectPath, "package.json"),
                """{"name":"root.package","version":"1.0.0","dependencies":{"ci.package":"current"}}""",
                TestContext.Current.CancellationToken);
            PackageReference ciAlias =
                new("ci.package", "current");
            DependencyResolutionFailure bootstrapFailure = new()
            {
                Code =
                    DependencyResolutionFailureCode.MetadataUnavailable,
                PackageId = "ci.package",
                VersionSpecifier = "current",
                Message =
                    "The CI package must be installed before its manifest can be resolved.",
            };
            PackageClosure bootstrapClosure = new()
            {
                Timestamp = DateTime.UtcNow,
                Resolved =
                    new Dictionary<string, PackageReference>(
                        StringComparer.OrdinalIgnoreCase),
                Missing =
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["ci.package"] =
                            bootstrapFailure.Message,
                    },
                Failures = [bootstrapFailure],
                BootstrapInstallOrder = [ciAlias],
                InstallOrderIsComplete = true,
            };
            PackageClosure completeClosure = new()
            {
                Timestamp = DateTime.UtcNow,
                Resolved =
                    new Dictionary<string, PackageReference>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["ci.package"] =
                            new PackageReference(
                                "ci.package",
                                "2.0.0"),
                    },
                Missing =
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase),
                InstallOrderIsComplete = true,
            };
            ResolvedDirective ciResolved = new()
            {
                Reference = ciAlias,
                TarballUri =
                    new Uri("https://example.test/ci.package.tgz"),
            };

            Queue<PackageClosure> closures =
                new([bootstrapClosure, completeClosure]);
            List<bool> preferCachedAliases = [];
            _dependencyResolverMock.Setup(
                    resolver => resolver.ResolveAsync(
                        It.IsAny<PackageManifest>(),
                        It.IsAny<DependencyResolveOptions?>(),
                        It.IsAny<CancellationToken>()))
                .Callback<
                    PackageManifest,
                    DependencyResolveOptions?,
                    CancellationToken>(
                    (_, options, _) =>
                        preferCachedAliases.Add(
                            options?.PreferCachedAliases
                            ?? false))
                .ReturnsAsync(() => closures.Dequeue());
            _cacheMock.Setup(cache => cache.IsInstalledAsync(
                    ciAlias,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            _registryMock.Setup(registry => registry.ResolveAsync(
                    It.Is<PackageDirective>(
                        directive =>
                            directive.PackageId == "ci.package"
                            && directive.RequestedVersion
                                == "current"),
                    It.IsAny<VersionResolveOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(ciResolved);
            _registryMock.Setup(registry => registry.DownloadAsync(
                    ciResolved,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PackageDownloadResult
                {
                    Content = new MemoryStream([1]),
                    ContentType = "application/gzip",
                });
            _cacheMock.Setup(cache => cache.InstallAsync(
                    ciAlias,
                    It.IsAny<Stream>(),
                    It.IsAny<InstallCacheOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateAliasPackageRecord(
                    "ci.package",
                    "current",
                    "2.0.0"));
            using FhirPackageManager manager = CreateManager();

            PackageClosure result = await manager.RestoreAsync(
                projectPath,
                cancellationToken:
                    TestContext.Current.CancellationToken);

            result.ShouldBe(completeClosure);
            preferCachedAliases.ShouldBe([false, true]);
            _registryMock.Verify(registry => registry.ResolveAsync(
                It.Is<PackageDirective>(
                    directive =>
                        directive.PackageId == "ci.package"
                        && directive.RequestedVersion == "2.0.0"),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (Directory.Exists(projectPath))
                Directory.Delete(projectPath, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_NotFoundClosureInstallThrows()
    {
        string projectPath = Path.Combine(
            Path.GetTempPath(),
            $"fhirpkg-restore-not-found-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectPath);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(projectPath, "package.json"),
                """{"name":"root.package","version":"1.0.0","dependencies":{"missing.package":"1.0.0"}}""",
                TestContext.Current.CancellationToken);
            Dictionary<string, string> missing = [];
            PackageClosure closure = new PackageClosure
            {
                Timestamp = DateTime.UtcNow,
                Resolved = new Dictionary<string, PackageReference>
                {
                    ["missing.package"] = new PackageReference(
                        "missing.package",
                        "1.0.0")
                },
                Missing = missing
            };
            _dependencyResolverMock.Setup(resolver => resolver.ResolveAsync(
                    It.IsAny<PackageManifest>(),
                    It.IsAny<DependencyResolveOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(closure);
            _cacheMock.Setup(cache => cache.IsInstalledAsync(
                    It.IsAny<PackageReference>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            _registryMock.Setup(registry => registry.ResolveAsync(
                    It.IsAny<PackageDirective>(),
                    It.IsAny<VersionResolveOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((ResolvedDirective?)null);
            using FhirPackageManager manager = CreateManager();

            PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
                () => manager.RestoreAsync(
                    projectPath,
                    cancellationToken:
                        TestContext.Current.CancellationToken));

            exception.ErrorCode.ShouldBe(PackageInstallErrorCode.ResolutionFailed);
            exception.Stage.ShouldBe(PackageInstallStage.Resolution);
        }
        finally
        {
            if (Directory.Exists(projectPath))
                Directory.Delete(projectPath, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_MissingManifest_ThrowsFileNotFound()
    {
        using FhirPackageManager manager = CreateManager();
        string nonexistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}");

        Func<Task<PackageClosure>> act = () => manager.RestoreAsync(nonexistentPath);

        await Should.ThrowAsync<FileNotFoundException>(act);
        Directory.Exists(nonexistentPath).ShouldBeFalse();
    }

    private void SetupMutableCiScenario(
        string alias,
        PackageRecord? cachedRecord,
        ResolvedDirective resolvedDirective,
        PackageRecord? installedRecord,
        PackageCacheInstallOutcome? installOutcome,
        CacheMetadata? metadata = null)
    {
        PackageReference aliasReference =
            new PackageReference("example.package", alias);
        _cacheMock.Setup(
                cache => cache.IsInstalledAsync(
                    aliasReference,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedRecord is not null);
        if (cachedRecord is not null)
        {
            _cacheMock.Setup(
                    cache => cache.GetPackageAsync(
                        aliasReference,
                        It.IsAny<CancellationToken>()))
                .ReturnsAsync(cachedRecord);
            _cacheMock.Setup(
                    cache => cache.GetMetadataAsync(
                        It.IsAny<CancellationToken>()))
                .ReturnsAsync(metadata ?? new CacheMetadata());
        }

        _registryMock.Setup(
                registry => registry.ResolveAsync(
                    It.Is<PackageDirective>(
                        directive =>
                            directive.PackageId == "example.package"
                            && directive.RequestedVersion == alias),
                    It.IsAny<VersionResolveOptions?>(),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedDirective);
        if (installedRecord is null)
            return;

        _registryMock.Setup(
                registry => registry.DownloadAsync(
                    resolvedDirective,
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1, 2, 3]),
                ContentType = "application/gzip"
            });
        _cacheMock.Setup(
                cache => cache.InstallAsync(
                    aliasReference,
                    It.IsAny<Stream>(),
                    It.IsAny<InstallCacheOptions?>(),
                    It.IsAny<CancellationToken>()))
            .Callback<
                PackageReference,
                Stream,
                InstallCacheOptions?,
                CancellationToken>(
                (_, _, options, _) =>
                {
                    if (installOutcome is not null)
                        options!.InstallOutcome = installOutcome;
                })
            .ReturnsAsync(installedRecord);
    }

    private static void AssertMutableCiResult(
        PackageInstallResult result,
        PackageInstallDisposition disposition,
        string? previousManifestDate,
        string? manifestDate)
    {
        result.Status.ShouldBe(PackageInstallStatus.Installed);
        result.Disposition.ShouldBe(disposition);
        result.PreviousManifestDate.ShouldBe(previousManifestDate);
        result.ManifestDate.ShouldBe(manifestDate);
    }

    private static async Task<PackageInstallResult> InvokeInstallResultAsync(
        FhirPackageManager manager,
        string directive,
        PackageReference expectedResolvedReference)
    {
        FhirPackageManagerOptions managerOptions = new();
        ResolvedPackageInstallPolicy policy =
            ResolvedPackageInstallPolicy.Resolve(
                managerOptions,
                PackageInstallLimits.ResolveManager(
                    managerOptions.InstallLimits),
                installOptions: null);
        MethodInfo method = typeof(FhirPackageManager).GetMethod(
                "InstallResultAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "InstallResultAsync was not found.");
        object? invocation = method.Invoke(
            manager,
            [
                directive,
                policy,
                TestContext.Current.CancellationToken,
                true,
                expectedResolvedReference
            ]);
        Task<PackageInstallResult> task =
            invocation as Task<PackageInstallResult>
            ?? throw new InvalidOperationException(
                "InstallResultAsync returned an unexpected value.");
        return await task;
    }

    private static PackageRecord CreatePackageRecord(
        string name,
        string version,
        Dictionary<string, string>? dependencies = null,
        string? manifestDate = null) =>
        new PackageRecord
        {
            Reference = new PackageReference(name, version),
            DirectoryPath = $"/cache/{name}#{version}",
            ContentPath = $"/cache/{name}#{version}/package",
            Manifest = new PackageManifest
            {
                Name = name,
                Version = version,
                Dependencies = dependencies,
                Date = manifestDate
            }
        };

    private static Dictionary<string, string> CreateDependencies(
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

    private static PackageListing CreateDependencyListing(
        params PackageVersionInfo[] versions) =>
        new()
        {
            PackageId = versions[0].Name,
            Versions = versions.ToDictionary(
                version => version.Version,
                StringComparer.Ordinal),
            VersionCandidates = versions,
        };

    private static PackageVersionInfo CreateDependencyVersion(
        string packageId,
        string version,
        IReadOnlyDictionary<string, string> dependencies) =>
        new()
        {
            Name = packageId,
            Version = version,
            Dependencies = dependencies,
        };

    private static PackageListing CreateWildcardGrammarListing()
    {
        string[] versions =
        [
            "2.0",
            "2.0.0",
            "4.0.0",
            "4.1.0",
            "6.0.0-ballot",
            "6.0.0",
            "6.1.0",
        ];
        PackageVersionInfo[] candidates = versions
            .Select(version => new PackageVersionInfo
            {
                Name = "example.package",
                Version = version,
            })
            .ToArray();
        return new PackageListing
        {
            PackageId = "example.package",
            Versions = candidates.ToDictionary(
                candidate => candidate.Version,
                StringComparer.Ordinal),
            VersionCandidates = candidates,
        };
    }

    private static ResolvedDirective? CreateResolvedDirective(
        PackageVersionSelection? selection) =>
        selection is null
            ? null
            : new ResolvedDirective
            {
                Reference = new PackageReference(
                    "example.package",
                    selection.Key),
                TarballUri = new Uri(
                    $"https://registry.example/{Uri.EscapeDataString(selection.Key)}.tgz"),
            };

    private static PackageRecord CreateAliasPackageRecord(
        string name,
        string alias,
        string manifestVersion,
        string? manifestDate = null) =>
        new()
        {
            Reference = new PackageReference(name, alias),
            DirectoryPath = $"/cache/{name}#{alias}",
            ContentPath = $"/cache/{name}#{alias}/package",
            Manifest = new PackageManifest
            {
                Name = name,
                Version = manifestVersion,
                Date = manifestDate
            },
        };

    private sealed class TimeoutReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new RegistryResponseTimeoutException(
                "Simulated registry body timeout.");

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(
                new RegistryResponseTimeoutException(
                    "Simulated registry body timeout."));

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
