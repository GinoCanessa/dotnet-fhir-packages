// Copyright (c) Gino Canessa. Licensed under the MIT License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using FhirPkg.Models;
using FhirPkg.Resolution;
using FhirPkg.Utilities;
using Microsoft.Extensions.Logging;

namespace FhirPkg.Registry;

/// <summary>
/// Abstract base class that provides shared HTTP infrastructure for registry client implementations.
/// </summary>
/// <remarks>
/// Applies user-agent on every outbound request and scopes authentication and
/// custom headers to exact trusted origins. Per-request
/// <see cref="HttpRequestMessage"/> headers allow one <see cref="HttpClient"/>
/// instance to be safely shared across multiple registry clients. Provides protected
/// helper methods for common HTTP operations with consistent error handling:
/// <list type="bullet">
///   <item><description>HTTP 404 → <see langword="null"/> (not found).</description></item>
///   <item><description>HTTP 5xx → <see cref="HttpRequestException"/> (server error).</description></item>
///   <item><description>Timeout → <see cref="RegistryResponseTimeoutException"/>.</description></item>
/// </list>
/// </remarks>
public abstract class RegistryClientBase : IRegistryClient
{
    private const string DefaultUserAgent = "FhirPkg/1.0";
    private static readonly HttpStatusCode[] s_redirectStatusCodes =
    [
        HttpStatusCode.MultipleChoices,
        HttpStatusCode.MovedPermanently,
        HttpStatusCode.Found,
        HttpStatusCode.SeeOther,
        HttpStatusCode.TemporaryRedirect,
        HttpStatusCode.PermanentRedirect
    ];

    private readonly RegistryHttpTransport _transport;
    private readonly HashSet<RegistryOrigin> _trustedHeaderOrigins;
    private readonly Uri _baseUri;

    /// <summary>
    /// Shared <see cref="JsonSerializerOptions"/> configured for case-insensitive property matching
    /// and null-value suppression.
    /// </summary>
    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Gets the <see cref="HttpClient"/> used for outbound requests.</summary>
    protected HttpClient Http { get; }

    /// <summary>Gets the registry endpoint configuration.</summary>
    protected RegistryEndpoint EndpointConfig { get; }

    /// <inheritdoc />
    public RegistryEndpoint Endpoint => EndpointConfig;

    /// <summary>Gets the logger instance for this client.</summary>
    protected ILogger Logger { get; }

    /// <summary>Gets the normalised base URL (always ends with <c>/</c>).</summary>
    protected string BaseUrl { get; }

    /// <inheritdoc />
    public abstract IReadOnlyList<PackageNameType> SupportedNameTypes { get; }

    /// <inheritdoc />
    public abstract IReadOnlyList<VersionType> SupportedVersionTypes { get; }

    /// <summary>
    /// Initialises the base class, configuring the <see cref="HttpClient"/> with authentication,
    /// user-agent, and custom headers from the endpoint.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use for requests.</param>
    /// <param name="endpoint">The registry endpoint configuration.</param>
    /// <param name="logger">The logger instance.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="httpClient"/>, <paramref name="endpoint"/>, or
    /// <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    protected RegistryClientBase(HttpClient httpClient, RegistryEndpoint endpoint, ILogger logger)
        : this(
            RegistryHttpTransport.CreateUnverified(httpClient),
            endpoint,
            logger)
    {
    }

    /// <summary>
    /// Initialises the base class with an explicit redirect-controlled transport capability.
    /// </summary>
    /// <param name="transport">The HTTP transport to use for requests.</param>
    /// <param name="endpoint">The registry endpoint configuration.</param>
    /// <param name="logger">The logger instance.</param>
    protected RegistryClientBase(
        RegistryHttpTransport transport,
        RegistryEndpoint endpoint,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(logger);

        _transport = transport;
        Http = transport.HttpClient;
        EndpointConfig = endpoint;
        Logger = logger;
        _baseUri = ParseAbsoluteHttpUri(
            endpoint.Url.TrimEnd('/') + "/",
            nameof(endpoint));
        BaseUrl = _baseUri.AbsoluteUri.TrimEnd('/');
        _trustedHeaderOrigins =
        [
            RegistryOrigin.Create(_baseUri)
        ];

        foreach (string trustedOrigin in endpoint.TrustedHeaderOrigins ?? [])
        {
            _trustedHeaderOrigins.Add(
                RegistryOrigin.Create(
                    ParseAbsoluteHttpUri(
                        trustedOrigin,
                        nameof(endpoint.TrustedHeaderOrigins))));
        }
    }

