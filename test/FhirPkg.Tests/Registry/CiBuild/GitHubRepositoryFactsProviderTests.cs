// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using FhirPkg.Registry;
using FhirPkg.Registry.CiBuild;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Registry.CiBuild;

public class GitHubRepositoryFactsProviderTests
{
    private const string NonForkPayload = """
        {
          "fork": false,
          "created_at": "2019-03-14T18:21:07Z",
          "default_branch": "master"
        }
        """;

    private const string ForkPayload = """
        {
          "fork": true,
          "created_at": "2024-11-02T09:00:00Z",
          "default_branch": "main",
          "parent": { "full_name": "HL7/fhir-subscription-backport-ig" }
        }
        """;

    [Fact]
    public async Task TryGetFactsAsync_NonForkPayload_MapsFacts()
    {
        StubHandler handler = new(HttpStatusCode.OK, NonForkPayload);
        GitHubRepositoryFactsProvider provider = Create(handler);

        GitHubRepositoryFacts? facts = await provider.TryGetFactsAsync(
            new CiBuildRepositoryIdentity("HL7", "fhir-subscription-backport-ig"),
            TestContext.Current.CancellationToken);

        facts.ShouldNotBeNull();
        facts.IsFork.ShouldBeFalse();
        facts.DefaultBranch.ShouldBe("master");
        facts.CreatedAt.ShouldBe(new DateTimeOffset(2019, 3, 14, 18, 21, 7, TimeSpan.Zero));
        facts.ParentFullName.ShouldBeNull();
        handler.Requests.Single().RequestUri.ShouldBe(
            new Uri("https://api.github.com/repos/HL7/fhir-subscription-backport-ig"));
    }

    [Fact]
    public async Task TryGetFactsAsync_ForkPayload_MapsParentFullName()
    {
        StubHandler handler = new(HttpStatusCode.OK, ForkPayload);
        GitHubRepositoryFactsProvider provider = Create(handler);

        GitHubRepositoryFacts? facts = await provider.TryGetFactsAsync(
            new CiBuildRepositoryIdentity("jkiddo", "fhir-subscription-backport-ig"),
            TestContext.Current.CancellationToken);

        facts.ShouldNotBeNull();
        facts.IsFork.ShouldBeTrue();
        facts.ParentFullName.ShouldBe("HL7/fhir-subscription-backport-ig");
    }

