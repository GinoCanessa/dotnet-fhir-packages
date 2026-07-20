// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;
using FhirPkg.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Registry;

public class FhirCiBuildClientPolicyTests
{
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
}
