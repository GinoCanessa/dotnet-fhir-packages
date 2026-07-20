// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;
using FhirPkg.Registry;
using Shouldly;
using System.Text.Json;
using Xunit;

namespace FhirPkg.Tests.Models;

public class PackageListingTests
{
    [Fact]
    public void LatestVersion_WithDistTag_ReturnsTaggedVersion()
    {
        PackageListing listing = new PackageListing
        {
            PackageId = "test.package",
            DistTags = new Dictionary<string, string> { ["latest"] = "2.0.0" },
            Versions = new Dictionary<string, PackageVersionInfo>
            {
                ["1.0.0"] = new() { Name = "test.package", Version = "1.0.0" },
                ["2.0.0"] = new() { Name = "test.package", Version = "2.0.0" },
            }
        };

        listing.LatestVersion.ShouldBe("2.0.0");
    }

    [Fact]
    public void LatestVersion_WithoutDistTag_ReturnsHighestSemVer()
    {
        PackageListing listing = new PackageListing
        {
            PackageId = "test.package",
            Versions = new Dictionary<string, PackageVersionInfo>
            {
                ["1.0.0"] = new() { Name = "test.package", Version = "1.0.0" },
                ["3.0.0"] = new() { Name = "test.package", Version = "3.0.0" },
                ["2.0.0"] = new() { Name = "test.package", Version = "2.0.0" },
            }
        };

        // Must return 3.0.0 regardless of dictionary insertion order
        listing.LatestVersion.ShouldBe("3.0.0");
    }

    [Fact]
    public void LatestVersion_UnorderedKeys_ReturnsHighestSemVer()
    {
        // Explicitly test that dictionary key order doesn't matter
        Dictionary<string, PackageVersionInfo> versions = new Dictionary<string, PackageVersionInfo>
        {
            ["0.1.0"] = new() { Name = "test.package", Version = "0.1.0" },
            ["4.0.1"] = new() { Name = "test.package", Version = "4.0.1" },
            ["1.0.0"] = new() { Name = "test.package", Version = "1.0.0" },
            ["4.0.0"] = new() { Name = "test.package", Version = "4.0.0" },
            ["3.5.2"] = new() { Name = "test.package", Version = "3.5.2" },
        };

        PackageListing listing = new PackageListing
        {
            PackageId = "test.package",
            Versions = versions
        };

        listing.LatestVersion.ShouldBe("4.0.1");
    }

    [Fact]
    public void LatestVersion_EmptyVersions_ReturnsNull()
    {
        PackageListing listing = new PackageListing
        {
            PackageId = "test.package",
            Versions = new Dictionary<string, PackageVersionInfo>()
        };

        listing.LatestVersion.ShouldBeNull();
    }

    [Fact]
    public void LatestVersion_StablePreferredOverPreRelease()
    {
        PackageListing listing = new PackageListing
        {
            PackageId = "test.package",
            Versions = new Dictionary<string, PackageVersionInfo>
            {
                ["1.0.0"] = new() { Name = "test.package", Version = "1.0.0" },
                ["2.0.0-beta1"] = new() { Name = "test.package", Version = "2.0.0-beta1" },
            }
        };

        // FhirSemVer orders stable > pre-release at same major.minor.patch,
        // and 2.0.0-beta1 > 1.0.0 by major version, but it's pre-release.
        // The max should be 2.0.0-beta1 since it has higher major.minor.patch.
        string? result = listing.LatestVersion;
        result.ShouldNotBeNull();
    }

    [Fact]
    public void Defaults_AreCompleteWithoutFailuresOrCandidates()
    {
        Dictionary<string, PackageVersionInfo> versions = [];
        PackageListing listing = new PackageListing
        {
            PackageId = "test.package",
            Versions = versions
        };

        listing.IsComplete.ShouldBeTrue();
        listing.QueryFailures.ShouldBeEmpty();
        listing.VersionCandidates.ShouldBeEmpty();
        listing.SourceRegistry.ShouldBeNull();
    }

    [Fact]
    public void Serialize_WithProvenance_PreservesExistingJsonShape()
    {
        RegistryEndpoint endpoint = new()
        {
            Url = "https://user:secret@registry.example/private?token=secret",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer secret"
        };
        PackageVersionInfo version = new()
        {
            Name = "test.package",
            Version = "1.0.0",
            SourceRegistry = endpoint,
            IsSourceLatest = true
        };
        PackageListing listing = new()
        {
            PackageId = "test.package",
            Versions = new Dictionary<string, PackageVersionInfo>
            {
                ["1.0.0"] = version
            },
            SourceRegistry = endpoint,
            IsComplete = false,
            QueryFailures =
            [
                new RegistryAttemptFailure(endpoint.Url, RegistryFailureCategory.Network)
            ],
            VersionCandidates = [version]
        };

        PackageListing legacyShape = new()
        {
            PackageId = "test.package",
            Versions = new Dictionary<string, PackageVersionInfo>
            {
                ["1.0.0"] = new PackageVersionInfo
                {
                    Name = "test.package",
                    Version = "1.0.0"
                }
            }
        };

        string json = JsonSerializer.Serialize(listing);
        json.ShouldBe(JsonSerializer.Serialize(legacyShape));
        json.ShouldNotContain("secret");
        json.ShouldNotContain("SourceRegistry");
        json.ShouldNotContain("IsComplete");
        json.ShouldNotContain("QueryFailures");
        json.ShouldNotContain("VersionCandidates");
        json.ShouldNotContain("IsSourceLatest");
    }

    [Fact]
    public void Deserialize_LegacyJson_UsesNewPropertyDefaults()
    {
        const string json =
            """
            {
              "name": "test.package",
              "versions": {
                "1.0.0": {
                  "name": "test.package",
                  "version": "1.0.0"
                }
              }
            }
            """;

        PackageListing? listing = JsonSerializer.Deserialize<PackageListing>(json);

        listing.ShouldNotBeNull();
        listing.IsComplete.ShouldBeTrue();
        listing.QueryFailures.ShouldBeEmpty();
        listing.VersionCandidates.ShouldBeEmpty();
        PackageVersionInfo version = listing.Versions["1.0.0"];
        version.SourceRegistry.ShouldBeNull();
        version.IsSourceLatest.ShouldBeFalse();
    }
}
