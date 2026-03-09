// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;
using FluentAssertions;
using Xunit;

namespace FhirPkg.Tests.Models;

public class PackageDirectiveTests
{
    [Fact]
    public void Parse_FhirStyleExact_ClassifiesCorrectly()
    {
        var directive = PackageDirective.Parse("hl7.fhir.r4.core#4.0.1");

        directive.PackageId.Should().Be("hl7.fhir.r4.core");
        directive.RequestedVersion.Should().Be("4.0.1");
        directive.NameType.Should().Be(PackageNameType.CoreFull);
        directive.VersionType.Should().Be(VersionType.Exact);
        directive.Alias.Should().BeNull();
    }

    [Fact]
    public void Parse_NpmStyleExact_ClassifiesCorrectly()
    {
        var directive = PackageDirective.Parse("hl7.fhir.r4.core@4.0.1");

        directive.PackageId.Should().Be("hl7.fhir.r4.core");
        directive.RequestedVersion.Should().Be("4.0.1");
        directive.NameType.Should().Be(PackageNameType.CoreFull);
        directive.VersionType.Should().Be(VersionType.Exact);
    }

    [Fact]
    public void Parse_NameOnly_ImpliesLatest()
    {
        var directive = PackageDirective.Parse("hl7.fhir.us.core");

        directive.PackageId.Should().Be("hl7.fhir.us.core");
        directive.RequestedVersion.Should().BeNull();
        directive.VersionType.Should().Be(VersionType.Latest);
    }

    [Fact]
    public void Parse_CorePartial_HasExpandedPackageIds()
    {
        var directive = PackageDirective.Parse("hl7.fhir.r4#4.0.1");

        directive.PackageId.Should().Be("hl7.fhir.r4");
        directive.NameType.Should().Be(PackageNameType.CorePartial);
        directive.ExpandedPackageIds.Should().NotBeNullOrEmpty();
        directive.ExpandedPackageIds.Should().Contain("hl7.fhir.r4.core");
        directive.ExpandedPackageIds.Should().Contain("hl7.fhir.r4.expansions");
    }

    [Fact]
    public void Parse_CurrentTag_CiBuild()
    {
        var directive = PackageDirective.Parse("hl7.fhir.us.core#current");

        directive.PackageId.Should().Be("hl7.fhir.us.core");
        directive.RequestedVersion.Should().Be("current");
        directive.VersionType.Should().Be(VersionType.CiBuild);
    }

    [Fact]
    public void Parse_CurrentBranch_CiBuildBranch()
    {
        var directive = PackageDirective.Parse("hl7.fhir.us.core#current$R5");

        directive.PackageId.Should().Be("hl7.fhir.us.core");
        directive.VersionType.Should().Be(VersionType.CiBuildBranch);
        directive.CiBranch.Should().Be("R5");
    }

    [Fact]
    public void Parse_DevTag_LocalBuild()
    {
        var directive = PackageDirective.Parse("hl7.fhir.us.core#dev");

        directive.PackageId.Should().Be("hl7.fhir.us.core");
        directive.VersionType.Should().Be(VersionType.LocalBuild);
    }

    [Fact]
    public void Parse_NpmAlias_ExtractsAliasAndPackageId()
    {
        var directive = PackageDirective.Parse("v610@npm:hl7.fhir.us.core@6.1.0");

        directive.Alias.Should().Be("v610");
        directive.PackageId.Should().Be("hl7.fhir.us.core");
        directive.RequestedVersion.Should().Be("6.1.0");
    }

    [Fact]
    public void Parse_WildcardVersion_ClassifiedCorrectly()
    {
        var directive = PackageDirective.Parse("hl7.fhir.r4.core#4.0.x");

        directive.VersionType.Should().Be(VersionType.Wildcard);
    }

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        var act = () => PackageDirective.Parse("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_NullString_Throws()
    {
        var act = () => PackageDirective.Parse(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Parse_RawDirective_PreservedExactly()
    {
        var input = "hl7.fhir.r4.core#4.0.1";
        var directive = PackageDirective.Parse(input);

        directive.RawDirective.Should().Be(input);
    }

    [Fact]
    public void ToReference_ProducesCorrectReference()
    {
        var directive = PackageDirective.Parse("hl7.fhir.r4.core#4.0.1");

        var reference = directive.ToReference();

        reference.Name.Should().Be("hl7.fhir.r4.core");
        reference.Version.Should().Be("4.0.1");
    }

    [Fact]
    public void Parse_RangeVersion_ClassifiedCorrectly()
    {
        var directive = PackageDirective.Parse("hl7.fhir.r4.core#^4.0.0");

        directive.VersionType.Should().Be(VersionType.Range);
    }

    [Fact]
    public void Parse_GuideWithoutSuffix_ClassifiedCorrectly()
    {
        var directive = PackageDirective.Parse("hl7.fhir.us.core#6.1.0");

        directive.NameType.Should().Be(PackageNameType.GuideWithoutSuffix);
    }

    [Fact]
    public void Parse_NonHl7Guide_ClassifiedCorrectly()
    {
        var directive = PackageDirective.Parse("some.third.party.package#1.0.0");

        directive.NameType.Should().Be(PackageNameType.NonHl7Guide);
    }
}
