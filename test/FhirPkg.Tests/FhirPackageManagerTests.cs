// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
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
        PackageRecord cachedRecord = CreatePackageRecord("example.package", "current");
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
    public async Task InstallAsync_CurrentWithSameArchiveHash_DoesNotReplace()
    {
        byte[] content = [1, 2, 3];
        string archiveSha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        PackageReference aliasReference = new PackageReference("example.package", "current");
        PackageRecord cachedRecord = CreatePackageRecord("example.package", "current");
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
        PackageRecord cachedRecord = CreatePackageRecord("example.package", "current");
        PackageRecord replacementRecord = CreatePackageRecord("example.package", "current");
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

        PackageRecord? result = await manager.InstallAsync(
            "root.package#1.0.0",
            new InstallOptions
            {
                IncludeDependencies = true,
                InstallLimits = new PackageInstallLimits
                {
                    MaxCompressedBytes = 3
                }
            },
            TestContext.Current.CancellationToken);

        result.ShouldBe(rootRecord);
        _cacheMock.Verify(cache => cache.InstallAsync(
            It.IsAny<PackageReference>(),
            It.IsAny<Stream>(),
            It.IsAny<InstallCacheOptions?>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task RestoreAsync_FailedClosureInstallThrowsAndDoesNotWriteLock()
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
                        WriteLockFile = true,
                        InstallLimits = new PackageInstallLimits
                        {
                            MaxCompressedBytes = 3
                        }
                    },
                    TestContext.Current.CancellationToken));

            exception.ErrorCode.ShouldBe(
                PackageInstallErrorCode.CompressedSizeLimitExceeded);
            exception.Stage.ShouldBe(PackageInstallStage.Acquisition);
            File.Exists(Path.Combine(projectPath, "fhirpkg.lock.json")).ShouldBeFalse();
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
    public async Task RestoreAsync_NotFoundClosureInstallThrowsAndDoesNotWriteLock()
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
                    new RestoreOptions { WriteLockFile = true },
                    TestContext.Current.CancellationToken));

            exception.ErrorCode.ShouldBe(PackageInstallErrorCode.ResolutionFailed);
            exception.Stage.ShouldBe(PackageInstallStage.Resolution);
            File.Exists(Path.Combine(projectPath, "fhirpkg.lock.json")).ShouldBeFalse();
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
    }

    private static PackageRecord CreatePackageRecord(
        string name,
        string version,
        Dictionary<string, string>? dependencies = null) =>
        new PackageRecord
        {
            Reference = new PackageReference(name, version),
            DirectoryPath = $"/cache/{name}#{version}",
            ContentPath = $"/cache/{name}#{version}/package",
            Manifest = new PackageManifest
            {
                Name = name,
                Version = version,
                Dependencies = dependencies
            }
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
