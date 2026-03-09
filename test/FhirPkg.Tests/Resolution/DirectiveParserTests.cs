// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;
using FhirPkg.Resolution;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Resolution;

public class DirectiveParserTests
{
    // ── ClassifyName ────────────────────────────────────────────────────

    [Theory]
    [InlineData("hl7.fhir.r4.core", PackageNameType.CoreFull)]
    [InlineData("hl7.fhir.r5.core", PackageNameType.CoreFull)]
    [InlineData("hl7.fhir.r4b.core", PackageNameType.CoreFull)]
    [InlineData("hl7.fhir.r4.expansions", PackageNameType.CoreFull)]
    [InlineData("hl7.fhir.r4.examples", PackageNameType.CoreFull)]
    [InlineData("hl7.fhir.r4.search", PackageNameType.CoreFull)]
    public void ClassifyName_CoreFull_ClassifiedCorrectly(
        string packageId, PackageNameType expected)
    {
        DirectiveParser.ClassifyName(packageId).ShouldBe(expected);
    }

    [Theory]
    [InlineData("hl7.fhir.r4", PackageNameType.CorePartial)]
    [InlineData("hl7.fhir.r5", PackageNameType.CorePartial)]
    [InlineData("hl7.fhir.r4b", PackageNameType.CorePartial)]
    public void ClassifyName_CorePartial_ClassifiedCorrectly(
        string packageId, PackageNameType expected)
    {
        DirectiveParser.ClassifyName(packageId).ShouldBe(expected);
    }

    [Theory]
    [InlineData("hl7.fhir.us.core", PackageNameType.GuideWithoutSuffix)]
    [InlineData("hl7.fhir.uv.ips", PackageNameType.GuideWithoutSuffix)]
    [InlineData("ihe.iti.mhd", PackageNameType.GuideWithoutSuffix)]
    public void ClassifyName_GuideWithoutSuffix_ClassifiedCorrectly(
        string packageId, PackageNameType expected)
    {
        DirectiveParser.ClassifyName(packageId).ShouldBe(expected);
    }

    [Fact]
    public void ClassifyName_GuideWithFhirSuffix_ClassifiedCorrectly()
    {
        // A guide package with a FHIR version suffix
        DirectiveParser.ClassifyName("hl7.fhir.uv.extensions.r4")
            .ShouldBe(PackageNameType.GuideWithFhirSuffix);
    }

    [Theory]
    [InlineData("some.third.party", PackageNameType.NonHl7Guide)]
    [InlineData("custom.package.name", PackageNameType.NonHl7Guide)]
    public void ClassifyName_NonHl7Guide_ClassifiedCorrectly(
        string packageId, PackageNameType expected)
    {
        DirectiveParser.ClassifyName(packageId).ShouldBe(expected);
    }

    // ── ClassifyVersion ─────────────────────────────────────────────────

