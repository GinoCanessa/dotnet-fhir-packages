// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Registry;
using FhirPkg.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public class FhirPackageManagerOptionsTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(
        AppContext.BaseDirectory,
        $"fhir-manager-options-{Guid.NewGuid():N}");

    [Fact]
    public void Unmodified_DefaultsFalse()
    {
        FhirPackageManagerOptions options = new();

        options.Unmodified.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateConfiguration_SnapshotsUnmodified(bool unmodified)
    {
        FhirPackageManagerOptions options = new()
        {
            Unmodified = unmodified,
        };
        FhirPackageManagerConfiguration configuration =
            FhirPackageManagerConfiguration.Create(options);

        options.Unmodified = !unmodified;
        ResolvedPackageInstallPolicy policy =
            ResolvedPackageInstallPolicy.Resolve(
                configuration.Options,
                configuration.InstallLimits,
                new InstallOptions { IncludeDependencies = true });

        configuration.Options.Unmodified.ShouldBe(unmodified);
        policy.Unmodified.ShouldBe(unmodified);
        policy.WithoutDependencies().Unmodified.ShouldBe(unmodified);
        policy.WithoutDependencies().IncludeDependencies.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectConstruction_PreservesUnmodifiedSnapshot(bool unmodified)
    {
        FhirPackageManagerOptions options = new()
        {
            CachePath = _cacheRoot,
            Unmodified = unmodified,
            IncludeCiBuilds = false,
            IncludeHl7WebsiteFallback = false,
        };
        using FhirPackageManager manager = new(options);

        options.Unmodified = !unmodified;

        await AssertRootInstallationAsync(manager, unmodified);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DependencyInjection_PreservesUnmodifiedSnapshot(
        bool unmodified,
        bool preRegistered)
    {
        FhirPackageManagerOptions? original = null;
        ServiceCollection services = new();
        services.AddLogging();
        if (preRegistered)
        {
            original = new FhirPackageManagerOptions
            {
                CachePath = _cacheRoot,
                Unmodified = unmodified,
                IncludeCiBuilds = false,
                IncludeHl7WebsiteFallback = false,
            };
            services.AddSingleton(original);
            services.AddFhirPackageManagement();
        }
        else
        {
            services.AddFhirPackageManagement(options =>
            {
                options.CachePath = _cacheRoot;
                options.Unmodified = unmodified;
                options.IncludeCiBuilds = false;
                options.IncludeHl7WebsiteFallback = false;
                original = options;
            });
        }

        using ServiceProvider provider = services.BuildServiceProvider();
        IFhirPackageManager manager =
            provider.GetRequiredService<IFhirPackageManager>();
        original.ShouldNotBeNull();
        original.Unmodified = !unmodified;
        provider.GetRequiredService<FhirPackageManagerOptions>()
            .Unmodified = !unmodified;

        await AssertRootInstallationAsync(manager, unmodified);
    }

    [Fact]
    public void Defaults_IncludeR4AndR4bVersionFixups()
    {
        FhirPackageManagerOptions options = new();

        options.VersionFixups["hl7.fhir.r4.core@4.0.0"].ShouldBe("4.0.1");
        options.VersionFixups["hl7.fhir.r4b.core@4.3.0-snapshot1"].ShouldBe("4.3.0");
    }

    [Fact]
    public void CreateConfiguration_SnapshotsMutableOptionsAndCollections()
    {
        RegistryEndpoint endpoint = new()
        {
            Url = "https://registry.example/",
            Type = RegistryType.FhirNpm,
            TrustedHeaderOrigins = ["https://downloads.example/"],
        };
        FhirPackageManagerOptions options = new()
        {
            MaxParallelRegistryQueries = 2,
            Registries = [endpoint],
            VersionFixups = new Dictionary<string, string>
            {
                ["example.package@1.0.0"] = "1.0.1",
            },
        };

        FhirPackageManagerConfiguration configuration =
            FhirPackageManagerConfiguration.Create(options);

        options.MaxParallelRegistryQueries = 9;
        options.Registries.Clear();
        options.VersionFixups["example.package@1.0.0"] = "2.0.0";

        configuration.Options.MaxParallelRegistryQueries.ShouldBe(2);
        configuration.Options.Registries.Count.ShouldBe(1);
        configuration.Options.VersionFixups["example.package@1.0.0"].ShouldBe("1.0.1");
        configuration.FixupPolicy
            .ApplyVersion("example.package", "1.0.0")
            .ShouldBe("1.0.1");
    }

    [Fact]
    public void CreateConfiguration_AcceptsTwoPartExactVersionFixups()
    {
        FhirPackageManagerOptions options = new()
        {
            VersionFixups = new Dictionary<string, string>
            {
                ["example.package@2.0"] = "2.1",
            },
        };

        FhirPackageManagerConfiguration configuration =
            FhirPackageManagerConfiguration.Create(options);

        configuration.FixupPolicy
            .ApplyVersion("example.package", "2.0")
            .ShouldBe("2.1");
    }

    [Theory]
    [InlineData("2.*", "2.0")]
    [InlineData("2.0", "2.*")]
    public void CreateConfiguration_RejectsWildcardVersionFixups(
        string sourceVersion,
        string targetVersion)
    {
        FhirPackageManagerOptions options = new()
        {
            VersionFixups = new Dictionary<string, string>
            {
                [$"example.package@{sourceVersion}"] = targetVersion,
            },
        };

        AssertInvalid(options);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateConfiguration_InvalidParallelism_Throws(int value)
    {
        FhirPackageManagerOptions options = new()
        {
            MaxParallelRegistryQueries = value,
        };

        AssertInvalid(options);
    }

    [Fact]
    public void CreateConfiguration_NegativeResourceCacheSize_Throws()
    {
        FhirPackageManagerOptions options = new()
        {
            ResourceCacheSize = -1,
        };

        AssertInvalid(options);
    }

    [Fact]
    public void CreateConfiguration_InvalidSafeMode_Throws()
    {
        FhirPackageManagerOptions options = new()
        {
            ResourceCacheSafeMode = (SafeMode)int.MaxValue,
        };

        AssertInvalid(options);
    }

    [Fact]
    public void CreateConfiguration_InvalidRegistryType_Throws()
    {
        FhirPackageManagerOptions options = new()
        {
            Registries =
            [
                new RegistryEndpoint
                {
                    Url = "https://registry.example/",
                    Type = (RegistryType)int.MaxValue,
                },
            ],
        };

        AssertInvalid(options);
    }

    [Fact]
    public void DependencyInjection_ExposesDefensiveOptionsCopy()
    {
        ServiceCollection services = new();
        services.AddFhirPackageManagement();
        using ServiceProvider provider = services.BuildServiceProvider();

        FhirPackageManagerOptions exposed =
            provider.GetRequiredService<FhirPackageManagerOptions>();
        exposed.MaxParallelRegistryQueries = 0;
        exposed.VersionFixups.Clear();

        FhirPackageManagerConfiguration configuration =
            provider.GetRequiredService<FhirPackageManagerConfiguration>();
        configuration.Options.MaxParallelRegistryQueries.ShouldBe(3);
        configuration.Options.VersionFixups.ShouldNotBeEmpty();
    }

    [Fact]
    public void DependencyInjection_SnapshotsPreRegisteredOptions()
    {
        FhirPackageManagerOptions registered = new()
        {
            MaxParallelRegistryQueries = 7,
            VersionFixups = new Dictionary<string, string>
            {
                ["example.package@1.0.0"] = "1.0.1",
            },
        };
        ServiceCollection services = new();
        services.AddSingleton(registered);
        services.AddFhirPackageManagement();
        using ServiceProvider provider = services.BuildServiceProvider();

        FhirPackageManagerConfiguration configuration =
            provider.GetRequiredService<FhirPackageManagerConfiguration>();
        registered.MaxParallelRegistryQueries = 0;
        registered.VersionFixups.Clear();

        configuration.Options.MaxParallelRegistryQueries.ShouldBe(7);
        configuration.FixupPolicy
            .ApplyVersion("example.package", "1.0.0")
            .ShouldBe("1.0.1");
    }

    private static void AssertInvalid(FhirPackageManagerOptions options)
    {
        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => FhirPackageManagerConfiguration.Create(options));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
        exception.Stage.ShouldBe(PackageInstallStage.PolicyValidation);
    }

    private static async Task AssertRootInstallationAsync(
        IFhirPackageManager manager,
        bool unmodified)
    {
        PackageReference reference = new("snapshot.package", "1.0.0");
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File(
                "package.json",
                """{"name":"snapshot.package","version":"1.0.0"}"""),
            ArbitraryTarBuilder.File(
                "patient.json",
                """{"resourceType":"Patient","id":"snapshot"}"""));

        PackageRecord record = await manager.InstallAsync(
            reference,
            archive,
            options: null,
            TestContext.Current.CancellationToken);

        record.ContentPath.ShouldBe(
            unmodified
                ? record.DirectoryPath
                : Path.Combine(record.DirectoryPath, "package"));
        record.Index.ShouldNotBeNull();
        record.Index.Files.Single().Id.ShouldBe("snapshot");
        File.Exists(Path.Combine(record.ContentPath, ".index.json"))
            .ShouldBe(!unmodified);
        archive.CanRead.ShouldBeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot))
            Directory.Delete(_cacheRoot, recursive: true);
    }
}
