// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using FhirPkg.Models;
using FhirPkg.Resolution;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Resolution;

public class PackageVersionSelectorTests
{
    [Fact]
    public void Select_ExplicitPrereleaseRejectedWhenDisabled()
    {
        PackageListing listing = CreateListing(
            "example.package",
            ("1.0.0-beta", CreateInfo("example.package", "1.0.0-beta", "4.0.1")));

        PackageVersionSelection? result = PackageVersionSelector.Select(
            PackageDirective.Parse("example.package#1.0.0-beta"),
            listing,
            new VersionResolveOptions { AllowPreRelease = false });

        result.ShouldBeNull();
    }

    [Fact]
    public void Select_LatestPrereleaseTagFallsBackToHighestStable()
    {
        PackageListing listing = CreateListing(
            "example.package",
            ("1.0.0", CreateInfo("example.package", "1.0.0", "4.0.1")),
            ("2.0.0-beta", CreateInfo("example.package", "2.0.0-beta", "4.0.1")));
        listing = listing with
        {
            DistTags = new Dictionary<string, string>
            {
                ["latest"] = "2.0.0-beta",
            },
        };

        PackageVersionSelection? result = PackageVersionSelector.Select(
            PackageDirective.Parse("example.package#latest"),
            listing,
            new VersionResolveOptions { AllowPreRelease = false });

        result.ShouldNotBeNull();
        result.Key.ShouldBe("1.0.0");
    }

    [Fact]
    public void Select_PreferredReleaseMatchesAnyArrayEntry()
    {
        PackageVersionInfo info = CreateInfo("example.package", "1.0.0", "4.0.1") with
        {
            FhirVersions = ["4.0.1", "5.0.0"],
        };
        PackageListing listing = CreateListing(
            "example.package",
            ("1.0.0", info));

        PackageVersionSelection? result = PackageVersionSelector.Select(
            PackageDirective.Parse("example.package#latest"),
            listing,
            new VersionResolveOptions { FhirRelease = FhirRelease.R5 });

        result.ShouldNotBeNull();
    }

    [Fact]
    public void Select_ExplicitIncompatibleMetadataOverridesPackageNameInference()
    {
        PackageListing listing = CreateListing(
            "hl7.fhir.r4.core",
            ("4.0.1", CreateInfo("hl7.fhir.r4.core", "4.0.1", "5.0.0")));

        PackageVersionSelection? result = PackageVersionSelector.Select(
            PackageDirective.Parse("hl7.fhir.r4.core#latest"),
            listing,
            new VersionResolveOptions { FhirRelease = FhirRelease.R4 });

        result.ShouldBeNull();
    }

    [Fact]
    public void Select_ExplicitEmptyMetadataDoesNotFallBackToPackageName()
    {
        const string json = """
            {
              "name": "hl7.fhir.r4.core",
              "versions": {
                "4.0.1": {
                  "name": "hl7.fhir.r4.core",
                  "version": "4.0.1",
                  "fhirVersions": []
                }
              }
            }
            """;
        PackageListing listing =
            JsonSerializer.Deserialize<PackageListing>(json)!;

        PackageVersionSelection? result = PackageVersionSelector.Select(
            PackageDirective.Parse("hl7.fhir.r4.core#latest"),
            listing,
            new VersionResolveOptions { FhirRelease = FhirRelease.R4 });

        result.ShouldBeNull();
    }

    [Fact]
    public void Select_MissingMetadataRejectedWhenPreferredReleaseConfigured()
    {
        PackageListing listing = CreateListing(
            "example.package",
            ("1.0.0", CreateInfo("example.package", "1.0.0", null)));

        PackageVersionSelection? result = PackageVersionSelector.Select(
            PackageDirective.Parse("example.package#latest"),
            listing,
            new VersionResolveOptions { FhirRelease = FhirRelease.R4 });

        result.ShouldBeNull();
    }

    [Fact]
    public void Select_NumericMetadataIsNotTreatedAsEnumOrdinal()
    {
        PackageListing listing = CreateListing(
            "example.package",
            ("1.0.0", CreateInfo("example.package", "1.0.0", "4")));

        PackageVersionSelection? result = PackageVersionSelector.Select(
            PackageDirective.Parse("example.package#latest"),
            listing,
            new VersionResolveOptions { FhirRelease = FhirRelease.R5 });

        result.ShouldBeNull();
    }

