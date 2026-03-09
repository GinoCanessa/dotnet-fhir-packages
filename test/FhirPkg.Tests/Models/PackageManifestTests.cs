// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using FhirPkg.Models;
using FluentAssertions;
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

        manifest.Name.Should().Be("hl7.fhir.r4.core");
        manifest.Version.Should().Be("4.0.1");
        manifest.Description.Should().Be("FHIR R4 Core definitions");
        manifest.License.Should().Be("CC0-1.0");
        manifest.Author.Should().Be("HL7 International");
        manifest.Homepage.Should().Be("https://hl7.org/fhir/R4/");
        manifest.Canonical.Should().Be("http://hl7.org/fhir");
        manifest.FhirVersions.Should().Contain("4.0.1");
        manifest.Type.Should().Be("fhir.core");
        manifest.Title.Should().Be("FHIR R4");
        manifest.Keywords.Should().Contain("fhir");
    }

    [Fact]
    public void Deserialize_MinimalManifest_OnlyRequiredFields()
    {
        var manifest = PackageManifest.Deserialize(MinimalManifestJson);

        manifest.Name.Should().Be("minimal.package");
        manifest.Version.Should().Be("1.0.0");
        manifest.Description.Should().BeNull();
        manifest.Dependencies.Should().BeNull();
        manifest.FhirVersions.Should().BeNull();
    }

    [Fact]
    public void Deserialize_WithDependencies_ParsedCorrectly()
    {
        var manifest = PackageManifest.Deserialize(FullManifestJson);

        manifest.Dependencies.Should().NotBeNull();
        manifest.Dependencies.Should().ContainKey("hl7.fhir.r4.expansions");
        manifest.Dependencies!["hl7.fhir.r4.expansions"].Should().Be("4.0.1");
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

        manifest.Name.Should().Be("case.test");
        manifest.Version.Should().Be("2.0.0");
    }

    [Fact]
    public void InferredFhirRelease_FromFhirVersions_Correct()
    {
        var manifest = PackageManifest.Deserialize(FullManifestJson);

        manifest.InferredFhirRelease.Should().Be(FhirRelease.R4);
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

        manifest.InferredFhirRelease.Should().Be(FhirRelease.R5);
    }

    [Fact]
    public void InferredFhirRelease_NoInfo_ReturnsNull()
    {
        var manifest = PackageManifest.Deserialize(MinimalManifestJson);

        manifest.InferredFhirRelease.Should().BeNull();
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var manifest = PackageManifest.Deserialize(FullManifestJson);

        var serialized = manifest.Serialize();
        var deserialized = PackageManifest.Deserialize(serialized);

        deserialized.Name.Should().Be(manifest.Name);
        deserialized.Version.Should().Be(manifest.Version);
        deserialized.Description.Should().Be(manifest.Description);
        deserialized.License.Should().Be(manifest.License);
    }

    [Fact]
    public void Deserialize_InvalidJson_Throws()
    {
        var act = () => PackageManifest.Deserialize("not valid json");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void SemVer_ParsesCorrectly()
    {
        var manifest = PackageManifest.Deserialize(FullManifestJson);

        manifest.SemVer.Should().NotBeNull();
        manifest.SemVer!.Major.Should().Be(4);
        manifest.SemVer.Minor.Should().Be(0);
        manifest.SemVer.Build.Should().Be(1);
    }
}