    [Theory]
    [InlineData("4.0.1", VersionType.Exact)]
    [InlineData("1.0.0", VersionType.Exact)]
    [InlineData("6.0.0-ballot1", VersionType.Exact)]
    public void ClassifyVersion_Exact_ClassifiedCorrectly(
        string version, VersionType expected)
    {
        DirectiveParser.ClassifyVersion(version).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, VersionType.Latest)]
    [InlineData("", VersionType.Latest)]
    [InlineData("latest", VersionType.Latest)]
    [InlineData("LATEST", VersionType.Latest)]
    public void ClassifyVersion_Latest_ClassifiedCorrectly(
        string? version, VersionType expected)
    {
        DirectiveParser.ClassifyVersion(version).ShouldBe(expected);
    }

    [Fact]
    public void ClassifyVersion_CiBuild_ClassifiedCorrectly()
    {
        DirectiveParser.ClassifyVersion("current").ShouldBe(VersionType.CiBuild);
    }

    [Fact]
    public void ClassifyVersion_CiBuildBranch_ClassifiedCorrectly()
    {
        DirectiveParser.ClassifyVersion("current$R5").ShouldBe(VersionType.CiBuildBranch);
    }

    [Fact]
    public void ClassifyVersion_LocalBuild_ClassifiedCorrectly()
    {
        DirectiveParser.ClassifyVersion("dev").ShouldBe(VersionType.LocalBuild);
    }

    [Theory]
    [InlineData("4.0.x", VersionType.Wildcard)]
    [InlineData("4.0.X", VersionType.Wildcard)]
    [InlineData("4.0.*", VersionType.Wildcard)]
    public void ClassifyVersion_Wildcard_ClassifiedCorrectly(
        string version, VersionType expected)
    {
        DirectiveParser.ClassifyVersion(version).ShouldBe(expected);
    }

    [Theory]
    [InlineData("^4.0.0", VersionType.Range)]
    [InlineData("~3.0.1", VersionType.Range)]
    [InlineData("1.0.0|2.0.0", VersionType.Range)]
    public void ClassifyVersion_Range_ClassifiedCorrectly(
        string version, VersionType expected)
    {
        DirectiveParser.ClassifyVersion(version).ShouldBe(expected);
    }

    // ── SplitDirective ──────────────────────────────────────────────────

    [Fact]
    public void SplitDirective_FhirStyle_SplitsCorrectly()
    {
        var (packageId, version, alias) = DirectiveParser.SplitDirective("hl7.fhir.r4.core#4.0.1");

        packageId.ShouldBe("hl7.fhir.r4.core");
        version.ShouldBe("4.0.1");
        alias.ShouldBeNull();
    }

    [Fact]
    public void SplitDirective_NpmStyle_SplitsCorrectly()
    {
        var (packageId, version, alias) = DirectiveParser.SplitDirective("hl7.fhir.r4.core@4.0.1");

        packageId.ShouldBe("hl7.fhir.r4.core");
        version.ShouldBe("4.0.1");
        alias.ShouldBeNull();
    }

    [Fact]
    public void SplitDirective_NameOnly_VersionIsNull()
    {
        var (packageId, version, alias) = DirectiveParser.SplitDirective("hl7.fhir.us.core");

        packageId.ShouldBe("hl7.fhir.us.core");
        version.ShouldBeNull();
        alias.ShouldBeNull();
    }

    [Fact]
    public void SplitDirective_NpmAlias_ExtractsAllParts()
    {
        var (packageId, version, alias) = DirectiveParser.SplitDirective("v610@npm:hl7.fhir.us.core@6.1.0");

        packageId.ShouldBe("hl7.fhir.us.core");
        version.ShouldBe("6.1.0");
        alias.ShouldBe("v610");
    }

    [Fact]
    public void SplitDirective_EmptyString_Throws()
    {
        var act = () => DirectiveParser.SplitDirective("");

        Should.Throw<ArgumentException>(() => act());
    }

    // ── ExtractCiBranch ─────────────────────────────────────────────────

    [Fact]
    public void ExtractCiBranch_CurrentBranch_ReturnsBranch()
    {
        DirectiveParser.ExtractCiBranch("current$R5").ShouldBe("R5");
    }

    [Fact]
    public void ExtractCiBranch_JustCurrent_ReturnsNull()
    {
        DirectiveParser.ExtractCiBranch("current").ShouldBeNull();
    }

    [Fact]
    public void ExtractCiBranch_Null_ReturnsNull()
    {
        DirectiveParser.ExtractCiBranch(null).ShouldBeNull();
    }

    [Fact]
    public void ExtractCiBranch_ExactVersion_ReturnsNull()
    {
        DirectiveParser.ExtractCiBranch("4.0.1").ShouldBeNull();
    }

    // ── ExpandPartialCoreName ───────────────────────────────────────────

    [Fact]
    public void ExpandPartialCoreName_R4_IncludesCorePackages()
    {
        var expanded = DirectiveParser.ExpandPartialCoreName("hl7.fhir.r4");

        expanded.ShouldNotBeEmpty();
        expanded.ShouldContain("hl7.fhir.r4.core");
        expanded.ShouldContain("hl7.fhir.r4.expansions");
        expanded.ShouldContain("hl7.fhir.r4.examples");
    }

    [Fact]
    public void ExpandPartialCoreName_Unknown_ReturnsEmpty()
    {
        var expanded = DirectiveParser.ExpandPartialCoreName("some.unknown.prefix");

        expanded.ShouldBeEmpty();
    }

    // ── IsKnownCoreType ─────────────────────────────────────────────────

    [Theory]
    [InlineData("core", true)]
    [InlineData("expansions", true)]
    [InlineData("examples", true)]
    [InlineData("search", true)]
    [InlineData("unknown", false)]
    public void IsKnownCoreType_ReturnsExpected(string segment, bool expected)
    {
        DirectiveParser.IsKnownCoreType(segment).ShouldBe(expected);
    }
}
