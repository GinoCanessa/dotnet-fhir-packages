// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;
using FhirPkg.Registry.CiBuild;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Registry.CiBuild;

public class CiBuildCanonicalRepositorySelectorTests
{
    [Fact]
    public async Task SelectAsync_EmptyCandidates_ReturnsNull()
    {
        RecordingFactsProvider provider = new();
        CiBuildCanonicalRepositorySelector selector = Create(provider);

        CiBuildRepositorySelection? selection = await selector.SelectAsync(
            "hl7.fhir.uv.subscriptions-backport",
            [],
            TestContext.Current.CancellationToken);

        selection.ShouldBeNull();
        provider.Queried.ShouldBeEmpty();
    }

    [Fact]
    public async Task SelectAsync_SingleRepository_ShortCircuitsWithoutGitHubQuery()
    {
        RecordingFactsProvider provider = new();
        CiBuildCanonicalRepositorySelector selector = Create(provider);

        CiBuildRepositorySelection? selection = await selector.SelectAsync(
            "example.package",
            [
                Candidate("someorg", "example-ig", "master", "2026-01-01T00:00:00+00:00"),
                Candidate("someorg", "example-ig", "feature", "2026-06-01T00:00:00+00:00"),
            ],
            TestContext.Current.CancellationToken);

        selection.ShouldNotBeNull();
        selection.Repository.ShouldBe(new CiBuildRepositoryIdentity("someorg", "example-ig"));
        selection.Tier.ShouldBe(CiBuildSelectionTier.SoleRepository);
        provider.Queried.ShouldBeEmpty();
    }

