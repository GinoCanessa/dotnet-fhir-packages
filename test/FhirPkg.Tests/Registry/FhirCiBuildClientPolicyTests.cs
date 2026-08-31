// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using FhirPkg.Models;
using FhirPkg.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Registry;

public class FhirCiBuildClientPolicyTests
{
    private const string PackageId = "hl7.fhir.uv.subscriptions-backport";

    /// <summary>The two live qas.json records from the reported defect.</summary>
    private const string SubscriptionsBackportQas = """
        [
          {
            "url": "https://build.fhir.org/ig/HL7/fhir-subscription-backport-ig",
            "name": "Subscriptions R5 Backport",
            "package-id": "hl7.fhir.uv.subscriptions-backport",
            "ig-ver": "1.2.0-ballot",
            "date": "Mon, 17 Jun, 2024 16:07:36 +0000",
            "dateISO8601": "2024-06-17T16:07:36+00:00",
            "repo": "HL7/fhir-subscription-backport-ig/branches/master/qa.json",
            "fhir-version": "4.0.1"
          },
          {
            "url": "https://build.fhir.org/ig/jkiddo/fhir-subscription-backport-ig",
            "name": "Subscriptions R5 Backport",
            "package-id": "hl7.fhir.uv.subscriptions-backport",
            "ig-ver": "1.1.0",
            "date": "Fri, 12 Jun, 2026 15:34:38 +0000",
            "dateISO8601": "2026-06-12T15:34:38+00:00",
            "repo": "jkiddo/fhir-subscription-backport-ig/branches/fixing-missing-extensions/qa.json",
            "fhir-version": "4.0.1"
          }
        ]
        """;

    private const string SubscriptionsBackportManifest = """
        {
          "name": "hl7.fhir.uv.subscriptions-backport",
          "version": "1.2.0-ballot",
          "date": "20240617160736",
          "fhirVersion": ["4.0.1"]
        }
        """;

    private const string ContestedQas = """
        [
          {
            "package-id": "example.ig.core",
            "ig-ver": "0.1.0",
            "date": "2020-01-01T00:00:00+00:00",
            "dateISO8601": "2020-01-01T00:00:00+00:00",
            "repo": "first-org/example-ig/branches/main/qa.json"
          },
          {
            "package-id": "example.ig.core",
            "ig-ver": "0.2.0",
            "date": "2026-01-01T00:00:00+00:00",
            "dateISO8601": "2026-01-01T00:00:00+00:00",
            "repo": "second-org/example-ig/branches/main/qa.json"
          }
        ]
        """;

    private const string ReleaseFilteredQas = """
        [
          {
            "package-id": "hl7.fhir.uv.example",
            "ig-ver": "1.0.0",
            "date": "2020-01-01T00:00:00+00:00",
            "dateISO8601": "2020-01-01T00:00:00+00:00",
            "repo": "HL7/example-ig/branches/master/qa.json",
            "fhir-version": "4.0.1"
          },
          {
            "package-id": "hl7.fhir.uv.example",
            "ig-ver": "2.0.0",
            "date": "2026-01-01T00:00:00+00:00",
            "dateISO8601": "2026-01-01T00:00:00+00:00",
            "repo": "forker/example-ig/branches/feature/qa.json",
            "fhir-version": "5.0.0"
          }
        ]
        """;

