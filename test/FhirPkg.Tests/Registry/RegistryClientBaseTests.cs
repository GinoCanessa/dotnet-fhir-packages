// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using FhirPkg.Models;
using FhirPkg.Registry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Registry;

public class RegistryClientBaseTests
{
    /// <summary>
    /// Minimal concrete subclass exposing the protected helpers for testing.
    /// </summary>
    private sealed class TestableRegistryClient : RegistryClientBase
    {
        public TestableRegistryClient(HttpClient httpClient, RegistryEndpoint endpoint, ILogger logger)
            : base(httpClient, endpoint, logger) { }

        public override IReadOnlyList<PackageNameType> SupportedNameTypes => [];
        public override IReadOnlyList<VersionType> SupportedVersionTypes => [];

        /// <summary>Expose GetResponseAsync for test assertions.</summary>
        public Task<HttpResponseMessage?> TestGetResponseAsync(string uri, CancellationToken ct) =>
            GetResponseAsync(uri, ct);
    }

    /// <summary>
    /// Handler that captures request headers and returns a canned 404 response.
    /// </summary>
    private sealed class HeaderCapturingHandler : HttpMessageHandler
    {
        public List<(string? Authorization, string? UserAgent, IEnumerable<KeyValuePair<string, IEnumerable<string>>> AllHeaders)>
            CapturedRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequests.Add((
                request.Headers.Authorization?.ToString(),
                request.Headers.UserAgent?.ToString(),
                request.Headers.ToList()));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task PerRequestHeaders_DoNotLeakAcrossClients()
    {
        // Arrange: two clients with different auth tokens sharing one HttpClient
        var handler = new HeaderCapturingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.com") };

        var ep1 = new RegistryEndpoint
        {
            Url = "https://reg1.example.com/",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer token-ONE",
            UserAgent = "Agent-1",
        };

        var ep2 = new RegistryEndpoint
        {
            Url = "https://reg2.example.com/",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer token-TWO",
            UserAgent = "Agent-2",
        };

        var logger = NullLogger.Instance;
        var client1 = new TestableRegistryClient(httpClient, ep1, logger);
        var client2 = new TestableRegistryClient(httpClient, ep2, logger);

        // Act: make one request per client
        await client1.TestGetResponseAsync("https://reg1.example.com/pkg1", CancellationToken.None);
        await client2.TestGetResponseAsync("https://reg2.example.com/pkg2", CancellationToken.None);

        // Assert: shared HttpClient has NO default auth headers set
        httpClient.DefaultRequestHeaders.Authorization.ShouldBeNull(
            "DefaultRequestHeaders must not be mutated by per-request header logic");

        // Assert: each request carried only its own endpoint's headers
        handler.CapturedRequests.Count.ShouldBe(2);

        handler.CapturedRequests[0].Authorization.ShouldBe("Bearer token-ONE");
        handler.CapturedRequests[0].UserAgent.ShouldBe("Agent-1");

        handler.CapturedRequests[1].Authorization.ShouldBe("Bearer token-TWO");
        handler.CapturedRequests[1].UserAgent.ShouldBe("Agent-2");
    }

    [Fact]
    public async Task PerRequestHeaders_CustomHeaders_Applied()
    {
        var handler = new HeaderCapturingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.com") };

        var ep = new RegistryEndpoint
        {
            Url = "https://private.example.com/",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Basic abc123",
            CustomHeaders = [("X-Tenant", "acme"), ("X-Correlation", "test-run-1")],
        };

        var client = new TestableRegistryClient(httpClient, ep, NullLogger.Instance);

        await client.TestGetResponseAsync("https://private.example.com/pkg", CancellationToken.None);

        var captured = handler.CapturedRequests.ShouldHaveSingleItem();
        captured.Authorization.ShouldBe("Basic abc123");

        var allHeaders = captured.AllHeaders.ToDictionary(
            h => h.Key,
            h => string.Join(", ", h.Value));

        allHeaders.ShouldContainKey("X-Tenant");
        allHeaders["X-Tenant"].ShouldBe("acme");

        allHeaders.ShouldContainKey("X-Correlation");
        allHeaders["X-Correlation"].ShouldBe("test-run-1");
    }
}