    [Fact]
    public void Select_PackageNameInferenceUsedOnlyWhenMetadataMissing()
    {
        PackageListing listing = CreateListing(
            "hl7.fhir.r4.core",
            ("4.0.1", CreateInfo("hl7.fhir.r4.core", "4.0.1", null)));

        PackageVersionSelection? result = PackageVersionSelector.Select(
            PackageDirective.Parse("hl7.fhir.r4.core#latest"),
            listing,
            new VersionResolveOptions { FhirRelease = FhirRelease.R4 });

        result.ShouldNotBeNull();
    }

    [Fact]
    public void Select_PreservesOriginalListingKey()
    {
        const string originalKey = "1.0.0+Build.7";
        PackageListing listing = CreateListing(
            "example.package",
            (originalKey, CreateInfo("example.package", originalKey, "4.0.1")));
        listing = listing with
        {
            DistTags = new Dictionary<string, string>
            {
                ["latest"] = originalKey,
            },
        };

        PackageVersionSelection? result = PackageVersionSelector.Select(
            PackageDirective.Parse("example.package#latest"),
            listing,
            null);

        result.ShouldNotBeNull();
        result.Key.ShouldBe(originalKey);
    }

    [Fact]
    public void PackageListing_DeserializesSingularArrayAndPluralFhirVersions()
    {
        const string json = """
            {
              "name": "example.package",
              "versions": {
                "1.0.0": {
                  "name": "example.package",
                  "version": "1.0.0",
                  "fhirVersion": ["4.0.1", "4.3.0"],
                  "fhirVersions": ["5.0.0"]
                }
              }
            }
            """;

        PackageListing? listing = JsonSerializer.Deserialize<PackageListing>(json);

        listing.ShouldNotBeNull();
        PackageVersionInfo info = listing.Versions["1.0.0"];
        info.FhirVersion.ShouldBe("4.0.1");
        info.FhirVersions.ShouldBe(["4.0.1", "4.3.0", "5.0.0"]);
    }

    [Fact]
    public void PackageListing_RoundTripPreservesExplicitEmptyFhirMetadata()
    {
        const string json = """
            {
              "name": "hl7.fhir.r4.core",
              "versions": {
                "4.0.1": {
                  "name": "hl7.fhir.r4.core",
                  "version": "4.0.1",
                  "fhirVersions": []
                }
              }
            }
            """;

        PackageListing listing =
            JsonSerializer.Deserialize<PackageListing>(json)!;
        string serialized = JsonSerializer.Serialize(listing);
        PackageListing roundTripped =
            JsonSerializer.Deserialize<PackageListing>(serialized)!;

        PackageVersionSelection? result = PackageVersionSelector.Select(
            PackageDirective.Parse("hl7.fhir.r4.core#latest"),
            roundTripped,
            new VersionResolveOptions { FhirRelease = FhirRelease.R4 });

        result.ShouldBeNull();
        serialized.ShouldContain("\"fhirVersions\":[]");
    }

    [Fact]
    public void PackageListing_NullResourceCountDeserializesAsNull()
    {
        const string json = """
            {
              "name": "example.package",
              "versions": {
                "1.0.0": {
                  "name": "example.package",
                  "version": "1.0.0",
                  "count": null
                }
              }
            }
            """;

        PackageListing listing =
            JsonSerializer.Deserialize<PackageListing>(json)!;

        listing.Versions["1.0.0"].ResourceCount.ShouldBeNull();
    }

    private static PackageListing CreateListing(
        string packageId,
        params (string Key, PackageVersionInfo Info)[] versions) =>
        new()
        {
            PackageId = packageId,
            Versions = versions.ToDictionary(
                version => version.Key,
                version => version.Info,
                StringComparer.Ordinal),
        };

    private static PackageVersionInfo CreateInfo(
        string packageId,
        string version,
        string? fhirVersion) =>
        new()
        {
            Name = packageId,
            Version = version,
            FhirVersion = fhirVersion,
            FhirVersions = fhirVersion is null ? null : [fhirVersion],
        };
}