    [Fact]
    public async Task SelectAsync_ReportedPackage_PicksHl7OverForkWithoutGitHubQuery()
    {
        RecordingFactsProvider provider = new();
        CiBuildCanonicalRepositorySelector selector = Create(provider);

        CiBuildRepositorySelection? selection = await selector.SelectAsync(
            "hl7.fhir.uv.subscriptions-backport",
            [
                Candidate("HL7", "fhir-subscription-backport-ig", "master", "20240617160736"),
                Candidate("jkiddo", "fhir-subscription-backport-ig", "fixing-missing-extensions", "2026-06-12T10:34:38-05:00"),
            ],
            TestContext.Current.CancellationToken);

        selection.ShouldNotBeNull();
        selection.Repository.ShouldBe(new CiBuildRepositoryIdentity("HL7", "fhir-subscription-backport-ig"));
        selection.Tier.ShouldBe(CiBuildSelectionTier.PrefixTable);
        provider.Queried.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ch.fhir.ig.ch-emr", "hl7ch", "ch-emr")]
    [InlineData("hl7.ehrs.uv.ehrsfmr2", "HL7", "ehrs-fm-r2")]
    [InlineData("hl7.fhir.uv.cgm", "HL7", "cgm")]
    public async Task SelectAsync_PrefixTableOutranksForkCheck(
        string packageId,
        string canonicalOrg,
        string repoName)
    {
        // The competing repository is both older and not a fork, so a fork-check-only
        // rule would choose it. The prefix table must win before tier 2 is consulted.
        RecordingFactsProvider provider = new();
        provider.Facts[new CiBuildRepositoryIdentity("someone-else", repoName)] =
            new GitHubRepositoryFacts { IsFork = false };
        provider.Facts[new CiBuildRepositoryIdentity(canonicalOrg, repoName)] =
            new GitHubRepositoryFacts { IsFork = false };

        CiBuildCanonicalRepositorySelector selector = Create(provider);

        CiBuildRepositorySelection? selection = await selector.SelectAsync(
            packageId,
            [
                Candidate("someone-else", repoName, "main", "2020-01-01T00:00:00+00:00"),
                Candidate(canonicalOrg, repoName, "master", "2026-01-01T00:00:00+00:00"),
            ],
            TestContext.Current.CancellationToken);

        selection.ShouldNotBeNull();
        selection.Repository.ShouldBe(new CiBuildRepositoryIdentity(canonicalOrg, repoName));
        selection.Tier.ShouldBe(CiBuildSelectionTier.PrefixTable);
        provider.Queried.ShouldBeEmpty();
    }

    [Fact]
    public async Task SelectAsync_TableOrganizationAbsent_FallsThroughToNonForkCheck()
    {
        RecordingFactsProvider provider = new();
        provider.Facts[new CiBuildRepositoryIdentity("forker", "example-ig")] =
            new GitHubRepositoryFacts { IsFork = true, ParentFullName = "upstream/example-ig" };
        provider.Facts[new CiBuildRepositoryIdentity("upstream", "example-ig")] =
            new GitHubRepositoryFacts { IsFork = false };

        CiBuildCanonicalRepositorySelector selector = Create(provider);

        // "hl7." names HL7, which published nothing here.
        CiBuildRepositorySelection? selection = await selector.SelectAsync(
            "hl7.fhir.uv.example",
            [
                Candidate("forker", "example-ig", "main", "2020-01-01T00:00:00+00:00"),
                Candidate("upstream", "example-ig", "main", "2021-01-01T00:00:00+00:00"),
            ],
            TestContext.Current.CancellationToken);

        selection.ShouldNotBeNull();
        selection.Repository.ShouldBe(new CiBuildRepositoryIdentity("upstream", "example-ig"));
        selection.Tier.ShouldBe(CiBuildSelectionTier.NonForkCheck);
        provider.Queried.ShouldBe(
        [
            new CiBuildRepositoryIdentity("forker", "example-ig"),
            new CiBuildRepositoryIdentity("upstream", "example-ig"),
        ]);
    }

    [Fact]
    public async Task SelectAsync_TableOrganizationWithTwoRepositories_PicksNewerBuild()
    {
        RecordingFactsProvider provider = new();
        CiBuildCanonicalRepositorySelector selector = Create(provider);

        CiBuildRepositorySelection? selection = await selector.SelectAsync(
            "hl7.fhir.uv.example",
            [
                Candidate("HL7", "example-ig-old", "master", "2020-01-01T00:00:00+00:00"),
                Candidate("HL7", "example-ig-new", "master", "2026-01-01T00:00:00+00:00"),
                Candidate("forker", "example-ig-old", "main", "2026-06-01T00:00:00+00:00"),
            ],
            TestContext.Current.CancellationToken);

        selection.ShouldNotBeNull();
        selection.Repository.ShouldBe(new CiBuildRepositoryIdentity("HL7", "example-ig-new"));
        selection.Tier.ShouldBe(CiBuildSelectionTier.PrefixTable);
        provider.Queried.ShouldBeEmpty();
    }

    [Fact]
    public async Task SelectAsync_AllFactsUnavailable_FallsThroughToOldest()
    {
        RecordingFactsProvider provider = new();
        CiBuildCanonicalRepositorySelector selector = Create(provider);

        CiBuildRepositorySelection? selection = await selector.SelectAsync(
            "example.package",
            [
                Candidate("newer-org", "example-ig", "main", "2026-01-01T00:00:00+00:00"),
                Candidate("older-org", "example-ig", "main", "2020-01-01T00:00:00+00:00"),
            ],
            TestContext.Current.CancellationToken);

        selection.ShouldNotBeNull();
        selection.Repository.ShouldBe(new CiBuildRepositoryIdentity("older-org", "example-ig"));
        selection.Tier.ShouldBe(CiBuildSelectionTier.Oldest);
        provider.Queried.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SelectAsync_NullBuildDate_NeverWinsTheOldestComparison()
    {
        RecordingFactsProvider provider = new();
        CiBuildCanonicalRepositorySelector selector = Create(provider);

        CiBuildRepositorySelection? selection = await selector.SelectAsync(
            "example.package",
            [
                Candidate("undated-org", "example-ig", "main", "not a date at all"),
                Candidate("dated-org", "example-ig", "main", "2026-01-01T00:00:00+00:00"),
            ],
            TestContext.Current.CancellationToken);

        selection.ShouldNotBeNull();
        selection.Repository.ShouldBe(new CiBuildRepositoryIdentity("dated-org", "example-ig"));
        selection.Tier.ShouldBe(CiBuildSelectionTier.Oldest);
        provider.Queried[0].ShouldBe(new CiBuildRepositoryIdentity("dated-org", "example-ig"));
    }

    [Fact]
    public void CanonicalOrganizationTable_RulesAreOrderedByDescendingPrefixLength()
    {
        IReadOnlyList<(string Prefix, string Organization)> rules = CanonicalOrganizationTable.Rules;

        rules.Count.ShouldBe(11);

        for (int i = 1; i < rules.Count; i++)
        {
            rules[i].Prefix.Length.ShouldBeLessThanOrEqualTo(
                rules[i - 1].Prefix.Length,
                $"Rule '{rules[i].Prefix}' must not precede the longer prefix '{rules[i - 1].Prefix}'.");
        }
    }

    [Theory]
    [InlineData("hl7.fhir.au.core", "hl7au")]
    [InlineData("hl7.fhir.be.core", "hl7-be")]
    [InlineData("hl7.fhir.eu.extensions", "hl7-eu")]
    [InlineData("ch.fhir.ig.ch-emr", "hl7ch")]
    [InlineData("hl7se.fhir.base", "HL7Sweden")]
    [InlineData("smart.who.int.base", "WorldHealthOrganization")]
    [InlineData("org.sql-on-fhir.ig", "FHIR")]
    [InlineData("openehr.base", "FHIR")]
    [InlineData("zw.fhir.ig.core", "mohcc")]
    [InlineData("et.fhir.ig.core", "MoH-Ethiopia")]
    [InlineData("hl7.fhir.uv.subscriptions-backport", "HL7")]
    public void CanonicalOrganizationTable_MapsValidatedPrefixes(string packageId, string expected)
    {
        CanonicalOrganizationTable.TryGetOrganization(packageId, out string organization).ShouldBeTrue();
        organization.ShouldBe(expected);
    }

    [Theory]
    [InlineData("hl7.fhir.ch.something")]
    [InlineData("fhir.ph.core")]
    [InlineData("")]
    public void CanonicalOrganizationTable_RejectedAndUnknownPrefixesDoNotMatch(string packageId)
    {
        // "hl7.fhir.ch." and "fhir.ph." were explicitly rejected as rules; the first
        // still matches the "hl7." catch-all, the second matches nothing.
        bool matched = CanonicalOrganizationTable.TryGetOrganization(packageId, out string organization);

        if (packageId.StartsWith("hl7.", StringComparison.OrdinalIgnoreCase))
        {
            matched.ShouldBeTrue();
            organization.ShouldBe("HL7");
        }
        else
        {
            matched.ShouldBeFalse();
        }
    }

    private static CiBuildCanonicalRepositorySelector Create(IGitHubRepositoryFactsProvider provider) =>
        new(provider, NullLogger.Instance);

    private static CiBuildCandidate Candidate(string org, string repo, string branch, string date) =>
        CiBuildCandidate.TryCreate(new CiBuildRecord
        {
            PackageId = "test.package",
            Repo = $"{org}/{repo}/branches/{branch}/qa.json",
            Date = date,
        })!;

    private sealed class RecordingFactsProvider : IGitHubRepositoryFactsProvider
    {
        public Dictionary<CiBuildRepositoryIdentity, GitHubRepositoryFacts?> Facts { get; } = [];

        public List<CiBuildRepositoryIdentity> Queried { get; } = [];

        public Task<GitHubRepositoryFacts?> TryGetFactsAsync(
            CiBuildRepositoryIdentity repository,
            CancellationToken cancellationToken)
        {
            Queried.Add(repository);

            return Task.FromResult(
                Facts.TryGetValue(repository, out GitHubRepositoryFacts? facts) ? facts : null);
        }
    }
}
