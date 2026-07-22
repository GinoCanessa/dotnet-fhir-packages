// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Net;
using System.Text;
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

        public TestableRegistryClient(
            RegistryHttpTransport transport,
            RegistryEndpoint endpoint,
            ILogger logger)
            : base(transport, endpoint, logger) { }

        public override IReadOnlyList<PackageNameType> SupportedNameTypes => [];
        public override IReadOnlyList<VersionType> SupportedVersionTypes => [];

        /// <summary>Expose GetResponseAsync for test assertions.</summary>
        public async Task<HttpStatusCode?> TestGetResponseAsync(string uri, CancellationToken ct)
        {
            using HttpResponseMessage? response =
                await GetResponseAsync(uri, ct);
            return response?.StatusCode;
        }

        public Task<HttpResponseMessage?> TestGetRawResponseAsync(
            string uri,
            CancellationToken ct) =>
            GetResponseAsync(uri, ct);

        public async Task<PackageDownloadResult?> TestFetchDownloadAsync(
            string uri,
            CancellationToken ct)
        {
            HttpResponseMessage? response =
                await GetResponseAsync(uri, ct);
            return response is null
                ? null
                : await CreateDownloadResultAsync(response, ct);
        }

        public Task<HttpResponseMessage> TestPutStreamAsync(
            string uri,
            Stream stream,
            CancellationToken ct) =>
            PutStreamAsync(uri, stream, "application/gzip", ct);

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
        public List<(Uri? Uri, string? Authorization, string? UserAgent, IEnumerable<KeyValuePair<string, IEnumerable<string>>> AllHeaders)>
            CapturedRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequests.Add((
                request.RequestUri,
                request.Headers.Authorization?.ToString(),
                request.Headers.UserAgent?.ToString(),
                request.Headers.ToList()));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class SequenceHandler(
        params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private int _index;

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            byte[]? body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => string.Join(", ", header.Value),
                    StringComparer.OrdinalIgnoreCase),
                body));

            int index = Interlocked.Increment(ref _index) - 1;
            if (index >= responses.Length)
                throw new InvalidOperationException("No response configured.");

            return responses[index](request);
        }
    }

    private sealed record CapturedRequest(
        Uri Uri,
        string? Authorization,
        IReadOnlyDictionary<string, string> Headers,
        byte[]? Body);

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(
                System.Threading.Timeout.InfiniteTimeSpan,
                cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class TrackingContent : ByteArrayContent
    {
        public TrackingContent(byte[] content)
            : base(content)
        {
        }

        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private static RegistryHttpTransport ControlledTransport(
        HttpClient httpClient,
        TimeSpan? timeout = null,
        int maxRedirects = 5) =>
        RegistryHttpTransport.CreateRedirectControlled(
            httpClient,
            timeout ?? TimeSpan.FromSeconds(5),
            maxRedirects);

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
        RegistryHttpTransport transport = ControlledTransport(httpClient);
        TestableRegistryClient client1 = new TestableRegistryClient(transport, ep1, logger);
        TestableRegistryClient client2 = new TestableRegistryClient(transport, ep2, logger);

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

        TestableRegistryClient client = new TestableRegistryClient(
            ControlledTransport(httpClient),
            ep,
            NullLogger.Instance);

        await client.TestGetResponseAsync("https://private.example.com/pkg", CancellationToken.None);

        (Uri? Uri, string? Authorization, string? UserAgent, IEnumerable<KeyValuePair<string, IEnumerable<string>>> AllHeaders) captured =
            handler.CapturedRequests.ShouldHaveSingleItem();
        captured.Authorization.ShouldBe("Basic abc123");

        Dictionary<string, string> allHeaders = captured.AllHeaders.ToDictionary(
            h => h.Key,
            h => string.Join(", ", h.Value));

        allHeaders.ShouldContainKey("X-Tenant");
        allHeaders["X-Tenant"].ShouldBe("acme");

        allHeaders.ShouldContainKey("X-Correlation");
        allHeaders["X-Correlation"].ShouldBe("test-run-1");
    }

    [Theory]
    [InlineData("https://sub.private.example.com/pkg")]
    [InlineData("http://private.example.com/pkg")]
    [InlineData("https://private.example.com:444/pkg")]
    public async Task SensitiveHeaders_UntrustedOrigin_AreStripped(
        string requestUri)
    {
        HeaderCapturingHandler handler = new();
        using HttpClient httpClient = new(handler);
        RegistryEndpoint endpoint = new()
        {
            Url = "https://private.example.com/",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer secret",
            CustomHeaders = [("X-Tenant", "acme")]
        };
        TestableRegistryClient client = new(
            ControlledTransport(httpClient),
            endpoint,
            NullLogger.Instance);

        await client.TestGetResponseAsync(
            requestUri,
            TestContext.Current.CancellationToken);

        (Uri? Uri, string? Authorization, string? UserAgent, IEnumerable<KeyValuePair<string, IEnumerable<string>>> AllHeaders) captured =
            handler.CapturedRequests.ShouldHaveSingleItem();
        captured.Authorization.ShouldBeNull();
        captured.UserAgent.ShouldNotBeNullOrWhiteSpace();
        captured.AllHeaders.Select(header => header.Key)
            .ShouldNotContain("X-Tenant");
    }

    [Fact]
    public async Task SensitiveHeaders_ExplicitTrustedOrigin_AreApplied()
    {
        HeaderCapturingHandler handler = new();
        using HttpClient httpClient = new(handler);
        RegistryEndpoint endpoint = new()
        {
            Url = "https://private.example.com/",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer secret",
            CustomHeaders = [("X-Tenant", "acme")],
            TrustedHeaderOrigins = ["https://cdn.example.com/content"]
        };
        TestableRegistryClient client = new(
            ControlledTransport(httpClient),
            endpoint,
            NullLogger.Instance);

        await client.TestGetResponseAsync(
            "https://cdn.example.com/pkg.tgz",
            TestContext.Current.CancellationToken);

        (Uri? Uri, string? Authorization, string? UserAgent, IEnumerable<KeyValuePair<string, IEnumerable<string>>> AllHeaders) captured =
            handler.CapturedRequests.ShouldHaveSingleItem();
        captured.Authorization.ShouldBe("Bearer secret");
        captured.AllHeaders.Select(header => header.Key)
            .ShouldContain("X-Tenant");
    }

    [Fact]
    public async Task SensitiveHeaders_IdnAndDefaultPort_AreEquivalent()
    {
        HeaderCapturingHandler handler = new();
        using HttpClient httpClient = new(handler);
        RegistryEndpoint endpoint = new()
        {
            Url = "https://bücher.example/",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer secret"
        };
        TestableRegistryClient client = new(
            ControlledTransport(httpClient),
            endpoint,
            NullLogger.Instance);

        await client.TestGetResponseAsync(
            "https://xn--bcher-kva.example:443/package",
            TestContext.Current.CancellationToken);

        handler.CapturedRequests.ShouldHaveSingleItem()
            .Authorization.ShouldBe("Bearer secret");
    }

    [Fact]
    public async Task SensitiveHeaders_UnverifiedTransport_IsRejectedBeforeSend()
    {
        HeaderCapturingHandler handler = new();
        using HttpClient httpClient = new(handler);
        RegistryEndpoint endpoint = new()
        {
            Url = "https://private.example.com/",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer secret"
        };
        TestableRegistryClient client = new(
            httpClient,
            endpoint,
            NullLogger.Instance);

        await Should.ThrowAsync<InvalidOperationException>(
            () => client.TestGetResponseAsync(
                "https://private.example.com/package",
                TestContext.Current.CancellationToken));

        handler.CapturedRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task DefaultRequestHeaders_AreRejectedBeforeCrossOriginSend()
    {
        HeaderCapturingHandler handler = new();
        using HttpClient httpClient = new(handler);
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                "default-secret");
        RegistryEndpoint endpoint = new()
        {
            Url = "https://private.example.com/",
            Type = RegistryType.FhirNpm
        };
        TestableRegistryClient client = new(
            ControlledTransport(httpClient),
            endpoint,
            NullLogger.Instance);

        await Should.ThrowAsync<InvalidOperationException>(
            () => client.TestGetResponseAsync(
                "https://cdn.example.com/package",
                TestContext.Current.CancellationToken));

        handler.CapturedRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Put_UnverifiedTransport_IsRejectedBeforeSend()
    {
        HeaderCapturingHandler handler = new();
        using HttpClient httpClient = new(handler);
        RegistryEndpoint endpoint = new()
        {
            Url = "https://private.example.com/",
            Type = RegistryType.FhirNpm
        };
        TestableRegistryClient client = new(
            httpClient,
            endpoint,
            NullLogger.Instance);
        using MemoryStream source = new([1, 2, 3]);

        await Should.ThrowAsync<InvalidOperationException>(
            () => client.TestPutStreamAsync(
                "https://private.example.com/package",
                source,
                TestContext.Current.CancellationToken));

        handler.CapturedRequests.ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task Redirect_CrossOrigin_StripsSensitiveHeaders()
    {
        TrackingContent redirectContent = new([]);
        SequenceHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers =
                {
                    Location = new Uri("https://cdn.example.com/package.tgz")
                },
                Content = redirectContent
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using HttpClient httpClient = new(handler);
        RegistryEndpoint endpoint = new()
        {
            Url = "https://private.example.com/",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer secret",
            CustomHeaders = [("X-Tenant", "acme")]
        };
        TestableRegistryClient client = new(
            ControlledTransport(httpClient),
            endpoint,
            NullLogger.Instance);

        await client.TestGetResponseAsync(
            "https://private.example.com/package",
            TestContext.Current.CancellationToken);

        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].Authorization.ShouldBe("Bearer secret");
        handler.Requests[1].Authorization.ShouldBeNull();
        handler.Requests[1].Headers.ContainsKey("X-Tenant").ShouldBeFalse();
        redirectContent.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Redirect_TrustedOrigin_RetainsSensitiveHeaders()
    {
        SequenceHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers =
                {
                    Location = new Uri("https://cdn.example.com/package.tgz")
                }
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using HttpClient httpClient = new(handler);
        RegistryEndpoint endpoint = new()
        {
            Url = "https://private.example.com/",
            Type = RegistryType.FhirNpm,
            AuthHeaderValue = "Bearer secret",
            TrustedHeaderOrigins = ["https://cdn.example.com/"]
        };
        TestableRegistryClient client = new(
            ControlledTransport(httpClient),
            endpoint,
            NullLogger.Instance);

        await client.TestGetResponseAsync(
            "https://private.example.com/package",
            TestContext.Current.CancellationToken);

        handler.Requests[1].Authorization.ShouldBe("Bearer secret");
    }

    [Fact]
    public async Task Redirect_LimitExceeded_Throws()
    {
        SequenceHandler handler = new(
            _ => RedirectTo("/two"),
            _ => RedirectTo("/three"));
        using HttpClient httpClient = new(handler);
        RegistryEndpoint endpoint = new()
        {
            Url = "https://private.example.com/",
            Type = RegistryType.FhirNpm
        };
        TestableRegistryClient client = new(
            ControlledTransport(httpClient, maxRedirects: 1),
            endpoint,
            NullLogger.Instance);

        HttpRequestException exception =
            await Should.ThrowAsync<HttpRequestException>(
                () => client.TestGetResponseAsync(
                    "https://private.example.com/one",
                    TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("redirect limit");
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Put_Redirect_IsNotFollowedAndCallerStreamRemainsOpen()
    {
        SequenceHandler handler = new(
            _ => RedirectTo("/second"));
        using HttpClient httpClient = new(handler);
        RegistryEndpoint endpoint = new()
        {
            Url = "https://private.example.com/",
            Type = RegistryType.FhirNpm
        };
        TestableRegistryClient client = new(
            ControlledTransport(httpClient),
            endpoint,
            NullLogger.Instance);
        using MemoryStream source = new(Encoding.UTF8.GetBytes("payload"));

        await Should.ThrowAsync<HttpRequestException>(
            () => client.TestPutStreamAsync(
                "https://private.example.com/package",
                source,
                TestContext.Current.CancellationToken));

        handler.Requests.Count.ShouldBe(1);
        source.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task ResponseBody_DeadlineThrowsTypedTimeout()
    {
        SequenceHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingReadStream())
            });
        using HttpClient httpClient = new(handler);
        RegistryEndpoint endpoint = new()
        {
            Url = "https://private.example.com/",
            Type = RegistryType.FhirNpm
        };
        TestableRegistryClient client = new(
            ControlledTransport(
                httpClient,
                TimeSpan.FromMilliseconds(50)),
            endpoint,
            NullLogger.Instance);

        await using PackageDownloadResult result =
            (await client.TestFetchDownloadAsync(
                "https://private.example.com/package",
                TestContext.Current.CancellationToken))!;
        byte[] buffer = new byte[1];

        await Should.ThrowAsync<RegistryResponseTimeoutException>(
            () => result.Content.ReadAsync(
                    buffer,
                    TestContext.Current.CancellationToken)
                .AsTask());
    }

    [Fact]
    public async Task BufferedResponseBody_DeadlineThrowsTypedTimeout()
    {
        SequenceHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingReadStream())
            });
        using HttpClient httpClient = new(handler);
        RegistryEndpoint endpoint = new()
        {
            Url = "https://private.example.com/",
            Type = RegistryType.FhirNpm
        };
        TestableRegistryClient client = new(
            ControlledTransport(
                httpClient,
                TimeSpan.FromMilliseconds(50)),
            endpoint,
            NullLogger.Instance);
        using HttpResponseMessage response =
            (await client.TestGetRawResponseAsync(
                "https://private.example.com/package",
                TestContext.Current.CancellationToken))!;

        await Should.ThrowAsync<RegistryResponseTimeoutException>(
            () => response.Content.ReadAsByteArrayAsync(
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SynchronousResponseStream_RemainsSupported()
    {
        SequenceHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x2A])
            });
        using HttpClient httpClient = new(handler);
        RegistryEndpoint endpoint = new()
        {
            Url = "https://private.example.com/",
            Type = RegistryType.FhirNpm
        };
        TestableRegistryClient client = new(
            ControlledTransport(httpClient),
            endpoint,
            NullLogger.Instance);
        using HttpResponseMessage response =
            (await client.TestGetRawResponseAsync(
                "https://private.example.com/package",
                TestContext.Current.CancellationToken))!;
        using Stream stream = response.Content.ReadAsStream(
            TestContext.Current.CancellationToken);

        stream.ReadByte().ShouldBe(0x2A);
    }

    [Fact]
    public async Task ResponseBody_CallerCancellationRemainsCancellation()
    {
        SequenceHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingReadStream())
            });
        using HttpClient httpClient = new(handler);
        RegistryEndpoint endpoint = new()
        {
            Url = "https://private.example.com/",
            Type = RegistryType.FhirNpm
        };
        TestableRegistryClient client = new(
            ControlledTransport(httpClient),
            endpoint,
            NullLogger.Instance);
        using CancellationTokenSource cancellationSource =
            new(TimeSpan.FromMilliseconds(50));

        await using PackageDownloadResult result =
            (await client.TestFetchDownloadAsync(
                "https://private.example.com/package",
                cancellationSource.Token))!;
        byte[] buffer = new byte[1];

        OperationCanceledException exception =
            await Should.ThrowAsync<OperationCanceledException>(
                () => result.Content.ReadAsync(
                        buffer,
                        cancellationSource.Token)
                    .AsTask());
        exception.ShouldNotBeOfType<RegistryResponseTimeoutException>();
    }

    private static HttpResponseMessage RedirectTo(string location) =>
        new(HttpStatusCode.Redirect)
        {
            Headers =
            {
                Location = new Uri(location, UriKind.Relative)
            }
        };

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
