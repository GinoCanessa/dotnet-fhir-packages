// Copyright (c) Gino Canessa. Licensed under the MIT License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using FhirPkg.Models;
using Microsoft.Extensions.Logging;

namespace FhirPkg.Registry;

/// <summary>
/// Abstract base class that provides shared HTTP infrastructure for registry client implementations.
/// </summary>
/// <remarks>
/// Applies authentication headers, user-agent, and custom headers from the
/// <see cref="RegistryEndpoint"/> on each outbound request (via per-request
/// <see cref="HttpRequestMessage"/> headers) so that a single <see cref="HttpClient"/>
/// instance can be safely shared across multiple registry clients. Provides protected
/// helper methods for common HTTP operations with consistent error handling:
/// <list type="bullet">
///   <item><description>HTTP 404 → <see langword="null"/> (not found).</description></item>
///   <item><description>HTTP 5xx → <see cref="HttpRequestException"/> (server error).</description></item>
///   <item><description>Timeout → <see cref="TaskCanceledException"/> (propagated).</description></item>
/// </list>
/// </remarks>
public abstract class RegistryClientBase : IRegistryClient
{
    private const string DefaultUserAgent = "FhirPkg/1.0";

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
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(logger);

        Http = httpClient;
        EndpointConfig = endpoint;
        Logger = logger;
        BaseUrl = endpoint.Url.TrimEnd('/');
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

        using var request = CreateRequestMessage(HttpMethod.Get, requestUri);
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            Logger.LogDebug("GET {Uri} returned 404 Not Found", requestUri);
            return null;
        }

        await EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
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

        using var request = CreateRequestMessage(HttpMethod.Get, requestUri);
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            Logger.LogDebug("GET {Uri} returned 404 Not Found", requestUri);
            return null;
        }

        await EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
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

        var request = CreateRequestMessage(HttpMethod.Get, requestUri);
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            request.Dispose();
            throw;
        }

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            Logger.LogDebug("GET {Uri} returned 404 Not Found", requestUri);
            response.Dispose();
            return null;
        }

        try
        {
            await EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        return response;
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

        var json = JsonSerializer.Serialize(content, JsonOptions);
        using var httpContent = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var request = CreateRequestMessage(HttpMethod.Post, requestUri);
        request.Content = httpContent;

        var response = await Http.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        await EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
        return response;
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

        using var httpContent = new StreamContent(stream);
        httpContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var request = CreateRequestMessage(HttpMethod.Put, requestUri);
        request.Content = httpContent;
        var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, requestUri, cancellationToken).ConfigureAwait(false);
        return response;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="PackageDownloadResult"/> that wraps an HTTP response, ensuring the
    /// response is disposed when the download result is disposed.
    /// </summary>
    protected static async Task<PackageDownloadResult> CreateDownloadResultAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var innerStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PackageDownloadResult
        {
            Content = new ResponseOwningStream(innerStream, response),
            ContentType = response.Content.Headers.ContentType?.MediaType ?? "application/gzip",
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
        var request = new HttpRequestMessage(method, requestUri);

        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            EndpointConfig.UserAgent ?? DefaultUserAgent);

        if (EndpointConfig.AuthHeaderValue is not null)
        {
            request.Headers.TryAddWithoutValidation(
                "Authorization", EndpointConfig.AuthHeaderValue);
        }

        if (EndpointConfig.CustomHeaders is { Count: > 0 } headers)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return request;
    }

    private async Task EnsureSuccessAsync(
        HttpResponseMessage response, string requestUri, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        string body;
        try
        {
            body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
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

    // ── ResponseOwningStream ────────────────────────────────────────────

    /// <summary>
    /// A <see cref="Stream"/> wrapper that takes ownership of an <see cref="HttpResponseMessage"/>,
    /// disposing it when the stream is disposed.
    /// </summary>
    private protected sealed class ResponseOwningStream(Stream inner, HttpResponseMessage response) : Stream
    {
        /// <inheritdoc />
        public override bool CanRead => inner.CanRead;

        /// <inheritdoc />
        public override bool CanSeek => inner.CanSeek;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => inner.Length;

        /// <inheritdoc />
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        /// <inheritdoc />
        public override int Read(Span<byte> buffer) =>
            inner.Read(buffer);

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            inner.ReadAsync(buffer, offset, count, ct);

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            inner.ReadAsync(buffer, ct);

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        /// <inheritdoc />
        public override void SetLength(long value) => inner.SetLength(value);

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        /// <inheritdoc />
        public override void Flush() => inner.Flush();

        /// <inheritdoc />
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        /// <inheritdoc />
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct) =>
            inner.CopyToAsync(destination, bufferSize, ct);

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
        }
    }

    // ── IRegistryClient virtual method implementations ──────────────────

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<CatalogEntry>> SearchAsync(
        PackageSearchCriteria criteria, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public virtual Task<PackageListing?> GetPackageListingAsync(
        string packageId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public virtual Task<ResolvedDirective?> ResolveAsync(
        PackageDirective directive, VersionResolveOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public virtual Task<PackageDownloadResult?> DownloadAsync(
        ResolvedDirective resolved, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public virtual Task<PublishResult> PublishAsync(
        PackageReference reference, Stream tarballStream,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
