// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using FhirPkg.Models;
using Shouldly;
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
        FhirSemVer version = FhirSemVer.Parse(input);

        version.Major.ShouldBe(expectedMajor);
        version.Minor.ShouldBe(expectedMinor);
        version.Patch.ShouldBe(expectedPatch);
        version.PreRelease.ShouldBeNull();
        version.IsWildcard.ShouldBeFalse();
    }

    [Theory]
    [InlineData("6.0.0-ballot1", "ballot1", FhirPreReleaseType.Ballot)]
    [InlineData("6.0.0-ballot2", "ballot2", FhirPreReleaseType.Ballot)]
    public void Parse_PreReleaseVersion_ParsesTag(
        string input, string expectedPreRelease, FhirPreReleaseType expectedType)
    {
        FhirSemVer version = FhirSemVer.Parse(input);

        version.PreRelease.ShouldBe(expectedPreRelease);
        version.PreReleaseType.ShouldBe(expectedType);
        version.IsPreRelease.ShouldBeTrue();
    }

    [Theory]
    [InlineData("1.0.0-snapshot2", "snapshot2", FhirPreReleaseType.Snapshot)]
    [InlineData("4.3.0-snapshot1", "snapshot1", FhirPreReleaseType.Snapshot)]
    public void Parse_SnapshotPreRelease_ParsesCorrectly(
        string input, string expectedPreRelease, FhirPreReleaseType expectedType)
    {
        FhirSemVer version = FhirSemVer.Parse(input);

        version.PreRelease.ShouldBe(expectedPreRelease);
        version.PreReleaseType.ShouldBe(expectedType);
    }

    [Fact]
    public void Parse_CiBuildPreRelease_ParsesCorrectly()
    {
        FhirSemVer version = FhirSemVer.Parse("5.0.0-cibuild");

        version.PreRelease.ShouldBe("cibuild");
        version.PreReleaseType.ShouldBe(FhirPreReleaseType.CiBuild);
    }

    [Fact]
    public void Parse_BuildMetadata_Ignored()
    {
        FhirSemVer version = FhirSemVer.Parse("1.2.3+20240115");

        version.Major.ShouldBe(1);
        version.Minor.ShouldBe(2);
        version.Patch.ShouldBe(3);
        version.BuildMetadata.ShouldBe("20240115");
        version.PreRelease.ShouldBeNull();
    }

    [Fact]
    public void Parse_WildcardPatch_IsWildcard()
    {
        FhirSemVer version = FhirSemVer.Parse("4.0.x");

        version.IsWildcard.ShouldBeTrue();
        version.Major.ShouldBe(4);
        version.Minor.ShouldBe(0);
    }

    [Fact]
    public void Parse_WildcardMinor_IsWildcard()
    {
        FhirSemVer version = FhirSemVer.Parse("4.x");

        version.IsWildcard.ShouldBeTrue();
        version.Major.ShouldBe(4);
    }

    [Fact]
    public void Parse_WildcardStar_IsWildcard()
    {
        FhirSemVer version = FhirSemVer.Parse("4.*");

        version.IsWildcard.ShouldBeTrue();
        version.Major.ShouldBe(4);
    }

    [Fact]
    public void Parse_WildcardAll_IsWildcard()
    {
        FhirSemVer version = FhirSemVer.Parse("*");

        version.IsWildcard.ShouldBeTrue();
    }

    [Fact]
    public void Parse_UpperCaseX_IsWildcard()
    {
        FhirSemVer version = FhirSemVer.Parse("4.0.X");

        version.IsWildcard.ShouldBeTrue();
    }

    [Fact]
    public void Parse_TwoSegment_TreatedAsWildcard()
    {
        FhirSemVer version = FhirSemVer.Parse("4.0");

        version.IsWildcard.ShouldBeTrue();
        version.Major.ShouldBe(4);
        version.Minor.ShouldBe(0);
    }

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        Func<FhirSemVer> act = () => FhirSemVer.Parse("");

        Should.Throw<ArgumentException>(() => act());
    }

    [Fact]
    public void TryParse_Valid_ReturnsTrue()
    {
        bool success = FhirSemVer.TryParse("4.0.1", out FhirSemVer? result);

        success.ShouldBeTrue();
        result.ShouldNotBeNull();
        result!.Major.ShouldBe(4);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("a.b.c")]
    public void TryParse_Invalid_ReturnsFalse(string? input)
    {
        bool success = FhirSemVer.TryParse(input, out FhirSemVer? result);

        success.ShouldBeFalse();
        result.ShouldBeNull();
    }

    // ── Comparison ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("1.0.0", "1.0.0-ballot1")]
    [InlineData("2.0.0", "2.0.0-draft1")]
    public void CompareTo_Release_GreaterThanPreRelease(string release, string preRelease)
    {
        FhirSemVer releaseVersion = FhirSemVer.Parse(release);
        FhirSemVer preReleaseVersion = FhirSemVer.Parse(preRelease);

        releaseVersion.CompareTo(preReleaseVersion).ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("1.0.0-ballot1", "1.0.0-draft1")]
    public void CompareTo_Ballot_GreaterThanDraft(string ballot, string draft)
    {
        FhirSemVer ballotVersion = FhirSemVer.Parse(ballot);
        FhirSemVer draftVersion = FhirSemVer.Parse(draft);

        ballotVersion.CompareTo(draftVersion).ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("5.0.0", "4.0.1")]
    [InlineData("4.1.0", "4.0.1")]
    [InlineData("4.0.2", "4.0.1")]
    public void CompareTo_HigherVersion_Greater(string higher, string lower)
    {
        FhirSemVer higherVersion = FhirSemVer.Parse(higher);
        FhirSemVer lowerVersion = FhirSemVer.Parse(lower);

        higherVersion.CompareTo(lowerVersion).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void CompareTo_SameVersion_Equal()
    {
        FhirSemVer a = FhirSemVer.Parse("4.0.1");
        FhirSemVer b = FhirSemVer.Parse("4.0.1");

        a.CompareTo(b).ShouldBe(0);
    }

    [Fact]
    public void Equals_SameVersion_True()
    {
        FhirSemVer a = FhirSemVer.Parse("4.0.1");
        FhirSemVer b = FhirSemVer.Parse("4.0.1");

        a.Equals(b).ShouldBeTrue();
        (a == b).ShouldBeTrue();
    }

    [Fact]
    public void Equals_DifferentVersion_False()
    {
        FhirSemVer a = FhirSemVer.Parse("4.0.1");
        FhirSemVer b = FhirSemVer.Parse("5.0.0");

        a.Equals(b).ShouldBeFalse();
        (a != b).ShouldBeTrue();
    }

    [Fact]
    public void Operator_LessThan_Works()
    {
        FhirSemVer a = FhirSemVer.Parse("3.0.0");
        FhirSemVer b = FhirSemVer.Parse("4.0.0");

        (a < b).ShouldBeTrue();
        (b < a).ShouldBeFalse();
    }

    [Fact]
    public void Operator_GreaterThan_Works()
    {
        FhirSemVer a = FhirSemVer.Parse("5.0.0");
        FhirSemVer b = FhirSemVer.Parse("4.0.0");

        (a > b).ShouldBeTrue();
        (b > a).ShouldBeFalse();
    }

    [Fact]
    public void Operator_LessThanOrEqual_Works()
    {
        FhirSemVer a = FhirSemVer.Parse("3.0.0");
        FhirSemVer b = FhirSemVer.Parse("3.0.0");
        FhirSemVer c = FhirSemVer.Parse("4.0.0");

        (a <= b).ShouldBeTrue();
        (a <= c).ShouldBeTrue();
        (c <= a).ShouldBeFalse();
    }

    [Fact]
    public void Operator_GreaterThanOrEqual_Works()
    {
        FhirSemVer a = FhirSemVer.Parse("4.0.0");
        FhirSemVer b = FhirSemVer.Parse("4.0.0");
        FhirSemVer c = FhirSemVer.Parse("3.0.0");

        (a >= b).ShouldBeTrue();
        (a >= c).ShouldBeTrue();
        (c >= a).ShouldBeFalse();
    }

    // ── Wildcard / Range Matching ───────────────────────────────────────

    [Fact]
    public void Satisfies_ExactMatch_True()
    {
        FhirSemVer version = FhirSemVer.Parse("4.0.1");

        version.Satisfies("4.0.1").ShouldBeTrue();
    }

    [Fact]
    public void Satisfies_ExactMismatch_False()
    {
        FhirSemVer version = FhirSemVer.Parse("4.0.1");

        version.Satisfies("4.0.2").ShouldBeFalse();
    }

    [Fact]
    public void Satisfies_WildcardPatch_MatchesSameMajorMinor()
    {
        FhirSemVer version = FhirSemVer.Parse("4.0.1");

        version.Satisfies("4.0.x").ShouldBeTrue();
    }

    [Fact]
    public void Satisfies_WildcardPatch_RejectsDifferentMinor()
    {
        FhirSemVer version = FhirSemVer.Parse("4.1.0");

        version.Satisfies("4.0.x").ShouldBeFalse();
    }

    [Fact]
    public void Satisfies_WildcardMinor_MatchesSameMajor()
    {
        FhirSemVer version = FhirSemVer.Parse("4.3.0");

        version.Satisfies("4.x").ShouldBeTrue();
    }

    [Fact]
    public void Satisfies_WildcardAll_MatchesAnything()
    {
        FhirSemVer version = FhirSemVer.Parse("99.99.99");

        version.Satisfies("*").ShouldBeTrue();
    }

    [Fact]
    public void MaxSatisfying_PatchWildcard_ReturnsHighestPatch()
    {
        FhirSemVer[] versions = new[]
        {
            FhirSemVer.Parse("4.0.0"),
            FhirSemVer.Parse("4.0.1"),
            FhirSemVer.Parse("4.0.2"),
            FhirSemVer.Parse("4.1.0"),
        };

        FhirSemVer? result = FhirSemVer.MaxSatisfying(versions, "4.0.x");

        result.ShouldNotBeNull();
        result!.Patch.ShouldBe(2);
    }

    [Fact]
    public void MaxSatisfying_NoMatch_ReturnsNull()
    {
        FhirSemVer[] versions = new[]
        {
            FhirSemVer.Parse("3.0.0"),
            FhirSemVer.Parse("3.0.1"),
        };

        FhirSemVer? result = FhirSemVer.MaxSatisfying(versions, "4.0.x");

        result.ShouldBeNull();
    }

    [Fact]
    public void SatisfyingRange_Caret_IncludesMinorBumps()
    {
        FhirSemVer[] versions = new[]
        {
            FhirSemVer.Parse("3.0.1"),
            FhirSemVer.Parse("3.1.0"),
            FhirSemVer.Parse("3.2.0"),
            FhirSemVer.Parse("4.0.0"),
        };

        List<FhirSemVer> results = FhirSemVer.SatisfyingRange(versions, "^3.0.1").ToList();

        results.ShouldContain(v => v.Minor == 0 && v.Patch == 1);
        results.ShouldContain(v => v.Minor == 1 && v.Patch == 0);
        results.ShouldContain(v => v.Minor == 2 && v.Patch == 0);
        results.ShouldNotContain(v => v.Major == 4);
    }

    [Fact]
    public void SatisfyingRange_Tilde_IncludesPatchOnly()
    {
        FhirSemVer[] versions = new[]
        {
            FhirSemVer.Parse("3.0.1"),
            FhirSemVer.Parse("3.0.2"),
            FhirSemVer.Parse("3.0.5"),
            FhirSemVer.Parse("3.1.0"),
        };

        List<FhirSemVer> results = FhirSemVer.SatisfyingRange(versions, "~3.0.1").ToList();

        results.ShouldContain(v => v.Patch == 1);
        results.ShouldContain(v => v.Patch == 2);
        results.ShouldContain(v => v.Patch == 5);
        results.ShouldNotContain(v => v.Minor == 1);
    }

    [Fact]
    public void SatisfyingRange_Pipe_EitherVersion()
    {
        FhirSemVer[] versions = new[]
        {
            FhirSemVer.Parse("1.0.0"),
            FhirSemVer.Parse("2.0.0"),
            FhirSemVer.Parse("3.0.0"),
        };

        List<FhirSemVer> results = FhirSemVer.SatisfyingRange(versions, "1.0.0|3.0.0").ToList();

        results.Count.ShouldBe(2);
        results.ShouldContain(v => v.Major == 1);
        results.ShouldContain(v => v.Major == 3);
    }

    [Fact]
    public void ToString_ExactVersion_FormatsCorrectly()
    {
        FhirSemVer version = FhirSemVer.Parse("4.0.1");

        version.ToString().ShouldBe("4.0.1");
    }

    [Fact]
    public void ToString_PreRelease_FormatsCorrectly()
    {
        FhirSemVer version = FhirSemVer.Parse("6.0.0-ballot1");

        version.ToString().ShouldBe("6.0.0-ballot1");
    }

    [Fact]
    public void ToString_WildcardAll_ReturnsAsterisk()
    {
        FhirSemVer version = FhirSemVer.Parse("*");

        version.ToString().ShouldBe("*");
    }
}
