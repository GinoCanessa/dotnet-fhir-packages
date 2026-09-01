// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using FhirPkg.Models;
using FhirPkg.Registry.CiBuild;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Registry.CiBuild;

public class CiBuildArtifactResolverTests
{
    private const string BaseUrl = "https://build.fhir.org";
    private const string RepoName = "fhir-subscription-backport-ig";

    private static readonly CiBuildRepositoryIdentity Hl7Repository = new("HL7", RepoName);

    [Fact]
    public async Task ResolveAsync_ManifestBackedDefaultBuild_UsesShortUrlAndManifestMetadata()
    {
        FakeManifestSource source = new(new CiBuildManifest
        {
            Name = "hl7.fhir.uv.subscriptions-backport",
            Version = "1.2.0-ballot",
            Date = "20240617160736",
            FhirVersion = ["4.0.1"],
        });

        CiBuildArtifactResolver resolver = Create(source);

        CiBuildArtifactLocation? location = await resolver.ResolveAsync(
            Hl7Repository,
            [Candidate("HL7", RepoName, "master", "2024-06-17T16:07:36+00:00", igVersion: "1.2.0-ballot")],
            requestedBranch: null,
            TestContext.Current.CancellationToken);

        location.ShouldNotBeNull();
        location.TarballUri.ShouldBe(
            new Uri("https://build.fhir.org/ig/HL7/fhir-subscription-backport-ig/package.tgz"));
        location.Version.ShouldBe("1.2.0-ballot");
        location.PublicationDate.ShouldNotBeNull();
        location.PublicationDate.Value.UtcDateTime.ShouldBe(
            new DateTime(2024, 6, 17, 16, 7, 36, DateTimeKind.Utc));
        location.FhirVersions.ShouldBe(["4.0.1"]);
        location.Branch.ShouldBeNull();
        location.IsDefaultBuild.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_ManifestUnavailable_FallsBackToNewestBranchBuild()
    {
        CiBuildArtifactResolver resolver = Create(new FakeManifestSource(null));

        CiBuildArtifactLocation? location = await resolver.ResolveAsync(
            Hl7Repository,
            [
                Candidate("HL7", RepoName, "old-branch", "2020-01-01T00:00:00+00:00"),
                Candidate("HL7", RepoName, "master", "2024-06-17T16:07:36+00:00", igVersion: "1.2.0-ballot"),
            ],
            requestedBranch: null,
            TestContext.Current.CancellationToken);

        location.ShouldNotBeNull();
        location.TarballUri.ShouldBe(
            new Uri("https://build.fhir.org/ig/HL7/fhir-subscription-backport-ig/branches/master/package.tgz"));
        location.Version.ShouldBe("1.2.0-ballot");
        location.Branch.ShouldBe("master");
        location.IsDefaultBuild.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_ManifestWithNullVersion_TakesTheSameFallback()
    {
        // Relaxing `required` on CiBuildManifest.Version makes a partial manifest
        // deserializable; it must not be treated as a usable default build.
        FakeManifestSource source = new(new CiBuildManifest
        {
            Name = "hl7.fhir.uv.subscriptions-backport",
            Date = "20240617160736",
        });

        CiBuildArtifactResolver resolver = Create(source);

        CiBuildArtifactLocation? location = await resolver.ResolveAsync(
            Hl7Repository,
            [Candidate("HL7", RepoName, "master", "2024-06-17T16:07:36+00:00", igVersion: "1.2.0-ballot")],
            requestedBranch: null,
            TestContext.Current.CancellationToken);

        location.ShouldNotBeNull();
        location.TarballUri.ShouldBe(
            new Uri("https://build.fhir.org/ig/HL7/fhir-subscription-backport-ig/branches/master/package.tgz"));
        location.IsDefaultBuild.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_ManifestWithOnlySingularFhirVersion_PopulatesFhirVersions()
    {
        CiBuildManifest manifest = JsonSerializer.Deserialize<CiBuildManifest>(
            """
            { "name": "example", "version": "1.0.0", "date": "20240617160736", "fhirVersion": ["4.0.1"] }
            """)!;

        manifest.FhirVersions.ShouldBeNull();
        manifest.EffectiveFhirVersions.ShouldBe(["4.0.1"]);

        CiBuildArtifactResolver resolver = Create(new FakeManifestSource(manifest));

        CiBuildArtifactLocation? location = await resolver.ResolveAsync(
            Hl7Repository,
            [Candidate("HL7", RepoName, "master", "2024-06-17T16:07:36+00:00")],
            requestedBranch: null,
            TestContext.Current.CancellationToken);

        location.ShouldNotBeNull();
        location.FhirVersions.ShouldBe(["4.0.1"]);
    }

    [Fact]
    public async Task ResolveAsync_ManifestUnavailableAndNoCandidateInRepository_ReturnsNull()
    {
        CiBuildArtifactResolver resolver = Create(new FakeManifestSource(null));

        CiBuildArtifactLocation? location = await resolver.ResolveAsync(
            Hl7Repository,
            [Candidate("jkiddo", RepoName, "fixing-missing-extensions", "2026-06-12T10:34:38-05:00")],
            requestedBranch: null,
            TestContext.Current.CancellationToken);

        location.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_ExplicitBranch_UsesBranchUrlAndSkipsTheManifest()
    {
        FakeManifestSource source = new(new CiBuildManifest { Version = "1.2.0-ballot" });
        CiBuildArtifactResolver resolver = Create(source);

        CiBuildArtifactLocation? location = await resolver.ResolveAsync(
            new CiBuildRepositoryIdentity("jkiddo", RepoName),
            [
                Candidate("jkiddo", RepoName, "fixing-missing-extensions", "2026-06-12T10:34:38-05:00", igVersion: "1.1.0"),
            ],
            requestedBranch: "fixing-missing-extensions",
            TestContext.Current.CancellationToken);

        location.ShouldNotBeNull();
        location.TarballUri.ShouldBe(new Uri(
            "https://build.fhir.org/ig/jkiddo/fhir-subscription-backport-ig/branches/fixing-missing-extensions/package.tgz"));
        location.Version.ShouldBe("1.1.0");
        location.Branch.ShouldBe("fixing-missing-extensions");
        location.IsDefaultBuild.ShouldBeFalse();
        source.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResolveAsync_ExplicitBranchWithNoMatchingCandidate_ReturnsNull()
    {
        CiBuildArtifactResolver resolver = Create(new FakeManifestSource(null));

        CiBuildArtifactLocation? location = await resolver.ResolveAsync(
            Hl7Repository,
            [Candidate("HL7", RepoName, "master", "2024-06-17T16:07:36+00:00")],
            requestedBranch: "no-such-branch",
            TestContext.Current.CancellationToken);

        location.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_BranchNeedingEscaping_IsEscapedExactlyOnce()
    {
        CiBuildArtifactResolver resolver = Create(new FakeManifestSource(null));

        CiBuildArtifactLocation? location = await resolver.ResolveAsync(
            Hl7Repository,
            [Candidate("HL7", RepoName, "my branch+1", "2024-06-17T16:07:36+00:00")],
            requestedBranch: "my branch+1",
            TestContext.Current.CancellationToken);

        location.ShouldNotBeNull();
        location.TarballUri.AbsoluteUri.ShouldBe(
            "https://build.fhir.org/ig/HL7/fhir-subscription-backport-ig/branches/my%20branch%2B1/package.tgz");
    }

    [Fact]
    public async Task ResolveAsync_ManifestFallbackPrefersNewestByInstantNotString()
    {
        CiBuildArtifactResolver resolver = Create(new FakeManifestSource(null));

        CiBuildArtifactLocation? location = await resolver.ResolveAsync(
            Hl7Repository,
            [
                Candidate("HL7", RepoName, "undated", "not a date at all"),
                Candidate("HL7", RepoName, "rfc-shaped", "Fri, 12 Jun, 2024 15:34:38 +0000"),
                Candidate("HL7", RepoName, "compact", "20260617160736"),
            ],
            requestedBranch: null,
            TestContext.Current.CancellationToken);

        location.ShouldNotBeNull();
        location.Branch.ShouldBe("compact");
    }

    private static CiBuildArtifactResolver Create(ICiBuildManifestSource source) =>
        new(BaseUrl, source, NullLogger.Instance);

    private static CiBuildCandidate Candidate(
        string org,
        string repo,
        string branch,
        string date,
        string? igVersion = null,
        string? fhirVersion = null) =>
        CiBuildCandidate.TryCreate(new CiBuildRecord
        {
            PackageId = "hl7.fhir.uv.subscriptions-backport",
            Repo = $"{org}/{repo}/branches/{branch}/qa.json",
            Date = date,
            IgVersion = igVersion,
            FhirVersion = fhirVersion,
        })!;

    private sealed class FakeManifestSource(CiBuildManifest? manifest) : ICiBuildManifestSource
    {
        public int CallCount { get; private set; }

        public Task<CiBuildManifest?> TryGetDefaultBuildManifestAsync(
            CiBuildRepositoryIdentity repository,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(manifest);
        }
    }
}
