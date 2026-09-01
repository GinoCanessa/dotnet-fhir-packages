// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg;
using FhirPkg.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Registry;

/// <summary>
/// Tests for <see cref="RegistryClientFactory"/>'s credential-safety guard: a
/// caller-supplied GitHub token is refused on any transport that is not
/// redirect-controlled, and is accepted on one that is.
/// </summary>
public class RegistryClientFactoryTests
{
    private const string TestToken = "gho_test-token";

    [Fact]
    public void BuildRegistryClient_HttpClientOverloadWithGitHubToken_Throws()
    {
        FhirPackageManagerOptions options = new() { GitHubToken = TestToken };
        using HttpClient httpClient = new();

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => RegistryClientFactory.BuildRegistryClient(
                options,
                httpClient,
                NullLoggerFactory.Instance));

        exception.Message.ShouldContain("GitHubToken");
        exception.Message.ShouldContain("redirect-controlled");
    }

    [Fact]
    public void BuildRegistryClient_HttpClientOverloadWithoutGitHubToken_BuildsClient()
    {
        FhirPackageManagerOptions options = new();
        using HttpClient httpClient = new();

        options.GitHubToken.ShouldBeNull();

        IRegistryClient client = RegistryClientFactory.BuildRegistryClient(
            options,
            httpClient,
            NullLoggerFactory.Instance);

        client.ShouldNotBeNull();
    }

    [Fact]
    public void BuildRegistryClient_RedirectControlledTransportWithGitHubToken_BuildsClient()
    {
        FhirPackageManagerOptions options = new() { GitHubToken = TestToken };

        options.IncludeCiBuilds.ShouldBeTrue();

        using HttpClient httpClient = new(new HttpClientHandler { AllowAutoRedirect = false });
        RegistryHttpTransport transport = RegistryHttpTransport.CreateRedirectControlled(
            httpClient,
            TimeSpan.FromSeconds(30),
            maxRedirects: 5);

        IRegistryClient client = RegistryClientFactory.BuildRegistryClient(
            options,
            transport,
            NullLoggerFactory.Instance);

        client.ShouldNotBeNull();
    }

    [Fact]
    public void BuildRegistryClient_UnverifiedTransportWithGitHubToken_Throws()
    {
        FhirPackageManagerOptions options = new() { GitHubToken = TestToken };

        options.IncludeCiBuilds.ShouldBeTrue();

        using HttpClient httpClient = new();
        RegistryHttpTransport transport = RegistryHttpTransport.CreateUnverified(httpClient);

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => RegistryClientFactory.BuildRegistryClient(
                options,
                transport,
                NullLoggerFactory.Instance));

        exception.Message.ShouldContain("Authenticated GitHub repository lookups");
        exception.Message.ShouldContain("redirect-controlled");
    }
}
