// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;
using FhirPkg.Registry;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Registry;

public class RegistryOperationExceptionTests
{
    [Fact]
    public void Capture_SanitizesEndpointAndExceptionMessage()
    {
        RegistryEndpoint endpoint = new()
        {
            Url = "https://user:password@registry.example:8443/private/path?token=secret#fragment",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer secret",
            CustomHeaders = [("X-Api-Key", "secret-key")]
        };
        HttpRequestException rawException = new(
            "Request failed with token=secret and response body: private-data");

        RegistryAttemptFailure failure =
            RegistryAttemptFailure.Capture(endpoint, rawException);

        failure.EndpointOrigin.ShouldBe("https://registry.example:8443");
        failure.Category.ShouldBe(RegistryFailureCategory.Network);
        failure.Message.ShouldBe("The registry could not be reached.");
        failure.Message.ShouldNotContain("secret");
        failure.Message.ShouldNotContain("private-data");
    }

    [Fact]
    public void Constructor_ExposesOnlySanitizedPublicFailureState()
    {
        RegistryEndpoint endpoint = new()
        {
            Url = "https://user:password@registry.example/private?token=secret",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer secret"
        };
        Exception rawException = new InvalidDataException(
            "Response body contained secret payload");
        RegistryAttemptFailure failure =
            RegistryAttemptFailure.Capture(endpoint, rawException);

        RegistryOperationException exception = new(
            "get-package-listing",
            "example.package",
            [failure]);

        exception.Operation.ShouldBe("get-package-listing");
        exception.PackageId.ShouldBe("example.package");
        exception.Failures.ShouldBe([failure]);
        exception.InnerException.ShouldBeNull();
        exception.Message.ShouldBe("The registry operation failed after one attempt.");
        exception.ToString().ShouldNotContain("password");
        exception.ToString().ShouldNotContain("token");
        exception.ToString().ShouldNotContain("secret payload");
        exception.Failures.Single().Category.ShouldBe(
            RegistryFailureCategory.InvalidResponse);
    }

    [Theory]
    [InlineData("not a URI")]
    [InlineData(null)]
    public void Constructor_InvalidEndpoint_DoesNotRetainRawValue(string? endpointUrl)
    {
        RegistryAttemptFailure failure = new(
            endpointUrl,
            RegistryFailureCategory.Unexpected);

        failure.EndpointOrigin.ShouldBe("unknown");
    }

    [Fact]
    public void ToProvenance_RetainsOnlyOriginAndType()
    {
        RegistryEndpoint endpoint = new()
        {
            Url =
                "https://user:password@registry.example:8443/private?token=secret",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer secret",
            CustomHeaders = [("X-Api-Key", "secret-key")],
            TrustedHeaderOrigins = ["https://trusted.example"],
            UserAgent = "secret-agent",
        };

        RegistryEndpoint provenance = endpoint.ToProvenance();

        provenance.Url.ShouldBe("https://registry.example:8443/");
        provenance.Type.ShouldBe(RegistryType.FhirNpm);
        provenance.AuthHeaderValue.ShouldBeNull();
        provenance.CustomHeaders.ShouldBeNull();
        provenance.TrustedHeaderOrigins.ShouldBeEmpty();
        provenance.UserAgent.ShouldBeNull();
        provenance.ToString().ShouldNotContain("password");
        provenance.ToString().ShouldNotContain("token");
        provenance.ToString().ShouldNotContain("secret");
        provenance.ToString().ShouldNotContain("private");
    }
}
