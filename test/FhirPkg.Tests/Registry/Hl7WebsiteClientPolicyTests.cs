// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;
using FhirPkg.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Registry;

public class Hl7WebsiteClientPolicyTests
{
    private static Hl7WebsiteClient CreateClient() =>
        new(
            new HttpClient(),
            RegistryEndpoint.Hl7Website,
            NullLogger<Hl7WebsiteClient>.Instance);

    [Fact]
    public async Task ResolveAsync_RejectsNonCanonicalExactVersion()
    {
        Hl7WebsiteClient client = CreateClient();

        ResolvedDirective? result = await client.ResolveAsync(
            PackageDirective.Parse("hl7.fhir.r4.core#9.9.9"),
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_RejectsExplicitPrereleaseWhenDisabled()
    {
        Hl7WebsiteClient client = CreateClient();

        ResolvedDirective? result = await client.ResolveAsync(
            PackageDirective.Parse("hl7.fhir.r4.core#4.0.1-beta"),
            new VersionResolveOptions { AllowPreRelease = false },
            TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_LatestReturnsCanonicalReleaseVersion()
    {
        Hl7WebsiteClient client = CreateClient();

        ResolvedDirective? result = await client.ResolveAsync(
            PackageDirective.Parse("hl7.fhir.r4.core#latest"),
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Reference.Version.ShouldBe("4.0.1");
    }
}
