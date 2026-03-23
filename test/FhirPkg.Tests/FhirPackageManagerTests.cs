// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
using Shouldly;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using System.Collections.ObjectModel;

namespace FhirPkg.Tests;

public class FhirPackageManagerTests
{
    private readonly Mock<IPackageCache> _cacheMock = new();
    private readonly Mock<IRegistryClient> _registryMock = new();
    private readonly Mock<IVersionResolver> _versionResolverMock = new();
    private readonly Mock<IDependencyResolver> _dependencyResolverMock = new();
    private readonly Mock<IPackageIndexer> _indexerMock = new();

    private FhirPackageManager CreateManager()
    {
        return new FhirPackageManager(
            _cacheMock.Object,
            _registryMock.Object,
            _versionResolverMock.Object,
            _dependencyResolverMock.Object,
            _indexerMock.Object,
            new FhirPackageManagerOptions(),
            NullLogger<FhirPackageManager>.Instance);
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

        PackageRecord? result = await manager.InstallAsync("hl7.fhir.r4.core#4.0.1");

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

        IReadOnlyList<PackageRecord> result = await manager.ListCachedAsync("hl7");

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

        PackageRecord? result = await manager.InstallAsync("hl7.fhir.r4.core#4.0.1");

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

        PackageRecord? result = await manager.InstallAsync("hl7.fhir.r4.core#4.0.1");

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

        using FhirPackageManager manager = CreateManager();

        Func<Task<PackageRecord?>> act = () => manager.InstallAsync("hl7.fhir.r4.core#4.0.1");

        await Should.ThrowAsync<InvalidOperationException>(act);
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
            new InstallOptions { OverwriteExisting = true });

        result.ShouldNotBeNull();
        result!.Reference.Version.ShouldBe("4.0.1");

        // Verify download was attempted despite being already installed
        _registryMock.Verify(r => r.DownloadAsync(
            It.IsAny<ResolvedDirective>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RestoreAsync_MissingManifest_ThrowsFileNotFound()
    {
        using FhirPackageManager manager = CreateManager();
        string nonexistentPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}");

        Func<Task<PackageClosure>> act = () => manager.RestoreAsync(nonexistentPath);

        await Should.ThrowAsync<FileNotFoundException>(act);
    }
}
