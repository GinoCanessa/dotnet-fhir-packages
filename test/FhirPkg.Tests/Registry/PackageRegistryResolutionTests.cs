// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using System.Text;
using FhirPkg.Models;
using FhirPkg.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Registry;

public class PackageRegistryResolutionTests
{
    [Fact]
    public async Task FhirNpmClient_UsesSharedVersionPolicy()
    {
        await AssertSharedPolicyAsync(
            httpClient => new FhirNpmRegistryClient(
                httpClient,
                new RegistryEndpoint
                {
                    Url = "https://registry.example/",
                    Type = RegistryType.FhirNpm,
                },
                NullLogger<FhirNpmRegistryClient>.Instance));
    }

    [Fact]
    public async Task NpmClient_UsesSharedVersionPolicy()
    {
        await AssertSharedPolicyAsync(
            httpClient => new NpmRegistryClient(
                httpClient,
                new RegistryEndpoint
                {
                    Url = "https://registry.example/",
                    Type = RegistryType.Npm,
                },
                NullLogger<NpmRegistryClient>.Instance));
    }

    private static async Task AssertSharedPolicyAsync(
        Func<HttpClient, IRegistryClient> createClient)
    {
        const string json = """
            {
              "name": "example.package",
              "dist-tags": { "latest": "2.0.0-beta" },
              "versions": {
                "1.0.0": {
                  "name": "example.package",
                  "version": "1.0.0",
                  "fhirVersion": "4.0.1",
                  "dist": {
                    "shasum": "sha",
                    "integrity": "sha512-strong",
                    "tarball": "https://downloads.example/1.0.0.tgz"
                  }
                },
                "2.0.0-beta": {
                  "name": "example.package",
                  "version": "2.0.0-beta",
                  "fhirVersion": "5.0.0",
                  "dist": {
                    "integrity": "sha512-beta",
                    "tarball": "https://downloads.example/2.0.0-beta.tgz"
                  }
                }
              }
            }
            """;
        using HttpClient httpClient = new(new JsonHandler(json));
        IRegistryClient client = createClient(httpClient);

        PackageListing? listing = await client.GetPackageListingAsync(
            "example.package",
            TestContext.Current.CancellationToken);
        ResolvedDirective? resolved = await client.ResolveAsync(
            PackageDirective.Parse("example.package#latest"),
            new VersionResolveOptions
            {
                AllowPreRelease = false,
                FhirRelease = FhirRelease.R4,
            },
            TestContext.Current.CancellationToken);

        listing.ShouldNotBeNull();
        listing.SourceRegistry.ShouldBe(client.Endpoint);
        listing.VersionCandidates.Count.ShouldBe(2);
        listing.VersionCandidates.Single(candidate => candidate.Version == "2.0.0-beta")
            .IsSourceLatest.ShouldBeTrue();
        listing.VersionCandidates.Single(candidate => candidate.Version == "1.0.0")
            .Distribution!.Integrity.ShouldBe("sha512-strong");
        resolved.ShouldNotBeNull();
        resolved.Reference.Version.ShouldBe("1.0.0");
        resolved.Integrity.ShouldBe("sha512-strong");
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
    }
}
