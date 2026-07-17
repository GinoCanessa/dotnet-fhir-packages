// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Indexing;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Resolution;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

[Collection("EnvironmentVariable")]
public class PackageInstallLimitsTests : IDisposable
{
    private static readonly string[] s_environmentVariables =
    [
        PackageInstallLimits.MaxCompressedBytesEnvironmentVariable,
        PackageInstallLimits.MaxExpandedBytesEnvironmentVariable,
        PackageInstallLimits.MaxEntryBytesEnvironmentVariable,
        PackageInstallLimits.MaxArchiveEntriesEnvironmentVariable,
        PackageInstallLimits.MaxArchivePathLengthEnvironmentVariable,
        PackageInstallLimits.MaxArchiveDepthEnvironmentVariable
    ];

    private readonly Dictionary<string, string?> _originalEnvironment = [];

    public PackageInstallLimitsTests()
    {
        foreach (string variableName in s_environmentVariables)
        {
            _originalEnvironment[variableName] =
                Environment.GetEnvironmentVariable(variableName);
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    public void Dispose()
    {
        foreach ((string variableName, string? value) in _originalEnvironment)
            Environment.SetEnvironmentVariable(variableName, value);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Defaults_AreDocumentedFiniteValues()
    {
        PackageInstallLimits limits = new PackageInstallLimits();

        PackageInstallLimits.DefaultMaxCompressedBytes.ShouldBe(100L * 1024 * 1024);
        PackageInstallLimits.DefaultMaxExpandedBytes.ShouldBe(1L * 1024 * 1024 * 1024);
        PackageInstallLimits.DefaultMaxEntryBytes.ShouldBe(128L * 1024 * 1024);
        PackageInstallLimits.DefaultMaxArchiveEntries.ShouldBe(50_000);
        PackageInstallLimits.DefaultMaxArchivePathLength.ShouldBe(1_024);
        PackageInstallLimits.DefaultMaxArchiveDepth.ShouldBe(32);
        limits.MaxCompressedBytes.ShouldBe(PackageInstallLimits.DefaultMaxCompressedBytes);
        limits.MaxExpandedBytes.ShouldBe(PackageInstallLimits.DefaultMaxExpandedBytes);
        limits.MaxEntryBytes.ShouldBe(PackageInstallLimits.DefaultMaxEntryBytes);
        limits.MaxArchiveEntries.ShouldBe(PackageInstallLimits.DefaultMaxArchiveEntries);
        limits.MaxArchivePathLength.ShouldBe(PackageInstallLimits.DefaultMaxArchivePathLength);
        limits.MaxArchiveDepth.ShouldBe(PackageInstallLimits.DefaultMaxArchiveDepth);
    }

    [Fact]
    public void FromEnvironment_ParsesEveryLimit()
    {
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxCompressedBytesEnvironmentVariable,
            "101");
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxExpandedBytesEnvironmentVariable,
            "1001");
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxEntryBytesEnvironmentVariable,
            "501");
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxArchiveEntriesEnvironmentVariable,
            "41");
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxArchivePathLengthEnvironmentVariable,
            "301");
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxArchiveDepthEnvironmentVariable,
            "21");

        PackageInstallLimits limits = PackageInstallLimits.FromEnvironment();

        limits.MaxCompressedBytes.ShouldBe(101);
        limits.MaxExpandedBytes.ShouldBe(1001);
        limits.MaxEntryBytes.ShouldBe(501);
        limits.MaxArchiveEntries.ShouldBe(41);
        limits.MaxArchivePathLength.ShouldBe(301);
        limits.MaxArchiveDepth.ShouldBe(21);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("9223372036854775808")]
    [InlineData("not-a-number")]
    public void FromEnvironment_InvalidLong_ThrowsTypedPolicyError(string value)
    {
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxCompressedBytesEnvironmentVariable,
            value);

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            PackageInstallLimits.FromEnvironment);

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
        exception.Stage.ShouldBe(PackageInstallStage.PolicyValidation);
    }

    [Fact]
    public void Validate_RejectsContradictoryAndOverflowProneValues()
    {
        PackageInstallLimits contradictory = new PackageInstallLimits
        {
            MaxExpandedBytes = 10,
            MaxEntryBytes = 11
        };
        PackageInstallLimits overflowProne = new PackageInstallLimits
        {
            MaxCompressedBytes = long.MaxValue,
            MaxExpandedBytes = long.MaxValue,
            MaxEntryBytes = 1
        };

        Should.Throw<PackageInstallException>(contradictory.Validate)
            .ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
        Should.Throw<PackageInstallException>(overflowProne.Validate)
            .ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
    }

    [Fact]
    public void ManagerConstruction_ExplicitValueOverridesMalformedEnvironment()
    {
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxCompressedBytesEnvironmentVariable,
            "malformed");
        FhirPackageManagerOptions options = new FhirPackageManagerOptions
        {
            InstallLimits = new PackageInstallLimits
            {
                MaxCompressedBytes = 64
            }
        };

        using FhirPackageManager manager = CreateManager(options);
    }

    [Fact]
    public void ManagerConstruction_InvalidEnvironmentFailsBeforeSourceAccess()
    {
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxArchiveEntriesEnvironmentVariable,
            "0");

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => CreateManager(new FhirPackageManagerOptions()));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
    }

    [Fact]
    public void CacheConstruction_InvalidEnvironmentFailsBeforeCreatingCacheRoot()
    {
        Environment.SetEnvironmentVariable(
            PackageInstallLimits.MaxArchiveDepthEnvironmentVariable,
            "invalid");
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"fhirpkg-policy-{Guid.NewGuid():N}");

        try
        {
            Should.Throw<PackageInstallException>(() => new DiskPackageCache(cacheRoot));
            Directory.Exists(cacheRoot).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PerCallLimits_CannotLoosenManagerPolicyBeforeResolution()
    {
        Mock<IRegistryClient> registry = new Mock<IRegistryClient>();
        FhirPackageManagerOptions managerOptions = new FhirPackageManagerOptions
        {
            InstallLimits = new PackageInstallLimits
            {
                MaxCompressedBytes = 10
            }
        };
        using FhirPackageManager manager = CreateManager(managerOptions, registry);

        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
            () => manager.InstallAsync(
                "example.package#1.0.0",
                new InstallOptions
                {
                    InstallLimits = new PackageInstallLimits
                    {
                        MaxCompressedBytes = 11
                    }
                },
                TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
        registry.Verify(
            client => client.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PerCallLimits_TightenCompressedAcquisition()
    {
        Mock<IPackageCache> cache = new Mock<IPackageCache>();
        Mock<IRegistryClient> registry = new Mock<IRegistryClient>();
        ResolvedDirective resolved = new ResolvedDirective
        {
            Reference = new PackageReference("example.package", "1.0.0"),
            TarballUri = new Uri("https://example.test/example.package.tgz")
        };
        registry.Setup(client => client.ResolveAsync(
                It.IsAny<PackageDirective>(),
                It.IsAny<VersionResolveOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);
        registry.Setup(client => client.DownloadAsync(
                resolved,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PackageDownloadResult
            {
                Content = new MemoryStream([1, 2, 3, 4]),
                ContentType = "application/gzip",
                ContentLength = 4
            });
        cache.Setup(instance => instance.IsInstalledAsync(
                It.IsAny<PackageReference>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        FhirPackageManagerOptions managerOptions = new FhirPackageManagerOptions
        {
            InstallLimits = new PackageInstallLimits
            {
                MaxCompressedBytes = 10
            }
        };
        using FhirPackageManager manager = CreateManager(managerOptions, registry, cache);

        PackageInstallException exception = await Should.ThrowAsync<PackageInstallException>(
            () => manager.InstallAsync(
                "example.package#1.0.0",
                new InstallOptions
                {
                    InstallLimits = new PackageInstallLimits
                    {
                        MaxCompressedBytes = 3
                    }
                },
                TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.CompressedSizeLimitExceeded);
        cache.Verify(instance => instance.InstallAsync(
            It.IsAny<PackageReference>(),
            It.IsAny<Stream>(),
            It.IsAny<InstallCacheOptions?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static FhirPackageManager CreateManager(
        FhirPackageManagerOptions options,
        Mock<IRegistryClient>? registry = null,
        Mock<IPackageCache>? cache = null)
    {
        Mock<IPackageCache> effectiveCache = cache ?? new Mock<IPackageCache>();
        Mock<IRegistryClient> effectiveRegistry = registry ?? new Mock<IRegistryClient>();

        return new FhirPackageManager(
            effectiveCache.Object,
            effectiveRegistry.Object,
            new Mock<IVersionResolver>().Object,
            new Mock<IDependencyResolver>().Object,
            new Mock<IPackageIndexer>().Object,
            options,
            NullLogger<FhirPackageManager>.Instance);
    }
}
