// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;
using FluentAssertions;
using Xunit;

namespace FhirPkg.Tests.Models;

public class FhirSemVerTests
{
    // ── Parsing ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("4.0.1", 4, 0, 1)]
    [InlineData("1.0.2", 1, 0, 2)]
    [InlineData("5.0.0", 5, 0, 0)]
    public void Parse_ExactVersion_ReturnsCorrectComponents(
        string input, int expectedMajor, int expectedMinor, int expectedPatch)
    {
        var version = FhirSemVer.Parse(input);

        version.Major.Should().Be(expectedMajor);
        version.Minor.Should().Be(expectedMinor);
        version.Patch.Should().Be(expectedPatch);
        version.PreRelease.Should().BeNull();
        version.IsWildcard.Should().BeFalse();
    }

    [Theory]
    [InlineData("6.0.0-ballot1", "ballot1", FhirPreReleaseType.Ballot)]
    [InlineData("6.0.0-ballot2", "ballot2", FhirPreReleaseType.Ballot)]
    public void Parse_PreReleaseVersion_ParsesTag(
        string input, string expectedPreRelease, FhirPreReleaseType expectedType)
    {
        var version = FhirSemVer.Parse(input);

        version.PreRelease.Should().Be(expectedPreRelease);
        version.PreReleaseType.Should().Be(expectedType);
        version.IsPreRelease.Should().BeTrue();
    }

    [Theory]
    [InlineData("1.0.0-snapshot2", "snapshot2", FhirPreReleaseType.Snapshot)]
    [InlineData("4.3.0-snapshot1", "snapshot1", FhirPreReleaseType.Snapshot)]
    public void Parse_SnapshotPreRelease_ParsesCorrectly(
        string input, string expectedPreRelease, FhirPreReleaseType expectedType)
    {
        var version = FhirSemVer.Parse(input);

        version.PreRelease.Should().Be(expectedPreRelease);
        version.PreReleaseType.Should().Be(expectedType);
    }

    [Fact]
    public void Parse_CiBuildPreRelease_ParsesCorrectly()
    {
        var version = FhirSemVer.Parse("5.0.0-cibuild");

        version.PreRelease.Should().Be("cibuild");
        version.PreReleaseType.Should().Be(FhirPreReleaseType.CiBuild);
    }

    [Fact]
    public void Parse_BuildMetadata_Ignored()
    {
        var version = FhirSemVer.Parse("1.2.3+20240115");

        version.Major.Should().Be(1);
        version.Minor.Should().Be(2);
        version.Patch.Should().Be(3);
        version.BuildMetadata.Should().Be("20240115");
        version.PreRelease.Should().BeNull();
    }

    [Fact]
    public void Parse_WildcardPatch_IsWildcard()
    {
        var version = FhirSemVer.Parse("4.0.x");

        version.IsWildcard.Should().BeTrue();
        version.Major.Should().Be(4);
        version.Minor.Should().Be(0);
    }

    [Fact]
    public void Parse_WildcardMinor_IsWildcard()
    {
        var version = FhirSemVer.Parse("4.x");

        version.IsWildcard.Should().BeTrue();
        version.Major.Should().Be(4);
    }

    [Fact]
    public void Parse_WildcardStar_IsWildcard()
    {
        var version = FhirSemVer.Parse("4.*");

        version.IsWildcard.Should().BeTrue();
        version.Major.Should().Be(4);
    }

    [Fact]
    public void Parse_WildcardAll_IsWildcard()
    {
        var version = FhirSemVer.Parse("*");

        version.IsWildcard.Should().BeTrue();
    }

    [Fact]
    public void Parse_UpperCaseX_IsWildcard()
    {
        var version = FhirSemVer.Parse("4.0.X");

        version.IsWildcard.Should().BeTrue();
    }

