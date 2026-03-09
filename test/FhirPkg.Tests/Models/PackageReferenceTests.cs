// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Models;

public class PackageReferenceTests
{
    [Fact]
    public void Parse_FhirStyle_NameAndVersion()
    {
        var reference = PackageReference.Parse("hl7.fhir.r4.core#4.0.1");

        reference.Name.ShouldBe("hl7.fhir.r4.core");
        reference.Version.ShouldBe("4.0.1");
        reference.HasVersion.ShouldBeTrue();
    }

    [Fact]
    public void Parse_NpmStyle_NameAndVersion()
    {
        var reference = PackageReference.Parse("hl7.fhir.r4.core@4.0.1");

        reference.Name.ShouldBe("hl7.fhir.r4.core");
        reference.Version.ShouldBe("4.0.1");
        reference.HasVersion.ShouldBeTrue();
    }

    [Fact]
    public void Parse_NameOnly_NoVersion()
    {
        var reference = PackageReference.Parse("hl7.fhir.us.core");

        reference.Name.ShouldBe("hl7.fhir.us.core");
        reference.Version.ShouldBeNull();
        reference.HasVersion.ShouldBeFalse();
    }

    [Fact]
    public void FhirDirective_IncludesHashSeparator()
    {
        var reference = new PackageReference("my.package", "1.0.0");

        reference.FhirDirective.ShouldBe("my.package#1.0.0");
    }

    [Fact]
    public void FhirDirective_NoVersion_JustName()
    {
        var reference = new PackageReference("my.package");

        reference.FhirDirective.ShouldBe("my.package");
    }

    [Fact]
    public void NpmDirective_IncludesAtSeparator()
    {
        var reference = new PackageReference("my.package", "1.0.0");

        reference.NpmDirective.ShouldBe("my.package@1.0.0");
    }

    [Fact]
    public void ImplicitConversion_FromString()
    {
        PackageReference reference = "hl7.fhir.r4.core#4.0.1";

        reference.Name.ShouldBe("hl7.fhir.r4.core");
        reference.Version.ShouldBe("4.0.1");
    }

    [Fact]
    public void ImplicitConversion_FromKeyValuePair()
    {
        var kvp = new KeyValuePair<string, string>("hl7.fhir.r4.core", "4.0.1");

        PackageReference reference = kvp;

        reference.Name.ShouldBe("hl7.fhir.r4.core");
        reference.Version.ShouldBe("4.0.1");
    }

    [Fact]
    public void Equality_SameNameAndVersion_Equal()
    {
        var a = new PackageReference("hl7.fhir.r4.core", "4.0.1");
        var b = new PackageReference("hl7.fhir.r4.core", "4.0.1");

        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_DifferentVersion_NotEqual()
    {
        var a = new PackageReference("hl7.fhir.r4.core", "4.0.1");
        var b = new PackageReference("hl7.fhir.r4.core", "5.0.0");

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        var act = () => PackageReference.Parse("");

        Should.Throw<ArgumentException>(() => act());
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        var act = () => PackageReference.Parse(null!);

        Should.Throw<ArgumentNullException>(() => act());
    }

    [Fact]
    public void ToString_ReturnsFhirDirective()
    {
        var reference = new PackageReference("my.package", "1.0.0");

        reference.ToString().ShouldBe("my.package#1.0.0");
    }

    [Fact]
    public void Parse_NpmScope_HandlesCorrectly()
    {
        var reference = PackageReference.Parse("@scope/my.package@1.0.0");

        reference.Name.ShouldBe("@scope/my.package");
        reference.Version.ShouldBe("1.0.0");
        reference.Scope.ShouldBe("@scope");
    }
}
