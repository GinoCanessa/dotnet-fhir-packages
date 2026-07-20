// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Registry;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public class FhirPackageManagerOptionsTests
{
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
}
