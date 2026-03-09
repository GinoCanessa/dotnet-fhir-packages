// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;
using FluentAssertions;
using Xunit;

namespace FhirPkg.Tests.Models;

public class PackageReferenceTests
{
    [Fact]
    public void Parse_FhirStyle_NameAndVersion()
    {
        var reference = PackageReference.Parse("hl7.fhir.r4.core#4.0.1");

        reference.Name.Should().Be("hl7.fhir.r4.core");
        reference.Version.Should().Be("4.0.1");
        reference.HasVersion.Should().BeTrue();
    }

    [Fact]
    public void Parse_NpmStyle_NameAndVersion()
    {
        var reference = PackageReference.Parse("hl7.fhir.r4.core@4.0.1");

        reference.Name.Should().Be("hl7.fhir.r4.core");
        reference.Version.Should().Be("4.0.1");
        reference.HasVersion.Should().BeTrue();
    }

    [Fact]
    public void Parse_NameOnly_NoVersion()
    {
        var reference = PackageReference.Parse("hl7.fhir.us.core");

        reference.Name.Should().Be("hl7.fhir.us.core");
        reference.Version.Should().BeNull();
        reference.HasVersion.Should().BeFalse();
    }

    [Fact]
    public void FhirDirective_IncludesHashSeparator()
    {
        var reference = new PackageReference("my.package", "1.0.0");

        reference.FhirDirective.Should().Be("my.package#1.0.0");
    }

    [Fact]
    public void FhirDirective_NoVersion_JustName()
    {
        var reference = new PackageReference("my.package");

        reference.FhirDirective.Should().Be("my.package");
    }

    [Fact]
    public void NpmDirective_IncludesAtSeparator()
    {
        var reference = new PackageReference("my.package", "1.0.0");

        reference.NpmDirective.Should().Be("my.package@1.0.0");
    }

    [Fact]
    public void ImplicitConversion_FromString()
    {
        PackageReference reference = "hl7.fhir.r4.core#4.0.1";

        reference.Name.Should().Be("hl7.fhir.r4.core");
        reference.Version.Should().Be("4.0.1");
    }

    [Fact]
    public void ImplicitConversion_FromKeyValuePair()
    {
        var kvp = new KeyValuePair<string, string>("hl7.fhir.r4.core", "4.0.1");

        PackageReference reference = kvp;

        reference.Name.Should().Be("hl7.fhir.r4.core");
        reference.Version.Should().Be("4.0.1");
    }

    [Fact]
    public void Equality_SameNameAndVersion_Equal()
    {
        var a = new PackageReference("hl7.fhir.r4.core", "4.0.1");
        var b = new PackageReference("hl7.fhir.r4.core", "4.0.1");

        a.Should().Be(b);
    }

    [Fact]
    public void Equality_DifferentVersion_NotEqual()
    {
        var a = new PackageReference("hl7.fhir.r4.core", "4.0.1");
        var b = new PackageReference("hl7.fhir.r4.core", "5.0.0");

        a.Should().NotBe(b);
    }

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        var act = () => PackageReference.Parse("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        var act = () => PackageReference.Parse(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToString_ReturnsFhirDirective()
    {
        var reference = new PackageReference("my.package", "1.0.0");

        reference.ToString().Should().Be("my.package#1.0.0");
    }

    [Fact]
    public void Parse_NpmScope_HandlesCorrectly()
    {
        var reference = PackageReference.Parse("@scope/my.package@1.0.0");

        reference.Name.Should().Be("@scope/my.package");
        reference.Version.Should().Be("1.0.0");
        reference.Scope.Should().Be("@scope");
    }
}
