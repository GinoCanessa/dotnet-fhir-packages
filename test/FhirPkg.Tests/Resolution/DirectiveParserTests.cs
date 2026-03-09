// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;
using FhirPkg.Resolution;
using FluentAssertions;
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
        DirectiveParser.ClassifyName(packageId).Should().Be(expected);
    }

    [Theory]
    [InlineData("hl7.fhir.r4", PackageNameType.CorePartial)]
    [InlineData("hl7.fhir.r5", PackageNameType.CorePartial)]
    [InlineData("hl7.fhir.r4b", PackageNameType.CorePartial)]
    public void ClassifyName_CorePartial_ClassifiedCorrectly(
        string packageId, PackageNameType expected)
    {
        DirectiveParser.ClassifyName(packageId).Should().Be(expected);
    }

    [Theory]
    [InlineData("hl7.fhir.us.core", PackageNameType.GuideWithoutSuffix)]
    [InlineData("hl7.fhir.uv.ips", PackageNameType.GuideWithoutSuffix)]
    [InlineData("ihe.iti.mhd", PackageNameType.GuideWithoutSuffix)]
    public void ClassifyName_GuideWithoutSuffix_ClassifiedCorrectly(
        string packageId, PackageNameType expected)
    {
        DirectiveParser.ClassifyName(packageId).Should().Be(expected);
    }

    [Fact]
    public void ClassifyName_GuideWithFhirSuffix_ClassifiedCorrectly()
    {
        // A guide package with a FHIR version suffix
        DirectiveParser.ClassifyName("hl7.fhir.uv.extensions.r4")
            .Should().Be(PackageNameType.GuideWithFhirSuffix);
    }

    [Theory]
    [InlineData("some.third.party", PackageNameType.NonHl7Guide)]
    [InlineData("custom.package.name", PackageNameType.NonHl7Guide)]
    public void ClassifyName_NonHl7Guide_ClassifiedCorrectly(
        string packageId, PackageNameType expected)
    {
        DirectiveParser.ClassifyName(packageId).Should().Be(expected);
    }

    // ── ClassifyVersion ─────────────────────────────────────────────────

    [Theory]
    [InlineData("4.0.1", VersionType.Exact)]
    [InlineData("1.0.0", VersionType.Exact)]
    [InlineData("6.0.0-ballot1", VersionType.Exact)]
    public void ClassifyVersion_Exact_ClassifiedCorrectly(
        string version, VersionType expected)
    {
        DirectiveParser.ClassifyVersion(version).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, VersionType.Latest)]
    [InlineData("", VersionType.Latest)]
    [InlineData("latest", VersionType.Latest)]
    [InlineData("LATEST", VersionType.Latest)]
    public void ClassifyVersion_Latest_ClassifiedCorrectly(
        string? version, VersionType expected)
    {
        DirectiveParser.ClassifyVersion(version).Should().Be(expected);
    }

    [Fact]
    public void ClassifyVersion_CiBuild_ClassifiedCorrectly()
    {
        DirectiveParser.ClassifyVersion("current").Should().Be(VersionType.CiBuild);
    }

    [Fact]
    public void ClassifyVersion_CiBuildBranch_ClassifiedCorrectly()
    {
        DirectiveParser.ClassifyVersion("current$R5").Should().Be(VersionType.CiBuildBranch);
    }

    [Fact]
    public void ClassifyVersion_LocalBuild_ClassifiedCorrectly()
    {
        DirectiveParser.ClassifyVersion("dev").Should().Be(VersionType.LocalBuild);
    }

    [Theory]
    [InlineData("4.0.x", VersionType.Wildcard)]
    [InlineData("4.0.X", VersionType.Wildcard)]
    [InlineData("4.0.*", VersionType.Wildcard)]
    public void ClassifyVersion_Wildcard_ClassifiedCorrectly(
        string version, VersionType expected)
    {
        DirectiveParser.ClassifyVersion(version).Should().Be(expected);
    }

    [Theory]
    [InlineData("^4.0.0", VersionType.Range)]
    [InlineData("~3.0.1", VersionType.Range)]
    [InlineData("1.0.0|2.0.0", VersionType.Range)]
    public void ClassifyVersion_Range_ClassifiedCorrectly(
        string version, VersionType expected)
    {
        DirectiveParser.ClassifyVersion(version).Should().Be(expected);
    }

    // ── SplitDirective ──────────────────────────────────────────────────

    [Fact]
    public void SplitDirective_FhirStyle_SplitsCorrectly()
    {
        var (packageId, version, alias) = DirectiveParser.SplitDirective("hl7.fhir.r4.core#4.0.1");

        packageId.Should().Be("hl7.fhir.r4.core");
        version.Should().Be("4.0.1");
        alias.Should().BeNull();
    }

    [Fact]
    public void SplitDirective_NpmStyle_SplitsCorrectly()
    {
        var (packageId, version, alias) = DirectiveParser.SplitDirective("hl7.fhir.r4.core@4.0.1");

        packageId.Should().Be("hl7.fhir.r4.core");
        version.Should().Be("4.0.1");
        alias.Should().BeNull();
    }

    [Fact]
    public void SplitDirective_NameOnly_VersionIsNull()
    {
        var (packageId, version, alias) = DirectiveParser.SplitDirective("hl7.fhir.us.core");

        packageId.Should().Be("hl7.fhir.us.core");
        version.Should().BeNull();
        alias.Should().BeNull();
    }

    [Fact]
    public void SplitDirective_NpmAlias_ExtractsAllParts()
    {
        var (packageId, version, alias) = DirectiveParser.SplitDirective("v610@npm:hl7.fhir.us.core@6.1.0");

        packageId.Should().Be("hl7.fhir.us.core");
        version.Should().Be("6.1.0");
        alias.Should().Be("v610");
    }

    [Fact]
    public void SplitDirective_EmptyString_Throws()
    {
        var act = () => DirectiveParser.SplitDirective("");

        act.Should().Throw<ArgumentException>();
    }

    // ── ExtractCiBranch ─────────────────────────────────────────────────

    [Fact]
    public void ExtractCiBranch_CurrentBranch_ReturnsBranch()
    {
        DirectiveParser.ExtractCiBranch("current$R5").Should().Be("R5");
    }

    [Fact]
    public void ExtractCiBranch_JustCurrent_ReturnsNull()
    {
        DirectiveParser.ExtractCiBranch("current").Should().BeNull();
    }

    [Fact]
    public void ExtractCiBranch_Null_ReturnsNull()
    {
        DirectiveParser.ExtractCiBranch(null).Should().BeNull();
    }

    [Fact]
    public void ExtractCiBranch_ExactVersion_ReturnsNull()
    {
        DirectiveParser.ExtractCiBranch("4.0.1").Should().BeNull();
    }

    // ── ExpandPartialCoreName ───────────────────────────────────────────

    [Fact]
    public void ExpandPartialCoreName_R4_IncludesCorePackages()
    {
        var expanded = DirectiveParser.ExpandPartialCoreName("hl7.fhir.r4");

        expanded.Should().NotBeEmpty();
        expanded.Should().Contain("hl7.fhir.r4.core");
        expanded.Should().Contain("hl7.fhir.r4.expansions");
        expanded.Should().Contain("hl7.fhir.r4.examples");
    }

    [Fact]
    public void ExpandPartialCoreName_Unknown_ReturnsEmpty()
    {
        var expanded = DirectiveParser.ExpandPartialCoreName("some.unknown.prefix");

        expanded.Should().BeEmpty();
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
        DirectiveParser.IsKnownCoreType(segment).Should().Be(expected);
    }
}
