// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Models;

public class PackageDirectiveTests
{
    [Fact]
    public void Parse_FhirStyleExact_ClassifiesCorrectly()
    {
        var directive = PackageDirective.Parse("hl7.fhir.r4.core#4.0.1");

        directive.PackageId.ShouldBe("hl7.fhir.r4.core");
        directive.RequestedVersion.ShouldBe("4.0.1");
        directive.NameType.ShouldBe(PackageNameType.CoreFull);
        directive.VersionType.ShouldBe(VersionType.Exact);
        directive.Alias.ShouldBeNull();
    }

    [Fact]
    public void Parse_NpmStyleExact_ClassifiesCorrectly()
    {
        var directive = PackageDirective.Parse("hl7.fhir.r4.core@4.0.1");

        directive.PackageId.ShouldBe("hl7.fhir.r4.core");
        directive.RequestedVersion.ShouldBe("4.0.1");
        directive.NameType.ShouldBe(PackageNameType.CoreFull);
        directive.VersionType.ShouldBe(VersionType.Exact);
    }

    [Fact]
    public void Parse_NameOnly_ImpliesLatest()
    {
        var directive = PackageDirective.Parse("hl7.fhir.us.core");

        directive.PackageId.ShouldBe("hl7.fhir.us.core");
        directive.RequestedVersion.ShouldBeNull();
        directive.VersionType.ShouldBe(VersionType.Latest);
    }

    [Fact]
    public void Parse_CorePartial_HasExpandedPackageIds()
    {
        var directive = PackageDirective.Parse("hl7.fhir.r4#4.0.1");

        directive.PackageId.ShouldBe("hl7.fhir.r4");
        directive.NameType.ShouldBe(PackageNameType.CorePartial);
        directive.ExpandedPackageIds.ShouldNotBeNull();
        directive.ExpandedPackageIds.ShouldNotBeEmpty();
        directive.ExpandedPackageIds.ShouldContain("hl7.fhir.r4.core");
        directive.ExpandedPackageIds.ShouldContain("hl7.fhir.r4.expansions");
    }

    [Fact]
    public void Parse_CurrentTag_CiBuild()
    {
        var directive = PackageDirective.Parse("hl7.fhir.us.core#current");

        directive.PackageId.ShouldBe("hl7.fhir.us.core");
        directive.RequestedVersion.ShouldBe("current");
        directive.VersionType.ShouldBe(VersionType.CiBuild);
    }

    [Fact]
    public void Parse_CurrentBranch_CiBuildBranch()
    {
        var directive = PackageDirective.Parse("hl7.fhir.us.core#current$R5");

        directive.PackageId.ShouldBe("hl7.fhir.us.core");
        directive.VersionType.ShouldBe(VersionType.CiBuildBranch);
        directive.CiBranch.ShouldBe("R5");
    }

    [Fact]
    public void Parse_DevTag_LocalBuild()
    {
        var directive = PackageDirective.Parse("hl7.fhir.us.core#dev");

        directive.PackageId.ShouldBe("hl7.fhir.us.core");
        directive.VersionType.ShouldBe(VersionType.LocalBuild);
    }

    [Fact]
    public void Parse_NpmAlias_ExtractsAliasAndPackageId()
    {
        var directive = PackageDirective.Parse("v610@npm:hl7.fhir.us.core@6.1.0");

        directive.Alias.ShouldBe("v610");
        directive.PackageId.ShouldBe("hl7.fhir.us.core");
        directive.RequestedVersion.ShouldBe("6.1.0");
    }

    [Fact]
    public void Parse_WildcardVersion_ClassifiedCorrectly()
    {
        var directive = PackageDirective.Parse("hl7.fhir.r4.core#4.0.x");

        directive.VersionType.ShouldBe(VersionType.Wildcard);
    }

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        var act = () => PackageDirective.Parse("");

        Should.Throw<ArgumentException>(() => act());
    }

    [Fact]
    public void Parse_NullString_Throws()
    {
        var act = () => PackageDirective.Parse(null!);

        Should.Throw<ArgumentNullException>(() => act());
    }

    [Fact]
    public void Parse_RawDirective_PreservedExactly()
    {
        var input = "hl7.fhir.r4.core#4.0.1";
        var directive = PackageDirective.Parse(input);

        directive.RawDirective.ShouldBe(input);
    }

    [Fact]
    public void ToReference_ProducesCorrectReference()
    {
        var directive = PackageDirective.Parse("hl7.fhir.r4.core#4.0.1");

        var reference = directive.ToReference();

        reference.Name.ShouldBe("hl7.fhir.r4.core");
        reference.Version.ShouldBe("4.0.1");
    }

    [Fact]
    public void Parse_RangeVersion_ClassifiedCorrectly()
    {
        var directive = PackageDirective.Parse("hl7.fhir.r4.core#^4.0.0");

        directive.VersionType.ShouldBe(VersionType.Range);
    }

    [Fact]
    public void Parse_GuideWithoutSuffix_ClassifiedCorrectly()
    {
        var directive = PackageDirective.Parse("hl7.fhir.us.core#6.1.0");

        directive.NameType.ShouldBe(PackageNameType.GuideWithoutSuffix);
    }

    [Fact]
    public void Parse_NonHl7Guide_ClassifiedCorrectly()
    {
        var directive = PackageDirective.Parse("some.third.party.package#1.0.0");

        directive.NameType.ShouldBe(PackageNameType.NonHl7Guide);
    }

    [Fact]
    public void Parse_ScopeSlashNameAtVersion_ParsesCorrectly()
    {
        var directive = PackageDirective.Parse("@scope/name@1.0.0");

        directive.PackageId.ShouldBe("@scope/name");
        directive.RequestedVersion.ShouldBe("1.0.0");
    }

    [Fact]
    public void Parse_AliasNpmSyntax_ParsesCorrectly()
    {
        var directive = PackageDirective.Parse("myalias@npm:hl7.fhir.r4.core@4.0.1");

        directive.Alias.ShouldBe("myalias");
        directive.PackageId.ShouldBe("hl7.fhir.r4.core");
        directive.RequestedVersion.ShouldBe("4.0.1");
    }

    [Fact]
    public void Parse_EmptyVersionAfterHash_HasNullVersion()
    {
        var directive = PackageDirective.Parse("hl7.fhir.us.core#");

        directive.PackageId.ShouldBe("hl7.fhir.us.core");
        directive.RequestedVersion.ShouldBeNull();
        directive.VersionType.ShouldBe(VersionType.Latest);
    }

    [Fact]
    public void Parse_DoubleAtSign_HandledGracefully()
    {
        var directive = PackageDirective.Parse("name@@");

        directive.PackageId.ShouldBe("name@");
        directive.RequestedVersion.ShouldBeNull();
    }
}
