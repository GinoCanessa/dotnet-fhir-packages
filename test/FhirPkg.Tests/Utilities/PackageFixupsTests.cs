// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;
using FhirPkg.Installation;
using FhirPkg.Utilities;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Utilities;

public class PackageFixupsTests
{
    [Fact]
    public void Apply_R4Core400_UpgradesTo401()
    {
        PackageReference reference = new PackageReference("hl7.fhir.r4.core", "4.0.0");

        PackageReference result = PackageFixups.Apply(reference);

        result.Name.ShouldBe("hl7.fhir.r4.core");
        result.Version.ShouldBe("4.0.1");
    }

    [Fact]
    public void Apply_R4bSnapshot_UpgradesTo430()
    {
        PackageReference reference = new PackageReference("hl7.fhir.r4b.core", "4.3.0-snapshot1");

        PackageReference result = PackageFixups.Apply(reference);

        result.Name.ShouldBe("hl7.fhir.r4b.core");
        result.Version.ShouldBe("4.3.0");
    }

    [Fact]
    public void Apply_UnknownPackage_NoChange()
    {
        PackageReference reference = new PackageReference("some.random.package", "1.0.0");

        PackageReference result = PackageFixups.Apply(reference);

        result.Name.ShouldBe("some.random.package");
        result.Version.ShouldBe("1.0.0");
    }

    [Fact]
    public void Apply_CiBuildSuffix_Stripped()
    {
        PackageReference reference = new PackageReference("hl7.fhir.us.core", "6.1.0-cibuild");

        PackageReference result = PackageFixups.Apply(reference);

        result.Version.ShouldBe("6.1.0");
    }

    [Fact]
    public void Apply_NoVersion_NoChange()
    {
        PackageReference reference = new PackageReference("hl7.fhir.r4.core");

        PackageReference result = PackageFixups.Apply(reference);

        result.Version.ShouldBeNull();
    }

    [Fact]
    public void Apply_UvExtensionsR4_RemapsName()
    {
        PackageReference reference = new PackageReference("hl7.fhir.uv.extensions", "1.0.0");

        PackageReference result = PackageFixups.Apply(reference);

        result.Name.ShouldBe("hl7.fhir.uv.extensions.r4");
    }

    [Fact]
    public void Apply_UvExtensionsR5_RemapsName()
    {
        PackageReference reference = new PackageReference("hl7.fhir.uv.extensions", "5.1.0");

        PackageReference result = PackageFixups.Apply(reference);

        result.Name.ShouldBe("hl7.fhir.uv.extensions.r5");
    }

    [Fact]
    public void Apply_AlreadyCorrectVersion_SameReference()
    {
        PackageReference reference = new PackageReference("hl7.fhir.r4.core", "4.0.1");

        PackageReference result = PackageFixups.Apply(reference);

        // Since nothing changed, should return the same struct
        result.ShouldBe(reference);
    }

    [Fact]
    public void ConfiguredPolicy_UsesFinalAtForScopedPackage()
    {
        PackageFixupPolicy policy = PackageFixupPolicy.Create(
            new Dictionary<string, string>
            {
                ["@scope/package@1.0.0"] = "1.0.1",
            });

        PackageReference result = PackageFixups.Apply(
            new PackageReference("@scope/package", "1.0.0"),
            policy);

        result.ShouldBe(new PackageReference("@scope/package", "1.0.1"));
    }

    [Fact]
    public void EmptyConfiguredPolicy_DisablesOnlyVersionRewrites()
    {
        PackageFixupPolicy policy = PackageFixupPolicy.Create(
            new Dictionary<string, string>());

        PackageReference versionResult = PackageFixups.Apply(
            new PackageReference("hl7.fhir.r4.core", "4.0.0"),
            policy);
        PackageReference suffixResult = PackageFixups.Apply(
            new PackageReference("hl7.fhir.us.core", "6.1.0-cibuild"),
            policy);
        PackageReference nameResult = PackageFixups.Apply(
            new PackageReference("hl7.fhir.uv.extensions", "5.1.0"),
            policy);

        versionResult.Version.ShouldBe("4.0.0");
        suffixResult.Version.ShouldBe("6.1.0");
        nameResult.Name.ShouldBe("hl7.fhir.uv.extensions.r5");
    }

    [Theory]
    [InlineData("missing-version", "1.0.0")]
    [InlineData("package@*", "1.0.0")]
    [InlineData("package@1.0.0", "^2.0.0")]
    public void ConfiguredPolicy_InvalidEntry_Throws(
        string key,
        string target)
    {
        Dictionary<string, string> fixups = new()
        {
            [key] = target,
        };

        PackageInstallException exception = Should.Throw<PackageInstallException>(
            () => PackageFixupPolicy.Create(fixups));

        exception.ErrorCode.ShouldBe(PackageInstallErrorCode.InvalidPolicy);
    }

    [Fact]
    public void ConfiguredPolicy_CaseInsensitiveDuplicate_Throws()
    {
        Dictionary<string, string> fixups = new(StringComparer.Ordinal)
        {
            ["Package@1.0.0"] = "1.0.1",
            ["package@1.0.0"] = "1.0.2",
        };

        Should.Throw<PackageInstallException>(
            () => PackageFixupPolicy.Create(fixups));
    }

    [Fact]
    public void ConfiguredPolicy_VersionPrereleaseText_IsCaseSensitive()
    {
        PackageFixupPolicy policy = PackageFixupPolicy.Create(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Example.Package@1.0.0-alpha"] = "1.0.1-alpha",
                ["example.package@1.0.0-Alpha"] = "1.0.1-Alpha",
            });

        PackageReference lower = PackageFixups.Apply(
            new PackageReference(
                "EXAMPLE.PACKAGE",
                "1.0.0-alpha"),
            policy);
        PackageReference upper = PackageFixups.Apply(
            new PackageReference(
                "example.package",
                "1.0.0-Alpha"),
            policy);

        lower.Version.ShouldBe("1.0.1-alpha");
        upper.Version.ShouldBe("1.0.1-Alpha");
    }

    [Fact]
    public void ConfiguredPolicy_CanonicalizesCiBuildSourcesAndTargets()
    {
        PackageFixupPolicy policy = PackageFixupPolicy.Create(
            new Dictionary<string, string>
            {
                ["example.package@1.0.0-cibuild"] = "2.0.0-cibuild",
            });

        PackageReference result = PackageFixups.Apply(
            new PackageReference("example.package", "1.0.0-cibuild"),
            policy);

        result.Version.ShouldBe("2.0.0");
    }
}
