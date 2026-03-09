// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;
using FhirPkg.Utilities;
using FluentAssertions;
using Xunit;

namespace FhirPkg.Tests.Utilities;

public class PackageFixupsTests
{
    [Fact]
    public void Apply_R4Core400_UpgradesTo401()
    {
        var reference = new PackageReference("hl7.fhir.r4.core", "4.0.0");

        var result = PackageFixups.Apply(reference);

        result.Name.Should().Be("hl7.fhir.r4.core");
        result.Version.Should().Be("4.0.1");
    }

    [Fact]
    public void Apply_R4bSnapshot_UpgradesTo430()
    {
        var reference = new PackageReference("hl7.fhir.r4b.core", "4.3.0-snapshot1");

        var result = PackageFixups.Apply(reference);

        result.Name.Should().Be("hl7.fhir.r4b.core");
        result.Version.Should().Be("4.3.0");
    }

    [Fact]
    public void Apply_UnknownPackage_NoChange()
    {
        var reference = new PackageReference("some.random.package", "1.0.0");

        var result = PackageFixups.Apply(reference);

        result.Name.Should().Be("some.random.package");
        result.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void Apply_CiBuildSuffix_Stripped()
    {
        var reference = new PackageReference("hl7.fhir.us.core", "6.1.0-cibuild");

        var result = PackageFixups.Apply(reference);

        result.Version.Should().Be("6.1.0");
    }

    [Fact]
    public void Apply_NoVersion_NoChange()
    {
        var reference = new PackageReference("hl7.fhir.r4.core");

        var result = PackageFixups.Apply(reference);

        result.Version.Should().BeNull();
    }

    [Fact]
    public void Apply_UvExtensionsR4_RemapsName()
    {
        var reference = new PackageReference("hl7.fhir.uv.extensions", "1.0.0");

        var result = PackageFixups.Apply(reference);

        result.Name.Should().Be("hl7.fhir.uv.extensions.r4");
    }

    [Fact]
    public void Apply_UvExtensionsR5_RemapsName()
    {
        var reference = new PackageReference("hl7.fhir.uv.extensions", "5.1.0");

        var result = PackageFixups.Apply(reference);

        result.Name.Should().Be("hl7.fhir.uv.extensions.r5");
    }

    [Fact]
    public void Apply_AlreadyCorrectVersion_SameReference()
    {
        var reference = new PackageReference("hl7.fhir.r4.core", "4.0.1");

        var result = PackageFixups.Apply(reference);

        // Since nothing changed, should return the same struct
        result.Should().Be(reference);
    }
}