    [Fact]
    public async Task TryGetFactsAsync_SameRepositoryTwice_IssuesOneRequest()
    {
        StubHandler handler = new(HttpStatusCode.OK, NonForkPayload);
        GitHubRepositoryFactsProvider provider = Create(handler);
        CiBuildRepositoryIdentity repository = new("HL7", "fhir-subscription-backport-ig");

        await provider.TryGetFactsAsync(repository, TestContext.Current.CancellationToken);
        GitHubRepositoryFacts? second = await provider.TryGetFactsAsync(
            repository, TestContext.Current.CancellationToken);

        second.ShouldNotBeNull();
        handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}")]
    [InlineData(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}")]
    [InlineData(HttpStatusCode.InternalServerError, "boom")]
    [InlineData(HttpStatusCode.OK, "{ this is not json")]
    public async Task TryGetFactsAsync_FailureModes_ReturnNullWithoutThrowing(
        HttpStatusCode statusCode,
        string body)
    {
        StubHandler handler = new(statusCode, body);
        GitHubRepositoryFactsProvider provider = Create(handler);

        GitHubRepositoryFacts? facts = await provider.TryGetFactsAsync(
            new CiBuildRepositoryIdentity("HL7", "example"),
            TestContext.Current.CancellationToken);

        facts.ShouldBeNull();
    }

    [Fact]
    public async Task TryGetFactsAsync_TransportFailure_ReturnsNullWithoutThrowing()
    {
        StubHandler handler = new(new HttpRequestException("connection refused"));
        GitHubRepositoryFactsProvider provider = Create(handler);

        GitHubRepositoryFacts? facts = await provider.TryGetFactsAsync(
            new CiBuildRepositoryIdentity("HL7", "example"),
            TestContext.Current.CancellationToken);

        facts.ShouldBeNull();
    }

    [Fact]
    public async Task TryGetFactsAsync_NegativeResult_IsCached()
    {
        StubHandler handler = new(HttpStatusCode.Forbidden, "{\"message\":\"rate limited\"}");
        GitHubRepositoryFactsProvider provider = Create(handler);
        CiBuildRepositoryIdentity repository = new("HL7", "example");

        (await provider.TryGetFactsAsync(repository, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await provider.TryGetFactsAsync(repository, TestContext.Current.CancellationToken)).ShouldBeNull();

        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task TryGetFactsAsync_SendsUserAgentAndAcceptHeaders()
    {
        StubHandler handler = new(HttpStatusCode.OK, NonForkPayload);
        GitHubRepositoryFactsProvider provider = Create(handler);

        await provider.TryGetFactsAsync(
            new CiBuildRepositoryIdentity("HL7", "example"),
            TestContext.Current.CancellationToken);

        CapturedRequest request = handler.Requests.Single();
        request.UserAgent.ShouldBe("FhirPkg/1.0");
        request.Accept.ShouldBe("application/vnd.github+json");
    }

    [Fact]
    public async Task TryGetFactsAsync_WithoutToken_SendsNoAuthorizationHeader()
    {
        StubHandler handler = new(HttpStatusCode.OK, NonForkPayload);
        GitHubRepositoryFactsProvider provider = Create(handler);

        await provider.TryGetFactsAsync(
            new CiBuildRepositoryIdentity("HL7", "example"),
            TestContext.Current.CancellationToken);

        handler.Requests.Single().Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task TryGetFactsAsync_WithToken_SendsBearerAuthorizationHeader()
    {
        StubHandler handler = new(HttpStatusCode.OK, NonForkPayload);
        GitHubRepositoryFactsProvider provider = Create(handler, token: "test-token");

        await provider.TryGetFactsAsync(
            new CiBuildRepositoryIdentity("HL7", "example"),
            TestContext.Current.CancellationToken);

        handler.Requests.Single().Authorization.ShouldBe("Bearer test-token");
    }

    [Fact]
    public async Task TryGetFactsAsync_WithToken_SendsItOnlyToTheConfiguredOrigin()
    {
        StubHandler handler = new(HttpStatusCode.OK, NonForkPayload);
        GitHubRepositoryFactsProvider provider = Create(
            handler,
            token: "test-token",
            apiBaseUri: new Uri("https://github.example.internal/api/"));

        await provider.TryGetFactsAsync(
            new CiBuildRepositoryIdentity("HL7", "example"),
            TestContext.Current.CancellationToken);

        CapturedRequest request = handler.Requests.Single();
        request.RequestUri.ShouldBe(new Uri("https://github.example.internal/api/repos/HL7/example"));
        request.Authorization.ShouldBe("Bearer test-token");
    }

    [Fact]
    public async Task TryGetFactsAsync_RedirectResponse_IsNotFollowedAndYieldsNullFacts()
    {
        // The provider never re-issues a request itself, so a credential can never
        // follow a redirect to another origin: a 3xx is simply a non-success.
        StubHandler handler = new(HttpStatusCode.MovedPermanently, string.Empty)
        {
            LocationHeader = new Uri("https://evil.example.com/repos/HL7/example"),
        };
        GitHubRepositoryFactsProvider provider = Create(handler, token: "test-token");

        GitHubRepositoryFacts? facts = await provider.TryGetFactsAsync(
            new CiBuildRepositoryIdentity("HL7", "example"),
            TestContext.Current.CancellationToken);

        facts.ShouldBeNull();
        handler.Requests.Count.ShouldBe(1);
        handler.Requests.Single().RequestUri!.Host.ShouldBe("api.github.com");
    }

    [Fact]
    public void Constructor_TokenWithUnverifiedTransport_Throws()
    {
        StubHandler handler = new(HttpStatusCode.OK, NonForkPayload);
        RegistryHttpTransport unverified = RegistryHttpTransport.CreateUnverified(new HttpClient(handler));

        Should.Throw<InvalidOperationException>(() => new GitHubRepositoryFactsProvider(
            unverified,
            NullLogger.Instance,
            token: "test-token"));
    }

    [Fact]
    public void Constructor_NoTokenWithUnverifiedTransport_DoesNotThrow()
    {
        StubHandler handler = new(HttpStatusCode.OK, NonForkPayload);
        RegistryHttpTransport unverified = RegistryHttpTransport.CreateUnverified(new HttpClient(handler));

        Should.NotThrow(() => new GitHubRepositoryFactsProvider(unverified, NullLogger.Instance));
    }

    private static GitHubRepositoryFactsProvider Create(
        StubHandler handler,
        string? token = null,
        Uri? apiBaseUri = null) =>
        new(
            RegistryHttpTransport.CreateRedirectControlled(
                new HttpClient(handler),
                TimeSpan.FromSeconds(30),
                maxRedirects: 5),
            NullLogger.Instance,
            token,
            apiBaseUri);

    private sealed record CapturedRequest(
        Uri? RequestUri,
        string? Authorization,
        string? UserAgent,
        string? Accept);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;
        private readonly Exception? _throw;

        public StubHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        public StubHandler(Exception exception)
        {
            _statusCode = HttpStatusCode.OK;
            _body = string.Empty;
            _throw = exception;
        }

        public List<CapturedRequest> Requests { get; } = [];

        public Uri? LocationHeader { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri,
                Header(request, "Authorization"),
                Header(request, "User-Agent"),
                Header(request, "Accept")));

            if (_throw is not null)
                return Task.FromException<HttpResponseMessage>(_throw);

            HttpResponseMessage response = new(_statusCode)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            };

            if (LocationHeader is not null)
                response.Headers.Location = LocationHeader;

            return Task.FromResult(response);
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out IEnumerable<string>? values)
                ? string.Join(" ", values)
                : null;
    }
}