    [Fact]
    public void Parse_TwoSegment_TreatedAsWildcard()
    {
        var version = FhirSemVer.Parse("4.0");

        version.IsWildcard.Should().BeTrue();
        version.Major.Should().Be(4);
        version.Minor.Should().Be(0);
    }

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        var act = () => FhirSemVer.Parse("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryParse_Valid_ReturnsTrue()
    {
        var success = FhirSemVer.TryParse("4.0.1", out var result);

        success.Should().BeTrue();
        result.Should().NotBeNull();
        result!.Major.Should().Be(4);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("a.b.c")]
    public void TryParse_Invalid_ReturnsFalse(string? input)
    {
        var success = FhirSemVer.TryParse(input, out var result);

        success.Should().BeFalse();
        result.Should().BeNull();
    }

    // ── Comparison ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("1.0.0", "1.0.0-ballot1")]
    [InlineData("2.0.0", "2.0.0-draft1")]
    public void CompareTo_Release_GreaterThanPreRelease(string release, string preRelease)
    {
        var releaseVersion = FhirSemVer.Parse(release);
        var preReleaseVersion = FhirSemVer.Parse(preRelease);

        releaseVersion.CompareTo(preReleaseVersion).Should().BePositive();
    }

    [Theory]
    [InlineData("1.0.0-ballot1", "1.0.0-draft1")]
    public void CompareTo_Ballot_GreaterThanDraft(string ballot, string draft)
    {
        var ballotVersion = FhirSemVer.Parse(ballot);
        var draftVersion = FhirSemVer.Parse(draft);

        ballotVersion.CompareTo(draftVersion).Should().BePositive();
    }

    [Theory]
    [InlineData("5.0.0", "4.0.1")]
    [InlineData("4.1.0", "4.0.1")]
    [InlineData("4.0.2", "4.0.1")]
    public void CompareTo_HigherVersion_Greater(string higher, string lower)
    {
        var higherVersion = FhirSemVer.Parse(higher);
        var lowerVersion = FhirSemVer.Parse(lower);

        higherVersion.CompareTo(lowerVersion).Should().BePositive();
    }

    [Fact]
    public void CompareTo_SameVersion_Equal()
    {
        var a = FhirSemVer.Parse("4.0.1");
        var b = FhirSemVer.Parse("4.0.1");

        a.CompareTo(b).Should().Be(0);
    }

    [Fact]
    public void Equals_SameVersion_True()
    {
        var a = FhirSemVer.Parse("4.0.1");
        var b = FhirSemVer.Parse("4.0.1");

        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Equals_DifferentVersion_False()
    {
        var a = FhirSemVer.Parse("4.0.1");
        var b = FhirSemVer.Parse("5.0.0");

        a.Equals(b).Should().BeFalse();
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void Operator_LessThan_Works()
    {
        var a = FhirSemVer.Parse("3.0.0");
        var b = FhirSemVer.Parse("4.0.0");

        (a < b).Should().BeTrue();
        (b < a).Should().BeFalse();
    }

    [Fact]
    public void Operator_GreaterThan_Works()
    {
        var a = FhirSemVer.Parse("5.0.0");
        var b = FhirSemVer.Parse("4.0.0");

        (a > b).Should().BeTrue();
        (b > a).Should().BeFalse();
    }

    [Fact]
    public void Operator_LessThanOrEqual_Works()
    {
        var a = FhirSemVer.Parse("3.0.0");
        var b = FhirSemVer.Parse("3.0.0");
        var c = FhirSemVer.Parse("4.0.0");

        (a <= b).Should().BeTrue();
        (a <= c).Should().BeTrue();
        (c <= a).Should().BeFalse();
    }

    [Fact]
    public void Operator_GreaterThanOrEqual_Works()
    {
        var a = FhirSemVer.Parse("4.0.0");
        var b = FhirSemVer.Parse("4.0.0");
        var c = FhirSemVer.Parse("3.0.0");

        (a >= b).Should().BeTrue();
        (a >= c).Should().BeTrue();
        (c >= a).Should().BeFalse();
    }

    // ── Wildcard / Range Matching ───────────────────────────────────────

    [Fact]
    public void Satisfies_ExactMatch_True()
    {
        var version = FhirSemVer.Parse("4.0.1");

        version.Satisfies("4.0.1").Should().BeTrue();
    }

    [Fact]
    public void Satisfies_ExactMismatch_False()
    {
        var version = FhirSemVer.Parse("4.0.1");

        version.Satisfies("4.0.2").Should().BeFalse();
    }

    [Fact]
    public void Satisfies_WildcardPatch_MatchesSameMajorMinor()
    {
        var version = FhirSemVer.Parse("4.0.1");

        version.Satisfies("4.0.x").Should().BeTrue();
    }

    [Fact]
    public void Satisfies_WildcardPatch_RejectsDifferentMinor()
    {
        var version = FhirSemVer.Parse("4.1.0");

        version.Satisfies("4.0.x").Should().BeFalse();
    }

    [Fact]
    public void Satisfies_WildcardMinor_MatchesSameMajor()
    {
        var version = FhirSemVer.Parse("4.3.0");

        version.Satisfies("4.x").Should().BeTrue();
    }

    [Fact]
    public void Satisfies_WildcardAll_MatchesAnything()
    {
        var version = FhirSemVer.Parse("99.99.99");

        version.Satisfies("*").Should().BeTrue();
    }

    [Fact]
    public void MaxSatisfying_PatchWildcard_ReturnsHighestPatch()
    {
        var versions = new[]
        {
            FhirSemVer.Parse("4.0.0"),
            FhirSemVer.Parse("4.0.1"),
            FhirSemVer.Parse("4.0.2"),
            FhirSemVer.Parse("4.1.0"),
        };

        var result = FhirSemVer.MaxSatisfying(versions, "4.0.x");

        result.Should().NotBeNull();
        result!.Patch.Should().Be(2);
    }

    [Fact]
    public void MaxSatisfying_NoMatch_ReturnsNull()
    {
        var versions = new[]
        {
            FhirSemVer.Parse("3.0.0"),
            FhirSemVer.Parse("3.0.1"),
        };

        var result = FhirSemVer.MaxSatisfying(versions, "4.0.x");

        result.Should().BeNull();
    }

    [Fact]
    public void SatisfyingRange_Caret_IncludesMinorBumps()
    {
        var versions = new[]
        {
            FhirSemVer.Parse("3.0.1"),
            FhirSemVer.Parse("3.1.0"),
            FhirSemVer.Parse("3.2.0"),
            FhirSemVer.Parse("4.0.0"),
        };

        var results = FhirSemVer.SatisfyingRange(versions, "^3.0.1").ToList();

        results.Should().Contain(v => v.Minor == 0 && v.Patch == 1);
        results.Should().Contain(v => v.Minor == 1 && v.Patch == 0);
        results.Should().Contain(v => v.Minor == 2 && v.Patch == 0);
        results.Should().NotContain(v => v.Major == 4);
    }

    [Fact]
    public void SatisfyingRange_Tilde_IncludesPatchOnly()
    {
        var versions = new[]
        {
            FhirSemVer.Parse("3.0.1"),
            FhirSemVer.Parse("3.0.2"),
            FhirSemVer.Parse("3.0.5"),
            FhirSemVer.Parse("3.1.0"),
        };

        var results = FhirSemVer.SatisfyingRange(versions, "~3.0.1").ToList();

        results.Should().Contain(v => v.Patch == 1);
        results.Should().Contain(v => v.Patch == 2);
        results.Should().Contain(v => v.Patch == 5);
        results.Should().NotContain(v => v.Minor == 1);
    }

    [Fact]
    public void SatisfyingRange_Pipe_EitherVersion()
    {
        var versions = new[]
        {
            FhirSemVer.Parse("1.0.0"),
            FhirSemVer.Parse("2.0.0"),
            FhirSemVer.Parse("3.0.0"),
        };

        var results = FhirSemVer.SatisfyingRange(versions, "1.0.0|3.0.0").ToList();

        results.Should().HaveCount(2);
        results.Should().Contain(v => v.Major == 1);
        results.Should().Contain(v => v.Major == 3);
    }

    [Fact]
    public void ToString_ExactVersion_FormatsCorrectly()
    {
        var version = FhirSemVer.Parse("4.0.1");

        version.ToString().Should().Be("4.0.1");
    }

    [Fact]
    public void ToString_PreRelease_FormatsCorrectly()
    {
        var version = FhirSemVer.Parse("6.0.0-ballot1");

        version.ToString().Should().Be("6.0.0-ballot1");
    }

    [Fact]
    public void ToString_WildcardAll_ReturnsAsterisk()
    {
        var version = FhirSemVer.Parse("*");

        version.ToString().Should().Be("*");
    }
}