    // ── Protected HTTP helpers ──────────────────────────────────────────

    /// <summary>
    /// Sends an HTTP GET and deserialises the JSON response body to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type to deserialise the response to.</typeparam>
    /// <param name="requestUri">The absolute or relative request URI.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deserialised object, or <see langword="null"/> if the server returns 404.</returns>
    protected async Task<T?> GetJsonAsync<T>(string requestUri, CancellationToken cancellationToken)
        where T : class
    {
        Logger.LogDebug("GET JSON {Uri}", requestUri);

        using RegistryResponseContext? response = await SendGetAsync(
                requestUri,
                cancellationToken)
            .ConfigureAwait(false);
        if (response is null)
            return null;

        await using DeadlineAwareHttpStream stream =
            await CreateResponseStreamAsync(
                    response,
                    response.Response.Content,
                    cancellationToken)
                .ConfigureAwait(false);

        return await JsonSerializer
            .DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an HTTP GET and deserialises the JSON response body to <typeparamref name="T"/>
    /// (value-type overload).
    /// </summary>
    protected async Task<T?> GetJsonValueAsync<T>(string requestUri, CancellationToken cancellationToken)
        where T : struct
    {
        Logger.LogDebug("GET JSON {Uri}", requestUri);

        using RegistryResponseContext? response = await SendGetAsync(
                requestUri,
                cancellationToken)
            .ConfigureAwait(false);
        if (response is null)
            return null;

        await using DeadlineAwareHttpStream stream =
            await CreateResponseStreamAsync(
                    response,
                    response.Response.Content,
                    cancellationToken)
                .ConfigureAwait(false);

        return await JsonSerializer
            .DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an HTTP GET and returns the response as an <see cref="HttpResponseMessage"/>,
    /// allowing streaming consumption of the body.
    /// </summary>
    /// <param name="requestUri">The absolute or relative request URI.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The response message (caller takes ownership and must dispose it),
    /// or <see langword="null"/> if the server returns 404.
    /// </returns>
    protected async Task<HttpResponseMessage?> GetResponseAsync(
        string requestUri, CancellationToken cancellationToken)
    {
        Logger.LogDebug("GET stream {Uri}", requestUri);
        RegistryResponseContext? context = await SendGetAsync(
                requestUri,
                cancellationToken)
            .ConfigureAwait(false);
        if (context is null)
            return null;

        HttpContent originalContent = context.Response.Content;
        context.Response.Content = new DeadlineAwareResponseContent(
            originalContent,
            context);
        return context.Response;
    }

    /// <summary>
    /// Sends an HTTP POST with a JSON-serialised body and returns the response message.
    /// </summary>
    /// <typeparam name="T">The type of the request body.</typeparam>
    /// <param name="requestUri">The absolute or relative request URI.</param>
    /// <param name="content">The object to serialise as the request body.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The HTTP response message. The caller must dispose it.</returns>
    protected async Task<HttpResponseMessage> PostJsonAsync<T>(
        string requestUri, T content, CancellationToken cancellationToken)
    {
        Logger.LogDebug("POST JSON {Uri}", requestUri);

        string json = JsonSerializer.Serialize(content, JsonOptions);
        using StringContent httpContent = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using HttpRequestMessage request = CreateRequestMessage(HttpMethod.Post, requestUri);
        request.Content = httpContent;

        EnsureDefaultHeadersAreSafe();
        EnsureBodyRequestHasControlledTransport();
        using CancellationTokenSource timeoutSource = CreateTimeoutSource();
        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
        HttpResponseMessage? response = null;
        try
        {
            response = await Http.SendAsync(request, linkedSource.Token)
                .ConfigureAwait(false);
            await EnsureSuccessAsync(
                    response,
                    ResolveRequestUri(requestUri),
                    timeoutSource,
                    cancellationToken)
                .ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw CreateTimeoutException(ResolveRequestUri(requestUri), exception);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sends an HTTP PUT with a stream body and returns the response message.
    /// </summary>
    /// <param name="requestUri">The absolute or relative request URI.</param>
    /// <param name="stream">The stream to send as the request body.</param>
    /// <param name="contentType">The MIME content type (e.g., <c>application/gzip</c>).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The HTTP response message. The caller must dispose it.</returns>
    protected async Task<HttpResponseMessage> PutStreamAsync(
        string requestUri, Stream stream, string contentType, CancellationToken cancellationToken)
    {
        Logger.LogDebug("PUT stream {Uri} ({ContentType})", requestUri, contentType);

        using StreamContent httpContent = new StreamContent(
            new NonDisposingStream(stream));
        httpContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using HttpRequestMessage request = CreateRequestMessage(HttpMethod.Put, requestUri);
        request.Content = httpContent;
        EnsureDefaultHeadersAreSafe();
        EnsureBodyRequestHasControlledTransport();
        using CancellationTokenSource timeoutSource = CreateTimeoutSource();
        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
        HttpResponseMessage? response = null;
        try
        {
            response = await Http.SendAsync(request, linkedSource.Token)
                .ConfigureAwait(false);
            await EnsureSuccessAsync(
                    response,
                    ResolveRequestUri(requestUri),
                    timeoutSource,
                    cancellationToken)
                .ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw CreateTimeoutException(ResolveRequestUri(requestUri), exception);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="PackageDownloadResult"/> that wraps an HTTP response, ensuring the
    /// response is disposed when the download result is disposed.
    /// </summary>
    protected static async Task<PackageDownloadResult> CreateDownloadResultAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        bool responseOwnedByStream =
            response.Content is DeadlineAwareResponseContent;
        Stream innerStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PackageDownloadResult
        {
            Content = responseOwnedByStream
                ? innerStream
                : new ResponseOwningStream(innerStream, response),
            ContentType = response.Content.Headers.ContentType?.MediaType
                ?? "application/gzip",
            ContentLength = response.Content.Headers.ContentLength,
        };
    }

    // ── Private infrastructure ──────────────────────────────────────────

    /// <summary>
    /// Creates an <see cref="HttpRequestMessage"/> with per-request headers from the endpoint configuration.
    /// This avoids mutating <see cref="HttpClient.DefaultRequestHeaders"/> on a shared instance.
    /// </summary>
    private protected HttpRequestMessage CreateRequestMessage(HttpMethod method, string requestUri)
    {
        Uri resolvedRequestUri = ResolveRequestUri(requestUri);
        HttpRequestMessage request = new HttpRequestMessage(
            method,
            resolvedRequestUri);

        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            EndpointConfig.UserAgent ?? DefaultUserAgent);

        if (_trustedHeaderOrigins.Contains(
                RegistryOrigin.Create(resolvedRequestUri)))
        {
            if (EndpointConfig.AuthHeaderValue is not null)
            {
                request.Headers.TryAddWithoutValidation(
                    "Authorization",
                    EndpointConfig.AuthHeaderValue);
            }

            if (EndpointConfig.CustomHeaders is { Count: > 0 } headers)
            {
                foreach ((string? name, string? value) in headers)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        return request;
    }

    private async Task<RegistryResponseContext?> SendGetAsync(
        string requestUri,
        CancellationToken cancellationToken)
    {
        EnsureDefaultHeadersAreSafe();
        EnsureSensitiveHeadersHaveControlledTransport();

        Uri currentUri = ResolveRequestUri(requestUri);
        CancellationTokenSource timeoutSource = CreateTimeoutSource();
        HttpResponseMessage? response = null;
        try
        {
            int redirectsFollowed = 0;
            while (true)
            {
                using HttpRequestMessage request =
                    CreateRequestMessage(HttpMethod.Get, currentUri.AbsoluteUri);
                using CancellationTokenSource linkedSource =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        timeoutSource.Token);

                response = await Http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        linkedSource.Token)
                    .ConfigureAwait(false);

                if (_transport.RedirectsControlled
                    && IsRedirect(response.StatusCode)
                    && response.Headers.Location is Uri location)
                {
                    if (redirectsFollowed >= _transport.MaxRedirects)
                    {
                        throw new HttpRequestException(
                            $"The registry request exceeded the configured redirect limit of {_transport.MaxRedirects}.",
                            inner: null,
                            response.StatusCode);
                    }

                    Uri nextUri = location.IsAbsoluteUri
                        ? location
                        : new Uri(currentUri, location);
                    response.Dispose();
                    response = null;
                    currentUri = nextUri;
                    redirectsFollowed++;
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.NotFound)
                {
                    Logger.LogDebug(
                        "GET {Uri} returned 404 Not Found",
                        currentUri);
                    response.Dispose();
                    response = null;
                    timeoutSource.Dispose();
                    return null;
                }

                await EnsureSuccessAsync(
                        response,
                        currentUri,
                        timeoutSource,
                        cancellationToken)
                    .ConfigureAwait(false);

                RegistryResponseContext context = new(
                    response,
                    timeoutSource,
                    cancellationToken,
                    currentUri);
                response = null;
                return context;
            }
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            timeoutSource.Dispose();
            throw CreateTimeoutException(currentUri, exception);
        }
        catch
        {
            response?.Dispose();
            timeoutSource.Dispose();
            throw;
        }
    }

    private static async Task<DeadlineAwareHttpStream> CreateResponseStreamAsync(
        RegistryResponseContext response,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                response.OperationToken,
                cancellationToken,
                response.TimeoutSource.Token);
        try
        {
            Stream innerStream = await content
                .ReadAsStreamAsync(linkedSource.Token)
                .ConfigureAwait(false);
            DeadlineAwareHttpStream stream = new(
                innerStream,
                response.Response,
                response.TimeoutSource,
                response.OperationToken,
                () => CreateTimeoutException(response.RequestUri),
                content.Headers.ContentLength);
            response.TransferOwnership();
            return stream;
        }
        catch (OperationCanceledException exception)
            when (response.OperationToken.IsCancellationRequested)
        {
            response.Dispose();
            throw new OperationCanceledException(
                exception.Message,
                exception,
                response.OperationToken);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            response.Dispose();
            throw new OperationCanceledException(
                exception.Message,
                exception,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (response.TimeoutSource.IsCancellationRequested)
        {
            response.Dispose();
            throw CreateTimeoutException(response.RequestUri, exception);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        Uri requestUri,
        CancellationTokenSource timeoutSource,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        string body;
        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
        try
        {
            body = await response.Content
                .ReadAsStringAsync(linkedSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
            when (timeoutSource.IsCancellationRequested)
        {
            throw CreateTimeoutException(requestUri, exception);
        }
        catch
        {
            body = "(unable to read response body)";
        }

        Logger.LogError(
            "HTTP {StatusCode} from {Uri}: {Body}",
            (int)response.StatusCode, requestUri, body);

        throw new HttpRequestException(
            $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) from {requestUri}: {body}",
            inner: null,
            response.StatusCode);
    }

    private void EnsureSensitiveHeadersHaveControlledTransport()
    {
        if (_transport.RedirectsControlled
            || (EndpointConfig.AuthHeaderValue is null
                && EndpointConfig.CustomHeaders is not { Count: > 0 }))
        {
            return;
        }

        throw new InvalidOperationException(
            "Authenticated or custom-header registry requests require a redirect-controlled transport.");
    }

    private void EnsureBodyRequestHasControlledTransport()
    {
        if (_transport.RedirectsControlled)
            return;

        throw new InvalidOperationException(
            "Registry requests with a body require a redirect-controlled transport.");
    }

    private void EnsureDefaultHeadersAreSafe()
    {
        if (!Http.DefaultRequestHeaders.Any())
            return;

        throw new InvalidOperationException(
            "Registry HTTP clients must not define DefaultRequestHeaders; configure user-agent, authorization, and custom headers through RegistryEndpoint.");
    }

    private Uri ResolveRequestUri(string requestUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestUri);
        return Uri.TryCreate(requestUri, UriKind.Absolute, out Uri? absolute)
            ? ParseAbsoluteHttpUri(absolute.AbsoluteUri, nameof(requestUri))
            : new Uri(_baseUri, requestUri);
    }

    private CancellationTokenSource CreateTimeoutSource()
    {
        CancellationTokenSource source = new();
        if (_transport.Timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            source.CancelAfter(_transport.Timeout);
        }

        return source;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        s_redirectStatusCodes.Contains(statusCode);

    private static Uri ParseAbsoluteHttpUri(string value, string parameterName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp
                && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "Registry URLs and trusted origins must be absolute HTTP or HTTPS URIs.",
                parameterName);
        }

        return uri;
    }

    private static RegistryResponseTimeoutException CreateTimeoutException(
        Uri requestUri,
        Exception? innerException = null)
    {
        string safeUri =
            $"{requestUri.Scheme}://{requestUri.IdnHost}:{requestUri.Port}{requestUri.AbsolutePath}";
        return new RegistryResponseTimeoutException(
            $"The registry response from '{safeUri}' did not complete before the configured deadline.",
            innerException);
    }

    private sealed class RegistryResponseContext : IDisposable
    {
        private int _ownershipTransferred;
        private int _disposed;

        internal RegistryResponseContext(
            HttpResponseMessage response,
            CancellationTokenSource timeoutSource,
            CancellationToken operationToken,
            Uri requestUri)
        {
            Response = response;
            TimeoutSource = timeoutSource;
            OperationToken = operationToken;
            RequestUri = requestUri;
        }

        internal HttpResponseMessage Response { get; }

        internal CancellationTokenSource TimeoutSource { get; }

        internal CancellationToken OperationToken { get; }

        internal Uri RequestUri { get; }

        internal void TransferOwnership() =>
            Interlocked.Exchange(ref _ownershipTransferred, 1);

        public void Dispose()
        {
            if (Volatile.Read(ref _ownershipTransferred) != 0
                || Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Response.Dispose();
            TimeoutSource.Dispose();
        }
    }

    private sealed class DeadlineAwareResponseContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly RegistryResponseContext _context;

        internal DeadlineAwareResponseContent(
            HttpContent inner,
            RegistryResponseContext context)
        {
            _inner = inner;
            _context = context;
            foreach (KeyValuePair<string, IEnumerable<string>> header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            SerializeToStreamWithDeadlineAsync(
                stream,
                CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            SerializeToStreamWithDeadlineAsync(
                stream,
                cancellationToken);

        protected override void SerializeToStream(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            SerializeToStreamWithDeadlineAsync(
                    stream,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();

        protected override Stream CreateContentReadStream(
            CancellationToken cancellationToken) =>
            CreateResponseStreamAsync(
                    _context,
                    _inner,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(
            CancellationToken cancellationToken) =>
            await CreateResponseStreamAsync(
                    _context,
                    _inner,
                    cancellationToken)
                .ConfigureAwait(false);

        protected override bool TryComputeLength(out long length)
        {
            if (_inner.Headers.ContentLength is long contentLength)
            {
                length = contentLength;
                return true;
            }

            length = 0;
            return false;
        }

        private async Task SerializeToStreamWithDeadlineAsync(
            Stream destination,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource linkedSource =
                CancellationTokenSource.CreateLinkedTokenSource(
                    _context.OperationToken,
                    cancellationToken,
                    _context.TimeoutSource.Token);
            try
            {
                await _inner.CopyToAsync(
                        destination,
                        linkedSource.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (_context.OperationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    exception.Message,
                    exception,
                    _context.OperationToken);
            }
            catch (OperationCanceledException exception)
                when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    exception.Message,
                    exception,
                    cancellationToken);
            }
            catch (OperationCanceledException exception)
                when (_context.TimeoutSource.IsCancellationRequested)
            {
                throw CreateTimeoutException(
                    _context.RequestUri,
                    exception);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _context.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private readonly record struct RegistryOrigin(
        string Scheme,
        string Host,
        int Port)
    {
        internal static RegistryOrigin Create(Uri uri) =>
            new(
                uri.Scheme.ToLowerInvariant(),
                uri.IdnHost.ToLowerInvariant(),
                uri.Port);
    }

    private sealed class NonDisposingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) =>
            inner.Read(buffer);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) =>
            inner.Write(buffer);

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ResponseOwningStream(
        Stream inner,
        HttpResponseMessage response) : Stream
    {
        private int _disposed;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing
                && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                response.Dispose();
            }

            GC.SuppressFinalize(this);
        }
    }

    // ── IRegistryClient virtual method implementations ──────────────────

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        PackageSearchCriteria criteria, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CatalogEntry>>(Array.Empty<CatalogEntry>());

    /// <inheritdoc />
    public virtual Task<PackageListing?> GetPackageListingAsync(
        string packageId, CancellationToken cancellationToken = default) =>
        Task.FromResult<PackageListing?>(null);

    /// <inheritdoc />
    public virtual Task<ResolvedDirective?> ResolveAsync(
        PackageDirective directive, VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ResolvedDirective?>(null);

    /// <inheritdoc />
    public virtual Task<PackageDownloadResult?> DownloadAsync(
        ResolvedDirective resolved, CancellationToken cancellationToken = default) =>
        Task.FromResult<PackageDownloadResult?>(null);

    /// <inheritdoc />
    public virtual Task<PublishResult> PublishAsync(
        PackageReference reference, Stream tarballStream,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"Publishing is not supported by {GetType().Name}.");

    // ── Version resolution helpers ──────────────────────────────────────

    /// <summary>Resolves the best matching version from a <see cref="PackageListing"/>.</summary>
    protected static string? ResolveVersion(
        PackageDirective directive, PackageListing listing, VersionResolveOptions? options)
        => PackageVersionSelector.Select(directive, listing, options)?.Key;

    /// <summary>Returns <paramref name="requestedVersion"/> if it exists in the listing, otherwise <see langword="null"/>.</summary>
    protected static string? ResolveExact(PackageListing listing, string requestedVersion)
    {
        return listing.Versions!.ContainsKey(requestedVersion) ? requestedVersion : null;
    }

    /// <summary>Returns the highest version satisfying a wildcard specifier.</summary>
    protected static string? ResolveWildcard(
        PackageListing listing, string specifier, VersionResolveOptions? options)
    {
        IEnumerable<FhirSemVer> versions = listing.Versions!.Keys.Select(FhirSemVer.Parse);
        bool includePreRelease = options?.AllowPreRelease ?? true;

        return FhirSemVer.MaxSatisfying(versions, specifier, includePreRelease)?.ToString();
    }

    /// <summary>Returns the highest version satisfying a semver range expression.</summary>
    protected static string? ResolveRange(
        PackageListing listing, string rangeExpression, VersionResolveOptions? options)
    {
        IEnumerable<FhirSemVer> versions = listing.Versions!.Keys.Select(FhirSemVer.Parse);
        IEnumerable<FhirSemVer> satisfying = FhirSemVer.SatisfyingRange(versions, rangeExpression);

        if (options?.AllowPreRelease is false)
            satisfying = satisfying.Where(v => !v.IsPreRelease);

        return satisfying.OrderByDescending(v => v).FirstOrDefault()?.ToString();
    }
}
