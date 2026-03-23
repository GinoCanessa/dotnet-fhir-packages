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
        PackageReference reference = PackageReference.Parse("hl7.fhir.r4.core#4.0.1");

        reference.Name.ShouldBe("hl7.fhir.r4.core");
        reference.Version.ShouldBe("4.0.1");
        reference.HasVersion.ShouldBeTrue();
    }

    [Fact]
    public void Parse_NpmStyle_NameAndVersion()
    {
        PackageReference reference = PackageReference.Parse("hl7.fhir.r4.core@4.0.1");

        reference.Name.ShouldBe("hl7.fhir.r4.core");
        reference.Version.ShouldBe("4.0.1");
        reference.HasVersion.ShouldBeTrue();
    }

    [Fact]
    public void Parse_NameOnly_NoVersion()
    {
        PackageReference reference = PackageReference.Parse("hl7.fhir.us.core");

        reference.Name.ShouldBe("hl7.fhir.us.core");
        reference.Version.ShouldBeNull();
        reference.HasVersion.ShouldBeFalse();
    }

    [Fact]
    public void FhirDirective_IncludesHashSeparator()
    {
        PackageReference reference = new PackageReference("my.package", "1.0.0");

        reference.FhirDirective.ShouldBe("my.package#1.0.0");
    }

    [Fact]
    public void FhirDirective_NoVersion_JustName()
    {
        PackageReference reference = new PackageReference("my.package");

        reference.FhirDirective.ShouldBe("my.package");
    }

    [Fact]
    public void NpmDirective_IncludesAtSeparator()
    {
        PackageReference reference = new PackageReference("my.package", "1.0.0");

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
        KeyValuePair<string, string> kvp = new KeyValuePair<string, string>("hl7.fhir.r4.core", "4.0.1");

        PackageReference reference = kvp;

        reference.Name.ShouldBe("hl7.fhir.r4.core");
        reference.Version.ShouldBe("4.0.1");
    }

    [Fact]
    public void Equality_SameNameAndVersion_Equal()
    {
        PackageReference a = new PackageReference("hl7.fhir.r4.core", "4.0.1");
        PackageReference b = new PackageReference("hl7.fhir.r4.core", "4.0.1");

        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_DifferentVersion_NotEqual()
    {
        PackageReference a = new PackageReference("hl7.fhir.r4.core", "4.0.1");
        PackageReference b = new PackageReference("hl7.fhir.r4.core", "5.0.0");

        a.ShouldNotBe(b);
    }

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        Func<PackageReference> act = () => PackageReference.Parse("");

        Should.Throw<ArgumentException>(() => act());
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Func<PackageReference> act = () => PackageReference.Parse(null!);

        Should.Throw<ArgumentNullException>(() => act());
    }

    [Fact]
    public void ToString_ReturnsFhirDirective()
    {
        PackageReference reference = new PackageReference("my.package", "1.0.0");

        reference.ToString().ShouldBe("my.package#1.0.0");
    }

    [Fact]
    public void Parse_NpmScope_HandlesCorrectly()
    {
        PackageReference reference = PackageReference.Parse("@scope/my.package@1.0.0");

        reference.Name.ShouldBe("@scope/my.package");
        reference.Version.ShouldBe("1.0.0");
        reference.Scope.ShouldBe("@scope");
    }
}
