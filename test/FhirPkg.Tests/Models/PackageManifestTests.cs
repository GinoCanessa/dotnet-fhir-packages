// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using FhirPkg.Models;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Models;

public class PackageManifestTests
{
    private const string FullManifestJson = """
        {
            "name": "hl7.fhir.r4.core",
            "version": "4.0.1",
            "description": "FHIR R4 Core definitions",
            "license": "CC0-1.0",
            "author": "HL7 International",
            "homepage": "https://hl7.org/fhir/R4/",
            "canonical": "http://hl7.org/fhir",
            "fhirVersions": ["4.0.1"],
            "type": "fhir.core",
            "title": "FHIR R4",
            "dependencies": {
                "hl7.fhir.r4.expansions": "4.0.1"
            },
            "keywords": ["fhir", "r4"]
        }
        """;

    private const string MinimalManifestJson = """
        {
            "name": "minimal.package",
            "version": "1.0.0"
        }
        """;

    [Fact]
    public void Deserialize_FullManifest_AllFieldsPopulated()
    {
        var manifest = PackageManifest.Deserialize(FullManifestJson);

        manifest.Name.ShouldBe("hl7.fhir.r4.core");
        manifest.Version.ShouldBe("4.0.1");
        manifest.Description.ShouldBe("FHIR R4 Core definitions");
        manifest.License.ShouldBe("CC0-1.0");
        manifest.Author.ShouldBe("HL7 International");
        manifest.Homepage.ShouldBe("https://hl7.org/fhir/R4/");
        manifest.Canonical.ShouldBe("http://hl7.org/fhir");
        manifest.FhirVersions.ShouldContain("4.0.1");
        manifest.Type.ShouldBe("fhir.core");
        manifest.Title.ShouldBe("FHIR R4");
        manifest.Keywords.ShouldContain("fhir");
    }

    [Fact]
    public void Deserialize_MinimalManifest_OnlyRequiredFields()
    {
        var manifest = PackageManifest.Deserialize(MinimalManifestJson);

        manifest.Name.ShouldBe("minimal.package");
        manifest.Version.ShouldBe("1.0.0");
        manifest.Description.ShouldBeNull();
        manifest.Dependencies.ShouldBeNull();
        manifest.FhirVersions.ShouldBeNull();
    }

    [Fact]
    public void Deserialize_WithDependencies_ParsedCorrectly()
    {
        var manifest = PackageManifest.Deserialize(FullManifestJson);

        manifest.Dependencies.ShouldNotBeNull();
        manifest.Dependencies.ShouldContainKey("hl7.fhir.r4.expansions");
        manifest.Dependencies!["hl7.fhir.r4.expansions"].ShouldBe("4.0.1");
    }

    [Fact]
    public void Deserialize_CaseInsensitive_HandlesVariations()
    {
        var json = """
            {
                "Name": "case.test",
                "Version": "2.0.0",
                "Description": "Case test"
            }
            """;

        var manifest = PackageManifest.Deserialize(json);

        manifest.Name.ShouldBe("case.test");
        manifest.Version.ShouldBe("2.0.0");
    }

    [Fact]
    public void InferredFhirRelease_FromFhirVersions_Correct()
    {
        var manifest = PackageManifest.Deserialize(FullManifestJson);

        manifest.InferredFhirRelease.ShouldBe(FhirRelease.R4);
    }

    [Fact]
    public void InferredFhirRelease_FromDependencies_WhenNoFhirVersions()
    {
        var json = """
            {
                "name": "test.package",
                "version": "1.0.0",
                "dependencies": {
                    "hl7.fhir.r5.core": "5.0.0"
                }
            }
            """;

        var manifest = PackageManifest.Deserialize(json);

        manifest.InferredFhirRelease.ShouldBe(FhirRelease.R5);
    }

    [Fact]
    public void InferredFhirRelease_NoInfo_ReturnsNull()
    {
        var manifest = PackageManifest.Deserialize(MinimalManifestJson);

        manifest.InferredFhirRelease.ShouldBeNull();
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var manifest = PackageManifest.Deserialize(FullManifestJson);

        var serialized = manifest.Serialize();
        var deserialized = PackageManifest.Deserialize(serialized);

        deserialized.Name.ShouldBe(manifest.Name);
        deserialized.Version.ShouldBe(manifest.Version);
        deserialized.Description.ShouldBe(manifest.Description);
        deserialized.License.ShouldBe(manifest.License);
    }

    [Fact]
    public void Deserialize_InvalidJson_Throws()
    {
        var act = () => PackageManifest.Deserialize("not valid json");

        Should.Throw<JsonException>(() => act());
    }

    [Fact]
    public void SemVer_ParsesCorrectly()
    {
        var manifest = PackageManifest.Deserialize(FullManifestJson);

        manifest.SemVer.ShouldNotBeNull();
        manifest.SemVer!.Major.ShouldBe(4);
        manifest.SemVer.Minor.ShouldBe(0);
        manifest.SemVer.Build.ShouldBe(1);
    }
}
