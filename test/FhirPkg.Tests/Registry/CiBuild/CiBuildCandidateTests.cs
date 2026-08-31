// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;
using FhirPkg.Registry.CiBuild;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Registry.CiBuild;

public class CiBuildCandidateTests
{
    [Fact]
    public void TryCreate_BranchesForm_ParsesOrgRepoAndBranch()
    {
        CiBuildRecord record = CreateRecord(
            repo: "HL7/fhir-subscription-backport-ig/branches/master/qa.json",
            date: "2026-06-12T10:34:38-05:00");

        CiBuildCandidate? candidate = CiBuildCandidate.TryCreate(record);

        candidate.ShouldNotBeNull();
        candidate.Repository.Org.ShouldBe("HL7");
        candidate.Repository.RepoName.ShouldBe("fhir-subscription-backport-ig");
        candidate.Branch.ShouldBe("master");
        candidate.Record.ShouldBeSameAs(record);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HL7")]
    [InlineData("HL7/only-two-segments")]
    public void TryCreate_MalformedRepo_ReturnsNull(string repo)
    {
        CiBuildCandidate? candidate = CiBuildCandidate.TryCreate(
            CreateRecord(repo: repo, date: "2026-06-12T10:34:38-05:00"));

        candidate.ShouldBeNull();
    }

    [Fact]
    public void TryCreate_PrefersDateISO8601OverDate()
    {
        CiBuildRecord record = CreateRecord(
            repo: "HL7/example/branches/main/qa.json",
            date: "20200101000000",
            dateIso8601: "2026-06-12T10:34:38-05:00");

        CiBuildCandidate? candidate = CiBuildCandidate.TryCreate(record);

        candidate.ShouldNotBeNull();
        candidate.BuildDate.ShouldNotBeNull();
        candidate.BuildDate.Value.UtcDateTime.ShouldBe(new DateTime(2026, 6, 12, 15, 34, 38, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("2026-06-12T10:34:38-05:00", 2026, 6, 12, 15, 34, 38)]
    [InlineData("Fri, 12 Jun, 2026 15:34:38 +0000", 2026, 6, 12, 15, 34, 38)]
    [InlineData("20240617160736", 2024, 6, 17, 16, 7, 36)]
    public void TryParse_LiveDateShapes_ParseToExpectedInstant(
        string value,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second)
    {
        bool parsed = CiBuildDate.TryParse(value, out DateTimeOffset result);

        parsed.ShouldBeTrue();
        result.UtcDateTime.ShouldBe(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date at all")]
    public void TryParse_UnparseableValue_ReturnsFalse(string? value)
    {
        CiBuildDate.TryParse(value, out DateTimeOffset result).ShouldBeFalse();
        result.ShouldBe(default);
    }

    [Fact]
    public void TryCreate_UnparseableDate_YieldsNullBuildDateWithoutThrowing()
    {
        CiBuildCandidate? candidate = CiBuildCandidate.TryCreate(
            CreateRecord(repo: "HL7/example/branches/main/qa.json", date: "not a date at all"));

        candidate.ShouldNotBeNull();
        candidate.BuildDate.ShouldBeNull();
    }

    [Fact]
    public void BuildDate_OrdersMixedFormatsChronologically_NotLexically()
    {
        // "Fri, ..." sorts after "2026..." in an ordinal string comparison, so a
        // descending string sort picks the older RFC-shaped record. Typed dates do not.
        CiBuildCandidate older = CiBuildCandidate.TryCreate(
            CreateRecord(repo: "HL7/example/branches/main/qa.json", date: "Fri, 12 Jun, 2024 15:34:38 +0000"))!;
        CiBuildCandidate newer = CiBuildCandidate.TryCreate(
            CreateRecord(repo: "HL7/example/branches/release/qa.json", date: "20260617160736"))!;

        List<CiBuildCandidate> candidates = [older, newer];

        string lexicalWinnerBranch = candidates
            .OrderByDescending(c => c.Record.DateISO8601 ?? c.Record.Date, StringComparer.Ordinal)
            .First()
            .Branch;
        lexicalWinnerBranch.ShouldBe("main");

        CiBuildCandidate chronologicalWinner = candidates
            .OrderByDescending(c => c.BuildDate ?? DateTimeOffset.MinValue)
            .First();
        chronologicalWinner.Branch.ShouldBe("release");
    }

    [Fact]
    public void RepositoryIdentity_EqualityAndHashing_AreCaseInsensitive()
    {
        CiBuildRepositoryIdentity left = new("HL7", "fhir-subscription-backport-ig");
        CiBuildRepositoryIdentity right = new("hl7", "FHIR-Subscription-Backport-IG");

        left.ShouldBe(right);
        left.GetHashCode().ShouldBe(right.GetHashCode());

        HashSet<CiBuildRepositoryIdentity> set = [left, right];
        set.Count.ShouldBe(1);
    }

    [Fact]
    public void RepositoryIdentity_ToStringAndUrlPath_EscapeEachSegmentOnce()
    {
        CiBuildRepositoryIdentity identity = new("hl7 org", "repo/name");

        identity.ToString().ShouldBe("hl7 org/repo/name");
        identity.ToUrlPath().ShouldBe("hl7%20org/repo%2Fname");
    }

    private static CiBuildRecord CreateRecord(
        string repo,
        string date,
        string? dateIso8601 = null,
        string packageId = "hl7.fhir.uv.subscriptions-backport") =>
        new()
        {
            PackageId = packageId,
            Repo = repo,
            Date = date,
            DateISO8601 = dateIso8601,
        };
}
