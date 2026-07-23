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
    public void Parse_TwoPartVersion_IsExactAndPreservesPrecision()
    {
        FhirSemVer twoPart = FhirSemVer.Parse("4.0");
        FhirSemVer threePart = FhirSemVer.Parse("4.0.0");

        twoPart.IsWildcard.ShouldBeFalse();
        twoPart.ToString().ShouldBe("4.0");
        twoPart.ShouldNotBe(threePart);
        twoPart.CompareTo(threePart).ShouldBeLessThan(0);
    }

    [Theory]
    [InlineData("2.0-alpha")]
    [InlineData("2.0+build")]
    [InlineData("2.0-alpha+build")]
    public void Parse_TwoPartLabels_AreConcreteAndRoundTrip(string input)
    {
        FhirSemVer version = FhirSemVer.Parse(input);

        version.IsWildcard.ShouldBeFalse();
        version.ToString().ShouldBe(input);
    }

    [Theory]
    [InlineData("2.x.x", "2.x.x")]
    [InlineData("2.X.X", "2.x.x")]
    [InlineData("2.0.0-x", "2.0.0-x")]
    [InlineData("2.0.0+x", "2.0.0+x")]
    public void Parse_WildcardAliases_AreContextSensitive(
        string input,
        string expected)
    {
        FhirSemVer version = FhirSemVer.Parse(input);

        version.ToString().ShouldBe(expected);
        version.IsWildcard.ShouldBe(input.Contains(".x", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("*.0", "*.0")]
    [InlineData("*.0.0", "*.0.0")]
    [InlineData("2.*.0", "2.x.0")]
    public void Parse_NonTrailingStar_IsPartSpecific(
        string input,
        string expected)
    {
        FhirSemVer version = FhirSemVer.Parse(input);

        version.IsWildcard.ShouldBeTrue();
        version.ToString().ShouldBe(expected);
    }

    [Theory]
    [InlineData("2.0?")]
    [InlineData("2.0.1?")]
    [InlineData("2.x?")]
    [InlineData("2.0.0-*?")]
    [InlineData("2.0.0+*?")]
    public void Parse_TrailingQuestion_PreservesBoundary(string input)
    {
        FhirSemVer version = FhirSemVer.Parse(input);

        version.IsWildcard.ShouldBeTrue();
        version.ToString().ShouldBe(input);
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
    [InlineData("2")]
    [InlineData("x")]
    [InlineData("X")]
    [InlineData("2.?")]
    [InlineData("2.0.?")]
    [InlineData("2.0??")]
    [InlineData("2.x.x.x")]
    [InlineData("2.0.x-")]
    [InlineData("2.0.0+")]
    [InlineData("2.0?-alpha")]
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
    public void CompareTo_CrossPrecisionPreRelease_UsesPartPresence()
    {
        FhirSemVer twoPart = FhirSemVer.Parse("2.0");
        FhirSemVer threePartPreRelease = FhirSemVer.Parse("2.0.0-alpha");
        FhirSemVer threePartRelease = FhirSemVer.Parse("2.0.0");

        (twoPart < threePartPreRelease).ShouldBeTrue();
        (threePartPreRelease < threePartRelease).ShouldBeTrue();
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
    public void Equals_ConcreteBuildMetadata_RemainsNeutral()
    {
        FhirSemVer first = FhirSemVer.Parse("2.0.0+first");
        FhirSemVer second = FhirSemVer.Parse("2.0.0+second");

        first.ShouldBe(second);
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }

    [Theory]
    [InlineData("2.0?", "2.0")]
    [InlineData("2.0?", "2.0.0?")]
    [InlineData("2.*", "2.x.x")]
    [InlineData("2.0.0+*", "2.0.0")]
    [InlineData("2.0.0-*", "2.0.0+*")]
    public void PatternEquality_IncludesShapeAndQuestionBoundary(
        string leftInput,
        string rightInput)
    {
        FhirSemVer left = FhirSemVer.Parse(leftInput);
        FhirSemVer right = FhirSemVer.Parse(rightInput);

        left.ShouldNotBe(right);
    }

    [Fact]
    public void PatternEquality_EquivalentAliases_AreEqual()
    {
        FhirSemVer lower = FhirSemVer.Parse("2.x.x");
        FhirSemVer upper = FhirSemVer.Parse("2.X.X");

        lower.ShouldBe(upper);
        lower.GetHashCode().ShouldBe(upper.GetHashCode());
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
        FhirSemVer version = FhirSemVer.Parse("4.3");

        version.Satisfies("4.x").ShouldBeTrue();
    }

    [Fact]
    public void Satisfies_WildcardAll_MatchesAnything()
    {
        FhirSemVer version = FhirSemVer.Parse("99.99.99");

        version.Satisfies("*").ShouldBeTrue();
    }

    [Theory]
    [InlineData("2.0", "2.0", true)]
    [InlineData("2.0", "2.0.0", false)]
    [InlineData("2.0", "2.0-alpha", false)]
    [InlineData("2.0.0", "2.0.0", true)]
    [InlineData("2.0.0", "2.0.0-alpha", false)]
    [InlineData("2.0.0", "2.0.0+build", false)]
    [InlineData("2.*", "2.0", true)]
    [InlineData("2.*", "2.1", true)]
    [InlineData("2.*", "2.0.0", false)]
    [InlineData("2.*", "2.0-alpha", false)]
    [InlineData("2.x.x", "2.0.0", true)]
    [InlineData("2.x.x", "2.1.5", true)]
    [InlineData("2.x.x", "2.0", false)]
    [InlineData("2.x.x", "2.0.0-alpha", false)]
    [InlineData("2.0.*", "2.0.0", true)]
    [InlineData("2.0.*", "2.0.9", true)]
    [InlineData("2.0.*", "2.0", false)]
    [InlineData("2.0.*", "2.0.0+build", false)]
    [InlineData("2.0.0-*", "2.0.0-alpha", true)]
    [InlineData("2.0.0-*", "2.0.0", false)]
    [InlineData("2.0.0-*", "2.0.0-alpha+build", false)]
    [InlineData("2.0.0+*", "2.0.0+build", true)]
    [InlineData("2.0.0+*", "2.0.0", false)]
    [InlineData("2.0.0+*", "2.0.0-alpha+build", false)]
    [InlineData("2.0.x-*", "2.0.1-ballot", true)]
    [InlineData("2.0.x-*", "2.0.1", false)]
    [InlineData("2.0.x-*", "2.0.1-ballot+build", false)]
    [InlineData("2.0?", "2.0", true)]
    [InlineData("2.0?", "2.0.1", true)]
    [InlineData("2.0?", "2.0.1-alpha+build", true)]
    [InlineData("2.0?", "2.1", false)]
    [InlineData("2.0.1?", "2.0.1", true)]
    [InlineData("2.0.1?", "2.0.1-alpha+build", true)]
    [InlineData("2.0.1?", "2.0", false)]
    [InlineData("2.0.1?", "2.0.2", false)]
    [InlineData("2.x?", "2.0", true)]
    [InlineData("2.x?", "2.1.3-alpha+build", true)]
    [InlineData("2.x?", "3.0", false)]
    [InlineData("*.0", "1.0", true)]
    [InlineData("*.0", "1.0.0", false)]
    [InlineData("*.0.0", "1.0.0", true)]
    [InlineData("*.0.0", "1.1.0", false)]
    [InlineData("2.*.0", "2.5.0", true)]
    [InlineData("2.*.0", "2.5.1", false)]
    [InlineData("2.0.0-x", "2.0.0-x", true)]
    [InlineData("2.0.0-x", "2.0.0-alpha", false)]
    [InlineData("2.0.0+x", "2.0.0+x", true)]
    [InlineData("2.0.0+x", "2.0.0+build", false)]
    [InlineData("*", "2.0", true)]
    [InlineData("*", "2.0.0-alpha+build", true)]
    public void Satisfies_DefinedWildcardGrammar_MatchesByPart(
        string pattern,
        string candidate,
        bool expected)
    {
        FhirSemVer version = FhirSemVer.Parse(candidate);

        version.Satisfies(pattern).ShouldBe(expected);
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
    public void MaxSatisfying_PatternPrereleaseBehavior_IsExplicit()
    {
        FhirSemVer[] versions =
        [
            FhirSemVer.Parse("2.0"),
            FhirSemVer.Parse("2.0.0-alpha"),
        ];

        FhirSemVer? defaultQuestion =
            FhirSemVer.MaxSatisfying(versions, "2.0?");
        FhirSemVer? enabledQuestion =
            FhirSemVer.MaxSatisfying(versions, "2.0?", true);
        FhirSemVer? requiredPreRelease =
            FhirSemVer.MaxSatisfying(versions, "2.0.0-*");

        defaultQuestion!.ToString().ShouldBe("2.0");
        enabledQuestion!.ToString().ShouldBe("2.0.0-alpha");
        requiredPreRelease!.ToString().ShouldBe("2.0.0-alpha");
    }

    [Fact]
    public void MaxSatisfying_RangeExpression_RemainsInvalid()
    {
        FhirSemVer[] versions = [FhirSemVer.Parse("2.0.0")];

        Should.Throw<FormatException>(
            () => FhirSemVer.MaxSatisfying(versions, "^2.0.0"));
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
    public void SatisfyingRange_ComparatorsIntersect()
    {
        FhirSemVer[] versions =
        [
            FhirSemVer.Parse("2.0.0"),
            FhirSemVer.Parse("1.5.0"),
            FhirSemVer.Parse("1.0.0"),
            FhirSemVer.Parse("1.9.9"),
            FhirSemVer.Parse("0.9.9"),
        ];

        List<string> results = FhirSemVer
            .SatisfyingRange(versions, ">= 1.0.0 <2.0.0")
            .Select(version => version.ToString())
            .ToList();
        string[] expected = ["1.5.0", "1.0.0", "1.9.9"];

        results.ShouldBe(expected);
    }

    [Theory]
    [InlineData("<2.0.0", "1.0.0")]
    [InlineData("<=2.0.0", "1.0.0,2.0.0")]
    [InlineData(">2.0.0", "3.0.0")]
    [InlineData(">=2.0.0", "2.0.0,3.0.0")]
    [InlineData("=2.0.0", "2.0.0")]
    public void SatisfyingRange_ComparatorOperators(
        string expression,
        string expectedVersions)
    {
        FhirSemVer[] versions =
        [
            FhirSemVer.Parse("1.0.0"),
            FhirSemVer.Parse("2.0.0"),
            FhirSemVer.Parse("3.0.0"),
        ];

        string actual = string.Join(
            ',',
            FhirSemVer.SatisfyingRange(versions, expression));

        actual.ShouldBe(expectedVersions);
    }

    [Fact]
    public void SatisfyingRange_HyphenRangeIsInclusive()
    {
        FhirSemVer[] versions =
        [
            FhirSemVer.Parse("2.0.1"),
            FhirSemVer.Parse("1.0.0"),
            FhirSemVer.Parse("2.0.0"),
            FhirSemVer.Parse("1.5.0"),
            FhirSemVer.Parse("0.9.9"),
        ];

        List<string> results = FhirSemVer
            .SatisfyingRange(versions, "1.0.0 - 2.0.0")
            .Select(version => version.ToString())
            .ToList();
        string[] expected = ["1.0.0", "2.0.0", "1.5.0"];

        results.ShouldBe(expected);
    }

    [Theory]
    [InlineData("^1.2.3", "1.2.3", "1.9.9", "2.0.0")]
    [InlineData("^0.2.3", "0.2.3", "0.2.9", "0.3.0")]
    [InlineData("^0.0.3", "0.0.3", null, "0.0.4")]
    public void SatisfyingRange_CaretUsesFirstNonZeroCeiling(
        string expression,
        string lowerVersion,
        string? includedVersion,
        string ceilingVersion)
    {
        List<FhirSemVer> versions = [FhirSemVer.Parse(lowerVersion)];
        if (includedVersion is not null)
            versions.Add(FhirSemVer.Parse(includedVersion));
        versions.Add(FhirSemVer.Parse(ceilingVersion));

        List<FhirSemVer> results = FhirSemVer.SatisfyingRange(versions, expression).ToList();

        results.ShouldContain(FhirSemVer.Parse(lowerVersion));
        if (includedVersion is not null)
            results.ShouldContain(FhirSemVer.Parse(includedVersion));
        results.ShouldNotContain(FhirSemVer.Parse(ceilingVersion));
    }

    [Fact]
    public void SatisfyingRange_Pipe_PreservesCandidateOrder()
    {
        FhirSemVer[] versions =
        [
            FhirSemVer.Parse("3.0.0"),
            FhirSemVer.Parse("2.0.0"),
            FhirSemVer.Parse("1.0.0"),
        ];

        List<string> results = FhirSemVer
            .SatisfyingRange(versions, "1.0.0|3.0.0")
            .Select(version => version.ToString())
            .ToList();
        string[] expected = ["3.0.0", "1.0.0"];

        results.ShouldBe(expected);
    }

    [Theory]
    [InlineData("^2.0.0", "1.0.0", false, false)]
    [InlineData("^2.0.0", "3.0.0", false, true)]
    [InlineData("2.x.x", "3.0.0", false, true)]
    [InlineData(
        ">1.0.0-alpha <1.0.0-rc",
        "2.0.0",
        true,
        true)]
    public void Range_HasSatisfyingVersionAtOrBelow_UsesBounds(
        string expression,
        string ceiling,
        bool allowPreRelease,
        bool expected)
    {
        FhirSemVerRange range =
            FhirSemVerRange.Parse(expression);

        bool actual =
            range.HasSatisfyingVersionAtOrBelow(
                FhirSemVer.Parse(ceiling),
                allowPreRelease);

        actual.ShouldBe(expected);
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
