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

        // Expose base virtual methods for testing
        public Task<IReadOnlyList<CatalogEntry>> TestSearchAsync(CancellationToken ct) =>
            SearchAsync(new PackageSearchCriteria { Name = "test" }, ct);

        public Task<PackageListing?> TestGetPackageListingAsync(CancellationToken ct) =>
            GetPackageListingAsync("test", ct);

        public Task<ResolvedDirective?> TestResolveAsync(CancellationToken ct) =>
            ResolveAsync(PackageDirective.Parse("test#1.0.0"), cancellationToken: ct);

        public Task<PackageDownloadResult?> TestDownloadAsync(CancellationToken ct) =>
            DownloadAsync(new ResolvedDirective
            {
                Reference = new PackageReference { Name = "test", Version = "1.0.0" },
                TarballUri = new Uri("https://example.com/test-1.0.0.tgz")
            }, ct);
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
        HeaderCapturingHandler handler = new HeaderCapturingHandler();
        using HttpClient httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.com") };

        RegistryEndpoint ep1 = new RegistryEndpoint
        {
            Url = "https://reg1.example.com/",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer token-ONE",
            UserAgent = "Agent-1",
        };

        RegistryEndpoint ep2 = new RegistryEndpoint
        {
            Url = "https://reg2.example.com/",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer token-TWO",
            UserAgent = "Agent-2",
        };

        NullLogger logger = NullLogger.Instance;
        TestableRegistryClient client1 = new TestableRegistryClient(httpClient, ep1, logger);
        TestableRegistryClient client2 = new TestableRegistryClient(httpClient, ep2, logger);

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
        HeaderCapturingHandler handler = new HeaderCapturingHandler();
        using HttpClient httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.com") };

        RegistryEndpoint ep = new RegistryEndpoint
        {
            Url = "https://private.example.com/",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Basic abc123",
            CustomHeaders = [("X-Tenant", "acme"), ("X-Correlation", "test-run-1")],
        };

        TestableRegistryClient client = new TestableRegistryClient(httpClient, ep, NullLogger.Instance);

        await client.TestGetResponseAsync("https://private.example.com/pkg", CancellationToken.None);

        (string? Authorization, string? UserAgent, IEnumerable<KeyValuePair<string, IEnumerable<string>>> AllHeaders) captured = handler.CapturedRequests.ShouldHaveSingleItem();
        captured.Authorization.ShouldBe("Basic abc123");

        Dictionary<string, string> allHeaders = captured.AllHeaders.ToDictionary(
            h => h.Key,
            h => string.Join(", ", h.Value));

        allHeaders.ShouldContainKey("X-Tenant");
        allHeaders["X-Tenant"].ShouldBe("acme");

        allHeaders.ShouldContainKey("X-Correlation");
        allHeaders["X-Correlation"].ShouldBe("test-run-1");
    }

    // ── M-5: Virtual methods return sensible defaults ───────────────────

    [Fact]
    public async Task VirtualSearchAsync_ReturnsEmptyList()
    {
        HeaderCapturingHandler handler = new HeaderCapturingHandler();
        using HttpClient httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.com") };
        RegistryEndpoint ep = new RegistryEndpoint { Url = "https://example.com/", Type = RegistryType.FhirNpm };
        TestableRegistryClient client = new TestableRegistryClient(httpClient, ep, NullLogger.Instance);

        IReadOnlyList<CatalogEntry> result = await client.TestSearchAsync(CancellationToken.None);

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task VirtualGetPackageListingAsync_ReturnsNull()
    {
        HeaderCapturingHandler handler = new HeaderCapturingHandler();
        using HttpClient httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.com") };
        RegistryEndpoint ep = new RegistryEndpoint { Url = "https://example.com/", Type = RegistryType.FhirNpm };
        TestableRegistryClient client = new TestableRegistryClient(httpClient, ep, NullLogger.Instance);

        PackageListing? result = await client.TestGetPackageListingAsync(CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task VirtualResolveAsync_ReturnsNull()
    {
        HeaderCapturingHandler handler = new HeaderCapturingHandler();
        using HttpClient httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.com") };
        RegistryEndpoint ep = new RegistryEndpoint { Url = "https://example.com/", Type = RegistryType.FhirNpm };
        TestableRegistryClient client = new TestableRegistryClient(httpClient, ep, NullLogger.Instance);

        ResolvedDirective? result = await client.TestResolveAsync(CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task VirtualDownloadAsync_ReturnsNull()
    {
        HeaderCapturingHandler handler = new HeaderCapturingHandler();
        using HttpClient httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.com") };
        RegistryEndpoint ep = new RegistryEndpoint { Url = "https://example.com/", Type = RegistryType.FhirNpm };
        TestableRegistryClient client = new TestableRegistryClient(httpClient, ep, NullLogger.Instance);

        PackageDownloadResult? result = await client.TestDownloadAsync(CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public void VirtualPublishAsync_ThrowsNotSupportedException()
    {
        HeaderCapturingHandler handler = new HeaderCapturingHandler();
        using HttpClient httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.com") };
        RegistryEndpoint ep = new RegistryEndpoint { Url = "https://example.com/", Type = RegistryType.FhirNpm };
        TestableRegistryClient client = new TestableRegistryClient(httpClient, ep, NullLogger.Instance);

        Should.Throw<NotSupportedException>(async () =>
            await client.PublishAsync(
                new PackageReference("test", "1.0.0"),
                Stream.Null,
                CancellationToken.None));
    }
}