    [Theory]
    [InlineData("hl7.fhir.r4.core", FhirRelease.R5)]
    [InlineData("hl7.fhir.r7.core", FhirRelease.R4)]
    public async Task ResolveAsync_CorePackageRequiresMatchingKnownRelease(
        string packageId,
        FhirRelease preferredRelease)
    {
        FhirCiBuildClient client = new(
            new HttpClient(),
            RegistryEndpoint.FhirCiBuild,
            NullLogger<FhirCiBuildClient>.Instance);

        ResolvedDirective? result = await client.ResolveAsync(
            PackageDirective.Parse($"{packageId}#current"),
            new VersionResolveOptions { FhirRelease = preferredRelease },
            TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_ReportedRegression_PicksCanonicalDefaultBuild()
    {
        RoutingHandler handler = new()
        {
            Qas = SubscriptionsBackportQas,
            Manifests =
            {
                ["HL7/fhir-subscription-backport-ig"] = SubscriptionsBackportManifest,
            },
        };

        FhirCiBuildClient client = CreateClient(handler);

        ResolvedDirective? result = await client.ResolveAsync(
            PackageDirective.Parse($"{PackageId}#current"),
            options: null,
            TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.TarballUri.ShouldBe(
            new Uri("https://build.fhir.org/ig/HL7/fhir-subscription-backport-ig/package.tgz"));
        result.Reference.Version.ShouldBe("1.2.0-ballot");
        result.PublicationDate.ShouldBe(new DateTime(2024, 6, 17, 16, 7, 36, DateTimeKind.Utc));
        result.FhirVersions.ShouldBe(["4.0.1"]);
        result.ResolutionWarnings.ShouldBeNull();
        handler.GitHubRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ExplicitBranch_ResolvesForkBranchAndWarns()
    {
        RoutingHandler handler = new()
        {
            Qas = SubscriptionsBackportQas,
            Manifests =
            {
                ["HL7/fhir-subscription-backport-ig"] = SubscriptionsBackportManifest,
            },
        };

        FhirCiBuildClient client = CreateClient(handler);

        ResolvedDirective? result = await client.ResolveAsync(
            PackageDirective.Parse($"{PackageId}#current$fixing-missing-extensions"),
            options: null,
            TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.TarballUri.ShouldBe(new Uri(
            "https://build.fhir.org/ig/jkiddo/fhir-subscription-backport-ig/branches/fixing-missing-extensions/package.tgz"));
        result.Reference.Version.ShouldBe("1.1.0");

        // publisherCount is measured before the branch filter, so it survives a filter
        // that leaves exactly one organization.
        result.ResolutionWarnings.ShouldNotBeNull();
        result.ResolutionWarnings.ShouldHaveSingleItem();
        result.ResolutionWarnings[0].ShouldContain("jkiddo/fhir-subscription-backport-ig");
        result.ResolutionWarnings[0].ShouldContain("2 organizations");
    }

    [Fact]
    public async Task ResolveAsync_MissingManifest_FallsBackToBranchUrlWithWarning()
    {
        RoutingHandler handler = new() { Qas = SubscriptionsBackportQas };

        FhirCiBuildClient client = CreateClient(handler);

        ResolvedDirective? result = await client.ResolveAsync(
            PackageDirective.Parse($"{PackageId}#current"),
            options: null,
            TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.TarballUri.ShouldBe(new Uri(
            "https://build.fhir.org/ig/HL7/fhir-subscription-backport-ig/branches/master/package.tgz"));
        result.ResolutionWarnings.ShouldNotBeNull();
        result.ResolutionWarnings.ShouldHaveSingleItem();
        result.ResolutionWarnings[0].ShouldContain("not the canonical repository's default build");
    }

    [Fact]
    public async Task ResolveAsync_ManifestServerError_TakesTheSameFallbackWithoutThrowing()
    {
        RoutingHandler handler = new()
        {
            Qas = SubscriptionsBackportQas,
            ManifestStatusCode = HttpStatusCode.InternalServerError,
        };

        FhirCiBuildClient client = CreateClient(handler);

        ResolvedDirective? result = await client.ResolveAsync(
            PackageDirective.Parse($"{PackageId}#current"),
            options: null,
            TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.TarballUri.ShouldBe(new Uri(
            "https://build.fhir.org/ig/HL7/fhir-subscription-backport-ig/branches/master/package.tgz"));
        result.ResolutionWarnings.ShouldNotBeNull();
    }

    [Fact]
    public async Task ResolveAsync_TierThreeSelection_WarnsNamingOrgRepoAndBranch()
    {
        // No prefix rule names these organizations and GitHub answers 403 for both,
        // so selection falls through to tier 3.
        RoutingHandler handler = new()
        {
            Qas = ContestedQas,
            GitHubStatusCode = HttpStatusCode.Forbidden,
        };

        FhirCiBuildClient client = CreateClient(handler);

        ResolvedDirective? result = await client.ResolveAsync(
            PackageDirective.Parse("example.ig.core#current"),
            options: null,
            TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.ResolutionWarnings.ShouldNotBeNull();

        List<string> tierWarnings = result.ResolutionWarnings
            .Where(w => w.Contains("oldest CI build"))
            .ToList();
        string tierWarning = tierWarnings.ShouldHaveSingleItem();
        tierWarning.ShouldContain("first-org");
        tierWarning.ShouldContain("example-ig");
        tierWarning.ShouldContain("main");
        handler.GitHubRequests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ResolveAsync_ReleaseFilterRemovesCanonicalOrganization_ResolvesForkAndWarns()
    {
        RoutingHandler handler = new() { Qas = ReleaseFilteredQas };
        FhirCiBuildClient client = CreateClient(handler);

        ResolvedDirective? result = await client.ResolveAsync(
            PackageDirective.Parse("hl7.fhir.uv.example#current"),
            new VersionResolveOptions { FhirRelease = FhirRelease.R5 },
            TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.TarballUri.ShouldBe(new Uri(
            "https://build.fhir.org/ig/forker/example-ig/branches/feature/package.tgz"));
        result.ResolutionWarnings.ShouldNotBeNull();
        result.ResolutionWarnings.ShouldContain(w =>
            w.Contains("canonical organization 'HL7'") && w.Contains("requested FHIR release"));
    }

    [Fact]
    public async Task ResolveAsync_ReleaseFilterWithExplicitBranch_EmitsNoCanonicalOrganizationWarning()
    {
        RoutingHandler handler = new() { Qas = ReleaseFilteredQas };
        FhirCiBuildClient client = CreateClient(handler);

        ResolvedDirective? result = await client.ResolveAsync(
            PackageDirective.Parse("hl7.fhir.uv.example#current$feature"),
            new VersionResolveOptions { FhirRelease = FhirRelease.R5 },
            TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        (result.ResolutionWarnings ?? []).ShouldNotContain(w => w.Contains("requested FHIR release"));
    }

    [Fact]
    public async Task ResolveAsync_SecondResolution_ServesQasFromCache()
    {
        RoutingHandler handler = new()
        {
            Qas = SubscriptionsBackportQas,
            Manifests =
            {
                ["HL7/fhir-subscription-backport-ig"] = SubscriptionsBackportManifest,
            },
        };

        FhirCiBuildClient client = CreateClient(handler);
        PackageDirective directive = PackageDirective.Parse($"{PackageId}#current");

        await client.ResolveAsync(directive, options: null, TestContext.Current.CancellationToken);
        await client.ResolveAsync(directive, options: null, TestContext.Current.CancellationToken);

        handler.QasRequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task ResolveAsync_UnknownPackageId_ReturnsNull()
    {
        RoutingHandler handler = new() { Qas = SubscriptionsBackportQas };
        FhirCiBuildClient client = CreateClient(handler);

        ResolvedDirective? result = await client.ResolveAsync(
            PackageDirective.Parse("no.such.package#current"),
            options: null,
            TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    private static FhirCiBuildClient CreateClient(RoutingHandler handler) =>
        new(
            RegistryHttpTransport.CreateRedirectControlled(
                new HttpClient(handler),
                TimeSpan.FromSeconds(30),
                maxRedirects: 5),
            RegistryEndpoint.FhirCiBuild,
            NullLogger<FhirCiBuildClient>.Instance);

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public string Qas { get; init; } = "[]";

        public Dictionary<string, string> Manifests { get; init; } = [];

        public HttpStatusCode? ManifestStatusCode { get; init; }

        public HttpStatusCode GitHubStatusCode { get; init; } = HttpStatusCode.NotFound;

        public int QasRequestCount { get; private set; }

        public List<Uri> GitHubRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;

            if (string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                GitHubRequests.Add(uri);
                return Task.FromResult(Respond(GitHubStatusCode, "{\"message\":\"Not Found\"}"));
            }

            if (uri.AbsolutePath.Equals("/ig/qas.json", StringComparison.OrdinalIgnoreCase))
            {
                QasRequestCount++;
                return Task.FromResult(Respond(HttpStatusCode.OK, Qas));
            }

            if (uri.AbsolutePath.EndsWith("/package.manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                if (ManifestStatusCode is HttpStatusCode status)
                    return Task.FromResult(Respond(status, "server error"));

                string key = uri.AbsolutePath["/ig/".Length..^"/package.manifest.json".Length];

                return Task.FromResult(Manifests.TryGetValue(key, out string? manifest)
                    ? Respond(HttpStatusCode.OK, manifest)
                    : Respond(HttpStatusCode.NotFound, "not found"));
            }

            return Task.FromResult(Respond(HttpStatusCode.NotFound, "not found"));
        }

        private static HttpResponseMessage Respond(HttpStatusCode statusCode, string body) =>
            new(statusCode)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
    }
}
