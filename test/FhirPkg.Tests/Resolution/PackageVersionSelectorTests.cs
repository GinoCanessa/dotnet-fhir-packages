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
    public void Select_LatestTagVersion_IsCaseSensitive()
    {
        PackageListing listing = CreateListing(
            "example.package",
            (
                "1.0.0-alpha",
                CreateInfo(
                    "example.package",
                    "1.0.0-alpha",
                    "4.0.1")),
            (
                "1.0.0-Alpha",
                CreateInfo(
                    "example.package",
                    "1.0.0-Alpha",
                    "4.0.1")));
        listing = listing with
        {
            DistTags = new Dictionary<string, string>
            {
                ["latest"] = "1.0.0-Alpha",
            },
        };

        PackageVersionSelection? result = PackageVersionSelector.Select(
            PackageDirective.Parse("example.package#latest"),
            listing,
            null);

        result.ShouldNotBeNull();
        result.Key.ShouldBe("1.0.0-Alpha");
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

    [Theory]
    [InlineData("2.0", "2.0")]
    [InlineData("2.*", "2.1")]
    [InlineData("2.x.x", "2.1.0")]
    [InlineData("2.0.*", "2.0.1")]
    [InlineData("*.0", "2.0")]
    [InlineData("*.0.0", "6.0.0")]
    [InlineData("2.*.0", "2.1.0")]
    [InlineData("2.0.0-*", "2.0.0-alpha")]
    [InlineData("2.0.0+*", "2.0.0+build.1")]
    [InlineData("2.0.x-*", "2.0.1-ballot")]
    [InlineData("2.0?", "2.0.1")]
    [InlineData("2.0.1?", "2.0.1")]
    [InlineData("2.x?", "2.1.0")]
    [InlineData("4.x?", "4.1.0")]
    [InlineData("6.1?", "6.1.0")]
    [InlineData("4.*.*", "4.1.0")]
    [InlineData("6.0.x-*", "6.0.0-ballot")]
    public void Select_DefinedWildcardGrammar_PreservesKey(
        string specifier,
        string expectedKey)
    {
        PackageListing listing = CreateGrammarListing();

        PackageVersionSelection? result = PackageVersionSelector.Select(
            PackageDirective.Parse($"example.package#{specifier}"),
            listing,
            new VersionResolveOptions { AllowPreRelease = true });

        result.ShouldNotBeNull();
        result.Key.ShouldBe(expectedKey);
        result.Version.ToString().ShouldBe(expectedKey);
    }

    [Theory]
    [InlineData("2.0.0-*")]
    [InlineData("2.0.x-*")]
    public void Select_PrereleaseOnlyPattern_IsRejectedWhenDisabled(
        string specifier)
    {
        PackageVersionSelection? result = PackageVersionSelector.Select(
            PackageDirective.Parse($"example.package#{specifier}"),
            CreateGrammarListing(),
            new VersionResolveOptions { AllowPreRelease = false });

        result.ShouldBeNull();
    }

    [Fact]
    public void Select_PatternPrereleasePolicy_IsConsistent()
    {
        PackageListing listing = CreateListing(
            "example.package",
            ("2.0", CreateInfo("example.package", "2.0", null)),
            (
                "2.0.0-alpha",
                CreateInfo("example.package", "2.0.0-alpha", null)),
            (
                "2.0.0+build",
                CreateInfo("example.package", "2.0.0+build", null)),
            (
                "2.0.1-alpha",
                CreateInfo("example.package", "2.0.1-alpha", null)),
            (
                "2.0.1+build",
                CreateInfo("example.package", "2.0.1+build", null)));

        PackageVersionSelection? disabledQuestion = PackageVersionSelector.Select(
            PackageDirective.Parse("example.package#2.0?"),
            listing,
            new VersionResolveOptions { AllowPreRelease = false });
        PackageVersionSelection? enabledQuestion = PackageVersionSelector.Select(
            PackageDirective.Parse("example.package#2.0?"),
            listing,
            new VersionResolveOptions { AllowPreRelease = true });
        PackageVersionSelection? stablePatch = PackageVersionSelector.Select(
            PackageDirective.Parse("example.package#2.0.*"),
            listing,
            new VersionResolveOptions { AllowPreRelease = true });
        PackageVersionSelection? buildOnly = PackageVersionSelector.Select(
            PackageDirective.Parse("example.package#2.0.0+*"),
            listing,
            new VersionResolveOptions { AllowPreRelease = true });

        disabledQuestion!.Key.ShouldBe("2.0.1+build");
        enabledQuestion!.Key.ShouldBe("2.0.1+build");
        stablePatch.ShouldBeNull();
        buildOnly!.Key.ShouldBe("2.0.0+build");
    }

    [Fact]
    public void Select_QuestionPattern_ConsidersPrereleaseOnlyWhenEnabled()
    {
        PackageListing listing = CreateListing(
            "example.package",
            ("2.0", CreateInfo("example.package", "2.0", null)),
            (
                "2.0.0-alpha",
                CreateInfo("example.package", "2.0.0-alpha", null)));

        PackageVersionSelection? disabled = PackageVersionSelector.Select(
            PackageDirective.Parse("example.package#2.0?"),
            listing,
            new VersionResolveOptions { AllowPreRelease = false });
        PackageVersionSelection? enabled = PackageVersionSelector.Select(
            PackageDirective.Parse("example.package#2.0?"),
            listing,
            new VersionResolveOptions { AllowPreRelease = true });

        disabled!.Key.ShouldBe("2.0");
        enabled!.Key.ShouldBe("2.0.0-alpha");
    }

    [Fact]
    public void Select_EqualPrecedenceBuildMatches_UsesCandidateOrder()
    {
        PackageVersionSelection? result = PackageVersionSelector.Select(
            PackageDirective.Parse("example.package#2.0.0+*"),
            CreateGrammarListing(),
            new VersionResolveOptions { AllowPreRelease = true });

        result.ShouldNotBeNull();
        result.Key.ShouldBe("2.0.0+build.1");
    }

    [Fact]
    public void Select_RangeBuildIdentity_PreservesExactKey()
    {
        PackageListing listing = CreateListing(
            "example.package",
            (
                "2.0.0+build.1",
                CreateInfo("example.package", "2.0.0+build.1", null)),
            (
                "2.0.0+build.2",
                CreateInfo("example.package", "2.0.0+build.2", null)));

        PackageVersionSelection? result = PackageVersionSelector.Select(
            PackageDirective.Parse(
                "example.package#2.0.0+build.2|9.0.0"),
            listing,
            null);

        result.ShouldNotBeNull();
        result.Key.ShouldBe("2.0.0+build.2");
    }

    private static PackageListing CreateGrammarListing() =>
        CreateListing(
            "example.package",
            ("2.0", CreateInfo("example.package", "2.0", null)),
            ("2.0.0", CreateInfo("example.package", "2.0.0", null)),
            (
                "2.0.0-alpha",
                CreateInfo("example.package", "2.0.0-alpha", null)),
            (
                "2.0.0+build.1",
                CreateInfo("example.package", "2.0.0+build.1", null)),
            (
                "2.0.0+build.2",
                CreateInfo("example.package", "2.0.0+build.2", null)),
            (
                "2.0.0-alpha+build",
                CreateInfo("example.package", "2.0.0-alpha+build", null)),
            ("2.0.1", CreateInfo("example.package", "2.0.1", null)),
            (
                "2.0.1-ballot",
                CreateInfo("example.package", "2.0.1-ballot", null)),
            ("2.1", CreateInfo("example.package", "2.1", null)),
            ("2.1.0", CreateInfo("example.package", "2.1.0", null)),
            ("4.0.0", CreateInfo("example.package", "4.0.0", null)),
            ("4.1.0", CreateInfo("example.package", "4.1.0", null)),
            (
                "6.0.0-ballot",
                CreateInfo("example.package", "6.0.0-ballot", null)),
            ("6.0.0", CreateInfo("example.package", "6.0.0", null)),
            ("6.1.0", CreateInfo("example.package", "6.1.0", null)));

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
